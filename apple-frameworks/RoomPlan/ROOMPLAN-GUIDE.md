# RoomPlan for .NET — Usage Guide

`SwiftBindings.Apple.RoomPlan` exposes Apple's [RoomPlan](https://developer.apple.com/documentation/roomplan)
framework — LiDAR-based 3D room capture and structured-floorplan reconstruction —
to C# through .NET 10's native Swift interop. This is an orientation guide: it
maps the Swift capture/build/result workflow onto the generated C# surface and
points out the few naming transforms that aren't obvious. RoomPlan capture is a
device-only, LiDAR-only API, so most of what you can exercise off-device is the
*result* side (inspecting and exporting a `CapturedRoom`).

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: build a room from captured data](#quick-start-build-a-room-from-captured-data)
- [Live capture: RoomCaptureSession](#live-capture-roomcapturesession)
- [The RoomCaptureView shortcut](#the-roomcaptureview-shortcut)
- [Inspecting a CapturedRoom](#inspecting-a-capturedroom)
- [Exporting USDZ](#exporting-usdz)
- [Errors & instructions](#errors--instructions)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- Built against the .NET 10 iOS 26.2 SDK; RoomPlan runtime availability follows
  Apple's framework (iOS 16/17+).
- macOS host for development
- **A LiDAR-equipped iPhone or iPad Pro for live capture** — there is no simulator
  path for scanning. Only the result/export types (`CapturedRoom` and friends)
  are reachable without LiDAR hardware.
- Camera + ARKit usage descriptions in `Info.plist`

```
dotnet add package SwiftBindings.Apple.RoomPlan
```

```csharp
using RoomPlan;
```

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `func capturedRoom(from:) async throws` | `CapturedRoomAsync(...)` | `async` methods gain an `Async` suffix and return `Task<T>` plus a trailing `CancellationToken` |
| `session.run(configuration:)` | `session.Run(configuration)` | first argument label dropped |
| `enum CaptureError` (plain) | `RoomCaptureSession.CaptureError` C# `enum` | plain Swift enums project to C# enums with the same case order |
| `enum Category { case wall(...) }` | nested `CategoryType` class with `.Tag` + `.CaseTag` | enums-with-payload become a class carrying a `CaseTag` discriminator |
| `error.errorDescription` | `error.GetErrorDescription()` | localized strings surface as extension methods, e.g. `RoomCaptureSessionCaptureErrorExtensions.GetErrorDescription` |
| `struct ConfigurationOptions: OptionSet` | `RoomBuilder.ConfigurationOptions` | an `OptionSet`, **not** a settable struct — see below |

> **`RoomBuilder.ConfigurationOptions` is an `OptionSet`, not a plain struct.**
> There is no parameterless constructor. Use the static member
> `RoomBuilder.ConfigurationOptions.BeautifyObjects`, or build an empty set with
> `new RoomBuilder.ConfigurationOptions(0)` (the `nint rawValue` constructor).

## Quick start: build a room from captured data

The reconstruction half of the API works from a `CapturedRoomData` buffer (the
payload your capture-session delegate hands you, or a `CapturedRoomData` decoded
from JSON). `RoomBuilder` turns it into a structured `CapturedRoom`:

```csharp
using RoomPlan;

// Empty option set (or use RoomBuilder.ConfigurationOptions.BeautifyObjects)
using var options = new RoomBuilder.ConfigurationOptions(0);
using var builder = new RoomBuilder(options);

// capturedRoomData comes from your RoomCaptureSession delegate callback
CapturedRoom room = await builder.CapturedRoomAsync(capturedRoomData);

Console.WriteLine($"Walls: {room.Walls.Count}, objects: {room.Objects.Count}");
```

`StructureBuilder` is the multi-room counterpart — it merges several
`CapturedRoom`s into one `CapturedStructure`:

```csharp
using var structureBuilder = new StructureBuilder(new RoomBuilder.ConfigurationOptions(0));
CapturedStructure structure = await structureBuilder.CapturedStructureAsync(new[] { roomA, roomB });
```

## Live capture: RoomCaptureSession

Live scanning is a delegate-driven flow. `RoomCaptureSession` is device-only and
requires LiDAR; `RoomCaptureSession.IsSupported` gates it.

```csharp
if (!RoomCaptureSession.IsSupported)
    return; // no LiDAR — bail out

using var session = new RoomCaptureSession();
session.Delegate = new MyCaptureDelegate();

using var config = new RoomCaptureSession.Configuration();
config.IsCoachingEnabled = true;
session.Run(config);

// later, when the user finishes:
session.Stop();                 // or session.Stop(pauseARSession: true)
```

Implement `IRoomCaptureSessionDelegate` to receive results. Note that all four
callbacks are Swift overloads of `captureSession(_:...)`, so they project to a
set of overloaded `CaptureSession(...)` methods discriminated by their second
parameter:

```csharp
internal sealed class MyCaptureDelegate : IRoomCaptureSessionDelegate
{
    // live re-localization / partial room updates
    public void CaptureSession(RoomCaptureSession session, CapturedRoom room) { }

    // user-guidance instruction (move closer to wall, slow down, …)
    public void CaptureSession(RoomCaptureSession session, RoomCaptureSession.Instruction instruction) { }

    // configuration applied
    public void CaptureSession(RoomCaptureSession session, RoomCaptureSession.Configuration configuration) { }

    // final result + optional error — feed `data` into RoomBuilder.CapturedRoomAsync
    public void CaptureSession(RoomCaptureSession session, CapturedRoomData data, Swift.Foundation.AnyError? error) { }
}
```

`RoomCaptureSession.ArSession` exposes the underlying `ARKit.ARSession`, and the
constructor optionally takes one: `new RoomCaptureSession(arSession)`.

## The RoomCaptureView shortcut

`RoomCaptureView` is a `UIView` subclass that wires up a session, the AR camera
feed, and the coaching overlay for you. Construct it with a frame (and optionally
an `ARSession`):

```csharp
using CGRect = Swift.CGRect;

var view = new RoomCaptureView(new CGRect(0, 0, 390, 844));
view.IsModelEnabled = true;            // show the live reconstructed model
view.Delegate = new MyViewDelegate();  // IRoomCaptureViewDelegate — see caveat
```

The bound surface on `RoomCaptureView` is intentionally thin — `Delegate` and
`IsModelEnabled` are the two settable members. Drive the actual scan through the
view's session as you would normally in Swift; refer to Apple's docs for the
full view lifecycle.

> **The `RoomCaptureView` delegate is not a working callback mechanism.** The
> `Delegate` *setter* exists, but no protocol proxy or dispatch was emitted for
> it: the getter throws `NotSupportedException` and every `IRoomCaptureViewDelegate`
> method is a throwing default — the View delegate callbacks are never delivered.
> For capture callbacks use the `RoomCaptureSession` delegate
> (`IRoomCaptureSessionDelegate`), which has a real proxy and is the supported
> path (see [Live capture](#live-capture-roomcapturesession)).

## Inspecting a CapturedRoom

`CapturedRoom` is the structured result. Its surfaces and objects are exposed as
`IReadOnlyList`s:

| Member | Type | |
|---|---|---|
| `Walls` | `IReadOnlyList<CapturedRoom.Surface>` | wall surfaces |
| `Doors` | `IReadOnlyList<CapturedRoom.Surface>` | |
| `Windows` | `IReadOnlyList<CapturedRoom.Surface>` | |
| `Openings` | `IReadOnlyList<CapturedRoom.Surface>` | |
| `Floors` | `IReadOnlyList<CapturedRoom.Surface>` | |
| `Objects` | `IReadOnlyList<CapturedRoom.Object>` | furniture, fixtures |
| `Sections` | `IReadOnlyList<CapturedRoom.Section>` | room sections (living room, bedroom, …) |
| `Identifier` | `System.Guid` | |
| `Story` | `int` | |
| `Version` | `int` | |

Each `CapturedRoom.Surface` carries:

| Member | Type | |
|---|---|---|
| `Category` | `CapturedRoom.Surface.CategoryType` | Door / Wall / Opening / Window / Floor — discriminate via `.Tag` (a `CaseTag`) |
| `Confidence` | `CapturedRoom.Confidence` | `High` / `Medium` / `Low` |
| `Dimensions` | `System.Numerics.Vector3` | |
| `Transform` | `System.Numerics.Matrix4x4` | world placement |
| `CompletedEdges` | `IReadOnlySet<CapturedRoom.Surface.Edge>` | |
| `PolygonCorners` | `IReadOnlyList<System.Numerics.Vector3>` | |
| `Identifier` / `ParentIdentifier` | `System.Guid` / `System.Guid?` | |
| `Story` | `int` | |
| `Curve` | `CapturedRoom.Surface.CurveType?` | non-null for curved walls |

`CapturedRoom.Object` is similar (`Category` is `CapturedRoom.Object.CategoryType`,
plus `Confidence`, `Dimensions`, `Transform`, `Identifier`, `ParentIdentifier`,
`Story`). Object subcategory detail is modeled by the top-level value types
`ChairType`, `SofaType`, `TableType`, `StorageType`, `TableShapeType`,
`ChairLegType`, `ChairArmType`, `ChairBackType` — each a tagged value exposing
static cases (e.g. `ChairType.Dining`, `SofaType.LShaped`) and a `.Tag`.

```csharp
foreach (var wall in room.Walls)
{
    Console.WriteLine($"wall {wall.Identifier} {wall.Dimensions} conf={wall.Confidence}");
    if (wall.Category.Tag == CapturedRoom.Surface.CategoryType.CaseTag.Window)
        Console.WriteLine("  (this surface is a window)");
}
```

Both `CapturedRoom` and its `Surface`/`Object` members support
`EncodeToJson()` / `DecodeFromJson(byte[])` for persistence, and
`CapturedRoomData` likewise (`DecodeFromJson`).

## Exporting USDZ

`CapturedRoom.Export` writes a USDZ model to disk. The simplest overload takes a
single destination URL; richer overloads accept a metadata URL, a custom
`ModelProvider`, and `USDExportOptions`:

```csharp
using Foundation;

var url = NSUrl.FromFilename("/path/to/room.usdz");
room.Export(url);

// or with options. USDExportOptions is also an OptionSet — construct it from a
// raw value (0 == default model export):
using var exportOptions = new CapturedRoom.USDExportOptions(0);
room.Export(url, exportOptions);
```

## Errors & instructions

The error enums are plain C# enums (case order pinned to Swift):

- `RoomCaptureSession.CaptureError` — `ExceedSceneSizeLimit` (0), `WorldTrackingFailure`,
  `InvalidARConfiguration`, `DeviceTooHot`, `DeviceNotSupported`, `InternalError` (5)
- `RoomCaptureSession.Instruction` — `MoveCloseToWall` (0), `MoveAwayFromWall`,
  `SlowDown`, `TurnOnLight`, `Normal`, `LowTexture` (5)
- `RoomBuilder.BuildError` — `InsufficientInput` (0) … `InternalError` (4)
- `StructureBuilder.BuildError` — `InsufficientInput` (0) … `InternalError` (5)
- `CapturedRoom.Error` — `UrlInvalidScheme` (0) … `DeviceNotSupported` (4)

Localized descriptions come through generated extension classes (the names are
verbose because they mirror the fully-qualified Swift type):

```csharp
string? msg = RoomCaptureSessionCaptureErrorExtensions
    .GetErrorDescription(RoomCaptureSession.CaptureError.ExceedSceneSizeLimit);

// these are C# extension methods, so the instance-style call works too:
string? buildMsg = RoomBuilder.BuildError.InsufficientInput.GetErrorDescription();
```

Other available description extensions: `StructureBuilderBuildErrorExtensions`,
`CapturedRoomErrorExtensions`. `CapturedRoomSurfaceEdgeExtensions.AllCases`
enumerates the `Surface.Edge` cases.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals
the finalizer cleans up, but `using var` is the recommended pattern for
deterministic cleanup — `Dispose` is safe on every generated type and
double-Dispose is a no-op.

```csharp
using var builder = new RoomBuilder(new RoomBuilder.ConfigurationOptions(0));
```

- `CapturedRoomAsync` / `CapturedStructureAsync` are `async` and accept a trailing
  `CancellationToken`. Build work runs off the calling thread; marshal back to the
  UI thread before touching `UIView`s.
- `RoomCaptureSession` and `RoomCaptureView` are UIKit-adjacent — create and drive
  them on the main thread.
- Delegate callbacks (`IRoomCaptureSessionDelegate`) arrive on the session's queue;
  hop to the main thread before updating UI.

## Reference links

- [Apple — RoomPlan](https://developer.apple.com/documentation/roomplan)
- [Apple — Create a 3D model of an interior room (RoomPlan sample)](https://developer.apple.com/documentation/roomplan/create-a-3d-model-of-an-interior-room-by-guiding-the-user-through-an-ar-experience)
