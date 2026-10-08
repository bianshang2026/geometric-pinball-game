using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codely.Newtonsoft.Json;
using Codely.Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityTcp.Editor.Helpers;

namespace UnityTcp.Editor.Tools.Jobs
{
    /// <summary>
    /// Step jobs for the UPM actions. Installing or removing a package makes Unity resolve the
    /// manifest and then reload the domain to load the new assemblies — which is exactly what the
    /// previous implementation could not survive: it held the <see cref="Request"/> and its
    /// <c>EditorApplication.update</c> callback in plain statics, so after the reload nothing was
    /// left to complete the tracker job and <c>wait_for_upm</c> polled until it timed out.
    ///
    /// A <see cref="StepJob"/> fixes that structurally. The live Request still cannot be persisted,
    /// so the job does not try: when it comes back in a new domain and finds the request gone, it
    /// establishes the outcome the only way that is still reliable — by asking UPM what is actually
    /// installed now.
    /// </summary>
    public class UpmJob : StepJob
    {
        /// <summary>False installs <see cref="Target"/>; true removes it.</summary>
        public bool Remove;

        /// <summary>Package id, "id@version", git URL or local path for install; package name for remove.</summary>
        public string Target;

        /// <summary>Registry name to verify against, or null when Target is a URL or local path.</summary>
        public string PackageName;

        public bool Started;
        public bool RequestObserved;   // the request completed while we still held it
        public bool RequestSucceeded;
        public string RequestError;

        /// <summary>Frames to let the operation settle before concluding the domain reload ate it.</summary>
        public int SettleFrames = 5;

        public string VerifiedVersion;
        public bool VerifiedPresent;
        public bool VerificationRan;

        /// <summary>
        /// Set to true when the package is in the resolved graph but not listed in
        /// Packages/manifest.json (a transitive or system dependency). Null when the check
        /// was skipped (URL targets, manifest unreadable, or the package is a direct entry).
        /// </summary>
        public bool? AlreadyPresentAsTransitive;

        // Neither survives a domain reload — that is the whole point of the fallback path.
        [JsonIgnore] private Request _request;
        [JsonIgnore] private ListRequest _list;

        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("start-request", StartRequest),

            new JobStep("await-request", ObserveRequest, CanLeaveRequest),

            // Always runs. A URL or local-path install has no registry name to look up, but the
            // resolved manifest still records where each package came from, so the install can be
            // confirmed by matching the source instead — see MatchesTarget. Skipping verification
            // there would leave exactly the case that needs it most (the domain reload ate the
            // request, so nothing else knows the outcome) reporting an unchecked success.
            new JobStep("verify", RunListRequest, () => _list != null && _list.IsCompleted),

            new JobStep("report", Report),
        };

        private void StartRequest()
        {
            if (Started) return;
            Started = true;

            if (string.IsNullOrEmpty(Target))
            {
                Fail(Remove
                    ? "'package_name' parameter required for remove_package."
                    : "'id_or_url' parameter required for install_package.");
                return;
            }

            if (!Remove && Target.Contains("com.unity.textmeshpro"))
            {
                // Must be armed before the request starts: the flag lives in SessionState so it
                // survives the reload UPM triggers and is picked up while that reload loads the TMP
                // assembly, ahead of any validate step. Prevents the interactive "TMP Importer"
                // window and the missing default-font NullReferenceException.
                TmpEssentialsAutoImporter.ScheduleImport();
            }

            _request = Remove
                ? (Request)Client.Remove(Target)
                : Client.Add(Target);
        }

        private void ObserveRequest()
        {
            if (_request == null)
            {
                // A domain reload took the request with it. UPM commits the manifest before
                // triggering that reload, so the operation itself is done — but give the editor a
                // few frames to finish importing before we go and verify.
                SettleFrames--;
                return;
            }

            if (!_request.IsCompleted) return;

            RequestObserved = true;
            RequestSucceeded = _request.Status == StatusCode.Success;
            RequestError = _request.Error?.message;

            // Backstop for an install that did NOT reload the domain (TMP already compiled): the
            // pre-request flag is still pending, so re-attempt the silent import now. No-op once
            // the essentials exist.
            if (RequestSucceeded && _request is AddRequest added &&
                added.Result?.name == "com.unity.textmeshpro")
            {
                TmpEssentialsAutoImporter.ScheduleImport();
            }
        }

        private bool CanLeaveRequest()
        {
            if (_request != null) return _request.IsCompleted;
            return SettleFrames <= 0 && !EditorApplication.isCompiling && !EditorApplication.isUpdating;
        }

        private void RunListRequest()
        {
            if (_list != null) return;
            // offlineMode: true — read the resolved manifest rather than hitting the registry.
            _list = Client.List(offlineMode: true, includeIndirectDependencies: false);
        }

        private void ReadListResult()
        {
            if (_list == null || !_list.IsCompleted) return;
            // A failed list tells us nothing about the package, so it must not count as
            // verification — Report falls back on whatever the request itself said, and fails
            // outright when the request is gone too.
            if (_list.Status != StatusCode.Success) return;

            VerificationRan = true;

            var found = !string.IsNullOrEmpty(PackageName)
                ? _list.Result?.FirstOrDefault(p => p.name == PackageName)
                : _list.Result?.FirstOrDefault(MatchesTarget);
            VerifiedPresent = found != null;
            VerifiedVersion = found?.version;

            if (VerifiedPresent)
            {
                bool? inManifest = CheckUserManifest();
                if (inManifest == false)
                    AlreadyPresentAsTransitive = true;
            }
        }

        /// <summary>
        /// Whether <see cref="PackageName"/> is a direct entry in Packages/manifest.json.
        /// Null when there is no name to look up, or the file cannot be read.
        /// </summary>
        private bool? CheckUserManifest()
        {
            if (string.IsNullOrEmpty(PackageName)) return null;
            try
            {
                string manifestPath = Path.Combine(
                    Directory.GetParent(Application.dataPath).FullName,
                    "Packages", "manifest.json");
                if (!File.Exists(manifestPath)) return null;
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                var deps = manifest["dependencies"] as JObject;
                return deps?[PackageName] != null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Matches a resolved package against a <see cref="Target"/> that carries no registry name
        /// — a git URL, a tarball or a local folder. UPM records these in the package id as
        /// "name@source", so the part after the first '@' is the target we asked for.
        /// </summary>
        private bool MatchesTarget(UnityEditor.PackageManager.PackageInfo package)
        {
            string id = package?.packageId;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(Target)) return false;

            int at = id.IndexOf('@');
            string source = at >= 0 ? id.Substring(at + 1) : id;
            if (string.IsNullOrEmpty(source)) return false;

            if (string.Equals(source, Target, StringComparison.OrdinalIgnoreCase)) return true;

            // Beyond an exact hit, only compare things that are actually locations. A registry
            // package's source is a bare version ("1.2.3"), and substring-matching that against a
            // URL would happily "verify" an unrelated package.
            if (!IsLocation(source) || !IsLocation(Target)) return false;

            // A git target may carry a #revision the manifest normalizes away, and a local path may
            // be recorded relative rather than as given.
            return source.IndexOf(Target, StringComparison.OrdinalIgnoreCase) >= 0
                   || Target.IndexOf(source, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLocation(string value)
            => value.IndexOf("://", StringComparison.Ordinal) >= 0
               || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("git", StringComparison.OrdinalIgnoreCase)
               || value.IndexOf('/') >= 0
               || value.IndexOf('\\') >= 0;

        internal void Report()
        {
            ReadListResult();

            string what = PackageName ?? Target;

            // An error we actually saw from UPM is the most specific thing we can report —
            // unless remove failed while the package is still in the resolved graph and is
            // confirmed not in Packages/manifest.json: that is a transitive / system
            // dependency, and UPM's own "not in the project manifest" is true but not actionable.
            if (RequestObserved && !RequestSucceeded)
            {
                if (Remove && VerificationRan && VerifiedPresent && AlreadyPresentAsTransitive == true)
                {
                    Fail(TransitiveRemoveMessage(what));
                    return;
                }
                Fail($"[EXPERIMENTAL] Package {(Remove ? "removal" : "installation")} failed for " +
                     $"'{what}': {RequestError ?? "UPM reported no error message."}");
                return;
            }

            if (VerificationRan)
            {
                bool ok = Remove ? !VerifiedPresent : VerifiedPresent;
                if (!ok)
                {
                    if (Remove && VerifiedPresent && AlreadyPresentAsTransitive == true)
                    {
                        Fail(TransitiveRemoveMessage(what));
                        return;
                    }
                    Fail($"[EXPERIMENTAL] Package {(Remove ? "removal" : "installation")} did not take " +
                         $"effect for '{what}': the package is {(Remove ? "still" : "not")} present in " +
                         "the resolved manifest.");
                    return;
                }

                if (!Remove && AlreadyPresentAsTransitive == true)
                {
                    var noopData = new Dictionary<string, object>
                    {
                        ["package"] = what,
                        ["type"] = "install",
                        ["verified"] = true,
                        ["already_present"] = true,
                        ["in_user_manifest"] = false,
                    };
                    if (!string.IsNullOrEmpty(VerifiedVersion)) noopData["version"] = VerifiedVersion;

                    Complete(Response.Success(
                        $"[EXPERIMENTAL] Package '{what}' is already present as a transitive or " +
                        $"system dependency (version {VerifiedVersion ?? "unknown"}). " +
                        "It was not added to Packages/manifest.json because UPM treats it as " +
                        "already installed. The package is usable in code. Note: 'remove_package' " +
                        "cannot remove transitive dependencies — to remove it, remove the package " +
                        "that depends on it.",
                        noopData));
                    return;
                }
            }
            else if (!RequestObserved)
            {
                // The domain reload took the request and the manifest could not be read back, so
                // nothing here knows whether the operation worked. Reporting success on that would
                // send the caller off building against a package that may not exist.
                Fail($"[EXPERIMENTAL] Package {(Remove ? "removal" : "installation")} could not be " +
                     $"confirmed for '{what}': the domain reload took the UPM request with it and " +
                     "the resolved manifest could not be read back. Check the Package Manager " +
                     "window or Packages/manifest.json, then retry if needed.");
                return;
            }

            var data = new Dictionary<string, object>
            {
                ["package"] = what,
                ["type"] = Remove ? "remove" : "install",
                // False when the resolved manifest could not be read back: the success below then
                // rests on UPM's own report of the request, which is a good signal but not proof
                // of what ended up in the manifest.
                ["verified"] = VerificationRan,
                // True when the outcome was established by re-reading the manifest after the domain
                // reload rather than from the request object itself.
                ["verified_after_reload"] = !RequestObserved,
            };
            if (!string.IsNullOrEmpty(VerifiedVersion)) data["version"] = VerifiedVersion;

            string message = Remove
                ? $"[EXPERIMENTAL] Package removed: {what}"
                : $"[EXPERIMENTAL] Package installed: {what}" +
                  (string.IsNullOrEmpty(VerifiedVersion) ? "" : $"@{VerifiedVersion}");

            if (!VerificationRan)
            {
                message += " — reported by UPM; the resolved manifest could not be read back to " +
                           "confirm it.";
            }

            Complete(Response.Success(message, data));
        }

        private static string TransitiveRemoveMessage(string what)
            => $"[EXPERIMENTAL] Package '{what}' is present in the resolved project " +
               "as a transitive or system dependency, but is not listed in " +
               "Packages/manifest.json as a direct entry. Unity's Package Manager " +
               "only removes packages you explicitly added. To remove this package, " +
               "remove the package that depends on it instead.";

        /// <summary>
        /// Registry name to verify an operation against, or null when the target is a git URL, a
        /// local tarball or a folder — those carry no name we can look up until UPM resolves them.
        /// </summary>
        public static string RegistryNameOf(string idOrUrl)
        {
            if (string.IsNullOrEmpty(idOrUrl)) return null;
            if (idOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                idOrUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                idOrUrl.StartsWith("git", StringComparison.OrdinalIgnoreCase) ||
                idOrUrl.Contains("/") || idOrUrl.Contains("\\"))
                return null;

            int at = idOrUrl.IndexOf('@');
            return at > 0 ? idOrUrl.Substring(0, at) : idOrUrl;
        }
    }

    /// <summary>
    /// Lists installed packages. A step job because <see cref="Client.List"/> is asynchronous: the
    /// previous implementation spun on <c>Thread.Sleep(100)</c> on the editor thread, freezing the
    /// whole editor for the duration of the call.
    /// </summary>
    public class ListPackagesJob : StepJob
    {
        [JsonIgnore] private ListRequest _list;

        // Issuing, awaiting and reporting are one step because the request cannot be persisted: a
        // domain reload landing anywhere in here leaves _list null in the new domain, and a
        // separate report step would dereference it. Kept as a single step, the reload simply
        // re-issues the query. The step ends by calling Complete/Fail rather than by leaving.
        protected override JobStep[] BuildSteps() => new[]
        {
            new JobStep("list-packages", Poll, canLeave: () => false),
        };

        private void Poll()
        {
            if (_list == null)
            {
                _list = Client.List(true, false);
                return;
            }
            if (!_list.IsCompleted) return;
            Report();
        }

        private void Report()
        {
            if (_list.Status != StatusCode.Success)
            {
                Fail($"[EXPERIMENTAL] Failed to list packages: " +
                     (_list.Error?.message ?? "UPM reported no error message."));
                return;
            }

            var packages = _list.Result.Select(p => new
            {
                name = p.name,
                version = p.version,
                displayName = p.displayName,
                description = p.description,
                source = p.source.ToString(),
            }).ToList();

            Complete(Response.Success($"[EXPERIMENTAL] Retrieved {packages.Count} packages.", packages));
        }
    }
}
