using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

namespace UnityTcp.Editor.Tools
{
    internal class FixMissingParenthesis : ScriptFixProvider
    {
        public override bool CanFix(Diagnostic diagnostic) => diagnostic.Id == "CS1026";

        public override bool ApplyFix(ref SyntaxTree tree, Diagnostic diagnostic, ScriptFixContext context)
        {
            var updated = tree.InsertTextAtLocation(diagnostic.Location, ")");
            if (ReferenceEquals(updated, tree)) return false;
            tree = updated;
            return true;
        }
    }
}
