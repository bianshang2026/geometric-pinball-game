# Codely.Roslyn.dll

`Codely.Roslyn.dll` is a **single merged, namespace-prefixed assembly** (assembly
identity: `Codely.Roslyn`, version `5.0.0.0`). It bundles the Roslyn compiler stack
and its BCL dependencies that the Codely Bridge editor tooling needs, with every
**public** type relocated under a `Codely.` namespace prefix.

## Why it exists

Two collisions are avoided:

1. **Duplicate file names.** Unity allows only one precompiled assembly with a given
   file name per platform. Several of the DLLs below ship under their canonical names
   in other packages (e.g. `System.Runtime.CompilerServices.Unsafe.dll`), triggering:

   ```
   PrecompiledAssemblyException: Multiple precompiled assemblies with the same name ...
   included on the current platform. Only one assembly with the same name is allowed
   per platform.
   ```

   Merging everything into the single renamed file `Codely.Roslyn.dll` removes all
   canonical file names from `Plugins/`.

2. **Ambiguous type references.** If another assembly in the project also exposes
   `Microsoft.CodeAnalysis.*` / `System.Collections.Immutable.*` etc., source that does
   `using Microsoft.CodeAnalysis;` fails to compile with `CS0433` (type exists in two
   assemblies). To make the bundle fully self-contained, every **public** type is moved
   under a `Codely.` prefix.

This mirrors `Codely.Newtonsoft.Json.dll`, the renamed Newtonsoft.Json assembly used by
this package.

## Namespaces — how callers reference it

Public types are prefixed, so source code references them under `Codely.`:

```csharp
using Codely.Microsoft.CodeAnalysis;
using Codely.Microsoft.CodeAnalysis.CSharp;
using Codely.Microsoft.CodeAnalysis.CSharp.Scripting;
// Roslyn API return types are likewise prefixed, e.g.
//   Codely.System.Collections.Immutable.ImmutableArray<Codely.Microsoft.CodeAnalysis.Diagnostic>
```

**Invariant:** every externally-reachable type is `Codely.`-prefixed. Concretely, all
1094 `public` top-level types live under `Codely.*`; the only externally-visible names
left canonical are 8 type-forwarders (see below).

What is **not** prefixed, and why it's safe:

- **`internal` types** — not externally reachable (the assembly's `InternalsVisibleTo`
  entries are all bound to Microsoft / Castle strong-name keys that no project assembly
  can satisfy), so they can't cause ambiguity. Leaving them canonical preserves the C#
  compiler's by-full-name recognition of the embedded marker attributes that *do* affect
  callers (`IsExternalInit`, `IsReadOnlyAttribute`, `IsByRefLikeAttribute`,
  `RequiredMemberAttribute`, `NullableAttribute`, … — all `internal` here) and Roslyn's
  embedded resource lookups (`CSharpResources`), so error-message strings still resolve.
- **Type-forwarders** (`System.HashCode`, `System.Numerics.Vector2/3/4`, `Matrix3x2`,
  `Matrix4x4`, `Plane`, `Quaternion`) — these are *not* type definitions; they forward to
  the runtime's own types, so they share identity with everyone else's copy and cannot
  produce `CS0433`. They are left canonical because renaming a forwarder would break the
  forward.

> The async/iterator implementation attributes (`AsyncMethodBuilderAttribute`,
> `EnumeratorCancellationAttribute`, …) **are** prefixed: they're `public` but callers
> never need them recognized by canonical name (they only matter when *defining* the
> async/iterator methods, which already happened when Roslyn was built).

## Contents

The following assemblies are merged into `Codely.Roslyn.dll` (versions as of the last
repack):

| Assembly                                      | Version    |
| --------------------------------------------- | ---------- |
| Microsoft.CodeAnalysis.dll                    | 5.0.0.0    |
| Microsoft.CodeAnalysis.CSharp.dll             | 5.0.0.0    |
| Microsoft.CodeAnalysis.Scripting.dll          | 5.0.0.0    |
| Microsoft.CodeAnalysis.CSharp.Scripting.dll   | 5.0.0.0    |
| Microsoft.Bcl.AsyncInterfaces.dll             | 10.0.0.1   |
| Microsoft.Bcl.HashCode.dll                    | 6.0.0.0    |
| Microsoft.Bcl.Memory.dll                      | 10.0.0.1   |
| System.Buffers.dll                            | 4.0.5.0    |
| System.Collections.Immutable.dll              | 10.0.0.1   |
| System.Memory.dll                             | 4.0.5.0    |
| System.Numerics.Vectors.dll                   | 4.1.6.0    |
| System.Reflection.Metadata.dll                | 10.0.0.1   |
| System.Runtime.CompilerServices.Unsafe.dll    | 6.0.3.0    |
| System.Text.Encoding.CodePages.dll            | 11.0.0.0   |
| System.Threading.Tasks.Extensions.dll         | 4.2.4.0    |

> Note: `Newtonsoft.Json` is **not** in this assembly. It ships separately, already
> renamed, as `Codely.Newtonsoft.Json.dll`.

## Platform / referencing

- Enabled for the **Editor** platform only (all standalone/runtime platforms excluded),
  matching the original DLLs' import settings.
- Referenced explicitly via `Editor/Bridge/UnityTcp.Editor.asmdef`
  (`precompiledReferences`: `Codely.Newtonsoft.Json.dll`, `Codely.Roslyn.dll`).

## How to reproduce / update the merge

Two steps: **(1)** merge with ILRepack, then **(2)** prefix public namespaces with
Mono.Cecil. When bumping the Roslyn version, re-run both with the new input DLLs
(`Microsoft.CodeAnalysis.dll` is primary — it gives the merged assembly its version; the
`/out` file name gives it the `Codely.Roslyn` identity).

### Step 1 — merge

```powershell
dotnet tool install --global dotnet-ilrepack   # one-time

# from Plugins/, with the 15 input DLLs present:
ilrepack /out:Codely.Roslyn.merged.dll /union /lib:. `
  Microsoft.CodeAnalysis.dll Microsoft.CodeAnalysis.CSharp.dll `
  Microsoft.CodeAnalysis.Scripting.dll Microsoft.CodeAnalysis.CSharp.Scripting.dll `
  Microsoft.Bcl.AsyncInterfaces.dll Microsoft.Bcl.HashCode.dll Microsoft.Bcl.Memory.dll `
  System.Buffers.dll System.Collections.Immutable.dll System.Memory.dll `
  System.Numerics.Vectors.dll System.Reflection.Metadata.dll `
  System.Runtime.CompilerServices.Unsafe.dll System.Text.Encoding.CodePages.dll `
  System.Threading.Tasks.Extensions.dll
```

### Step 2 — prefix public namespaces (Mono.Cecil)

ILRepack does not rename namespaces, so run a small Mono.Cecil pass. In a throwaway
`dotnet` console project (`<PackageReference Include="Mono.Cecil" Version="0.11.5" />`):

```csharp
using System.Linq; using Mono.Cecil;
string[] roots = { "Microsoft", "System", "Roslyn", "FxResources" };
var m = ModuleDefinition.ReadModule("Codely.Roslyn.merged.dll");
foreach (var t in m.Types) {                     // top-level types only
  var ns = t.Namespace ?? "";
  if (ns.Length == 0 || !t.IsPublic) continue;   // skip global + internal (non-reachable) types
  if (!roots.Contains(ns.Split('.')[0])) continue;
  t.Namespace = "Codely." + ns;                   // nested types move with parent; forwarders untouched
}
m.Write("Codely.Roslyn.dll");
```

Prefixing **every** `public` type (no exceptions beyond the leave-as-is rules above) is
what guarantees the invariant: no externally-reachable type keeps a canonical namespace,
so nothing can collide with another assembly's `Microsoft.CodeAnalysis.*` / `System.*`.
The pass is idempotent — re-running skips types already under `Codely.` (root `Codely`
isn't in `roots`).

Replace `Plugins/Codely.Roslyn.dll` with the result, keep the existing
`Codely.Roslyn.dll.meta` (Editor-only import settings, stable GUID), and do **not**
re-add the individual DLLs to `Plugins/`.

Smoke-test the output: reference it from a `net9.0` console and confirm
`Codely.Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript.EvaluateAsync<int>("1+2*3")`
returns `7` and that `CSharpSyntaxTree.ParseText(...).GetDiagnostics()` yields real
message strings (e.g. `CS1525`), which verifies embedded resources still resolve.
