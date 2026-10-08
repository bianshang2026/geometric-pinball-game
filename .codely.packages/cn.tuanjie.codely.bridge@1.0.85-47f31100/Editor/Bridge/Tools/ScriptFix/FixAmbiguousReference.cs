using System.Text.RegularExpressions;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

namespace UnityTcp.Editor.Tools
{
    internal class FixAmbiguousReference : ScriptFixProvider
    {
        public override bool CanFix(Diagnostic diagnostic) => diagnostic.Id == "CS0104";

        public override bool ApplyFix(ref SyntaxTree tree, Diagnostic diagnostic, ScriptFixContext context)
        {
            var message = diagnostic.GetMessage();
            var aliasMatch = Regex.Match(message, @"(?<=')[^']*(?=' is)");
            var nsMatch = Regex.Match(message, @"(?<=between ')[^']*(?=' and)");
            if (!nsMatch.Success || !aliasMatch.Success)
                return false;

            var updated = tree.AddUsingAliasDirective(nsMatch.Value, aliasMatch.Value);
            if (ReferenceEquals(updated, tree)) return false;
            tree = updated;
            return true;
        }
    }
}
