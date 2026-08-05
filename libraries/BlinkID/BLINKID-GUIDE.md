# BlinkID for .NET — Usage Guide

`SwiftBindings.BlinkID` exposes Microblink's [BlinkID](https://docs.microblink.com/blinkid) identity-document scanning engine to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

BlinkID's model is a small, well-shaped pipeline: you initialize **one SDK instance** with a license key, ask it for a **scanning session**, feed the session **input images** (camera frames or `UIImage`s) one at a time, watch each frame's **process result** until the document is scanned, and then pull the **scanning result** — a large flat record of extracted fields plus per-side VIZ / MRZ / barcode sub-results. This guide maps that pipeline to the generated C# surface.

> This package is the **core engine only** — no camera, no UI. For Microblink's prebuilt camera scanning UX on top of it, see [**BlinkIDUX**](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkIDUX).

## Contents

- [Requirements & install](#requirements--install)
- [Namespaces](#namespaces)
- [Swift → C# translation rules](#swift--c-translation-rules)
- [Object lifetime](#object-lifetime)
- [1. Initialize the SDK (license key)](#1-initialize-the-sdk-license-key)
- [2. Create a scanning session](#2-create-a-scanning-session)
- [3. Feed images to the session](#3-feed-images-to-the-session)
- [4. Read the scanning result](#4-read-the-scanning-result)
- [5. Per-side results: VIZ, MRZ, barcode](#5-per-side-results-viz-mrz-barcode)
- [6. Cropped images](#6-cropped-images)
- [Tuning `ScanningSettings`](#tuning-scanningsettings)
- [Enums: two shapes](#enums-two-shapes)
- [Errors](#errors)
- [Caveats & limitations](#caveats--limitations)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- Target framework `net10.0-ios` (single-TFM package), iOS 15.0+
- macOS host for development
- **A Microblink license key** — BlinkID is commercial. Create one on the [Microblink developer hub](https://developer.microblink.com/); keys are bound to your app's bundle identifier.

```
dotnet add package SwiftBindings.BlinkID
```

## Namespaces

```csharp
using BlinkID;              // BlinkIDSdkInfo, BlinkIDSession, BlinkIDScanningResult, settings, enums
using BlinkID.BlinkIDSDK;   // StringResult, ProcessResult, ResultCompleteness,
                            // DocumentClassInfo, InputImageAnalysisResult, ScanningStatus
using Swift;                // runtime types
using Swift.Runtime;        // SwiftException<T>, SwiftObjectHelper<T>
```

Note the three similarly-named identifiers — this is the single most confusing corner of the binding:

| Identifier | What it is |
|---|---|
| `BlinkIDSdkInfo` | the **C# class you instantiate** — the entry point, `BlinkID.BlinkIDSdkInfo` |
| `BlinkIDSdk` | the **Swift** class name only. It does not exist in C#; every C#-side use is `BlinkIDSdkInfo` |
| `BlinkIDSDK` | a **namespace** (all-caps `SDK`) holding the nested result types Swift declares inside `enum BlinkIDSDK` |

`BlinkID.BlinkIDSDK.StringResult` is the fully-qualified name of the OCR string wrapper you'll touch constantly.

The `Info` suffix is a **collision-avoidance rename**, not an upstream name. Swift declares both `class BlinkIDSdk` and `enum BlinkIDSDK` in one module; the two differ only by the casing of `Sdk`/`SDK`, so projecting both verbatim would put a type and a namespace at the same case-insensitive name inside `namespace BlinkID`. The generator disambiguates the *class* by appending `Info` and leaves the namespace alone. Only the type name moves — the method names keep their Swift spelling (`CreateBlinkIDSdkAsync`, `TerminateBlinkIDSdk`), which is why the call site reads a little oddly: `BlinkIDSdkInfo.CreateBlinkIDSdkAsync(...)`.

## Swift → C# translation rules

| Swift | C# | Rule |
|---|---|---|
| `BlinkIDSdk.createBlinkIDSdk(withSettings:)` | `BlinkIDSdkInfo.CreateBlinkIDSdkAsync(settings)` | `async throws` → `Task<T>` with an `Async` suffix and a trailing `CancellationToken`; note the type is renamed, the method is not (see [Namespaces](#namespaces)) |
| `session.process(inputImage:) async throws` | `session.ProcessAsync(inputImage, ct)` | same; cancellation is wired through to the Swift task |
| `session.getResult()` (non-async in Swift, actor-isolated) | `session.GetResultAsync()` | actor-isolated members project as `Task`-returning even when not `async` in Swift |
| `struct ScanningSettings` | `class ScanningSettings : ISwiftObject, ISwiftStruct, IDisposable` | Swift **structs** become disposable C# classes wrapping a native value buffer |
| `final class BlinkIDSdk` | `class BlinkIDSdkInfo : ISwiftObject, IDisposable` | Swift **classes** bridge to ARC; `Dispose` is optional (finalizer releases) |
| `var licenseKey: String` | `public string LicenseKey { get; set; }` | `var` → get/set property; `let` → get-only |
| `String?` / `Country?` | `string?` / `Country?` | Swift optionals → C# nullable |
| `enum ScanningMode { case single, automatic }` | `enum ScanningMode : int` | payload-free, non-`String`-raw enums → plain C# enums |
| `enum Country: String { case usa … }` | `class Country` with `.Tag` (`CaseTag`), static singletons, `RawValue`, `FromRawValue` | raw-value / payload-carrying enums → classes (see [Enums](#enums-two-shapes)) |
| `stringResult.value` **and** `stringResult.value(_ alphabet:)` | `Value` property **and** `ValueMethod(AlphabetType)` | when a property and a method share a Swift name, the method gets a `Method` suffix |
| `[FieldType]` | `IReadOnlyList<FieldType>` | Swift arrays project as read-only lists |
| `init(x: Float = 0, y: Float = 0, …)` | overload set, longest-first, trailing defaults dropped | Swift default arguments become C# overloads; only *trailing* defaults collapse |

## Object lifetime

Two lifetime classes, and the distinction matters:

- **Struct-backed types** (`ISwiftStruct`) own a native value buffer and **must be disposed**. That's nearly everything in this binding: `BlinkIDSdkSettings`, `BlinkIDSessionSettings`, `ScanningSettings`, `BlinkIDScanningResult`, `SingleSideScanningResult`, `VIZResult`, `MRZResult`, `BarcodeResult`, `StringResult`, `ProcessResult`, `ResultCompleteness`, `DocumentClassInfo`, the tag-enum classes (`Country`, `DocumentType`, `FieldType`, `AlphabetType`, `RecognitionMode`, …). Use `using var`.
- **Class-backed types** (ARC-bridged): `BlinkIDSdkInfo`, `BlinkIDSession`, `InputImage`. `Dispose` is available for determinism but the GC finalizer releases them.

Every property getter on a struct-backed type returns a **fresh copy**, so:

```csharp
// WRONG — mutates a temporary that is then discarded (and leaked).
sessionSettings.ScanningSettings.ReturnInputImages = true;

// RIGHT — read, modify, write back.
using (var scanning = sessionSettings.ScanningSettings)
{
    scanning.ReturnInputImages = true;
    sessionSettings.ScanningSettings = scanning;
}
```

Double-`Dispose` is a no-op on all generated types.

## 1. Initialize the SDK (license key)

`BlinkIDSdkSettings` carries the license key and resource-download configuration. The C# binding emits one overload per trailing-default suffix, down to a one-argument overload that takes just the license key and uses Swift's defaults for everything else:

```csharp
using BlinkID;

using var settings = new BlinkIDSdkSettings("<your-microblink-license-key>");

// Or spell out as many of Swift's defaults as you want to be explicit about —
// overloads exist at 1, 5, 6, 7, 8 and 9 arguments:
using var explicitSettings = new BlinkIDSdkSettings(
    licenseKey:          "<your-microblink-license-key>",
    licensee:            null,     // Swift default: nil
    helloLogEnabled:     false,    // Swift default: false
    downloadResources:   true,     // Swift default: true
    resourceDownloadUrl: "https://models.cdn.microblink.com/resources"); // Swift default

// Any remaining knob is a settable property:
settings.ResourceLocalFolder = "MLModels";   // Swift default
settings.MicroblinkProxyURL  = null;
using var timeout = RequestTimeout.Default;
settings.ResourceRequestTimeout = timeout;

BlinkIDSdkInfo sdk = await BlinkIDSdkInfo.CreateBlinkIDSdkAsync(settings);
```

Full property set on `BlinkIDSdkSettings`: `LicenseKey`, `Licensee`, `HelloLogEnabled`, `DownloadResources`, `ResourceDownloadUrl`, `ResourceLocalFolder`, `BundleURL` (`NSUrl?`), `ResourceRequestTimeout`, `MicroblinkProxyURL`.

Static lifecycle members on `BlinkIDSdkInfo`:

```csharp
await BlinkIDSdkInfo.RefreshLicenseLeaseAsync();
BlinkIDSdkInfo.TerminateBlinkIDSdk();
BlinkIDSdkInfo.TerminateBlinkIDSdkAndDeleteCachedResources();
```

`CreateBlinkIDSdkAsync` faults the returned `Task` on a bad key or missing resources — see [Errors](#errors).

## 2. Create a scanning session

```csharp
using var sessionSettings = new BlinkIDSessionSettings();   // Swift defaults below
// InputImageSource = Video, ScanningMode = Automatic,
// ScanningSettings = ScanningSettings(), StepTimeoutDuration = 15.0 seconds

sessionSettings.InputImageSource    = InputImageSource.Photo;   // or .Video
sessionSettings.ScanningMode        = ScanningMode.Single;      // or .Automatic
sessionSettings.StepTimeoutDuration = 20.0;

BlinkIDSession session = await sdk.CreateScanningSessionAsync(sessionSettings);
// or: await sdk.CreateScanningSessionAsync();  // uses BlinkIDSessionSettings() defaults
```

`ScanningMode.Single` scans one side; `ScanningMode.Automatic` decides how many sides the document needs. `InputImageSource.Video` expects a continuous stream (stability checks apply); `.Photo` expects one still capture.

Session control surface:

| Member | Purpose |
|---|---|
| `ProcessAsync(InputImage, ct)` | feed one frame, get a `FrameProcessResult` |
| `GetResultAsync(ct)` | pull the accumulated `BlinkIDScanningResult` |
| `ResetAsync(ct)` | discard state and start over |
| `AllowBarcodeStepAsync(ct)` | permit the barcode step to run |
| `CancelActiveProcessing()` / `ResumeActiveProcessing()` | synchronous stop/resume of in-flight work |
| `GetSessionIdAsync(ct)` / `GetSessionNumberAsync(ct)` | diagnostics |
| `Settings` | the `BlinkIDSessionSettings` this session was created with |

## 3. Feed images to the session

An `InputImage` comes from either a `UIImage` or a camera `CameraFrame`.

```csharp
using BlinkID;
using BlinkID.BlinkIDSDK;

// From a still image. RegionOfInterest defaults to the whole frame (0,0,1,1),
// in normalized coordinates.
using var roi = new RegionOfInterest();
using var input = new InputImage(uiImage, roi);

using var frame = await session.ProcessAsync(input);

if (frame.SessionError is { } sessionError)
{
    // SessionError.ProcessCallAfterDocumentScanned | .ResetCallAfterResultRetrieved
    return;
}

using var process = frame.ProcessResult;
if (process is null) return;

using var completeness = process.ResultCompleteness;
using var analysis     = process.InputImageAnalysisResult;

// Live feedback for the user while scanning:
using var detection = analysis.DocumentDetectionStatus;   // tag enum
switch (detection.Tag)
{
    case DetectionStatus.CaseTag.CameraTooFar:   /* "move closer" */ break;
    case DetectionStatus.CaseTag.CameraTooClose: /* "move back"   */ break;
    case DetectionStatus.CaseTag.Success:        /* framed well   */ break;
}

if (completeness.ScanningStatus == ScanningStatus.DocumentScanned)
{
    // ready — go to step 4
}
```

`ScanningStatus` (in the `BlinkID.BlinkIDSDK` namespace): `ScanningSideInProgress`, `ScanningBarcodeInProgress`, `SideScanned`, `DocumentScanned`, `Cancelled`.

`ResultCompleteness` also reports what has been extracted so far: `VizExtracted`, `MrzExtracted`, `BarcodeExtracted`, `DocumentImageExtracted`, `FaceImageExtracted`, `SignatureImageExtracted`.

`InputImageAnalysisResult` is the per-frame quality report: `ProcessingStatus` (tag enum — `MandatoryFieldMissing`, `ScanningWrongSide`, `UnsupportedDocument`, `AwaitingOtherSide`, …), the field lists `MissingMandatoryFields` / `ExtractedFields` / `InvalidCharacterFields` / `ExtraPresentFields` (each `IReadOnlyList<FieldType>`), the image-quality statuses `BlurDetectionStatus` / `GlareDetectionStatus` / `DocumentLightingStatus` / `DocumentHandOcclusionStatus` / `FaceDetectionStatus` / `MrzDetectionStatus` / `BarcodeDetectionStatus`, and geometry (`DocumentLocation` as `Quadrilateral?`, `DocumentOrientation`, `DocumentRotation`, `ScanningSide`, `DocumentClassInfo`).

**From a camera frame** (`CMSampleBuffer` off an `AVCaptureVideoDataOutput`):

```csharp
using var buffer = new MBSampleBufferWrapper(cmSampleBuffer);   // ObjC-bridged wrapper
using var roi    = new RegionOfInterest();
using var frame  = new CameraFrame(buffer, roi, CameraFrameVideoOrientation.Portrait);
using var input  = new InputImage(frame);

using var result = await session.ProcessAsync(input);
```

`CameraFrameVideoOrientation`: `Portrait`, `PortraitUpsideDown`, and the two landscape cases.

## 4. Read the scanning result

```csharp
using var result = await session.GetResultAsync();

// Document classification
using var classInfo = result.DocumentClassInfo;
using var country   = classInfo.Country;       // tag-enum class
using var docType   = classInfo.DocumentType;  // tag-enum class
string countryName  = classInfo.CountryName;
string iso3         = classInfo.IsoAlpha3CountryCode;

// Extracted fields — each is a StringResult? (multi-alphabet)
string? firstName  = result.FirstName?.Value;
string? lastName   = result.LastName?.Value;
string? docNumber  = result.DocumentNumber?.Value;
string? address    = result.Address?.Value;
string? sex        = result.Sex?.Value;

// Dates
using var dob = result.DateOfBirth;                 // DateResult<StringResult>?
DateTimeOffset? birthDate = dob?.Date;
int? birthYear = dob?.Year;
bool parsedOk  = dob?.SuccessfullyParsed ?? false;

// Cross-source consistency (VIZ vs MRZ vs barcode)
using var dataMatch = result.DataMatchResult;
if (dataMatch?.OverallState == DataMatchState.Success) { /* fields agree */ }

using var mode = result.RecognitionMode;   // tag enum: MrzId, MrzPassport, PhotoId, FullRecognition, …
```

**`StringResult`** is the multi-alphabet OCR wrapper. `Value` gives the best available string; the alphabet-specific accessors are `Method`-suffixed:

```csharp
using var name = result.FullName;
string? best     = name?.Value;                              // property
string  latin    = name?.ValueMethod(AlphabetType.Latin) ?? "";
ScanningSide? side = name?.SideMethod(AlphabetType.Latin);   // which side it came from
RectangleF? box    = name?.LocationMethod(AlphabetType.Latin);
```

`AlphabetType`: `Latin`, `Arabic`, `Cyrillic`, `Greek`.

`BlinkIDScanningResult` carries ~55 more `StringResult?` fields in the same shape (`Nationality`, `PlaceOfBirth`, `PersonalIdNumber`, `IssuingAuthority`, `MaidenName`, `Employer`, `BloodType`, `VisaType`, `StateName`, …), plus `DateOfIssue` / `DateOfExpiry` / `DateOfEntry` / `EffectiveDate`, `DateOfExpiryPermanent` (`bool?`), `DriverLicenseDetailedInfo`, `DependentsInfo`, and `ParentsInfo`.

## 5. Per-side results: VIZ, MRZ, barcode

`result.SubResults` is one `SingleSideScanningResult` per scanned side, each exposing the raw per-source extractions:

```csharp
foreach (var side in result.SubResults)
{
    using (side)
    {
        using var viz = side.Viz;          // VIZResult?  — visual inspection zone (OCR)
        using var mrz = side.Mrz;          // MRZResult?  — machine-readable zone
        using var bar = side.Barcode;      // BarcodeResult?

        string? vizName = viz?.FirstName?.Value;

        if (mrz is not null)
        {
            string raw       = mrz.RawMRZString;
            string number    = mrz.DocumentNumber;
            string issuer    = mrz.IssuerName;
            bool   checksOk  = mrz.Verified;
            var    mrzType   = mrz.DocumentType;   // MRZDocumentType (plain enum)
            using var mrzDob = mrz.DateOfBirth;    // DateResult<SwiftString>
        }

        if (bar is not null && bar.Parsed)
        {
            string barName = bar.FullName;
            string barAddr = bar.Address;
            using var barcodeData = bar.BarcodeData;
        }
    }
}
```

Note the shape difference: **`VIZResult` fields are `StringResult?`** (optional, multi-alphabet, with `.Value`), while **`MRZResult` and `BarcodeResult` fields are plain non-nullable `string`** — empty when absent. Their date fields are `DateResult<Swift.SwiftString>` rather than `DateResult<StringResult>`.

`MRZDocumentType` is a plain enum: `Unknown`, `IdentityCard`, `Passport`, `Visa`, `GreenCard`, `MysPassIMM13P`, `DriverLicense`, `InternalTravelDocument`, `BorderCrossingCard`.

## 6. Cropped images

Images are off by default — enable them in `CroppedImageSettings` before scanning, then read them from the result:

```csharp
using (var scanning = sessionSettings.ScanningSettings)
using (var images   = new CroppedImageSettings(
           dotsPerInch:          250,
           extensionFactor:      0f,
           returnDocumentImage:  true,
           returnFaceImage:      true,
           returnSignatureImage: false))
{
    scanning.CroppedImageSettings = images;
    scanning.ReturnInputImages    = true;   // also keep the full input frames
    sessionSettings.ScanningSettings = scanning;
}

// …after scanning:
using var face     = result.GetFaceImage();                       // DetailedCroppedImageResult?
using var docImage = result.GetDocumentImage(ScanningSide.First);  // CroppedImageResult?
using var fullFrame= result.GetInputImage(ScanningSide.First);     // InputImageResult?

UIImage? faceUi   = face?.UiImage;
byte[]?  faceRaw  = face?.RawData;
RectangleF? where = face?.Location;
```

Also available: `result.GetSignatureImage()` and `result.GetBarcodeInputImage()`. The same images hang off each `SingleSideScanningResult` as `DocumentImage`, `FaceImage`, `SignatureImage`, `InputImage`, `BarcodeInputImage`.

## Tuning `ScanningSettings`

`ScanningSettings` now emits Swift's no-argument `init()` as a real parameterless C# constructor (`new ScanningSettings()`), alongside the long explicit overloads. In practice you still rarely construct one: taking the default instance out of `BlinkIDSessionSettings` and mutating it is the shorter path.

```csharp
using (var s = sessionSettings.ScanningSettings)
{
    s.BlurDetectionLevel  = DetectionLevel.High;   // Off | Low | Mid | High
    s.SkipImagesWithBlur  = true;
    s.GlareDetectionLevel = DetectionLevel.Mid;
    s.SkipImagesWithGlare = true;
    s.TiltDetectionLevel  = DetectionLevel.Mid;
    s.SkipImagesWithInadequateLightingConditions = true;
    s.SkipImagesOccludedByHand = true;

    s.CombineResultsFromMultipleInputImages = true;
    s.EnableCharacterValidation             = true;
    s.MaxAllowedMismatchesPerField          = 0;
    s.InputImageMargin                      = 0.02f;
    s.ScanPassportDataPageOnly              = true;
    s.ScanUnsupportedBack                   = false;
    s.AllowUncertainFrontSideScan           = false;
    s.EnableBarcodeScanOnly                 = false;

    using var anonymization = AnonymizationMode.FullResult;  // None | ImageOnly | ResultFieldsOnly | FullResult
    s.AnonymizationMode = anonymization;

    using var filter = new RecognitionModeFilter(
        enableMrzId: true, enableMrzVisa: true, enableMrzPassport: true,
        enablePhotoId: true, enableBarcodeId: true, enableFullDocumentRecognition: true);
    s.RecognitionModeFilter = filter;

    sessionSettings.ScanningSettings = s;
}
```

Restricting which documents are accepted, or which fields are required, goes through `DocumentFilter` + `DocumentRules`:

```csharp
using var usa    = Country.Usa;
using var ca     = Region.California;
using var filter = new DocumentFilter(country: usa, region: ca, documentType: null);

// Require first + last name in Latin script on matching documents.
using var latin     = AlphabetType.Latin;
using var firstName = new DetailedFieldType(FieldType.FirstName, latin);
using var lastName  = new DetailedFieldType(FieldType.LastName,  latin);
using var rules     = new DocumentRules(filter, new[] { firstName, lastName });

using (var s = sessionSettings.ScanningSettings)
{
    s.CustomDocumentRules = new[] { rules };
    sessionSettings.ScanningSettings = s;
}
```

`DocumentAnonymizationSettings(documentFilter, fields, documentNumberAnonymizationSettings)` follows the same pattern for `CustomDocumentAnonymizationSettings`.

## Enums: two shapes

**Plain C# enums** — payload-free Swift enums without a `String` raw value:

`ScanningMode`, `ScanningSide`, `InputImageSource`, `DetectionLevel`, `DataMatchState`, `DataMatchFieldType`, `MRZDocumentType`, `BarcodeType`, `BarcodeElementKey`, `ImageExtractionType`, `DocumentRotation`, `CameraFrameVideoOrientation`, `SessionError`, `ImageAnalysisDetectionStatus`, `ImageAnalysisLightingStatus`, `DocumentImageColorStatus`, `DocumentOrientation`, and `BlinkIDSDK.ScanningStatus`.

The ones Swift declares as `CaseIterable` also get an `AllCases` list on a generated `<Name>Extensions` class — that's `BarcodeType`, `DataMatchFieldType`, `DataMatchState`, `DetectionLevel`, `MRZDocumentType`, `ScanningMode` and `ScanningSide`. The rest are plain enums with no companion class; use `Enum.GetValues<T>()` there.

```csharp
IReadOnlyList<DetectionLevel> levels = DetectionLevelExtensions.AllCases;   // 4
IReadOnlyList<ScanningMode>   modes  = ScanningModeExtensions.AllCases;     // 2

DocumentRotation[] rotations = Enum.GetValues<DocumentRotation>();          // no Extensions class
```

**Tag-enum classes** — Swift enums with a `String` raw value or associated values become disposable classes:

`Country` (257 cases), `Region`, `DocumentType`, `FieldType` (72 cases), `AlphabetType`, `RecognitionMode`, `AnonymizationMode`, `ProcessingStatus`, `DetectionStatus`, plus the error types.

(`DetailedFieldType` is *not* one of these — despite the name it's a two-field struct, `new DetailedFieldType(fieldType, alphabetType)`, used to build `DocumentRules`.)

They share one shape:

```csharp
using var germany = Country.Germany;              // static singleton per case
Country.CaseTag tag = germany.Tag;                // == Country.CaseTag.Germany
string raw = germany.RawValue;                    // Swift raw value, e.g. "germany"

Country? parsed = Country.FromRawValue(raw);      // null for an unknown raw value
IReadOnlyList<AnonymizationMode> all = AnonymizationMode.AllCases;

if (germany == parsed) { /* value equality via IEquatable<T> and ==/!= */ }
```

Cases with associated values get static factories plus `TryGet*` accessors:

```csharp
using var err = ResourceDownloaderError.DownloadFailed(500);
if (err.Tag == ResourceDownloaderError.CaseTag.DownloadFailed &&
    err.TryGetDownloadFailed(out var statusCode))
{
    // statusCode == 500
}
```

## Errors

Throwing Swift APIs surface as **typed** `Swift.Runtime.SwiftException<TError>`, carrying the Swift error value:

```csharp
using Swift.Runtime;

try
{
    sdk = await BlinkIDSdkInfo.CreateBlinkIDSdkAsync(settings);
}
catch (SwiftException<InvalidLicenseKeyError> ex)
{
    string detail = ex.Error?.Message ?? ex.Message;
}
catch (SwiftException<MissingResources>)         { /* model files not present */ }
catch (SwiftException<MissingBundle>)            { /* bundleURL required but not set */ }
catch (SwiftException<ResourcesError> ex)
{
    // ex.Error?.Tag: CorruptedAssets | ResourceDownload | InvalidBundle
}
catch (SwiftException<ResourceDownloaderError> ex)
{
    // ex.Error?.Tag: InvalidURL | DownloadFailed | FileNotFound | HashMismatch |
    //   FileAccessError | FileCreationError | CacheDirNotFound | NoInternetConnection |
    //   InvalidResponse | ResourceUnavailable | TimedOut | …
}
catch (SwiftException<SDKInitError> ex)
{
    // ex.Error?.Tag: ResourceLoad | MemoryReserve | LicenseError
    // ex.Error?.ErrorDescription for a human-readable string
}
catch (SwiftException ex)                        { /* untyped fallback */ }
```

`SwiftException<TError>.Error` is nullable (`TError?`) — it falls back to `null` in the rare case the Swift error box can't be cast; `Exception.Message` is always populated.

Mapped error types: `InvalidLicenseKeyError`, `MissingBundle`, `MissingResources`, `ModelLoadError`, `ResourceDownloaderError`, `ResourcesError`, `SDKInitError`, `SessionError`.

Session-level misuse is **not** thrown — it arrives as `FrameProcessResult.SessionError`, a plain enum with two cases: `ProcessCallAfterDocumentScanned` and `ResetCallAfterResultRetrieved`. Check it on every frame.

Cancellation works normally: pass a `CancellationToken` to any `*Async` method and the cancel propagates into the Swift task.

## Caveats & limitations

- **No camera or UI in this package.** `InputImage` is the only entry point; you supply frames yourself (or use [BlinkIDUX](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkIDUX)).
- **Struct getters return copies.** Mutating `x.Y.Z` silently does nothing and leaks. Always read into a `using var`, mutate, assign back.
- **The binding is generated with `SwiftWrapperRequired=false`** because BlinkID is a closed-source binary framework whose internal types defeat wrapper compilation. This is expected and does not affect the surface documented here.
- **Repo test coverage is API-shape, not end-to-end.** The in-repo test app (`libraries/BlinkID/tests/`) validates type metadata, enum round-trips (`RawValue` ↔ `FromRawValue`), tag dispatch, error construction, property access, and dispose/memory-pressure cycles — all passing, no skips. It does **not** run a live scan, because that needs a real license key and a physical document. The pipeline shape above is derived from the generated surface and Microblink's own Swift API; validate it against a real key in your app.
- **No `SB0001` stubs.** No member in this binding is emitted as an `[Obsolete("SB0001")]` missing-wrapper stub — the concrete surface is fully callable. The only stubs are four `SB0003` *protocol-proxy* members that can't be dispatched through a witness table: `InputImageResultProtocolProxy.RawData`, and `SdkSettingsProxy.Licensee` / `.BundleURL` / `.ResourceRequestTimeout`. Call those on the concrete type (`InputImageResult`, `BlinkIDSdkSettings`) instead of through the protocol.

## Reference links

For full API semantics — what each field means, which documents are supported, how licensing and resource downloads work — consult Microblink's own documentation. This guide only teaches the Swift→C# translation.

- [**BlinkID documentation**](https://docs.microblink.com/blinkid) — product overview, concepts, supported documents
- [**BlinkID quickstart**](https://docs.microblink.com/blinkid/quickstart) — integration walkthrough
- [**Microblink SDKs**](https://docs.microblink.com/sdks) — platform SDK docs including iOS
- [**Microblink developer hub**](https://developer.microblink.com/) — create and manage license keys
- [**blinkid-ios** (upstream GitHub)](https://github.com/microblink/blinkid-ios) — the Swift package these bindings are generated from
- [**blinkid-ios wiki**](https://github.com/BlinkID/blinkid-ios/wiki) — integration guides and tips
- [**BlinkID release notes**](https://docs.microblink.com/blinkid/release-notes)
- [**SwiftBindings.BlinkIDUX guide**](https://github.com/justinwojo/swift-dotnet-packages/wiki/BlinkIDUX) — the prebuilt camera scanning UX layered on this package
