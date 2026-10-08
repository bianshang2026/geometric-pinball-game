using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityTcp.Editor.Tools
{
    public static partial class Repl
    {
        // Ported from cn.tuanjie.csharp-repl's ApiHelper (TuanJie.CSharpRepl.Editor.ApiHelper),
        // nested here so it rides Repl's existing unconditional-imports injection instead of needing
        // its own. Reflecting a package's full member set on every query is the slow path this class
        // exists to avoid, so results are indexed once and kept -- in memory for the session, and on
        // disk (unlike every other REPL cache in this file, which is deliberately memory-only and
        // relies on a domain reload to invalidate it) because building the index is expensive enough
        // (reflecting every type in every matching assembly in the AppDomain) that it's worth
        // surviving a reload or an Editor restart. The on-disk root moves from the original
        // Packages/cn.tuanjie.csharp-repl/Agents~/References/ApiDocs (a directory convention this
        // package doesn't have) to Library/CodelyBridge/ApiDocs -- Unity's standard per-project,
        // git-ignored, freely-regenerable-cache location.
        public static class ApiHelper
        {
            internal static readonly string ApiDocsRoot = Path.Combine("Library", "CodelyBridge", "ApiDocs");
            static readonly Dictionary<string, PackageIndex> s_indexes = new Dictionary<string, PackageIndex>(StringComparer.OrdinalIgnoreCase);
            static readonly HashSet<string> s_fallbackBuilt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public struct MemberRef
            {
                public string type, name, kind, acc;
                public bool isStatic;
                public string Describe() => $"{(isStatic ? "static " : "")}{kind} {type}.{name} ({acc})";
            }

            class PackageIndex
            {
                public Dictionary<string, List<MemberRef>> members;
                public Dictionary<string, string> types;
                public Dictionary<string, List<DetailEntry>> details;
                public string detailPath;
                public bool detailLoaded;
                public string version; // stored version from manifest
            }

            public struct DetailEntry { public string acc; public bool isStatic; public string sig; }

            // Chainable query builder. Defaults: kind="method", access="pub". .Get() -> human-readable.
            // .Json() -> structured.
            public class QueryBuilder
            {
                internal string pkg, kw, kind, acc;
                internal bool asJson;
                const int MaxResults = 200;

                internal QueryBuilder(string p, string k) { pkg = p; kw = k; kind = "method"; acc = "pub"; }

                public QueryBuilder Kind(string k)    { kind = k; return this; }
                public QueryBuilder Access(string a)  { acc = a; return this; }
                public QueryBuilder Methods() => Kind("method");
                public QueryBuilder Props()   => Kind("prop");
                public QueryBuilder Fields()  => Kind("field");
                public QueryBuilder Public()    => Access("pub");
                public QueryBuilder Internal()  => Access("int");
                public QueryBuilder Private()   => Access("pri");
                public QueryBuilder AllKinds()  { kind = null; return this; }
                public QueryBuilder AllAccess() { acc = null; return this; }
                public QueryBuilder Json()      { asJson = true; return this; }

                public string Get()
                {
                    if (pkg == null) return GetAllPackages();
                    var idx = GetIndex(pkg);
                    if (idx == null) idx = BuildFallbackIndex(pkg);
                    if (idx == null) return asJson ? "{\"error\":\"Package not found: " + pkg + "\"}" : "[ApiHelper] Package not found: " + pkg;
                    return SearchIndex(idx, pkg);
                }

                string GetAllPackages()
                {
                    var allPkgs = new List<string>();
                    if (Directory.Exists(ApiDocsRoot))
                        allPkgs = Directory.GetDirectories(ApiDocsRoot).Select(Path.GetFileName).Where(n => n != "_archive" && File.Exists(Path.Combine(ApiDocsRoot, n, "api-index.json"))).ToList();
                    if (allPkgs.Count == 0) return asJson ? "{\"count\":0,\"error\":\"No indexed packages\"}" : "[ApiHelper] No indexed packages found.";
                    var norm = Normalize(kw.ToLowerInvariant());
                    if (asJson)
                    {
                        var jsb = new StringBuilder(); jsb.Append("{\"count\":0,\"packages\":{");
                        var firstPkg = true; var total = 0;
                        foreach (var p in allPkgs.OrderBy(n => n))
                        {
                            var i = GetIndex(p); if (i == null) continue;
                            var cnt = 0; foreach (var kv in i.members) { if (!Normalize(kv.Key).Contains(norm)) continue; cnt += kv.Value.Count(r => (kind == null || r.kind == kind) && (acc == null || r.acc == acc)); } total += cnt;
                            if (!firstPkg) jsb.Append(","); firstPkg = false;
                            jsb.Append("\"" + p + "\":" + cnt);
                        }
                        jsb.Append("},\"total\":" + total + "}"); return jsb.ToString();
                    }
                    var sb = new StringBuilder();
                    sb.AppendLine("Searching " + allPkgs.Count + " packages for '" + kw + "':");
                    var tot = 0; var pkgCount = 0;
                    foreach (var p in allPkgs.OrderBy(n => n))
                    {
                        var i = GetIndex(p); if (i == null) continue;
                        var cnt = 0; foreach (var kv in i.members) { if (!Normalize(kv.Key).Contains(norm)) continue; cnt += kv.Value.Count(r => (kind == null || r.kind == kind) && (acc == null || r.acc == acc)); }
                        if (cnt > 0) { sb.AppendLine("  " + p + ": " + cnt + " match(es)"); tot += cnt; pkgCount++; }
                    }
                    if (tot == 0) return "[ApiHelper] No match for '" + kw + "' across " + allPkgs.Count + " packages.";
                    sb.AppendLine("Total: " + tot + " match(es) across " + pkgCount + " packages. Use ApiHelper.Find(package, \"" + kw + "\").Get() to see details.");
                    return sb.ToString();
                }

                string SearchIndex(PackageIndex idx, string pkgName)
                {
                    var results = new List<MemberRef>();
                    var lower = kw.ToLowerInvariant();
                    var normalized = Normalize(lower);
                    foreach (var kv in idx.members)
                    {
                        var nk = Normalize(kv.Key);
                        if (!nk.Contains(normalized)) continue;
                        foreach (var r in kv.Value)
                        {
                            if (kind != null && r.kind != kind) continue;
                            if (acc != null && r.acc != acc) continue;
                            results.Add(r);
                            if (results.Count >= MaxResults) break;
                        }
                        if (results.Count >= MaxResults) break;
                    }
                    if (results.Count == 0)
                    {
                        var msg = "[ApiHelper] No " + (kind ?? "member") + " matching '" + kw + "' in " + pkgName + (acc != null ? " (access=" + acc + ")" : "") + ". Try .AllKinds() or .AllAccess().";
                        return asJson ? "{\"count\":0,\"hint\":\"" + msg.Replace("\"", "\\\"") + "\"}" : msg;
                    }
                    if (asJson) return FormatJson(results);
                    return FormatText(results);
                }

                string FormatText(List<MemberRef> results)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(results.Count + " match(es):");
                    var shown = new HashSet<string>();
                    foreach (var r in results)
                    {
                        sb.AppendLine("  " + r.Describe());
                        var key = r.type + "." + r.name;
                        if (shown.Add(key))
                            sb.AppendLine("    => Detail(\"" + r.type + "\", \"" + r.name + "\")");
                    }
                    return sb.ToString();
                }

                string FormatJson(List<MemberRef> results)
                {
                    var sb = new StringBuilder();
                    sb.Append("{\"count\":" + results.Count + ",\"results\":[");
                    for (int i = 0; i < results.Count; i++)
                    {
                        var r = results[i];
                        sb.Append("{\"type\":\"" + r.type + "\",\"name\":\"" + r.name + "\",\"kind\":\"" + r.kind + "\",\"access\":\"" + r.acc + "\",\"static\":" + (r.isStatic ? "1" : "0") + "}");
                        if (i < results.Count - 1) sb.Append(",");
                    }
                    sb.Append("]}");
                    return sb.ToString();
                }

                static string Normalize(string s)
                {
                    var sb = new StringBuilder(s.Length);
                    foreach (var c in s) { if (c != '_' && c != '-' && c != '.') sb.Append(c); }
                    return sb.ToString();
                }
            }

            // -- Public API --

            public static QueryBuilder Find(string package, string memberNamePart)
                => new QueryBuilder(package, memberNamePart);

            // Search ALL indexed packages for a member keyword. Returns QueryBuilder (needs .Get()).
            public static QueryBuilder SearchAll(string keyword)
                => new QueryBuilder(null, keyword);

            static string NormalizeAll(string s)
            {
                var sb = new StringBuilder(s.Length);
                foreach (var c in s) { if (c != '_' && c != '-' && c != '.') sb.Append(c); }
                return sb.ToString();
            }

            public static string Detail(string typeFullName, string memberName)
            {
                var key = typeFullName + "." + memberName;
                foreach (var pkg in s_indexes.Keys.ToArray())
                {
                    var idx2 = s_indexes[pkg];
                    if (!idx2.detailLoaded) LoadDetails(pkg, idx2);
                    if (idx2.details.TryGetValue(key, out var entries))
                        return FormatDetailList(key, entries);
                }
                if (Directory.Exists(ApiDocsRoot))
                {
                    foreach (var dir in Directory.GetDirectories(ApiDocsRoot))
                    {
                        var pkgName = Path.GetFileName(dir);
                        if (pkgName == "_archive") continue;
                        var idx3 = GetIndex(pkgName);
                        if (idx3 == null) continue;
                        if (!idx3.detailLoaded) LoadDetails(pkgName, idx3);
                        if (idx3.details.TryGetValue(key, out var entries))
                            return FormatDetailList(key, entries);
                    }
                }
                return "[ApiHelper] No detail for " + key;
            }

            public static string ListType(string typeFullName)
            {
                foreach (var pkg in s_indexes.Keys.ToArray())
                    if (s_indexes[pkg].types.TryGetValue(typeFullName, out var mn))
                        return typeFullName + " members: " + mn;
                if (Directory.Exists(ApiDocsRoot))
                {
                    foreach (var dir in Directory.GetDirectories(ApiDocsRoot))
                    {
                        var pkgName = Path.GetFileName(dir);
                        if (pkgName == "_archive") continue;
                        var idx2 = GetIndex(pkgName);
                        if (idx2 != null && idx2.types.TryGetValue(typeFullName, out var mn))
                            return typeFullName + " members: " + mn;
                    }
                }
                return "[ApiHelper] Type not found: " + typeFullName;
            }

            public static string Browse(string package, string keyword = null)
            {
                var idx = GetIndex(package);
                if (idx == null) idx = BuildFallbackIndex(package);
                if (idx == null) return "[ApiHelper] Package not found: " + package;
                if (string.IsNullOrEmpty(keyword))
                {
                    var ns = new SortedDictionary<string, int>();
                    foreach (var tn in idx.types.Keys)
                    {
                        var ld = tn.LastIndexOf('.');
                        var n = ld > 0 ? tn.Substring(0, ld) : "(root)";
                        if (!ns.ContainsKey(n)) ns[n] = 0;
                        ns[n]++;
                    }
                    var sb = new StringBuilder();
                    sb.AppendLine(package + ": " + ns.Count + " namespaces, " + idx.types.Count + " types");
                    foreach (var kv in ns) sb.AppendLine("  " + kv.Key + ": " + kv.Value);
                    return sb.ToString();
                }
                else
                {
                    var lower = keyword.ToLowerInvariant();
                    var norm = NormalizeAll(lower);
                    var matched = new List<string>();
                    foreach (var tn in idx.types.Keys)
                    {
                        if (NormalizeAll(tn.ToLowerInvariant()).Contains(norm))
                        {
                            matched.Add(tn);
                            if (matched.Count >= 100) break;
                        }
                    }
                    if (matched.Count == 0) return "[ApiHelper] No type matching '" + keyword + "' in " + package;
                    var sb2 = new StringBuilder();
                    sb2.AppendLine(matched.Count + " type(s) matching '" + keyword + "':");
                    foreach (var t in matched)
                    {
                        var mc = idx.types.TryGetValue(t, out var ms) ? ms.Split(',').Length : 0;
                        sb2.AppendLine("  " + t + " (" + mc + " members)");
                    }
                    return sb2.ToString();
                }
            }

            public static string ClearCache() { s_indexes.Clear(); s_fallbackBuilt.Clear(); return "Cache cleared."; }

            // Not part of the original ApiHelper: staleness there is judged purely by comparing the
            // manifest's stored version against PackageInfo.FindForPackageName(package).version, so
            // editing a package's code without bumping its version leaves the cached index (memory
            // or disk) looking valid forever -- ClearCache() alone doesn't fix this either, since the
            // next query just reloads that same still-version-matching disk file. This bypasses the
            // version check entirely: drop both the in-memory entry and the on-disk folder for this
            // one package, so the next query is a guaranteed real rebuild, regardless of version.
            public static string Refresh(string package)
            {
                s_indexes.Remove(package);
                s_fallbackBuilt.Remove(package);
                var dir = Path.Combine(ApiDocsRoot, package);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return "Refreshed " + package + ".";
            }

            public static string Packages()
            {
                var sb = new StringBuilder();
                sb.AppendLine("Documented packages:");
                var dirs = Directory.Exists(ApiDocsRoot)
                    ? Directory.GetDirectories(ApiDocsRoot).Select(Path.GetFileName).Where(n => n != "_archive").OrderBy(n => n).ToArray()
                    : new string[0];
                foreach (var d in dirs)
                {
                    var has = File.Exists(Path.Combine(ApiDocsRoot, d, "api-index.json"));
                    sb.AppendLine("  " + d + (has ? " [indexed]" : ""));
                }
                return sb.ToString();
            }

            // -- Reflection fallback (with version tracking) --

            // Not the original ApiHelper's algorithm: that version matched a package id against
            // loaded assembly names by fuzzy per-segment substring (e.g. "com.unity.burst" ->
            // requires "com"/"unity"/"burst" all as substrings somewhere in the assembly name).
            // Tested against every package actually installed in this project: every real UPM
            // package id (com.unity.*, cn.tuanjie.*) matched zero assemblies, because assembly
            // names come from each package's own .asmdef "name" field (chosen independently by the
            // package author) and have no reliable string relationship to the reverse-DNS package
            // id -- e.g. cn.tuanjie.codely.bridge's four asmdefs are named UnityTcp.Editor /
            // Codely.Common / Cn.Tuanjie.Codely.Editor / UnityTcp.Editor.Tests, none containing
            // "bridge". Worse, when the fuzzy match did hit, it silently hit the *wrong* assembly:
            // both "com.unity.burst" and "com.unity.collections" matched the same unrelated helper
            // assembly Unity.Collections.BurstCompatibilityGen (its name happens to contain both
            // "unity"+"burst" and "unity"+"collections" as substrings) -- a false positive that
            // returns another package's signatures instead of reporting "not found", strictly worse
            // than a miss. Replaced with two deterministic layers and no substring guessing: (1)
            // resolve the package's own .asmdef-declared assembly names via PackageInfo, matched
            // exactly; (2) exact assembly-name equality, for callers who pass a bare assembly name
            // instead of a package id. If neither finds anything, this is genuinely not found.
            static Assembly[] ResolvePackageAssemblies(string package)
            {
                string pkgAssetPath = null;
                try
                {
                    var pkgInfoType = Type.GetType("UnityEditor.PackageManager.PackageInfo, UnityEditor.CoreModule");
                    if (pkgInfoType != null)
                    {
                        var findMethod = pkgInfoType.GetMethod("FindForPackageName", BindingFlags.Public | BindingFlags.Static);
                        if (findMethod != null)
                        {
                            var info = findMethod.Invoke(null, new object[] { package }) as UnityEditor.PackageManager.PackageInfo;
                            if (info != null && !string.IsNullOrEmpty(info.assetPath))
                                pkgAssetPath = info.assetPath;
                        }
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(pkgAssetPath))
                    pkgAssetPath = ResolvePackageDirectory(package);
                if (!string.IsNullOrEmpty(pkgAssetPath) && Directory.Exists(pkgAssetPath))
                {
                    var asmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var asmdefPath in Directory.GetFiles(pkgAssetPath, "*.asmdef", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var name = ExtractAsmdefName(File.ReadAllText(asmdefPath));
                            if (!string.IsNullOrEmpty(name))
                                asmNames.Add(name);
                        }
                        catch { }
                    }
                    if (asmNames.Count > 0)
                    {
                        var viaAsmdef = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(a => asmNames.Contains(a.GetName().Name))
                            .ToArray();
                        if (viaAsmdef.Length > 0)
                            return viaAsmdef;
                    }
                }

                return AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => string.Equals(a.GetName().Name, package, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            static string ResolvePackageDirectory(string package)
            {
                var projectRoot = Directory.GetCurrentDirectory();
                var standardPath = Path.Combine(projectRoot, "Packages", package);
                if (Directory.Exists(standardPath))
                    return standardPath;

                var cacheDir = Path.Combine(projectRoot, "Library", "PackageCache");
                try
                {
                    if (Directory.Exists(cacheDir))
                    {
                        var matchedDir = Directory.EnumerateDirectories(cacheDir, package + "@*").FirstOrDefault();
                        if (matchedDir != null)
                            return matchedDir;
                    }
                }
                catch { }

                return null;
            }

            static string ExtractPackageJsonVersion(string json)
            {
                var m = Regex.Match(json, @"(?m)^\s*""version""\s*:\s*""([^""]+)""");
                return m.Success ? m.Groups[1].Value : null;
            }

            // Deliberately not ExtractJsonValue (below): that helper's exact-substring search
            // ("\"name\":\"") assumes the compact, no-whitespace JSON this class writes for its own
            // persisted cache. A real .asmdef file is pretty-printed by Unity with a space after the
            // colon ("name": "UnityTcp.Editor"), which silently misses that search and falls through
            // to ExtractJsonValue's non-quoted-value branch, returning garbage (quote characters and
            // all) instead of failing loudly. Asmdef JSON is externally authored, not this class's
            // own output, so it gets its own whitespace-tolerant parse.
            static string ExtractAsmdefName(string json)
            {
                var m = Regex.Match(json, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                return m.Success ? m.Groups[1].Value : null;
            }

            static PackageIndex BuildFallbackIndex(string package)
            {
                if (!s_fallbackBuilt.Add(package)) return null;
                var asms = ResolvePackageAssemblies(package).Distinct().ToArray();
                if (asms.Length == 0) return null;

                var idx = new PackageIndex { members = new Dictionary<string, List<MemberRef>>(StringComparer.OrdinalIgnoreCase), types = new Dictionary<string, string>(), details = new Dictionary<string, List<DetailEntry>>(), detailPath = null, detailLoaded = true };
                var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var di = new Dictionary<string, List<DetailEntry>>();
                foreach (var asm in asms)
                {
                    try { foreach (var t in asm.GetTypes()) { if (t.IsNested || t.Name.StartsWith("<")) continue;
                        var isP = t.IsPublic || t.IsNestedPublic; var a = isP ? "pub" : "int";
                        var mn = new List<string>();
                        try { foreach (var m in t.GetMethods(bf)) { if (m.IsSpecialName) continue;
                            var n = m.Name; var st = m.IsStatic; var sig = m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ") : " + m.ReturnType.Name;
                            var lo = n.ToLowerInvariant(); var rf = new MemberRef { type = t.FullName, name = n, kind = "method", acc = a, isStatic = st };
                            if (!idx.members.ContainsKey(lo)) idx.members[lo] = new List<MemberRef>(); idx.members[lo].Add(rf); mn.Add(n);
                            var dk = t.FullName + "." + n; if (!di.ContainsKey(dk)) di[dk] = new List<DetailEntry>();
                            di[dk].Add(new DetailEntry { acc = a, isStatic = st, sig = sig });
                        } } catch { }
                        try { foreach (var p in t.GetProperties(bf)) { if (p.GetIndexParameters().Length > 0) continue;
                            var n = p.Name; var st = p.GetAccessors(true).Length > 0 && p.GetAccessors(true)[0].IsStatic; var sig = p.Name + " : " + p.PropertyType.Name + (p.CanWrite ? " rw" : " ro");
                            var lo = n.ToLowerInvariant(); var rf = new MemberRef { type = t.FullName, name = n, kind = "prop", acc = a, isStatic = st };
                            if (!idx.members.ContainsKey(lo)) idx.members[lo] = new List<MemberRef>(); idx.members[lo].Add(rf); mn.Add(n);
                            var dk = t.FullName + "." + n; if (!di.ContainsKey(dk)) di[dk] = new List<DetailEntry>();
                            di[dk].Add(new DetailEntry { acc = a, isStatic = st, sig = sig });
                        } } catch { }
                        try { foreach (var f2 in t.GetFields(bf)) {
                            var n = f2.Name; var st = f2.IsStatic; var sig = f2.Name + " : " + f2.FieldType.Name;
                            var lo = n.ToLowerInvariant(); var rf = new MemberRef { type = t.FullName, name = n, kind = "field", acc = a, isStatic = st };
                            if (!idx.members.ContainsKey(lo)) idx.members[lo] = new List<MemberRef>(); idx.members[lo].Add(rf); mn.Add(n);
                            var dk = t.FullName + "." + n; if (!di.ContainsKey(dk)) di[dk] = new List<DetailEntry>();
                            di[dk].Add(new DetailEntry { acc = a, isStatic = st, sig = sig });
                        } } catch { }
                        if (mn.Count > 0) idx.types[t.FullName] = string.Join(",", mn);
                    } } catch (ReflectionTypeLoadException) { } catch { }
                }
                idx.details = di;

                // Get installed version
                var version = "";
                try
                {
                    var pkgInfoType = Type.GetType("UnityEditor.PackageManager.PackageInfo, UnityEditor.CoreModule");
                    if (pkgInfoType != null)
                    {
                        var findMethod = pkgInfoType.GetMethod("FindForPackageName", BindingFlags.Public | BindingFlags.Static);
                        if (findMethod != null)
                        {
                            var info = findMethod.Invoke(null, new object[] { package });
                            if (info != null)
                            {
                                var verProp = info.GetType().GetProperty("version");
                                if (verProp != null) version = verProp.GetValue(info)?.ToString() ?? "";
                            }
                        }
                    }
                }
                catch { }

                // Persist to disk with version
                try
                {
                    var dir = Path.Combine(ApiDocsRoot, package);
                    Directory.CreateDirectory(dir);
                    var sch = "$schema";
                    var isb = new StringBuilder();
                    isb.Append("{\"" + sch + "\":\"api-index/v1\",\"pkg\":\"" + package + "\",\"types\":{");
                    var tks = new List<string>(idx.types.Keys); tks.Sort();
                    for (int i = 0; i < tks.Count; i++) { isb.Append("\"" + tks[i] + "\":{\"ms\":\"" + idx.types[tks[i]] + "\"}"); if (i < tks.Count - 1) isb.Append(","); }
                    isb.Append("},\"mem\":{");
                    var mks = new List<string>(idx.members.Keys); mks.Sort();
                    for (int i = 0; i < mks.Count; i++) { isb.Append("\"" + mks[i] + "\":["); var list = idx.members[mks[i]]; for (int j = 0; j < list.Count; j++) { var r = list[j]; isb.Append("{\"t\":\"" + r.type + "\",\"n\":\"" + r.name + "\",\"k\":\"" + r.kind + "\",\"a\":\"" + r.acc + "\",\"s\":" + (r.isStatic ? "1" : "0") + "}"); if (j < list.Count - 1) isb.Append(","); } isb.Append("]"); if (i < mks.Count - 1) isb.Append(","); }
                    isb.Append("}}");
                    File.WriteAllText(Path.Combine(dir, "api-index.json"), isb.ToString());
                    var dsb = new StringBuilder();
                    dsb.Append("{\"" + sch + "\":\"api-detail/v1\",\"pkg\":\"" + package + "\",\"sig\":{");
                    var dks = new List<string>(di.Keys); dks.Sort();
                    for (int i = 0; i < dks.Count; i++) { dsb.Append("\"" + dks[i] + "\":["); var el = di[dks[i]]; for (int j = 0; j < el.Count; j++) { dsb.Append("{\"a\":\"" + el[j].acc + "\",\"s\":" + (el[j].isStatic ? "1" : "0") + ",\"v\":\"" + el[j].sig.Replace("\\","\\\\").Replace("\"","") + "\"}"); if (j < el.Count - 1) dsb.Append(","); } dsb.Append("]"); if (i < dks.Count - 1) dsb.Append(","); }
                    dsb.Append("}}");
                    File.WriteAllText(Path.Combine(dir, "api-detail.json"), dsb.ToString());
                    File.WriteAllText(Path.Combine(dir, "manifest.json"),
                        "{\"$schema\":\"api-docs/v1\",\"package\":\"" + package + "\",\"version\":\"" + version + "\",\"description\":\"Auto-generated from reflection fallback.\"}");
                }
                catch { }
                idx.version = version;
                s_indexes[package] = idx;
                return idx;
            }

            // -- Index loading (with schema validation + stale detection) --

            static PackageIndex GetIndex(string package)
            {
                if (s_indexes.TryGetValue(package, out var idx)) return idx;
                var dir = Path.Combine(ApiDocsRoot, package);
                var indexFile = Path.Combine(dir, "api-index.json");
                if (!File.Exists(indexFile)) return null;

                idx = new PackageIndex { members = new Dictionary<string, List<MemberRef>>(StringComparer.OrdinalIgnoreCase), types = new Dictionary<string, string>(), details = new Dictionary<string, List<DetailEntry>>(), detailPath = Path.Combine(dir, "api-detail.json"), detailLoaded = false };

                try
                {
                    var json = File.ReadAllText(indexFile);
                    // Schema validation
                    if (!json.Contains("\"mem\":{") || !json.Contains("\"types\":{"))
                    {
                        // Malformed -- rebuild
                        s_fallbackBuilt.Remove(package);
                        return BuildFallbackIndex(package);
                    }
                    var memStart = json.IndexOf("\"mem\":{") + 7;
                    ParseMembers(json, memStart, idx);
                    var typesStart = json.IndexOf("\"types\":{") + 9;
                    ParseTypes(json, typesStart, idx);

                    // Version check: if manifest has a version, compare with installed
                    var mfPath = Path.Combine(dir, "manifest.json");
                    if (File.Exists(mfPath))
                    {
                        var mf = File.ReadAllText(mfPath);
                        var verIdx = mf.IndexOf("\"version\":\"");
                        if (verIdx > 0)
                        {
                            var verStart = verIdx + 11;
                            var verEnd = mf.IndexOf('"', verStart);
                            idx.version = mf.Substring(verStart, verEnd - verStart);
                            // Check if still installed with same version
                            try
                            {
                                var pkgInfoType = Type.GetType("UnityEditor.PackageManager.PackageInfo, UnityEditor.CoreModule");
                                var findMethod = pkgInfoType?.GetMethod("FindForPackageName", BindingFlags.Public | BindingFlags.Static);
                                if (findMethod != null)
                                {
                                    var info = findMethod.Invoke(null, new object[] { package });
                                    if (info != null)
                                    {
                                        var verProp = info.GetType().GetProperty("version");
                                        var currentVer = verProp?.GetValue(info)?.ToString() ?? "";
                                        if (!string.IsNullOrEmpty(currentVer) && currentVer != idx.version)
                                        {
                                            // Version mismatch -- stale, rebuild
                                            s_fallbackBuilt.Remove(package);
                                            return BuildFallbackIndex(package);
                                        }
                                    }
                                    else
                                    {
                                        // Package not installed but index exists -- stale, attempt rebuild
                                        s_fallbackBuilt.Remove(package);
                                        return BuildFallbackIndex(package);
                                    }
                                }
                                else
                                {
                                    // FindForPackageName unavailable -- fall back to directory check
                                    var pkgDir = ResolvePackageDirectory(package);
                                    if (pkgDir == null)
                                    {
                                        s_fallbackBuilt.Remove(package);
                                        return BuildFallbackIndex(package);
                                    }
                                    // Directory exists -- also check package.json version
                                    var pkgJsonPath = Path.Combine(pkgDir, "package.json");
                                    if (File.Exists(pkgJsonPath))
                                    {
                                        try
                                        {
                                            var pkgVer = ExtractPackageJsonVersion(File.ReadAllText(pkgJsonPath));
                                            if (!string.IsNullOrEmpty(pkgVer) && pkgVer != idx.version)
                                            {
                                                s_fallbackBuilt.Remove(package);
                                                return BuildFallbackIndex(package);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception)
                {
                    s_fallbackBuilt.Remove(package);
                    return BuildFallbackIndex(package);
                }
                s_indexes[package] = idx;
                return idx;
            }

            static void ParseMembers(string json, int start, PackageIndex idx)
            {
                var pos = start;
                while (pos < json.Length)
                {
                    if (json[pos] == '}') break;
                    if (json[pos] != '"') { pos++; continue; }
                    pos++;
                    var keyEnd = json.IndexOf('"', pos);
                    if (keyEnd < 0) break;
                    var key = json.Substring(pos, keyEnd - pos);
                    pos = keyEnd + 1;
                    while (pos < json.Length && json[pos] != '[') pos++;
                    if (pos >= json.Length) break;
                    pos++;
                    var list = new List<MemberRef>();
                    while (pos < json.Length)
                    {
                        if (json[pos] == ']') { pos++; break; }
                        if (json[pos] == '{')
                        {
                            var objEnd = json.IndexOf('}', pos);
                            if (objEnd < 0) break;
                            var obj = json.Substring(pos + 1, objEnd - pos - 1);
                            list.Add(new MemberRef { type = ExtractJsonValue(obj, "t"), name = ExtractJsonValue(obj, "n"), kind = ExtractJsonValue(obj, "k"), acc = ExtractJsonValue(obj, "a"), isStatic = obj.Contains("\"s\":1") || obj.Contains("\"s\":true") });
                            pos = objEnd + 1;
                        }
                        else pos++;
                    }
                    if (list.Count > 0) idx.members[key] = list;
                }
            }

            static void ParseTypes(string json, int start, PackageIndex idx)
            {
                var pos = start;
                while (pos < json.Length)
                {
                    if (json[pos] == '}') { pos++; break; }
                    if (json[pos] != '"') { pos++; continue; }
                    pos++;
                    var keyEnd = json.IndexOf('"', pos);
                    if (keyEnd < 0) break;
                    var typeName = json.Substring(pos, keyEnd - pos);
                    pos = keyEnd + 1;
                    while (pos < json.Length && json[pos] != ':') pos++;
                    pos++;
                    if (pos < json.Length && json[pos] == '{') pos++;
                    var valStart = json.IndexOf("\"ms\":\"", pos);
                    if (valStart < 0) { pos++; continue; }
                    valStart += 6;
                    var valEnd = json.IndexOf('"', valStart);
                    if (valEnd < 0) break;
                    idx.types[typeName] = json.Substring(valStart, valEnd - valStart);
                    pos = valEnd + 1;
                    if (pos < json.Length && json[pos] == '"') pos++;
                    if (pos < json.Length && json[pos] == '}') pos++;
                    if (pos < json.Length && json[pos] == ',') pos++;
                }
            }

            static string ExtractJsonValue(string json, string key)
            {
                var search = "\"" + key + "\":\"";
                var i = json.IndexOf(search);
                if (i < 0) { search = "\"" + key + "\":"; i = json.IndexOf(search); if (i < 0) return ""; i += search.Length; return json.Substring(i, json.IndexOfAny(new[] { ',', '}' }, i) - i); }
                i += search.Length;
                var end = json.IndexOf('"', i);
                return end > i ? json.Substring(i, end - i) : "";
            }

            static void LoadDetails(string package, PackageIndex idx)
            {
                if (idx.detailLoaded || !File.Exists(idx.detailPath)) return;
                try
                {
                    var json = File.ReadAllText(idx.detailPath);
                    var sigStart = json.IndexOf("\"sig\":{");
                    if (sigStart < 0) return;
                    sigStart += 7;
                    var pos = sigStart;
                    while (pos < json.Length)
                    {
                        if (json[pos] == '}') break;
                        if (json[pos] != '"') { pos++; continue; }
                        pos++;
                        var keyEnd = json.IndexOf('"', pos);
                        if (keyEnd < 0) break;
                        var key = json.Substring(pos, keyEnd - pos);
                        pos = keyEnd + 1;
                        while (pos < json.Length && json[pos] != '[') pos++;
                        if (pos >= json.Length) break;
                        pos++;
                        var entries = new List<DetailEntry>();
                        while (pos < json.Length)
                        {
                            if (json[pos] == ']') { pos++; break; }
                            if (json[pos] == '{')
                            {
                                var objEnd = json.IndexOf('}', pos);
                                if (objEnd < 0) break;
                                var obj = json.Substring(pos + 1, objEnd - pos - 1);
                                entries.Add(new DetailEntry { acc = ExtractJsonValue(obj, "a"), isStatic = obj.Contains("\"s\":1") || obj.Contains("\"s\":true"), sig = ExtractJsonValue(obj, "v") });
                                pos = objEnd + 1;
                            }
                            else pos++;
                        }
                        if (entries.Count > 0) idx.details[key] = entries;
                    }
                }
                catch (Exception) { }
                idx.detailLoaded = true;
            }

            static string FormatDetailList(string key, List<DetailEntry> entries)
            {
                var sb = new StringBuilder();
                sb.AppendLine(key + " (" + entries.Count + " overload(s)):");
                foreach (var e in entries)
                {
                    var access = e.acc == "pub" ? "public" : e.acc == "int" ? "internal" : e.acc == "pri" ? "private" : e.acc == "pro" ? "protected" : e.acc;
                    sb.AppendLine("  [" + access + "] " + (e.isStatic ? "static " : "") + e.sig);
                }
                return sb.ToString();
            }
        }
    }
}
