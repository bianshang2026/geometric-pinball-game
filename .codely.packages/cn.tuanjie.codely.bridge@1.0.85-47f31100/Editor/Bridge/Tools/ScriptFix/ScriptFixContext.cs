using System.Collections.Generic;
using Codely.Microsoft.CodeAnalysis;

namespace UnityTcp.Editor.Tools
{
    internal class ScriptFixContext
    {
        public List<string> Imports { get; }
        public List<MetadataReference> References { get; }
        public HashSet<string> AddedLocations { get; }

        public ScriptFixContext(
            List<string> imports,
            List<MetadataReference> references,
            HashSet<string> addedLocations)
        {
            Imports = imports;
            References = references;
            AddedLocations = addedLocations;
        }
    }
}
