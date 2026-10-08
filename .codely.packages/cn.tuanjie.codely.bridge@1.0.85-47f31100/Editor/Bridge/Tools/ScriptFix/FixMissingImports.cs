using System.Collections.Generic;
using System.Linq;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

namespace UnityTcp.Editor.Tools
{
    internal class FixMissingImports : ScriptFixProvider
    {
        static readonly string[] k_DiagnosticIds = { "CS0246", "CS0103", "CS1061" };

        readonly Dictionary<string, string[]> k_NamespaceKeywords = new Dictionary<string, string[]>
        {
            { "System.Linq",                 new[] { "Where", "Select", "OrderBy", "Concat", "Any", "First", "Last", "ToList", "ToArray", "GroupBy", "Distinct" } },
            { "System.Collections.Generic",  new[] { "List<>", "Dictionary<>", "HashSet<>", "Queue<>", "Stack<>", "SortedList<>" } },
            { "UnityEditor",                 new[] { "MonoScript", "AssetDatabase", "EditorUtility", "Selection", "SceneView", "PrefabUtility", "Undo", "EditorGUILayout", "SerializedObject", "SerializedProperty" } },
            { "UnityEngine.SceneManagement", new[] { "SceneManager", "Scene", "LoadSceneMode" } },
            { "UnityEditor.SceneManagement", new[] { "EditorSceneManager" } },
        };

        public override bool CanFix(Diagnostic diagnostic)
        {
            if (!k_DiagnosticIds.Contains(diagnostic.Id))
                return false;
            var message = diagnostic.GetMessage();
            return k_NamespaceKeywords.Values.Any(kws => kws.Any(kw => message.Contains($"'{kw}'")));
        }

        public override bool ApplyFix(ref SyntaxTree tree, Diagnostic diagnostic, ScriptFixContext context)
        {
            var message = diagnostic.GetMessage();
            foreach (var pair in k_NamespaceKeywords)
            {
                if (pair.Value.Any(kw => message.Contains($"'{kw}'")))
                {
                    if (!context.Imports.Contains(pair.Key))
                    {
                        context.Imports.Add(pair.Key);
                        return true;
                    }
                    return false;
                }
            }
            return false;
        }
    }
}
