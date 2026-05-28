# RealityKit for .NET — Usage Guide

`SwiftBindings.Apple.RealityKit` exposes the **RealityKit-specific** surface of
Apple's [RealityKit](https://developer.apple.com/documentation/realitykit)
framework to C# through .NET 10's native Swift interop — primarily `ARView`, the
entity gesture recognizers, and the render/debug/environment options that hang off
the view. This is an **orientation guide** covering those entry points and the
runtime gaps you must work around.

> **Most of RealityKit's type surface lives elsewhere.** `Entity`, `Component`,
> `Scene`, `Transform`, `ModelEntity`, `MeshResource`, materials, and animation are
> bound in the sibling **`SwiftBindings.Apple.RealityFoundation`** package, which
> this package depends on (it's pulled in automatically on restore). For the ECS /
> scene / transform surface, see the
> [RealityFoundation guide](https://github.com/justinwojo/swift-dotnet-packages/wiki/RealityFoundation)
> — including its [Known limitations](https://github.com/justinwojo/swift-dotnet-packages/wiki/RealityFoundation#known-limitations),
> which apply whenever you touch those types through an `ARView`.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: create an ARView and read its scene](#quick-start-create-an-arview-and-read-its-scene)
- [ARView](#arview)
- [Hit-testing & projection](#hit-testing--projection)
- [Entity gestures](#entity-gestures)
- [Render, debug & environment options](#render-debug--environment-options)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+
- macOS host for development
- AR features require a physical device with a back camera; a simulator can host a
  **non-AR** `ARView` (use `CameraModeType.NonAR`) for view/scene wiring.

```
dotnet add package SwiftBindings.Apple.RealityKit
```

```csharp
using RealityKit;          // ARView and the RealityKit-specific surface
using RealityFoundation;   // Entity, Scene, Transform, … (sibling package)
```

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `arView.scene` | `arView.Scene` | properties are PascalCase |
| `enum CameraMode` (plain) | `ARView.CameraModeType` enum | nested enums gain a `Type` suffix; cases keep their order |
| `ARView.DebugOptions` (OptionSet) | `ARView.DebugOptionsType` | nested option-set/struct types gain a `Type` suffix |
| `ARView.Environment` | `ARView.EnvironmentType` | (same `Type` suffix rule) |
| `view.hitTest(_:)` | `view.HitTest(point)` | first label dropped |
| `view.project(_:)` | `view.Project(point)` returning `Swift.CGPoint?` | optionals project to `T?` / `Swift.SwiftOptional<T>` |

The `…Type` suffix on `CameraModeType`, `DebugOptionsType`, `RenderOptionsType`,
`EnvironmentType`, etc. is the generator disambiguating a nested type from a
same-named member on `ARView`.

## Quick start: create an ARView and read its scene

A non-AR `ARView` can be created and inspected even on the simulator (no camera
session is started). This is the path that's verified working:

```csharp
using RealityKit;
using RealityFoundation;
using CGRect = Swift.CGRect;   // disambiguate from UIKit's CoreGraphics.CGRect

// NonAR avoids starting an ARSession (no back camera needed)
using var arView = new ARView(
    new CGRect(0, 0, 320, 240),
    ARView.CameraModeType.NonAR,
    automaticallyConfigureSession: false);

// Scene is a RealityFoundation.Scene reached across the module boundary
Scene scene = arView.Scene;
string sceneName = scene.Name;

// Read the camera transform (identity reads round-trip correctly)
using var camera = arView.CameraTransform;
Vector3 cameraPos = camera.Translation;

// Read environment / render / debug option values
using var env = arView.Environment;
using var debug = arView.DebugOptions;
using var render = arView.RenderOptions;
```

## ARView

`ARView` is a `UIKit.UIView` subclass. Constructors:

```csharp
new ARView(CGRect frame);
new ARView(CGRect frame, ARView.CameraModeType cameraMode, bool automaticallyConfigureSession);
```

`ARView.CameraModeType` is a plain enum: `Ar` (0), `NonAR` (1).

Key properties:

| Member | Type | |
|---|---|---|
| `Scene` | `RealityFoundation.Scene` | the entity scene graph |
| `CameraTransform` | `RealityFoundation.Transform` | current camera transform (read) |
| `CameraMode` | `ARView.CameraModeType` | |
| `Environment` | `ARView.EnvironmentType` | background, lighting, scene-understanding, reverb |
| `DebugOptions` | `ARView.DebugOptionsType` | |
| `RenderOptions` | `ARView.RenderOptionsType` | |
| `RenderCallbacks` | `ARView.RenderCallbacksType` | |
| `Session` | `ARKit.ARSession` | underlying AR session |
| `AudioListener` | `RealityFoundation.Entity?` | |
| `PhysicsOrigin` | `RealityFoundation.Entity?` | |
| `AutomaticallyConfigureSession` | `bool` | |
| `ContentScaleFactor` | `double` | |

## Hit-testing & projection

`ARView` binds the screen↔world conversion calls. These return meaningful results
only when an AR session is running; on a non-AR simulator they exercise the
marshalling path but typically return `null`/empty.

```csharp
using CGPoint = Swift.CGPoint;

// World point -> screen point (optional)
CGPoint? screen = arView.Project(new Vector3(0f, 0f, -1f));

// Screen point -> world ray
var ray = arView.Ray(new CGPoint(160, 120));   // Swift.SwiftOptional<(Vector3 origin, Vector3 direction)>

// Screen point -> world point on a plane / viewport
var unprojected = arView.Unproject(new CGPoint(160, 120), viewport: new CGRect(0, 0, 320, 240));

// Collision hit-test
IReadOnlyList<RealityFoundation.CollisionCastHit> hits = arView.HitTest(new CGPoint(160, 120));
```

## Entity gestures

Entity gesture recognizers are **not usable from C#** in the current binding — see
[Known limitations](#known-limitations). `ARView.InstallGestures` is present but
deprecated and throws at runtime, and the concrete recognizer types
(`EntityTranslationGestureRecognizer`, `EntityScaleGestureRecognizer`,
`EntityRotationGestureRecognizer`) throw on their `.Entity` / `.Location` accessors.

## Render, debug & environment options

`ARView.DebugOptionsType` and `ARView.RenderOptionsType` are option-set value types
with static members you combine:

- `DebugOptionsType`: `None`, `ShowPhysics`, `ShowStatistics`, `ShowWorldOrigin`,
  `ShowAnchorOrigins`, `ShowAnchorGeometry`, `ShowFeaturePoints`,
  `ShowSceneUnderstanding`.
- `RenderOptionsType`: `DisableCameraGrain`, `DisableGroundingShadows`,
  `DisableMotionBlur`, `DisableDepthOfField`, `DisableHDR`,
  `DisablePersonOcclusion`, `DisableAREnvironmentLighting`, `DisableFaceMesh`.

`ARView.EnvironmentType` exposes the scene's `Background`, `SceneUnderstanding`
(`SceneUnderstandingType`), `Reverb` (`ReverbType`), and image-based lighting via
the `Lighting` property (type `ImageBasedLight`) — all readable:

```csharp
using var env = arView.Environment;
using var su = env.SceneUnderstanding;   // ARView.EnvironmentType.SceneUnderstandingType
using var bg = env.Background;
```

Other RealityKit-specific types in this package: `MultipeerConnectivityService`
(a `RealityFoundation.ISynchronizationService` for shared AR sessions),
`ARKitAnchorComponent`, and `MaterialColorParameter`.

## Known limitations

The RealityKit *view* surface (`ARView` construction, scene/environment/option
reads, hit-testing entrypoints) is verified working. The gaps you'll hit come from
the **RealityFoundation** types reached through `ARView` — they apply here too:

1. **SIMD transform setters truncate.** Writing `Transform.Translation`/`.Scale`/
   `.Rotation` or using `new Transform(Matrix4x4)` loses every lane past the first.
   Reading the camera/identity transform works. (RealityFoundation gap 1.)

2. **`Entity.ObservableValue.Transform` setter traps without a live Scene.** Reads
   are fine; writes on a detached entity raise `EXC_BREAKPOINT`. (RealityFoundation
   gap 2.)

3. **Typed mesh buffers fail on NativeAOT.** `MeshBuffer<T>` / `MeshBuffers.Semantic<T>`
   work on the Mono interpreter (simulator) but not NativeAOT device builds.
   (RealityFoundation gap 3.)

4. **`Scene.AddAnchor` refuses an `AnchorEntity`.** Walking `arView.Scene.Anchors`
   works, and you can construct an `AnchorEntity`, name it, and `AddChild` to it —
   but `Scene.AddAnchor(IHasAnchoring)` won't accept the `AnchorEntity` because the
   binding doesn't project the `IHasAnchoring` conformance. (RealityFoundation gap 4.)

5. **Entity gesture recognizers are not usable from C#.** `ARView.InstallGestures`
   is deprecated and throws `NotSupportedException` at runtime because the gesture
   protocol projections were not emitted. The concrete recognizer types
   (`EntityTranslationGestureRecognizer`, `EntityScaleGestureRecognizer`,
   `EntityRotationGestureRecognizer`) likewise throw on their `.Entity` / `.Location`
   accessors.

See the
[RealityFoundation Known limitations](https://github.com/justinwojo/swift-dotnet-packages/wiki/RealityFoundation#known-limitations)
for the full explanation of each.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals
the finalizer cleans up, but `using var` is the recommended pattern for
deterministic cleanup — `Dispose` is safe on every generated type and
double-Dispose is a no-op.

```csharp
using var arView = new ARView(new Swift.CGRect(0, 0, 320, 240));
using var env = arView.Environment;
```

- `ARView` is a `UIView` — create and drive it on the main thread.
- The option/environment views (`DebugOptionsType`, `EnvironmentType`, etc.) wrap
  Swift structs; dispose them deterministically.
- Note the `using CGRect = Swift.CGRect;` alias in the examples: `ARView`'s
  constructor takes the Swift-marshalled `Swift.CGRect`, which would otherwise
  collide with UIKit's implicitly-imported `CoreGraphics.CGRect`.

## Reference links

- [Apple — RealityKit](https://developer.apple.com/documentation/realitykit)
- [Apple — ARView](https://developer.apple.com/documentation/realitykit/arview)
- RealityFoundation for .NET guide (sibling `SwiftBindings.Apple.RealityFoundation` package) — entities, scenes, transforms, meshes, materials
