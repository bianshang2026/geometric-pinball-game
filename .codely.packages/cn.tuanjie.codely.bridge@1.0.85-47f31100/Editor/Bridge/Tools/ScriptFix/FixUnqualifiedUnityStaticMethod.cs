using System.Collections.Generic;
using System.Linq;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEngine;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Tools
{
    // Handles CS0103 for common UnityEngine.Object static methods called without qualification
    // in Roslyn scripting context (e.g., FindObjectOfType<T>() → UnityEngine.Object.FindObjectOfType<T>()).
    //
    // In MonoBehaviour scripts these methods are inherited and can be called unqualified,
    // but Roslyn scripting has no base class, so they must be explicitly qualified.
    internal class FixUnqualifiedUnityStaticMethod : ScriptFixProvider
    {
        static readonly HashSet<string> k_UnityObjectMethods = new HashSet<string>
        {
            "FindObjectOfType",
            "FindObjectsOfType",
            "FindAnyObjectByType",
            "FindObjectsByType",
            "Instantiate",
            "Destroy",
            "DestroyImmediate",
            "DontDestroyOnLoad"
        };

        const string k_Qualifier = "UnityEngine.Object.";

        public override bool CanFix(Diagnostic diagnostic)
        {
            if (diagnostic.Id != "CS0103")
                return false;

            var message = diagnostic.GetMessage();
            return k_UnityObjectMethods.Any(m => message.Contains($"'{m}'"));
        }

        public override bool ApplyFix(ref SyntaxTree tree, Diagnostic diagnostic, ScriptFixContext context)
        {
            // Extract method name from diagnostic message
            var message = diagnostic.GetMessage();
            string methodName = null;
            foreach (var m in k_UnityObjectMethods)
            {
                if (message.Contains($"'{m}'"))
                {
                    methodName = m;
                    break;
                }
            }
            if (methodName == null)
                return false;

            // Locate the exact node via diagnostic span. Spans are always fresh here because
            // CompileAndAutoFix recompiles after every tree-modifying fix before calling ApplyFix again.
            var root = tree.GetRoot();
            var spanNode = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

            SyntaxNode target = null;
            for (var n = spanNode; n != null; n = n.Parent)
            {
                if (n is IdentifierNameSyntax id && id.Identifier.Text == methodName) { target = n; break; }
                if (n is GenericNameSyntax gen && gen.Identifier.Text == methodName)  { target = n; break; }
            }

            if (target == null || target.Parent is MemberAccessExpressionSyntax)
                return false;

            var source = tree.GetText().ToString();
            tree = SyntaxFactory.ParseSyntaxTree(source.Insert(target.SpanStart, k_Qualifier));

            CodelyLogger.Log($"[FixUnqualifiedUnityStaticMethod] Qualified '{methodName}' → '{k_Qualifier}{methodName}'");
            return true;
        }
    }
}
