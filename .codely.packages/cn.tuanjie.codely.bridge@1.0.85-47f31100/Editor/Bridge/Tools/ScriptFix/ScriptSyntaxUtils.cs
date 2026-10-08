using System.Linq;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.CSharp.Syntax;
using Codely.Microsoft.CodeAnalysis.Text;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

namespace UnityTcp.Editor.Tools
{
    internal static class ScriptSyntaxUtils
    {
        internal static SyntaxTree InsertTextAtLocation(this SyntaxTree tree, Location location, string text)
        {
            var lineSpan = location.GetLineSpan().Span;
            var sourceText = SourceText.From(tree.ToString());
            var position = sourceText.Lines.GetPosition(lineSpan.Start);
            var newSource = tree.ToString().Insert(position, text);
            return SyntaxFactory.ParseSyntaxTree(newSource);
        }

        internal static SyntaxTree AddUsingAliasDirective(this SyntaxTree tree, string namespaceName, string aliasName)
        {
            if (!(tree.GetRoot() is CompilationUnitSyntax root))
                return tree;

            bool exists = root.Usings.Any(u =>
                u.Name.ToString() == namespaceName && u.Alias?.Name.ToString() == aliasName);
            if (exists)
                return tree;

            var usingDirective = SyntaxFactory
                .UsingDirective(SyntaxFactory.ParseName(namespaceName))
                .WithAlias(SyntaxFactory.NameEquals(SyntaxFactory.IdentifierName(aliasName)))
                .NormalizeWhitespace()
                .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

            var newRoot = root.AddUsings(usingDirective).NormalizeWhitespace();
            return CSharpSyntaxTree.Create(newRoot);
        }
    }
}
