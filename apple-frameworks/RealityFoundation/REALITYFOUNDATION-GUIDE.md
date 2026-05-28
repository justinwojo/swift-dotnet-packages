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
| `SIMD3<Float>` | `System.Numerics.Vector3` | SIMD vectors map to `System.Numerics` types (**see limitations — setters truncate**) |
| `simd_quatf` / `simd_float4x4` | `System.Numerics.Quaternion` / `Matrix4x4` | (**see limitations**) |

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

> **Don't set the transform on a detached entity.** Reading the identity transform
> works; *writing* a transform on an entity that isn't attached to a live `Scene`
> traps. See [Known limitations](#known-limitations).

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

> Attaching an `AnchorEntity` to a `Scene` via `Scene.AddAnchor` does **not** work
> — see limitations.

## Transforms

`Transform` is a value type with `Scale`, `Rotation` (`Quaternion`), and
`Translation` (`Vector3`). The identity transform and all *getters* work:

```csharp
using var t = Transform.Identity;
Vector3 translation = t.Translation;   // (0,0,0)
Vector3 scale = t.Scale;               // (1,1,1)
Quaternion rotation = t.Rotation;
```

Constructors: `new Transform()`, `new Transform(scale, rotation, translation)`,
`new Transform(x, y, z)`, `new Transform(Matrix4x4)`. JSON round-trip is available
via `EncodeToJson()` / `Transform.DecodeFromJson(byte[])`.

> **The `Vector3`/`Quaternion`/`Matrix4x4` setters and the `Transform(Matrix4x4)`
> constructor truncate today.** See [Known limitations](#known-limitations) before
> writing transforms.

## Components

`Entity.Components` (`Entity.ComponentSet`) is the per-entity component bag.
Reading the count is safe:

```csharp
using var components = root.Components;
int n = components.Count;
```

The existential `Has(object componentType)` and `Remove(object componentType)`
overloads (the `any Component` shape) are marked `[Obsolete]` — no `@_cdecl`
wrapper or native thunk is generated for them, so they are not usable. The
supported mutation path is the typed overloads (e.g. `Set(ModelComponent)`),
which are not obsolete; validate on-device for your component types.

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

RealityFoundation exposes a broad surface, but several paths have **confirmed
runtime gaps** in the current SDK. The binding test app pins each one with a
`Skip` so they're re-validated on every SDK rebuild. Avoid them:

1. **SIMD setters truncate to the first lane.** Assigning `Transform.Translation`,
   `Transform.Scale`, or `Transform.Rotation`, and the `new Transform(Matrix4x4)`
   constructor, silently lose every lane past X. The Swift side takes a 16-byte
   4-lane `SIMD3<Float>` / `simd_quatf` / `simd_float4x4`, but the generated PInvoke
   marshals a 12-byte `System.Numerics.Vector3`; under AAPCS only the first lane
   survives the register split, so writes truncate (rotation writes vanish
   entirely). **Reading** the identity transform and individual components works
   fine — the gap is on the *write* boundary. Until the SDK lands a wider SIMD
   marshal, treat transform setters as non-functional.

2. **`Entity.ObservableValue.Transform` setter traps without a live Scene.**
   Reading `root.ObservableValue.Transform` (and `.Position` / `.Scale`) is fine.
   *Assigning* to it on a detached entity raises `EXC_BREAKPOINT` inside
   RealityKit's `re::ecs2::TransformComponent` `willSet` hook, which expects a
   `Scene` to be driving the observation framework. Don't set transforms on
   entities that aren't attached to a running scene.

3. **NativeAOT generic-metadata gap for mesh buffers.** `MeshBuffer<T>`,
   `MeshBuffers.Semantic<T>`, and `UnsafeForceEffectBuffer<T>` resolve correctly on
   the Mono interpreter (the simulator default) but fail on NativeAOT / full-AOT
   builds (physical-device release), which trim the generic-specialization
   metadata these types need. If you ship NativeAOT, don't rely on the typed mesh
   buffers.

4. **`Scene.AddAnchor` refuses an `AnchorEntity`.** `Scene.AddAnchor(IHasAnchoring)`
   (and `Scene.AnchorCollection.Append(IHasAnchoring)` / `.Remove(...)`) won't
   accept an `AnchorEntity` argument — the `AnchorEntity` binding doesn't project
   the `IHasAnchoring` conformance, so the existential box refuses the cast.
   Constructing an `AnchorEntity`, naming it, and adding children all work; only
   handing it to the scene's anchor collection does not.

Everything in [Quick start](#quick-start-build-an-entity-hierarchy) — entity
construction, the hierarchy operations, `Name`/`IsEnabled`/`Id` round-trips,
`FindEntity`, `Clone`, identity-transform *reads*, and component-count reads — is
verified working.

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
