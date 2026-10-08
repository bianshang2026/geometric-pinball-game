using System.Text.RegularExpressions;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.Text;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

namespace UnityTcp.Editor.Tools
{
    internal class FixMissingBrace : ScriptFixProvider
    {
        // Jagged array rank: Type[][] or new Type[][]
        static readonly Regex k_JaggedArrayPattern = new Regex(
            @"\[\]\s*\[\]", RegexOptions.Compiled);

        // Implicit array creation with named tuple: new[] { (name: ...)
        static readonly Regex k_NamedTupleArrayPattern = new Regex(
            @"new\s*\[\].*\(\s*\w+\s*:", RegexOptions.Compiled);

        public override bool CanFix(Diagnostic diagnostic) => diagnostic.Id == "CS1513";

        public override bool ApplyFix(ref SyntaxTree tree, Diagnostic diagnostic, ScriptFixContext context)
        {
            var source = tree.ToString();
            var lineSpan = diagnostic.Location.GetLineSpan();
            int diagLine = lineSpan.Span.Start.Line;

            // Guard: skip CS1513 caused by script-incompatible syntax rather than
            // actual missing braces. These patterns confuse the script parser and
            // inserting } would make things worse.
            var lines = source.Split('\n');
            if (diagLine >= 0 && diagLine < lines.Length)
            {
                var lineText = lines[diagLine].Trim();
                if (k_JaggedArrayPattern.IsMatch(lineText))
                    return false;
                if (k_NamedTupleArrayPattern.IsMatch(lineText))
                    return false;
            }

            // Insert } at the diagnostic position
            var sourceText = SourceText.From(source);
            int insertPos = sourceText.Lines.GetPosition(lineSpan.Span.Start);
            var newSource = source.Insert(insertPos, "}\n");

            var updated = SyntaxFactory.ParseSyntaxTree(newSource);
            if (ReferenceEquals(updated, tree)) return false;
            tree = updated;
            return true;
        }
    }
}