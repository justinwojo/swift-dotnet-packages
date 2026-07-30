# MatterSupport for .NET — Usage Guide

`SwiftBindings.Apple.MatterSupport` exposes Apple's
[MatterSupport](https://developer.apple.com/documentation/mattersupport)
framework to C# through .NET 10's native Swift interop — direct Swift calls, not
Objective-C proxy wrappers. MatterSupport is the small, high-level entry point
for adding a Matter accessory to the user's home: you describe the ecosystem
topology and hand it a setup payload, and `perform()` drives Apple's **system
commissioning UI**. This guide maps that flow to the generated C# surface.

The one structural detail worth knowing up front: `MatterAddDeviceRequest.SetupPayload`
is typed `Matter.MTRSetupPayload` — a type from the sibling **Matter** framework.
MatterSupport therefore takes a NuGet dependency on `SwiftBindings.Apple.Matter`,
and you import both namespaces. (`MTRSetupPayload` is an Objective-C type, so its
binding shape follows the [Matter guide](Matter), not the Swift conventions below.)

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: add a device](#quick-start-add-a-device)
- [Describing the topology](#describing-the-topology)
- [Device criteria](#device-criteria)
- [The cross-module Matter reference](#the-cross-module-matter-reference)
- [Extension request handler](#extension-request-handler)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- **Target framework: `net10.0-ios26.2` (or `-macos26.2` / `-maccatalyst26.2`) or higher.** The package is compiled against the iOS 26.2 SDK supplement, so your app's `<TargetFramework>` platform version must be **≥ 26.2** to restore it — an explicit lower TPV (e.g. `net10.0-ios18.0`) or a bare `net10.0-ios` whose installed workload defaults below 26.2 fails restore with `NU1202`. This is the compile-SDK pin, **not** your app's deployment minimum.
- **Deployment minimum is separate** — set your app's real minimum OS with `<SupportedOSPlatformVersion>` (independent of the TFM above). MatterSupport's own runtime floor is iOS 16.1+, macOS 13.3+, Mac Catalyst 16.4+; individual APIs that need a newer OS are gated by generated `[SupportedOSPlatform]` attributes, and the C# compiler flags a call that isn't valid for your target.
- `MatterAddDeviceRequest.IsSupported` requires iOS 17+
- macOS host for development
- A Matter entitlement on the consuming app plus the
  `com.apple.developer.matter.allow-setup-payload` capability. The
  `perform()` flow only runs on a real device with HomeKit configured.

```
dotnet add package SwiftBindings.Apple.MatterSupport
```

```csharp
using Matter;          // MTRSetupPayload comes from here
using MatterSupport;   // the request flow
```

## Naming conventions

MatterSupport binds through the Swift-interop pipeline, so the Swift-to-C#
transforms apply:

| Swift | C# | Rule |
|---|---|---|
| `func perform() async throws` | `PerformAsync(CancellationToken = default)` | `async` methods gain an `Async` suffix, return `Task`, and accept a trailing `CancellationToken` |
| `static var isSupported` | `MatterAddDeviceRequest.IsSupported` | properties are PascalCase; `static` is preserved |
| `obj.ecosystemName` | `obj.EcosystemName` | properties are PascalCase |
| `enum DeviceCriteria { case vendorID(Int) }` | `DeviceCriteria` class with `.Tag` + `TryGetVendorID(out …)` and a static `VendorID(nint)` factory | Swift enums-with-payload project to a class: a `CaseTag` discriminator, `TryGet*` accessors, and static case factories |
| nested `MatterAddDeviceRequest.Topology` | `MatterAddDeviceRequest.TopologyInfo` | a nested type whose name would collide with a member gets a suffix — `Info` for struct-like types, `Kind` for enum-like ones |

> Note the nested topology type is `TopologyInfo` in C# (Swift's
> `MatterAddDeviceRequest.Topology`), to avoid clashing with the request's
> `Topology` property.

## Quick start: add a device

```csharp
using Matter;
using MatterSupport;

// 1. Parse the accessory's setup code into a Matter MTRSetupPayload.
using var payload = new MTRSetupPayload("MT:U9VJ0OMV172PX813000");

// 2. Describe where the accessory will live.
using var home     = new MatterAddDeviceRequest.Home("Living Room");
using var topology = new MatterAddDeviceRequest.TopologyInfo("My Ecosystem", new[] { home });

// 3. Build the request and drive Apple's system commissioning UI.
using var request = new MatterAddDeviceRequest(topology, setupPayload: payload);

if (MatterAddDeviceRequest.IsSupported)   // iOS 17+
{
    await request.PerformAsync();
}
```

`PerformAsync()` presents Apple's system add-accessory sheet. It only produces a
real result on a device with HomeKit set up and an entitled app — in the
simulator it has nothing to drive. Gate the call on `IsSupported`.

`MatterAddDeviceRequest` has three constructors:

```csharp
// topology + payload (criteria defaults to .allDevices, shouldScanNetworks = true)
new MatterAddDeviceRequest(MatterAddDeviceRequest.TopologyInfo topology,
                           Matter.MTRSetupPayload? setupPayload);

// + an explicit device criteria
new MatterAddDeviceRequest(MatterAddDeviceRequest.TopologyInfo topology,
                           Matter.MTRSetupPayload? setupPayload,
                           MatterAddDeviceRequest.DeviceCriteria deviceCriteria);

// + control over network scanning
new MatterAddDeviceRequest(MatterAddDeviceRequest.TopologyInfo topology,
                           Matter.MTRSetupPayload? setupPayload,
                           MatterAddDeviceRequest.DeviceCriteria deviceCriteria,
                           bool shouldScanNetworks = true);
```

Request properties (read/write): `Topology` (`TopologyInfo`),
`SetupPayload` (`Matter.MTRSetupPayload?`), `ShowDeviceCriteria`
(`DeviceCriteria`), and `ShouldScanNetworks` (`bool`, iOS 16.4+).

## Describing the topology

The topology describes the ecosystem and the homes the user can choose from. All
three nested types are constructed from a display name:

```csharp
using var home     = new MatterAddDeviceRequest.Home("Living Room");      // .DisplayName
using var room     = new MatterAddDeviceRequest.Room("Kitchen");          // .DisplayName
using var topology = new MatterAddDeviceRequest.TopologyInfo(
    "My Ecosystem",                                                       // .EcosystemName
    new[] { home });                                                      // .Homes
```

| Type | Constructor | Members |
|---|---|---|
| `MatterAddDeviceRequest.Home` | `new Home(string displayName)` | `DisplayName` |
| `MatterAddDeviceRequest.Room` | `new Room(string displayName)` | `DisplayName` |
| `MatterAddDeviceRequest.TopologyInfo` | `new TopologyInfo(string ecosystemName, IEnumerable<Home> homes)` | `EcosystemName`, `Homes` |

Each of these also exposes `EncodeToJson()` / static `DecodeFromJson(byte[])`
for serializing the request shape.

## Device criteria

`MatterAddDeviceRequest.DeviceCriteria` is a projected Swift enum-with-payload —
it filters which accessories the picker will offer. Build it from the static
case factories, and discriminate an existing value via `.Tag` / `TryGet*`:

```csharp
// Pre-built singleton: no filtering (the default for the 2-arg ctor)
using var any = MatterAddDeviceRequest.DeviceCriteria.AllDevices;

// Case factories (each returns a DeviceCriteria)
var byVendor = MatterAddDeviceRequest.DeviceCriteria.VendorID(0x1234);
var byProduct = MatterAddDeviceRequest.DeviceCriteria.ProductID(0x5678);
var bySerial = MatterAddDeviceRequest.DeviceCriteria.SerialNumber("ABC123");
var byUuid   = MatterAddDeviceRequest.DeviceCriteria.CommissioningID(someGuid);
var anyOf    = MatterAddDeviceRequest.DeviceCriteria.Any(new[] { byVendor, byProduct });
var allOf    = MatterAddDeviceRequest.DeviceCriteria.All(new[] { byVendor, bySerial });
var negated  = MatterAddDeviceRequest.DeviceCriteria.Not(byVendor);
var fabric   = MatterAddDeviceRequest.DeviceCriteria.FabricNode(rootPublicKey, nodeID);
```

`DeviceCriteria.CaseTag` values: `Any`, `All`, `Not`, `CommissioningID`,
`VendorID`, `ProductID`, `SerialNumber`, `FabricNode`, `AllDevices`. Inspect a
value with the matching accessor:

```csharp
using var criteria = request.ShowDeviceCriteria;
if (criteria.Tag == MatterAddDeviceRequest.DeviceCriteria.CaseTag.VendorID &&
    criteria.TryGetVendorID(out nint vendor))
{
    // …
}
```

## The cross-module Matter reference

`SetupPayload` is the binding's one cross-framework type: it's
`Matter.MTRSetupPayload`, an Objective-C class from the sibling Matter package,
flowing through a Swift API.

```csharp
using var payload = new MTRSetupPayload("MT:U9VJ0OMV172PX813000");   // Matter (ObjC)
using var request = new MatterAddDeviceRequest(topology, setupPayload: payload);

Matter.MTRSetupPayload? roundTrip = request.SetupPayload;             // getter unwraps the Swift optional
uint? passcode = roundTrip?.SetupPasscode?.UInt32Value;
```

The getter returns `Matter.MTRSetupPayload?` (nullable). Because it crosses
module boundaries, restoring `SwiftBindings.Apple.MatterSupport` automatically
pulls in `SwiftBindings.Apple.Matter`; you only need to add the one package. See
the [Matter guide](Matter) for everything `MTRSetupPayload` can do.

## Extension request handler

For an out-of-app **Matter setup extension** (where your app participates in
commissioning network selection), subclass
`MatterAddDeviceExtensionRequestHandler` (an `NSObject` subclass) and override
its async hooks:

| Override | Purpose |
|---|---|
| `SelectWiFiNetworkAsync(IEnumerable<WiFiScanResult>, …)` | choose a Wi-Fi network for the accessory; returns a `WiFiNetworkAssociation` |
| `SelectThreadNetworkAsync(IEnumerable<ThreadScanResult>, …)` | choose a Thread network; returns a `ThreadNetworkAssociation` |
| `CommissionDeviceAsync(Home?, string onboardingPayload, Guid commissioningID, …)` | perform the actual commissioning |
| `RoomsAsync(Home?, …)` | report the rooms available in a home |
| `ConfigureDeviceAsync(string name, Room?, …)` | name / place the device |
| `ValidateDeviceCredentialAsync(DeviceCredential, …)` | validate the accessory's credential |

Helper factories for the network-association return types:

```csharp
var wifi   = MatterAddDeviceExtensionRequestHandler.WiFiNetworkAssociation.Network(ssid, credentials);
var thread = MatterAddDeviceExtensionRequestHandler.ThreadNetworkAssociation.Network(extendedPANID);
// Or defer to the system-configured network:
var dflt   = MatterAddDeviceExtensionRequestHandler.WiFiNetworkAssociation.DefaultSystemNetwork;
```

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals
the finalizer cleans up, but `using var` is the recommended pattern for
deterministic cleanup — `Dispose` is safe on every generated type and
double-Dispose is a no-op.

```csharp
using var home     = new MatterAddDeviceRequest.Home("Living Room");
using var topology = new MatterAddDeviceRequest.TopologyInfo("My Ecosystem", new[] { home });
using var request  = new MatterAddDeviceRequest(topology, setupPayload: payload);
```

- **`PerformAsync()` drives system UI.** It's `@MainActor`-bound in Swift; the
  binding handles the hop — you just `await` it. Continuations resume on a
  thread-pool thread, so marshal back to your UI thread before touching UI. It
  only does anything real on a HomeKit-configured device with an entitled app.
- **`SetupPayload` is nullable.** The getter returns `Matter.MTRSetupPayload?` —
  null-check before dereferencing.
- **Extension overrides run in a separate process.** The
  `MatterAddDeviceExtensionRequestHandler` hooks execute in Apple's setup
  extension, not your main app.

## Reference links

- [Apple — MatterSupport framework](https://developer.apple.com/documentation/mattersupport)
- [Apple — MatterAddDeviceRequest](https://developer.apple.com/documentation/mattersupport/matteradddevicerequest)
- [Matter guide](Matter) — the `MTRSetupPayload` type `SetupPayload` exposes
