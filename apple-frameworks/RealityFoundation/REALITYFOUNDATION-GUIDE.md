# RealityFoundation for .NET — Usage Guide

`SwiftBindings.Apple.RealityFoundation` exposes Apple's
[RealityFoundation](https://developer.apple.com/documentation/realityfoundation)
framework to C# through .NET 10's native Swift interop. RealityFoundation is the
entity-component-scene (ECS) foundation that RealityKit re-exports: `Entity`,
`Component`, `Scene`, `Transform`, `ModelEntity`, `MeshResource`, materials, and
animation all live here. This is an **orientation guide** — RealityFoundation has
a very large generated surface (~250 top-level types) and several **runtime gaps**
that you must work around. It covers the entry points that work today and steers
you clearly away from the ones that don't.

> The view layer (`ARView`, gesture recognizers, render/debug options) lives in
> the sibling **`SwiftBindings.Apple.RealityKit`** package. See the RealityKit guide.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: build an entity hierarchy](#quick-start-build-an-entity-hierarchy)
- [Entities](#entities)
- [Transforms](#transforms)
- [Components](#components)
- [Meshes & materials](#meshes--materials)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+
- macOS host for development

```
dotnet add package SwiftBindings.Apple.RealityFoundation
```

```csharp
using RealityFoundation;
```

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `entity.name` | `entity.Name` | properties are PascalCase |
| `entity.observable` (iOS 26 `@Observable`) | `entity.ObservableValue` | the `observable` projection gains a `Value` suffix to avoid clashing with the nested `Observable` type |
| `entity.addChild(_:preservingWorldTransform:)` | `entity.AddChild(child, preservingWorldTransform: false)` | first label dropped; remaining labels kept as C# named args |
| `enum AntialiasingMode` (plain) | C# `enum` | plain Swift enums map to C# enums |
| `func clone(recursive:)` | `Clone(recursive: false)` | — |
| `static let identity` | `Transform.Identity` | static members PascalCase |
| `SIMD3<Float>` | `System.Numerics.Vector3` | SIMD vectors map to `System.Numerics` types; reads and setters round-trip all lanes |
| `simd_quatf` / `simd_float4x4` | `System.Numerics.Quaternion` / `Matrix4x4` | full-lane round-trip on all constructors and setters |

## Quick start: build an entity hierarchy

This is the working core of the API — entity construction, naming, parent/child
hierarchy, lookup, and clone. All of it round-trips correctly today.

```csharp
using RealityFoundation;

using var root = new Entity();
root.Name = "root";

var child = new Entity { Name = "lamp" };
root.AddChild(child, preservingWorldTransform: false);

// Enumerate children through the iOS 26 Observable projection
using var children = root.ObservableValue.Children;
int count = children.EndIndex - children.StartIndex;   // == 1
Entity first = children[0];                            // first.Name == "lamp"

// Name-based descendant lookup
Entity? found = root.FindEntity("lamp");

// Toggle enablement, read the opaque id
root.IsEnabled = false;
ulong id = root.Id;

// Deep/shallow copy
Entity copy = root.Clone(recursive: true);

// Detach
child.RemoveFromParent(preservingWorldTransform: false);
```

> **Don't set the Observable transform on a detached entity.** Reading
> `entity.ObservableValue.Transform` works; *writing* it when the entity is not
> attached to a live `Scene` traps inside RealityKit's `willSet` hook. Guard with
> `if (entity.Scene is not null)` before assigning. See [Known limitations](#known-limitations).

## Entities

`Entity` is the central ECS type. Useful members:

| Member | Type | |
|---|---|---|
| `Name` | `string` | read/write |
| `Id` | `ulong` | opaque identifier |
| `IsEnabled` / `IsEnabledInHierarchy` | `bool` | |
| `IsActive` / `IsAnchored` | `bool` | read-only state |
| `Components` | `Entity.ComponentSet` | the entity's components (see below) |
| `Scene` | `Scene?` | `null` while detached |
| `ObservableValue` | `Entity.Observable` | iOS 26 observation projection (children, transform, position, scale) |
| `Anchor` | `IHasAnchoring?` | |
| `DebugDescription` | `string` | |
| `AvailableAnimations` | `IReadOnlyList<AnimationResource>` | |

Methods: `AddChild(Entity, preservingWorldTransform:)`, `RemoveChild(...)`,
`RemoveFromParent(preservingWorldTransform:)`, `SetParent(Entity?, preservingWorldTransform:)`,
`FindEntity(string)`, `Clone(recursive:)`,
`GenerateCollisionShapes(recursive:)`, `PlayAnimation(...)`, `StopAllAnimations(recursive:)`.
(The `FindEntity(ulong id)` overload lives on `Scene`, not `Entity`.)

Subclasses include `AnchorEntity`, `ModelEntity`, `PerspectiveCamera`,
`DirectionalLight`, `PointLight`, `SpotLight`, `BodyTrackedEntity`, and
`TriggerVolume`.

`AnchorEntity` constructs and parents children fine:

```csharp
using var anchor = new AnchorEntity();   // also: new AnchorEntity(AnchoringComponent.Target), (Vector3), (Matrix4x4), …
anchor.Name = "anchor";
anchor.AddChild(new Entity { Name = "child" }, preservingWorldTransform: false);
```

> `AnchorEntity` boxes correctly as `IHasAnchoring`, so `Scene.AddAnchor(anchor)`
> and `Scene.AnchorCollection.Append(anchor)` / `.Remove(anchor)` are callable.
> Full scene-attachment behavior depends on a live AR session; test on device.

## Transforms

`Transform` is a value type with `Scale`, `Rotation` (`Quaternion`), and
`Translation` (`Vector3`). Getters **and** setters round-trip every lane:

```csharp
using var t = Transform.Identity;
t.Translation = new Vector3(1.5f, -2.5f, 3.5f);   // all 3 lanes survive
t.Scale       = new Vector3(2f, 3f, 4f);
t.Rotation    = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

Vector3 translation = t.Translation;   // reads back (1.5, -2.5, 3.5)
Quaternion rotation = t.Rotation;
```

All SIMD-typed constructors and setters — including `new Transform(scale, rotation,
translation)` and `new Transform(Matrix4x4)` — are routed through the SDK's
indirect/pointer marshal path, so all lanes survive on both simulator/Mono and
device/NativeAOT.

Constructors: `new Transform()`, `new Transform(x, y, z)`, `new Transform(scale, rotation, translation)`, `new Transform(Matrix4x4)`.
JSON round-trip is available via `EncodeToJson()` / `Transform.DecodeFromJson(byte[])`.

## Components

`Entity.Components` (`Entity.ComponentSet`) is the per-entity component bag.
Reading the count is safe:

```csharp
using var components = root.Components;
int n = components.Count;
```

The existential `Has(object componentType)` and `Remove(object componentType)`
overloads (the `any Component` shape) are marked `[Obsolete(SB0001)]` — they
call through `CallConvSwift` without a `@_cdecl` wrapper and the ABI may
mismatch on AArch64. The typed `Set<T>(T component)` overload is also
`[Obsolete(SB0001)]` for the same reason. The supported mutation path today is
the `Set(IEnumerable<IComponent>)` overload (uses a `@_cdecl` wrapper) and
reading `Count`; validate typed set/has on-device for your component types.

The framework binds a large catalog of component types, e.g. `ModelComponent`,
`CollisionComponent`, `AnchoringComponent`, `PhysicsBodyComponent`,
`CharacterControllerComponent`, `SpatialAudioComponent`, `OpacityComponent`,
`TextComponent`, `PointLightComponent`, `DirectionalLightComponent`,
`SpotLightComponent`, `ParticleEmitterComponent`. Most follow the same
constructor-then-`Components.Set` pattern as in Swift.

## Meshes & materials

`MeshResource` generates primitive geometry synchronously:

```csharp
MeshResource box = MeshResource.GenerateBox(size: 0.1f);          // also (width,height,depth, cornerRadius:, splitFaces:)
MeshResource plane = MeshResource.GeneratePlane(width: 0.1f, height: 0.1f);
MeshResource sphere = MeshResource.GenerateSphere(radius: 0.1f);
MeshResource cone = MeshResource.GenerateCone(height: 0.2f, radius: 0.1f);
MeshResource cyl = MeshResource.GenerateCylinder(height: 0.2f, radius: 0.05f);
```

> On the simulator, Metal default-device allocation may be unavailable, so these
> generators can throw there. They are validated for binding shape; run them on a
> real device for actual geometry.

Materials include `SimpleMaterial` (`new SimpleMaterial()`), `UnlitMaterial`,
`OcclusionMaterial`, `VideoMaterial`, `PhysicallyBasedMaterial`,
`ShaderGraphMaterial`, and `PortalMaterial`. A `ModelComponent` ties a mesh to
materials:

```csharp
using var material = new SimpleMaterial();
using var model = new ModelComponent(box, new[] { material });   // IEnumerable<IMaterial>
```

`ModelEntity` bundles mesh + materials (+ optional collision/mass) into a ready
entity:

```csharp
var modelEntity = new ModelEntity(box, new[] { (IMaterial)new SimpleMaterial() });
```

Low-level mesh data is exposed through the generic buffer types `MeshBuffer<T>`,
`MeshBuffers.Semantic<T>` (e.g. `MeshBuffers.Positions`, `.Normals`, `.Tangents`),
and `MeshDescriptor`. **These generics have a NativeAOT runtime gap** — see below.

## Known limitations

RealityFoundation exposes a broad surface, but a few paths have **confirmed
runtime gaps** in the current SDK. The binding test app gates each one with
`IsDynamicCodeSupported` or a `Scene` preflight so they're re-validated on
every SDK rebuild. Avoid them:

1. **`Entity.ObservableValue.Transform` setter traps without a live Scene.**
   Reading `root.ObservableValue.Transform` (and `.Position` / `.Scale`) is fine.
   *Assigning* to it on a detached entity raises `EXC_BREAKPOINT` inside
   RealityKit's `re::ecs2::TransformComponent` `willSet` hook, which expects a
   `Scene` to be driving the observation framework. Guard with
   `if (entity.Scene is not null)` before writing, or only set transforms on
   entities already attached to a running scene. (Won't-fix — no ABI route
   bypasses a Swift property observer.)

2. **NativeAOT generic-metadata gap for mesh buffers (deferred to 0.13.0).**
   `MeshBuffer<T>`, `MeshBuffers.Semantic<T>`, and `UnsafeForceEffectBuffer<T>`
   resolve correctly on the Mono interpreter (the simulator default) but fail on
   NativeAOT / full-AOT builds (physical-device release), which trim the
   generic-specialization metadata these types need. The test suite
   capability-gates on `RuntimeFeature.IsDynamicCodeSupported` — it passes on
   Mono/sim and skips on NativeAOT/device. If you ship NativeAOT, don't rely on
   the typed mesh buffers.

3. **`Entity.ComponentSet.Has` / `Remove` / typed `Set<T>` are `[Obsolete(SB0001)]`.**
   These existential-shaped overloads call through `CallConvSwift` without a
   `@_cdecl` wrapper; the ABI may mismatch on AArch64. Use
   `Set(IEnumerable<IComponent>)` (which has a wrapper) for bulk mutation, and
   read `Count` for inspection.

Everything in [Quick start](#quick-start-build-an-entity-hierarchy) — entity
construction, the hierarchy operations, `Name`/`IsEnabled`/`Id` round-trips,
`FindEntity`, `Clone`, and component-count reads — is verified working, as are
all `Transform` constructors (including `new Transform(scale, rotation,
translation)`), the SIMD setters, `new Transform(Matrix4x4)`, and
`Scene.AddAnchor(IHasAnchoring)` (AnchorEntity now boxes correctly as
`IHasAnchoring`).

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals
the finalizer cleans up, but `using var` is the recommended pattern for
deterministic cleanup — `Dispose` is safe on every generated type and
double-Dispose is a no-op.

```csharp
using var entity = new Entity();
using var transform = Transform.Identity;
```

- Most entity/scene mutation is expected on the main thread, as in Swift.
- Value-type wrappers (`Transform`, the collection views like `Entity.Observable`,
  `ChildCollection`, `ComponentSet`) wrap a Swift struct; dispose them
  deterministically, especially inside loops.

## Reference links

- [Apple — RealityFoundation](https://developer.apple.com/documentation/realityfoundation)
- [Apple — RealityKit](https://developer.apple.com/documentation/realitykit) (umbrella docs cover most of these types)
- RealityKit for .NET guide (sibling `SwiftBindings.Apple.RealityKit` package) — `ARView`, gestures, render/debug options
