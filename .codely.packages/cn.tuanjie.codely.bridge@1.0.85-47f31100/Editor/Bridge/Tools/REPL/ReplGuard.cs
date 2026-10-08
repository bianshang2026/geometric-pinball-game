using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityTcp.Editor.Tools
{
    // Detects domain-reload-triggering calls in a REPL session submission. Reload silently wipes
    // every static session field (ScriptState, declared variables, imports) -- the point of this
    // scan is not to forbid reloads but to turn that implicit, session-destroying side effect into
    // an explicit, acknowledged step. Purely syntactic (matches real invocation expressions by
    // identifier text, so tokens inside comments/string literals never false-positive); the caller
    // owns the exemption policy (empty-session first submission, `unlock_domain_reload`).
    static class ReplGuard
    {
        static readonly string[] s_CsWriteMethodNames =
        {
            "WriteAllText", "WriteAllLines", "AppendAllText", "AppendAllLines", "CreateText"
        };

        // Returns null if `code` is safe to run, or a descriptive error (with recovery options)
        // if it contains a call that would trigger a domain reload.
        public static string FindReloadTrigger(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            CompilationUnitSyntax root;
            try
            {
                var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Script));
                root = tree.GetCompilationUnitRoot();
            }
            catch
            {
                // If it won't even parse, let the compiler surface the real error.
                return null;
            }

            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

            if (invocations.Any(inv => IsMemberCall(inv, "AssetDatabase", "Refresh")))
                return BuildBlockedMessage("AssetDatabase.Refresh() triggers a domain reload");

            if (invocations.Any(inv => IsMemberCall(inv, "AssetDatabase", "SaveAssets")))
                return BuildBlockedMessage("AssetDatabase.SaveAssets() can trigger a domain reload");

            if (invocations.Any(inv => IsMemberCall(inv, "AssetDatabase", "ImportAsset")))
                return BuildBlockedMessage(
                    "AssetDatabase.ImportAsset(...) triggers a domain reload when the imported asset is a script");

            if (invocations.Any(inv => IsMemberCall(inv, "CompilationPipeline", "RequestScriptCompilation")))
                return BuildBlockedMessage("CompilationPipeline.RequestScriptCompilation() triggers a domain reload");

            if (invocations.Any(inv => IsMemberCall(inv, "EditorUtility", "RequestScriptReload")))
                return BuildBlockedMessage("EditorUtility.RequestScriptReload() triggers a domain reload");

            if (WritesCsFile(root))
                return BuildBlockedMessage("writing a .cs file triggers script compilation and a domain reload");

            return null;
        }

        // Appends actionable hints to a failure's error message when it matches one of a handful of
        // recurring patterns that ScriptFix cannot fix (compile-time residue) or that only show up at
        // runtime. Never removes or replaces the original error text -- the hint is additive so the
        // raw diagnostic/exception message is always still there for anyone who wants it verbatim.
        // `possibleSessionRebuild` is passed by the caller (it needs session context this method
        // doesn't have): true when this execute silently created a new session instead of continuing
        // one the caller didn't explicitly ask to discard -- i.e. a domain reload may have just wiped
        // the previous session's variables out from under it.
        // Takes the actual exception (not just its message) so the AmbiguousMatchException check
        // below can match on the real runtime type -- AmbiguousMatchException.Message is just the
        // literal string "Ambiguous match found." and never contains its own type name, so a
        // substring check against the message text alone can never fire.
        public static string EnhanceError(Exception exception, bool possibleSessionRebuild = false)
        {
            string error = exception?.Message;
            if (string.IsNullOrEmpty(error))
                return error;

            var hints = new List<string>();

            if (error.Contains("does not contain a definition for") ||
                error.Contains("does not exist in the current context"))
            {
                hints.Add("The member or name you used may not exist. Don't guess -- query the real " +
                    "signature with Repl.ApiHelper.Find(package, \"name\").Get() (indexed, faster and " +
                    "more reliable than reflection), or fall back to typeof(T).GetMembers() if the " +
                    "package isn't indexed, then retry with the correct name.");
            }

            if (error.Contains("Cannot modify a value type return value") ||
                error.Contains("Cannot modify the return value of"))
            {
                hints.Add("You are assigning into a nested struct property chain. Take a local " +
                    "variable first: `var m = ps.main; m.duration = 3f;` instead of " +
                    "`ps.main.duration = 3f;`");
            }

            if (exception is AmbiguousMatchException)
            {
                hints.Add("Reflection matched a same-named member on both a base and derived type. " +
                    "Use BindingFlags.DeclaredOnly and walk BaseType manually instead of a single " +
                    "GetMember/GetProperty/GetField call.");
            }

            if (possibleSessionRebuild &&
                (error.Contains("does not exist in the current context") || error.Contains("CS0103")))
            {
                hints.Add("This execute created a new session instead of continuing the previous " +
                    "one -- if that wasn't intentional, a domain reload likely destroyed it and its " +
                    "variables are gone. Re-declare what you need, or call Repl.ById() to recover any " +
                    "object handle you stashed with Repl.Id() before the reload.");
            }

            return hints.Count == 0
                ? error
                : error + "\n\n[REPL HINT] " + string.Join(" ", hints);
        }

        static string BuildBlockedMessage(string reason) =>
            $"Blocked by ReplGuard: {reason} -- this session's variables would be lost. " +
            "To proceed anyway, resend this exact script with \"unlock_domain_reload\": true -- if you " +
            "want to keep a handle to a key object across the reload, call Repl.Id(obj) before resending " +
            "and Repl.ById(token) afterward to recover it. " +
            "If this call doesn't need the session, resend with \"enable_repl\": false for a one-shot " +
            "execution instead. If you don't need the current session's state, resend with " +
            "\"script_session_id\": \"new\" -- a new session's first submission is never blocked.";

        // Matches `Type.Method(...)` and `qualified.Type.Method(...)` invocations by the trailing
        // type + method name, ignoring how the type was qualified (namespace, `this`, etc.).
        static bool IsMemberCall(InvocationExpressionSyntax invocation, string typeName, string methodName)
        {
            if (!(invocation.Expression is MemberAccessExpressionSyntax member))
                return false;
            if (member.Name.Identifier.ValueText != methodName)
                return false;

            switch (member.Expression)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.ValueText == typeName;
                case MemberAccessExpressionSyntax inner:
                    return inner.Name.Identifier.ValueText == typeName;
                default:
                    return false;
            }
        }

        // A .cs write == a call to one of the write-method names above, or `new StreamWriter(...)`,
        // gated on a ".cs" string literal appearing anywhere in the submission -- so AppendAllText
        // writing a plain log line, or a StreamWriter to a .txt file, is not blocked.
        static bool WritesCsFile(CompilationUnitSyntax root)
        {
            var mentionsCs = root.DescendantTokens().Any(t =>
                t.IsKind(SyntaxKind.StringLiteralToken) &&
                t.ValueText.IndexOf(".cs", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!mentionsCs)
                return false;

            var writesFile = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(inv => inv.Expression is MemberAccessExpressionSyntax m &&
                            s_CsWriteMethodNames.Contains(m.Name.Identifier.ValueText));

            var newStreamWriter = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Any(oc => oc.Type is IdentifierNameSyntax idn && idn.Identifier.ValueText == "StreamWriter" ||
                           oc.Type is QualifiedNameSyntax qn && qn.Right.Identifier.ValueText == "StreamWriter");

            return writesFile || newStreamWriter;
        }
    }
}
