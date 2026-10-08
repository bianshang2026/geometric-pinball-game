using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using UnityEngine;
using SyntaxTree = Codely.Microsoft.CodeAnalysis.SyntaxTree;

using UnityTcp.Editor.Helpers;
namespace UnityTcp.Editor.Tools
{
    // Handles CS0246 / CS0234 by searching all loaded assemblies for the missing type
    // and adding the discovered assembly + its namespace on demand.
    // For CS0234 where the type exists in a different namespace (e.g. UnityEngine.SerializedObject
    // when SerializedObject actually lives in UnityEditor), this provider also rewrites the
    // source code to replace the wrong namespace prefix with the correct one.
    internal class FixMissingAssemblyReference : ScriptFixProvider
    {
        static readonly string[] k_DiagnosticIds = { "CS0246", "CS0234", "CS0103" };

        public override bool CanFix(Diagnostic diagnostic) =>
            k_DiagnosticIds.Contains(diagnostic.Id);

        public override bool ApplyFix(ref SyntaxTree tree, Diagnostic diagnostic, ScriptFixContext context)
        {
            var message = diagnostic.GetMessage();
            ExtractNames(message, out var missingName, out var parentNamespace);

            if (string.IsNullOrEmpty(missingName))
                return false;

            // CS0234: 'X' does not exist in namespace 'Y' → the missing thing is the namespace Y.X.
            // Try resolving it as a namespace first.
            if (!string.IsNullOrEmpty(parentNamespace))
            {
                var fullNamespace = parentNamespace + "." + missingName;
                if (TryResolveNamespace(fullNamespace, context))
                    return true;

                // CS0234 with a parent namespace that doesn't contain the type:
                // the type might exist in a different namespace. Try to find the correct one
                // and rewrite the source code's namespace prefix.
                var correctNamespace = FindCorrectNamespace(missingName, parentNamespace);
                if (correctNamespace != null)
                {
                    // Also ensure the assembly reference + import are registered.
                    TryResolveTypeName(missingName, context);
                    // Rewrite source: parentNamespace.missingName → correctNamespace.missingName
                    var updatedTree = RewriteNamespacePrefix(tree, parentNamespace, correctNamespace, missingName);
                    if (updatedTree != null)
                    {
                        tree = updatedTree;
                        CodelyLogger.Log($"[FixMissingAssemblyReference] Rewrote '{parentNamespace}.{missingName}' → '{correctNamespace}.{missingName}'");
                        return true;
                    }
                }
            }

            // Fallback / CS0246: resolve by type name.
            return TryResolveTypeName(missingName, context);
        }

        // Searches all assemblies for any exported type whose namespace equals or starts with
        // fullNamespace, then registers ALL matching assemblies.
        bool TryResolveNamespace(string fullNamespace, ScriptFixContext context)
        {
            var prefix = fullNamespace + ".";
            var resolved = false;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || context.AddedLocations.Contains(location))
                    continue;

                try
                {
                    var hasMatch = assembly.GetExportedTypes()
                        .Any(t => t.IsPublic &&
                                  (t.Namespace == fullNamespace ||
                                   (t.Namespace != null && t.Namespace.StartsWith(prefix, StringComparison.Ordinal))));

                    if (!hasMatch)
                        continue;

                    context.References.Add(MetadataReference.CreateFromFile(location));
                    context.AddedLocations.Add(location);

                    if (!context.Imports.Contains(fullNamespace))
                        context.Imports.Add(fullNamespace);

                    CodelyLogger.Log($"[FixMissingAssemblyReference] Resolved namespace '{fullNamespace}' → {assembly.GetName().Name}");
                    resolved = true;
                }
                catch
                {
                    // Some assemblies throw on GetExportedTypes; skip them.
                }
            }

            return resolved;
        }

        // Searches all assemblies for exported types whose simple name matches typeName,
        // then registers ALL matching assemblies and their namespaces.
        bool TryResolveTypeName(string typeName, ScriptFixContext context)
        {
            var resolved = false;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                var location = assembly.Location;
                if (string.IsNullOrEmpty(location) || context.AddedLocations.Contains(location))
                    continue;

                try
                {
                    var matches = assembly.GetExportedTypes()
                        .Where(t => t.Name == typeName && t.IsPublic)
                        .ToList();

                    if (matches.Count == 0)
                        continue;

                    context.References.Add(MetadataReference.CreateFromFile(location));
                    context.AddedLocations.Add(location);

                    foreach (var t in matches)
                    {
                        if (!string.IsNullOrEmpty(t.Namespace) && !context.Imports.Contains(t.Namespace))
                            context.Imports.Add(t.Namespace);
                    }

                    var names = string.Join(", ", matches.Select(t => t.FullName));
                    CodelyLogger.Log($"[FixMissingAssemblyReference] Resolved '{typeName}' → {assembly.GetName().Name} ({names})");
                    resolved = true;
                }
                catch
                {
                    // Some assemblies throw on GetExportedTypes; skip them.
                }
            }

            return resolved;
        }

        // CS0234: "The type or namespace name 'X' does not exist in the namespace 'Y'"
        static readonly Regex k_Cs0234Pattern =
            new Regex(@"'([^']+)'\s+does not exist in the namespace\s+'([^']+)'", RegexOptions.Compiled);

        // CS0246: "The type or namespace name 'X' could not be found ..."
        static readonly Regex k_SingleQuotePattern =
            new Regex(@"'([^']+)'", RegexOptions.Compiled);

        // Extracts the missing name and, for CS0234, the parent namespace.
        static void ExtractNames(string message, out string missingName, out string parentNamespace)
        {
            var m = k_Cs0234Pattern.Match(message);
            if (m.Success)
            {
                missingName = m.Groups[1].Value;
                parentNamespace = m.Groups[2].Value;
                return;
            }

            var m2 = k_SingleQuotePattern.Match(message);
            missingName = m2.Success ? m2.Groups[1].Value : null;
            parentNamespace = null;
        }

        // Searches all assemblies for a public type whose simple name matches typeName
        // but whose namespace differs from wrongNamespace. Returns the first matching
        // namespace, or null if no such type is found.
        internal static string FindCorrectNamespace(string typeName, string wrongNamespace)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    var match = assembly.GetExportedTypes()
                        .FirstOrDefault(t => t.IsPublic
                                             && t.Name == typeName
                                             && t.Namespace != null
                                             && t.Namespace != wrongNamespace);
                    if (match != null)
                        return match.Namespace;
                }
                catch
                {
                    // Some assemblies throw on GetExportedTypes; skip them.
                }
            }

            return null;
        }

        // Rewrites all occurrences of wrongNamespace.typeName in the source code to
        // correctNamespace.typeName by performing a text replacement and reparsing.
        static SyntaxTree RewriteNamespacePrefix(SyntaxTree tree, string wrongNamespace, string correctNamespace, string typeName)
        {
            var source = tree.GetText().ToString();
            var wrongPrefix = $"{wrongNamespace}.{typeName}";
            var correctPrefix = $"{correctNamespace}.{typeName}";

            if (!source.Contains(wrongPrefix))
                return null;

            var updatedSource = source.Replace(wrongPrefix, correctPrefix);
            return SyntaxFactory.ParseSyntaxTree(updatedSource);
        }
    }
}
