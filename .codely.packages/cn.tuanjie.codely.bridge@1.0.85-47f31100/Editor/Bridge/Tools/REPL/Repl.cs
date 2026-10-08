using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Codely.Microsoft.CodeAnalysis.Scripting;
using UnityEditor;
using UnityEngine;

namespace UnityTcp.Editor.Tools
{
    // Static host class exposed to script code via an unconditional imports entry (HandleCommand
    // always adds this namespace, even when the caller overrides `imports`). No globals/preamble
    // injection is used -- the API is stateless (Id/ById) or reads shared static session state
    // (Vars), so a plain static class is sufficient and has zero injection cost. A script declaring
    // its own `var Repl = ...;` shadows this class for the rest of the session; that's an accepted,
    // documented trade-off rather than something worth guarding against.
    // partial: the ApiHelper nested class (ported from cn.tuanjie.csharp-repl, reflection signature
    // lookup with a disk-cached index) lives in Repl.ApiHelper.cs to keep this file to the
    // Id/ById/Vars surface.
    public static partial class Repl
    {
        // Stashes a cross-domain-reload handle for `obj`, built on GlobalObjectId. A domain reload
        // wipes every `var` a REPL session holds, so a script can call Repl.Id(x) before a
        // reload-inducing operation (unlocking ReplGuard, entering Play Mode) and hand the token to
        // Repl.ById() afterward to get a live reference back, instead of re-resolving by name/path
        // (which breaks on duplicate names or objects with no stable path).
        public static string Id(UnityEngine.Object obj)
        {
            if (obj == null)
                return "OBJECT <null>  NOT FOUND (missing or destroyed)";

            // "Unsaved" cannot be determined from GlobalObjectId.targetObjectId == 0 alone: a
            // brand-new GameObject in an already-saved scene gets a nonzero targetObjectId
            // immediately (predictive local-file-id assignment) and round-trips fine even before
            // its own save. Two checks instead: (1) a scene GameObject/Component whose containing
            // scene has never been saved has nothing on disk to resolve against after a real
            // scene close/reopen, even though it round-trips within the current in-memory session;
            // (2) a round-trip through the same resolver ById() uses, which also catches a pure
            // in-memory object with no scene and no asset path (e.g. an unsaved ScriptableObject).
            if (!EditorUtility.IsPersistent(obj))
            {
                var transform = AsTransform(obj);
                if (transform != null && string.IsNullOrEmpty(transform.gameObject.scene.path))
                    return "UNSAVED " + PathFor(obj) + "  (id does not survive reload -- save the scene first)";
            }

            var gid = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (resolved != obj)
                return "UNSAVED " + PathFor(obj) + "  (id does not survive reload -- save the scene first)";

            return gid.ToString();
        }

        // Returns null and logs a Debug.LogWarning with the reason on failure (missing/malformed
        // token, scene not loaded, object deleted). Deliberately Debug.LogWarning rather than the
        // internal CodelyLogger -- this bridge's log capture surfaces Application.logMessageReceived
        // output in the response's `logs` field, so the reason for a null comes back with the call
        // instead of requiring a separate console read.
        public static UnityEngine.Object ById(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Debug.LogWarning("Repl.ById('') NOT FOUND: empty token");
                return null;
            }

            if (!GlobalObjectId.TryParse(token, out var gid))
            {
                Debug.LogWarning($"Repl.ById('{token}') NOT FOUND: malformed token");
                return null;
            }

            var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
            if (obj == null)
            {
                Debug.LogWarning(
                    $"Repl.ById('{token}') NOT FOUND: object not resolvable (its scene may not be loaded, or it was deleted)");
                return null;
            }

            return obj;
        }

        // Read-only snapshot of the active session's top-level variables, taken from the
        // ScriptState left behind by the last successfully completed submission (a script calling
        // Repl.Vars mid-submission only sees variables from earlier submissions, same as a real
        // REPL). Empty during a one-shot execution (enable_repl:false) even if a session happens to
        // exist in the background -- one-shot must not read session state, only bypass it.
        public static ReplVars Vars
        {
            get
            {
                var vars = new ReplVars();
                var state = ExecuteCSharpScript.CurrentSessionStateForVars;
                if (state == null)
                    return vars;

                foreach (var variable in state.Variables)
                    vars.Set(variable.Name, variable.Type, variable.Value);

                return vars;
            }
        }

        static Transform AsTransform(UnityEngine.Object obj)
        {
            switch (obj)
            {
                case GameObject go:
                    return go.transform;
                case Component comp:
                    return comp.transform;
                default:
                    return null;
            }
        }

        static string PathFor(UnityEngine.Object obj)
        {
            if (EditorUtility.IsPersistent(obj))
            {
                var path = AssetDatabase.GetAssetPath(obj);
                return string.IsNullOrEmpty(path) ? obj.name : path;
            }

            var transform = AsTransform(obj);
            return transform != null ? GetPath(transform) : obj.name;
        }

        static string GetPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            var stack = new Stack<string>();
            var cur = transform;
            while (cur != null)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            return string.Join("/", stack.ToArray());
        }
    }

    // Read-only view over a REPL session's top-level variables. Not a raw forward of
    // ScriptState.Variables: it collapses same-named redeclarations across submissions down to the
    // last one (matching what a real REPL session sees -- a later `var x = ...;` shadows an
    // earlier one), and it renders as a Name/Type/value table via ToString() instead of the default
    // dictionary/collection output, so submitting a bare `Repl.Vars` line is a session-state
    // overview. Deliberately no write API: unlike Python's globals(), there's no scenario here where
    // the caller isn't already in scope to just assign the variable directly, and ScriptVariable's
    // setter throws for variables declared with `readonly`/const-like semantics -- exposing writes
    // would only add an error surface with no real use case.
    public sealed class ReplVars : IReadOnlyDictionary<string, object>
    {
        readonly List<string> m_Order = new List<string>();
        readonly Dictionary<string, object> m_Values = new Dictionary<string, object>();
        readonly Dictionary<string, Type> m_Types = new Dictionary<string, Type>();

        internal ReplVars() { }

        internal void Set(string name, Type type, object value)
        {
            if (!m_Values.ContainsKey(name))
                m_Order.Add(name);
            m_Values[name] = value;
            m_Types[name] = type;
        }

        public object this[string key] => m_Values[key];
        public IEnumerable<string> Keys => m_Order;
        public IEnumerable<object> Values
        {
            get
            {
                foreach (var name in m_Order)
                    yield return m_Values[name];
            }
        }
        public int Count => m_Order.Count;

        public bool ContainsKey(string key) => m_Values.ContainsKey(key);
        public bool TryGetValue(string key, out object value) => m_Values.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var name in m_Order)
                yield return new KeyValuePair<string, object>(name, m_Values[name]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Only variables are visible here (methods/types declared in the session have no
        // ScriptVariable entry -- a boundary of ScriptState.Variables itself, not something this
        // wrapper can fill in).
        public override string ToString()
        {
            if (m_Order.Count == 0)
                return "(no session variables)";

            var sb = new StringBuilder();
            sb.Append("Name".PadRight(24)).Append(' ').Append("Type".PadRight(24)).Append(' ').Append("Value").Append('\n');
            foreach (var name in m_Order)
            {
                var typeName = m_Types[name]?.FullName ?? "?";
                var preview = ExecuteCSharpScript.FormatResultValue(m_Values[name]) ?? "null";
                sb.Append(name.PadRight(24)).Append(' ').Append(typeName.PadRight(24)).Append(' ').Append(preview).Append('\n');
            }
            return sb.ToString();
        }
    }
}
