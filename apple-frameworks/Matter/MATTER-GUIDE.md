# Matter for .NET — Usage Guide

`SwiftBindings.Apple.Matter` exposes Apple's [Matter](https://developer.apple.com/documentation/matter)
framework — the client surface for the Matter smart-home / IoT commissioning
protocol — to C#. Matter is a **pure Objective-C** framework (every public type
is an `MTR…` class or enum), so these bindings are generated through the SDK's
Objective-C pipeline (`bgen`) rather than the Swift-interop path. The result is
the standard .NET-for-iOS binding shape: `NSObject`-derived classes you `new`,
static factory methods, and `out NSError` error handling.

The Matter surface is **huge** — over a thousand generated types, the bulk of
them per-cluster data/command structs that mirror the Matter device spec. This
guide is an orientation: it covers the entry points a commissioning integration
actually touches and the binding's naming rules, then points you at Apple's docs
for the full cluster catalog. It does not attempt to document every `MTR*` type.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: parse a setup payload](#quick-start-parse-a-setup-payload)
- [Setup payloads](#setup-payloads)
- [Parsing setup codes](#parsing-setup-codes)
- [The device-controller surface](#the-device-controller-surface)
- [Clusters & the rest of the surface](#clusters--the-rest-of-the-surface)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 16.1+, macOS 13.0+, Mac Catalyst 16.1+
- macOS host for development
- A Matter entitlement on the consuming app — Apple requires
  `com.apple.developer.matter.allow-setup-payload` for setup-payload usage, and
  the full controller/commissioning surface needs HomeKit configured on a real
  device.

```
dotnet add package SwiftBindings.Apple.Matter
```

```csharp
using Matter;
```

> Most production code reaches Matter through **MatterSupport**, which drives
> Apple's system commissioning UI. If that's your goal, install
> `SwiftBindings.Apple.MatterSupport` instead — it pulls this package in
> transitively. See the [MatterSupport guide](MatterSupport).

## Naming conventions

Because Matter binds through the Objective-C pipeline, the transforms are the
familiar .NET-for-iOS ones, not the Swift-interop ones:

| Objective-C | C# | Rule |
|---|---|---|
| `-[MTRSetupPayload initWithPayload:]` | `new MTRSetupPayload(string payload)` | ObjC `init…` selectors become C# constructors |
| `+[MTRSetupPayload generateRandomPIN]` | `MTRSetupPayload.GenerateRandomPIN()` | class (`+`) methods become `static` methods |
| `@property setupPasscode` | `payload.SetupPasscode` | properties are PascalCase |
| `-[… populatePayload:]` (trailing `NSError**`) | `PopulatePayload(out NSError? error)` | trailing `error:` out-params become `out NSError?` |
| enum `MTRNetworkCommissioningWiFiBand` case `2G4` | `MTRNetworkCommissioningWiFiBand._2G4` | digit-leading enum cases gain a leading `_` |
| numeric arguments (passcode, discriminator) | `NSNumber` | Matter takes boxed `NSNumber`, not raw ints |

Numeric fields like `SetupPasscode`, `Discriminator`, `VendorID`, and `ProductID`
are `NSNumber`, so read them with `.UInt32Value` / `.UInt16Value` and construct
them with `NSNumber.FromUInt32(...)` etc.

## Quick start: parse a setup payload

Parsing a QR / manual pairing code into an `MTRSetupPayload` is the most common
standalone use of this package (typically as input to the MatterSupport flow):

```csharp
using Foundation;
using Matter;

// "MT:…" is a Matter QR-code payload string scanned from an accessory.
using var payload = new MTRSetupPayload("MT:U9VJ0OMV172PX813000");

uint? passcode = payload.SetupPasscode?.UInt32Value;
ushort? discriminator = payload.Discriminator?.UInt16Value;

Console.WriteLine($"passcode={passcode}, discriminator={discriminator}");
```

## Setup payloads

`MTRSetupPayload` is the validated, structured form of a Matter onboarding code.
Construct it three ways:

```csharp
// 1. From a QR / manual pairing code string
using var fromCode = new MTRSetupPayload("MT:U9VJ0OMV172PX813000");

// 2. From an explicit passcode + discriminator
//    (this pre-sets version / productID / vendorID to 0, per Apple's docs)
using var fromParts = new MTRSetupPayload(
    NSNumber.FromUInt32(20202021u),   // setupPasscode
    NSNumber.FromUInt16(3840));       // discriminator
```

Key members:

| Member | Type | Notes |
|---|---|---|
| `SetupPasscode` | `NSNumber` | the 27-bit setup passcode |
| `Discriminator` | `NSNumber` | device discriminator |
| `HasShortDiscriminator` | `bool` | true when only the high 4 bits are present |
| `VendorID` / `ProductID` | `NSNumber` | manufacturer identifiers |
| `Version` | `NSNumber` | payload version |
| `CommissioningFlow` | `MTRCommissioningFlow` | standard / user-intent / custom |
| `DiscoveryCapabilities` | `MTRDiscoveryCapabilities` | BLE / SoftAP / on-network flags |
| `SerialNumber` | `string?` | serial-number extension element, if present |
| `ManualEntryCode()` | `string?` | render back to an 11/21-digit manual code |
| `QrCodeString()` | `string?` | render back to a `MT:` QR string |

Static helpers:

```csharp
nuint pin       = MTRSetupPayload.GenerateRandomPIN();
NSNumber code   = MTRSetupPayload.GenerateRandomSetupPasscode();
bool valid      = MTRSetupPayload.IsValidSetupPasscode(NSNumber.FromUInt32(20202021u));
MTRSetupPayload? p = MTRSetupPayload.SetupPayloadWithOnboardingPayload("MT:…", out NSError? err);
```

## Parsing setup codes

Three dedicated parser surfaces exist. The direct `MTRSetupPayload(string)`
constructor above wraps the same logic; use these when you want explicit error
handling or to constrain the input format.

```csharp
// QR codes (base-38 "MT:…" representation)
using var qr = new MTRQRCodeSetupPayloadParser("MT:U9VJ0OMV172PX813000");
MTRSetupPayload? payload = qr.PopulatePayload(out NSError? qrError);

// Manual pairing codes (decimal digit strings)
using var manual = new MTRManualSetupPayloadParser("34970112332");
MTRSetupPayload? p2 = manual.PopulatePayload(out NSError? manualError);

// Format-agnostic dispatcher — accepts either a QR or a manual code
MTRSetupPayload? p3 =
    MTROnboardingPayloadParser.SetupPayloadForOnboardingPayload("MT:…", out NSError? error);
```

`PopulatePayload` returns `null` and populates `error` on a malformed code, so
check the return value before reading the payload.

## The device-controller surface

The full commissioning / device-interaction surface is present but is **not a
smoke-test concern** — standing it up requires a controller factory plus crypto
and storage delegates, and it only runs meaningfully on a HomeKit-configured
device. The headline types, all reachable from C#:

| Type | Role |
|---|---|
| `MTRDeviceControllerFactory` | factory singleton (`SharedInstance()`, `StartControllerFactory(params, out error)`); spins up controllers |
| `MTRDeviceController` | a running fabric controller; pair / commission accessories |
| `MTRDeviceControllerStartupParams` | controller startup configuration |
| `MTRCommissioningParameters` | parameters for the commissioning step |
| `MTRBaseDevice` | direct (non-cached) interaction with a commissioned node |
| `MTRDevice` | the higher-level cached device API |
| `MTRClusterStateCacheContainer` | attribute/event cache for a device |

Representative commissioning entry points on `MTRDeviceController` (each takes a
trailing `out NSError? error`):

```csharp
controller.SetupCommissioningSessionWithPayload(payload, newNodeID, out var error);
controller.CommissionNodeWithID(nodeID, commissioningParams, out error);
controller.CancelCommissioningForNodeID(nodeID, out error);
```

> The names above are emitted verbatim by the binding from the ObjC selectors;
> IntelliSense is the source of truth for the full controller surface. Defer to
> [Apple's MTRDeviceController docs](https://developer.apple.com/documentation/matter/mtrdevicecontroller)
> for the semantics of fabrics, node IDs, and the commissioning state machine.

## Clusters & the rest of the surface

Matter models every device capability as a **cluster**. The binding emits one
type family per cluster — roughly 150 `MTRBaseCluster…` / `MTRCluster…` classes
(e.g. `MTRBaseClusterOnOff`, `MTRBaseClusterLevelControl`, `MTRBaseClusterColorControl`)
plus hundreds of per-cluster command/response/struct/event types
(`MTR<Cluster>Cluster<Thing>`). There are also frozen enums for protocol values
— for example `MTRNetworkCommissioningWiFiBand` (`._2G4`, `._5G`, `._6G`, …) and
`MTRErrorCode` (with the `MTRErrorDomain` constant).

This guide does **not** enumerate the cluster catalog — it tracks the Matter
device specification one-to-one and is best read from Apple's reference. Every
`MTR*` type the OS exposes is reachable from C# under the `Matter` namespace;
use IntelliSense to discover the cluster you need by name.

## Memory & threading

- **Disposal.** All Matter types are `NSObject` subclasses and implement
  `IDisposable`. For short-lived locals (a parsed `MTRSetupPayload` you read and
  discard) `using var` is the clean pattern; the GC finalizer is the backstop.
- **`out NSError?` is the error channel.** ObjC Matter APIs report failure by
  returning `null` / `false` and filling a trailing `out NSError? error`. They
  do **not** throw — always check the return value before using the result.
- **Threading.** The controller/device APIs are asynchronous and callback-driven
  on Matter's own work queues; marshal back to your UI thread before touching
  UI. The parsing and payload APIs shown above are synchronous and cheap.
- **The framework ships with the OS.** This package contains only the .NET
  binding assembly; `Matter.framework` is loaded from the device, so the
  `[Register]` symbols only resolve on a Matter-capable OS version.

## Reference links

- [Apple — Matter framework](https://developer.apple.com/documentation/matter)
- [Apple — MTRSetupPayload](https://developer.apple.com/documentation/matter/mtrsetuppayload)
- [Apple — MTRDeviceController](https://developer.apple.com/documentation/matter/mtrdevicecontroller)
- [MatterSupport guide](MatterSupport) — the accessory-setup flow built on these types
