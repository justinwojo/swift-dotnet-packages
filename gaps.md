# Known Gaps & Workarounds

## Gap 1: DllImport resolver required for framework-style native libraries

**Impact:** All consumers (end users AND test apps)

**Problem:** Generated bindings use `[DllImport("Nuke")]` but on iOS the actual binary lives at `@rpath/Nuke.framework/Nuke`. The .NET runtime doesn't automatically search framework bundles, so P/Invoke fails with a DllNotFoundException at runtime.

**Current workaround:** Manually register a `NativeLibrary.SetDllImportResolver` that maps any library name to `@rpath/{name}.framework/{name}`. Must be registered on the **library assembly** (where P/Invoke declarations live), not just the consuming app assembly.

**Proper fix:** Emit a `[ModuleInitializer]` in the generated C# bindings that auto-registers the resolver when the assembly loads. This is a generator change in `swift-bindings` (`src/Swift.Bindings/src/Emitter/`). The emitted class would look like:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Nuke;

internal static class SwiftFrameworkResolver
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(typeof(SwiftFrameworkResolver).Assembly, (libraryName, assembly, searchPath) =>
        {
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
                return handle;
            return IntPtr.Zero;
        });
    }
}
```

Once that's in the generator, the workaround in test apps and the template can be removed. End users would just `dotnet add package Swift.Nuke` and it works.

**Where to add in generator:** `ModuleEmitter.cs` or a new `FrameworkResolverEmitter.cs` — emit the class at the end of the generated `.cs` file, inside the module namespace.

---

## Gap 2: NativeReference doesn't propagate through ProjectReference

**Impact:** Test apps only (NOT end users consuming NuGet packages)

**Problem:** When the test project uses `<ProjectReference>` to reference the library project, `<NativeReference>` items from the library project (injected by the SDK targets) don't flow to the test project. The app bundle ends up without the native frameworks.

**Current workaround:** Test projects explicitly declare their own NativeReference items pointing at the library's xcframework paths:

```xml
<NativeReference Include="../../libraries/Nuke/Nuke.xcframework">
  <Kind>Framework</Kind>
</NativeReference>
<NativeReference Include="../../libraries/Nuke/obj/Debug/net10.0-ios/swift-binding/NukeSwiftBindings.xcframework"
                 Condition="Exists('...')">
  <Kind>Framework</Kind>
</NativeReference>
```

**Why NuGet consumers are fine:** The generated `.targets` file (placed in `buildTransitive/` in the NuGet package) injects NativeReference items automatically. This only fires for NuGet package consumers, not ProjectReference consumers.

**Potential fix options:**
1. Accept it as test-infrastructure-only and keep the explicit references in the template
2. Add a `.targets` file to the SDK output that also handles the ProjectReference case (would need to resolve paths relative to the library project, not the NuGet layout)
3. Use a shared `.props` file imported by both library and test projects

For now, option 1 is fine — the template handles it.

---

## Gap 3: Template needs manual library assembly resolver registration

**Impact:** Template scaffolding workflow

**Problem:** The template's `Program.cs.template` can't know the library's assembly type at scaffolding time, so it leaves a TODO comment for the user to add:
```csharp
// TODO: Add resolver for your library assembly, e.g.:
// NativeLibrary.SetDllImportResolver(typeof(SomeLibraryType).Assembly, resolver);
```

**Fix:** Once Gap 1 is resolved (ModuleInitializer in generated bindings), this entire resolver section can be removed from both the template and the Nuke test app. The template becomes simpler and works out of the box.

---

## Current Validated State

- **Library build:** `dotnet build libraries/Nuke` succeeds using `Swift.Bindings.Sdk` — 0 errors, SDK auto-discovers xcframework, generates bindings, compiles Swift wrapper
- **Test app build:** `dotnet build tests/Nuke.SimTests` succeeds — 0 errors
- **Simulator validation:** 6/6 tests pass (framework loading, SwiftString, ImagePipeline.Shared, ImageRequest constructor, ImageRequest.Priority)
- **Local NuGet feed:** `local-packages/` contains `Swift.Runtime.0.1.0-preview.1.nupkg` and `Swift.Bindings.Sdk.0.1.0-preview.1.nupkg`, referenced via `NuGet.config`

## Dependencies for End-to-End

| Dependency | Status | Notes |
|------------|--------|-------|
| `Swift.Runtime` NuGet | Local only | `local-packages/Swift.Runtime.0.1.0-preview.1.nupkg` |
| `Swift.Bindings.Sdk` NuGet | Local only | `local-packages/Swift.Bindings.Sdk.0.1.0-preview.1.nupkg` |
| Nuke.xcframework | Built locally | `libraries/Nuke/build-xcframework.sh` (gitignored) |
| Generator ModuleInitializer | Not implemented | Gap 1 — blocks zero-config end-user experience |
