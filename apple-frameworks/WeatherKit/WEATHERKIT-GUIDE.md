# WeatherKit for .NET — Usage Guide

`SwiftBindings.Apple.WeatherKit` exposes Apple's [WeatherKit](https://developer.apple.com/documentation/weatherkit) framework to C# through .NET 10's native Swift interop — current conditions, hourly and daily forecasts, severe-weather alerts, sun/moon events, and the required Apple attribution. These are direct Swift calls, not Objective-C proxy wrappers. This guide maps the Swift workflow to the generated C# surface and documents exactly what is and isn't bound today.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: fetch the weather for a location](#quick-start-fetch-the-weather-for-a-location)
- [The `WeatherService`](#the-weatherservice)
- [The `Weather` bundle](#the-weather-bundle)
- [Current conditions](#current-conditions)
- [Reading measurements](#reading-measurements)
- [Hourly & daily forecasts](#hourly--daily-forecasts)
- [Weather conditions, precipitation & pressure](#weather-conditions-precipitation--pressure)
- [Sun & moon events](#sun--moon-events)
- [Wind & UV index](#wind--uv-index)
- [Severe-weather alerts](#severe-weather-alerts)
- [Availability & metadata](#availability--metadata)
- [Attribution (required by Apple)](#attribution-required-by-apple)
- [Errors](#errors)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- Target frameworks: `net10.0-ios26.2`, `net10.0-macos26.2`, `net10.0-maccatalyst26.2`, `net10.0-tvos26.2`
- iOS 26.2+, macOS 26.2+, Mac Catalyst 26.2+, tvOS 26.2+
- macOS host for development
- A paid Apple Developer Program membership with the **WeatherKit** capability enabled. WeatherKit data is metered and entitlement-gated; calls fail without a provisioned app. The bindings themselves load and dispatch on the simulator, but live data requires the entitlement.

```
dotnet add package SwiftBindings.Apple.WeatherKit
```

```csharp
using WeatherKit;
```

> WeatherKit uses Foundation `Measurement<Unit…>` values and `CoreLocation.CLLocation`, so you'll typically also want `using CoreLocation;`. Both are pulled in transitively by the SwiftBindings runtime.

## Naming conventions

The generator applies a few consistent transforms over the Swift names. Knowing them makes the whole surface predictable:

| Swift | C# | Rule |
|---|---|---|
| `func weather(for:) async throws` | `WeatherAsync(CLLocation, CancellationToken)` | `async` methods gain an `Async` suffix, return `Task<T>`, drop the first argument label, and take a trailing defaulted `CancellationToken` |
| `WeatherService.shared` | `WeatherService.Shared` | static singletons are PascalCase properties |
| `current.dewPoint` | `current.DewPoint` | properties are PascalCase |
| `enum WeatherCondition { case clear }` (RawRepresentable + descriptions) | `WeatherCondition` **class** with static singletons (`WeatherCondition.Clear`), a `.Tag` (`CaseTag`), `.Description`, `.RawValue`, and `.AllCases` | rich Swift enums that carry behavior project to a class, not a C# `enum` |
| `enum MoonPhase: Int` | C# `enum MoonPhase : int` + `MoonPhaseExtensions` | plain integer-backed enums stay C# enums; helper methods (`GetDescription`, `ToRawValue`, `AllCases`) live on a sibling `…Extensions` static class |
| `var hourlyForecast: Forecast<HourWeather>` | `Forecast<HourWeather>` implementing `IReadOnlyList<HourWeather>` | `Forecast<T>` projects to a standard read-only list (`.Count`, indexer, `foreach`) |
| `var weatherAlerts: [WeatherAlert]?` | `IReadOnlyList<WeatherAlert>?` | optional Swift arrays project to a nullable `IReadOnlyList<>` |
| `var highTemperatureTime: Date?` | `DateTimeOffset?` | optional `Date` → nullable `DateTimeOffset` |

## Quick start: fetch the weather for a location

```csharp
using WeatherKit;
using CoreLocation;

// 1. Get the singleton service (Swift's WeatherService.shared)
var service = WeatherService.Shared;

// 2. Fetch the full weather bundle for a coordinate
using var location = new CLLocation(37.3349, -122.0090); // Apple Park
WeatherKit.Weather weather = await service.WeatherAsync(location);

// 3. Read current conditions
CurrentWeather now = weather.CurrentWeather;
Console.WriteLine($"{now.Condition.Description}");          // e.g. "Clear"
Console.WriteLine($"{now.Temperature.Value}°");            // value in the stored unit (Celsius)
Console.WriteLine($"Humidity: {now.Humidity:P0}");          // 0.0–1.0 fraction

// 4. Walk the daily forecast — Forecast<T> is an IReadOnlyList<T>
foreach (DayWeather day in weather.DailyForecast)
{
    Console.WriteLine($"{day.Date:d}: {day.Condition.Description}, " +
                      $"hi {day.HighTemperature.Value}° / lo {day.LowTemperature.Value}°");
}
```

`WeatherAsync` throws when the app lacks a WeatherKit entitlement or on network failure — see [Errors](#errors).

## The `WeatherService`

`WeatherService` is the single entry point. It's a Swift class (reference type), so it does not need disposing for correctness.

| Member | Signature | Notes |
|---|---|---|
| `WeatherService.Shared` | `static WeatherService` | the shared singleton — use this |
| `new WeatherService()` | constructor | also available if you prefer your own instance |
| `WeatherAsync(...)` | `Task<Weather> WeatherAsync(CLLocation location, CancellationToken ct = default)` | fetches everything for a location |
| `GetAttributionAsync()` | `Task<WeatherAttribution> GetAttributionAsync(CancellationToken ct = default)` | the legally-required attribution metadata |

```csharp
var service = WeatherService.Shared;
WeatherKit.Weather weather = await service.WeatherAsync(location);
```

> The variadic, query-based overloads of Swift's `weather(for:including:)` (the ones that fetch just `.current`, `.hourly`, `.daily`, etc. via `WeatherQuery<T>`) are **not** bound in this release — only the all-in-one `WeatherAsync(CLLocation)` is callable. See [Known limitations](#known-limitations). To get a single dataset, fetch the full `Weather` and read the property you need.

## The `Weather` bundle

`WeatherAsync` returns a `Weather` value aggregating every dataset:

| Property | Type | Notes |
|---|---|---|
| `CurrentWeather` | `CurrentWeather` | conditions right now |
| `MinuteForecast` | `Forecast<MinuteWeather>?` | next-hour minute-by-minute precipitation; null where unavailable |
| `HourlyForecast` | `Forecast<HourWeather>` | hour-by-hour |
| `DailyForecast` | `Forecast<DayWeather>` | day-by-day |
| `WeatherAlerts` | `IReadOnlyList<WeatherAlert>?` | active severe-weather alerts; null where none |
| `Availability` | `WeatherAvailability` | which datasets are supported at this location |

```csharp
WeatherKit.Weather weather = await service.WeatherAsync(location);

CurrentWeather now      = weather.CurrentWeather;
var hourly              = weather.HourlyForecast;       // Forecast<HourWeather>
var daily               = weather.DailyForecast;        // Forecast<DayWeather>
var minute              = weather.MinuteForecast;       // Forecast<MinuteWeather>? (may be null)
var alerts              = weather.WeatherAlerts;        // IReadOnlyList<WeatherAlert>? (may be null)
```

## Current conditions

`CurrentWeather` carries the instantaneous reading:

| Property | Type |
|---|---|
| `Date` | `DateTimeOffset` |
| `Condition` | `WeatherCondition` |
| `SymbolName` | `string` (SF Symbol name) |
| `Temperature` | `Measurement<NSUnitTemperature>` |
| `ApparentTemperature` | `Measurement<NSUnitTemperature>` |
| `DewPoint` | `Measurement<NSUnitTemperature>` |
| `Humidity` | `double` (0.0–1.0) |
| `Pressure` | `Measurement<NSUnitPressure>` |
| `PressureTrend` | `PressureTrend` |
| `CloudCover` | `double` (0.0–1.0) |
| `CloudCoverByAltitude` | `CloudCoverByAltitude` |
| `IsDaylight` | `bool` |
| `PrecipitationIntensity` | `Measurement<NSUnitSpeed>` |
| `UvIndex` | `UVIndex` |
| `Visibility` | `Measurement<NSUnitLength>` |
| `Wind` | `Wind` |
| `Metadata` | `WeatherMetadata` |

```csharp
CurrentWeather c = weather.CurrentWeather;
Console.WriteLine($"{c.Condition.Description} ({c.SymbolName})");
Console.WriteLine($"Temp {c.Temperature.Value}°, feels {c.ApparentTemperature.Value}°");
Console.WriteLine($"Daylight: {c.IsDaylight}, UV {c.UvIndex.Value} ({c.UvIndex.Category})");
```

## Reading measurements

WeatherKit returns physical quantities as Foundation `Measurement<TUnit>` (e.g. `Swift.Foundation.Measurement<Foundation.NSUnitTemperature>`). The binding exposes a single member:

```csharp
public double Value { get; }   // magnitude in the measurement's stored unit
```

WeatherKit stores values in SI/metric units (Celsius for temperature, meters for length, m/s for speed, hPa-class pressure). Read the raw magnitude with `.Value` and format/convert as your app requires:

```csharp
double celsius = weather.CurrentWeather.Temperature.Value;
double fahrenheit = celsius * 9.0 / 5.0 + 32.0;
```

> `Measurement<T>` implements `IDisposable`. When you read several measurements off a forecast element in a tight loop, wrap them in `using` (see [Memory & threading](#memory--threading)).

## Hourly & daily forecasts

Both forecast collections are `Forecast<T>`, which implements `IReadOnlyList<T>` — use `.Count`, the indexer, or `foreach`:

```csharp
var hourly = weather.HourlyForecast;     // Forecast<HourWeather>
HourWeather firstHour = hourly[0];
int hours = hourly.Count;

foreach (HourWeather h in hourly)
    Console.WriteLine($"{h.Date:t}: {h.Temperature.Value}°, {h.PrecipitationChance:P0} precip");
```

`HourWeather` properties: `Date`, `Condition`, `SymbolName`, `Temperature`, `ApparentTemperature`, `DewPoint`, `Humidity`, `IsDaylight`, `CloudCover`, `CloudCoverByAltitude`, `Precipitation`, `PrecipitationChance` (`double`), `PrecipitationAmount` (`Measurement<NSUnitLength>`), `SnowfallAmount` (`Measurement<NSUnitLength>`), `Pressure`, `PressureTrend`, `UvIndex`, `Visibility`, `Wind`.

`DayWeather` properties: `Date`, `Condition`, `SymbolName`, `HighTemperature`, `HighTemperatureTime` (`DateTimeOffset?`), `LowTemperature`, `LowTemperatureTime` (`DateTimeOffset?`), `MaximumHumidity`, `MinimumHumidity`, `Precipitation`, `PrecipitationChance`, `PrecipitationAmount`, `RainfallAmount`, `SnowfallAmount`, `PrecipitationAmountByType`, `Sun` (`SunEvents`), `Moon` (`MoonEvents`), `UvIndex`, `MaximumVisibility` / `MinimumVisibility` (`double`), `Wind`, `HighWindSpeed` (`Measurement<NSUnitSpeed>?`), `DaytimeForecast` / `OvernightForecast` (`DayPartForecast`), `RestOfDayForecast` (`DayPartForecast?`).

A `DayPartForecast` (day/night split) exposes: `CloudCover`, `CloudCoverByAltitude`, `Condition`, `HighTemperature`, `LowTemperature`, `Precipitation`, `PrecipitationAmountByType`, `PrecipitationChance`, `MaximumHumidity`, `MinimumHumidity`, `MaximumVisibility`, `MinimumVisibility`, `Wind`, `HighWindSpeed`.

```csharp
DayWeather today = weather.DailyForecast[0];
DayPartForecast night = today.OvernightForecast;
Console.WriteLine($"Tonight: {night.Condition.Description}, low {night.LowTemperature.Value}°");
if (today.RestOfDayForecast is { } rest)
    Console.WriteLine($"Rest of day: {rest.Condition.Description}");
```

## Weather conditions, precipitation & pressure

`WeatherCondition` is a projected rich Swift enum — a **class** with static singletons, not a C# `enum`. Discriminate with `.Tag` (a `WeatherCondition.CaseTag`), or read the localized text directly:

```csharp
WeatherCondition cond = weather.CurrentWeather.Condition;

string text  = cond.Description;               // localized, e.g. "Partly Cloudy"
string a11y  = cond.AccessibilityDescription;  // VoiceOver string
string raw   = cond.RawValue;                  // e.g. "partlyCloudy"
cond.ToString();                               // same as Description

if (cond.Tag == WeatherCondition.CaseTag.Rain) { /* … */ }

// Singletons & lookups
WeatherCondition clear = WeatherCondition.Clear;
WeatherCondition? parsed = WeatherCondition.FromRawValue("snow");
IReadOnlyList<WeatherCondition> all = WeatherCondition.AllCases;
```

Available singletons include `Blizzard`, `BlowingDust`, `BlowingSnow`, `Breezy`, `Clear`, `Cloudy`, `Drizzle`, `Flurries`, `Foggy`, `FreezingDrizzle`, `FreezingRain`, `Frigid`, `Hail`, `Haze`, `HeavyRain`, `HeavySnow`, `Hot`, `Hurricane`, `IsolatedThunderstorms`, `MostlyClear`, `MostlyCloudy`, `PartlyCloudy`, `Rain`, `ScatteredThunderstorms`, `Sleet`, `Smoky`, `Snow`, `StrongStorms`, `SunFlurries`, `SunShowers`, `Thunderstorms`, `TropicalStorm`, `Windy`, `WintryMix`.

`Precipitation` follows the same class-with-singletons shape: singletons `None`, `Hail`, `Mixed`, `Rain`, `Sleet`, `Snow`; members `.Tag`, `.Description`, `.AccessibilityDescription`, `.RawValue`, `.AllCases`, `Precipitation.FromRawValue(...)`.

`PressureTrend` likewise: singletons `Rising`, `Falling`, `Steady`; members `.Tag`, `.Description`, `.AccessibilityDescription`, `.RawValue`, `.AllCases`, `PressureTrend.FromRawValue(...)`.

## Sun & moon events

`DayWeather.Sun` (`SunEvents`) — all `DateTimeOffset?` (null where the event doesn't occur, e.g. polar day/night):

`AstronomicalDawn`, `NauticalDawn`, `CivilDawn`, `Sunrise`, `SolarNoon`, `Sunset`, `CivilDusk`, `NauticalDusk`, `AstronomicalDusk`, `SolarMidnight`.

`DayWeather.Moon` (`MoonEvents`):

| Property | Type |
|---|---|
| `Phase` | `MoonPhase` |
| `Moonrise` | `DateTimeOffset?` |
| `Moonset` | `DateTimeOffset?` |

`MoonPhase` is a plain C# `enum` (`New`, `WaxingCrescent`, `FirstQuarter`, `WaxingGibbous`, `Full`, `WaningGibbous`, `LastQuarter`, `WaningCrescent`). Its helpers live on `MoonPhaseExtensions`:

```csharp
MoonEvents moon = weather.DailyForecast[0].Moon;
string phase  = moon.Phase.GetDescription();              // localized
string a11y   = moon.Phase.GetAccessibilityDescription();
string symbol = moon.Phase.GetSymbolName();               // SF Symbol
string raw    = moon.Phase.ToRawValue();                  // "full"
MoonPhase? p  = MoonPhaseExtensions.FromRawValue("full");
var allPhases = MoonPhaseExtensions.AllCases;

SunEvents sun = weather.DailyForecast[0].Sun;
if (sun.Sunrise is { } rise) { /* DateTimeOffset */ }
```

## Wind & UV index

`Wind`:

| Property | Type |
|---|---|
| `CompassDirection` | `Wind.CompassDirectionType` |
| `Direction` | `Measurement<NSUnitAngle>` |
| `Speed` | `Measurement<NSUnitSpeed>` |
| `Gust` | `Measurement<NSUnitSpeed>?` |

`Wind.CompassDirectionType` is a C# `enum` with all 16 points (`North`=0 … `NorthNorthwest`=15). Helpers live on `WindCompassDirectionTypeExtensions`:

```csharp
Wind wind = weather.CurrentWeather.Wind;
Console.WriteLine($"{wind.Speed.Value} m/s from {wind.CompassDirection.GetDescription()}");
string abbr = wind.CompassDirection.GetAbbreviation();    // e.g. "NNW"
var allDirs = WindCompassDirectionTypeExtensions.AllCases; // 16 entries
```

`UVIndex`:

| Property | Type |
|---|---|
| `Value` | `int` |
| `Category` | `UVIndex.ExposureCategory` |

`UVIndex.ExposureCategory` is a C# `enum` (`Low`=0, `Moderate`, `High`, `VeryHigh`, `Extreme`=4) with helpers on `UVIndexExposureCategoryExtensions`:

```csharp
UVIndex uv = weather.CurrentWeather.UvIndex;
Console.WriteLine($"UV {uv.Value}: {uv.Category.GetDescription()}");
var allCats = UVIndexExposureCategoryExtensions.AllCases;
```

## Severe-weather alerts

`Weather.WeatherAlerts` is a nullable `IReadOnlyList<WeatherAlert>`. Each `WeatherAlert`:

| Property | Type | Notes |
|---|---|---|
| `Summary` | `string` | human-readable summary |
| `Source` | `string` | issuing agency |
| `Region` | `string?` | affected region |
| `Severity` | `WeatherSeverity` | severity level |
| `DetailsURL` | `Foundation.NSUrl` | link to full details |
| `Metadata` | `WeatherMetadata` | data provenance |

```csharp
if (weather.WeatherAlerts is { } alerts)
{
    foreach (WeatherAlert alert in alerts)
    {
        Console.WriteLine($"[{alert.Severity.Description}] {alert.Summary} — {alert.Source}");
        Console.WriteLine(alert.DetailsURL.AbsoluteString);
        if (alert.Region is { } region) Console.WriteLine($"Region: {region}");
    }
}
```

`WeatherSeverity` is a projected rich enum (class): singletons `Minor`, `Moderate`, `Severe`, `Extreme`, `Unknown`; members `.Tag`, `.Description`, `.AccessibilityDescription`, `.RawValue`, `.AllCases`, `WeatherSeverity.FromRawValue(...)`.

## Availability & metadata

`Weather.Availability` (`WeatherAvailability`) tells you which datasets the location supports:

| Property | Type |
|---|---|
| `MinuteAvailability` | `WeatherAvailability.AvailabilityKind` |
| `AlertAvailability` | `WeatherAvailability.AvailabilityKind` |

`AvailabilityKind` is a projected rich enum (class): singletons `Available`, `TemporarilyUnavailable`, `Unsupported`, `Unknown`; members `.Tag` (`CaseTag`: `Available`=0, `TemporarilyUnavailable`, `Unsupported`, `Unknown`), `.RawValue`, `AvailabilityKind.FromRawValue(...)`.

```csharp
if (weather.Availability.MinuteAvailability.Tag ==
    WeatherKit.WeatherAvailability.AvailabilityKind.CaseTag.Available)
{
    var minute = weather.MinuteForecast; // safe to expect data
}
```

`WeatherMetadata` (carried by `CurrentWeather`, `WeatherAlert`, etc.) describes data provenance:

| Property | Type |
|---|---|
| `Date` | `DateTimeOffset` (when the data is valid) |
| `ExpirationDate` | `DateTimeOffset` (cache it until this time) |
| `Location` | `CoreLocation.CLLocation` |

## Attribution (required by Apple)

Apple's WeatherKit terms require you to display the Apple Weather logo and a link to the legal/data-source page. Fetch `WeatherAttribution` via the service:

```csharp
WeatherAttribution attribution = await service.GetAttributionAsync();

string name = attribution.ServiceName;                  // "Apple Weather"
string text = attribution.LegalAttributionText;
Foundation.NSUrl legal      = attribution.LegalPageURL;
Foundation.NSUrl squareMark = attribution.SquareMarkURL;        // logo (square)
Foundation.NSUrl darkMark   = attribution.CombinedMarkDarkURL;  // logo for dark UI
Foundation.NSUrl lightMark  = attribution.CombinedMarkLightURL; // logo for light UI
```

Download the appropriate mark image for your UI's appearance and link it to `LegalPageURL`.

## Errors

`WeatherAsync` and `GetAttributionAsync` are `async throws` in Swift; the binding surfaces a thrown Swift error as a faulted `Task`. Awaiting rethrows it as a `Swift.Runtime.SwiftException` (or a typed `SwiftException<TError>`); a synchronous `.Wait()`/`.Result` wraps it in an `AggregateException`.

```csharp
using Swift.Runtime;

try
{
    var weather = await service.WeatherAsync(location);
}
catch (SwiftException ex)
{
    // No WeatherKit entitlement, network failure, throttling, etc.
    Console.WriteLine($"WeatherKit error: {ex.Message}");
}
```

There is also a plain `WeatherError` enum (`PermissionDenied`=0, `Unknown`=1) with localized-text extension methods on `WeatherErrorExtensions`:

```csharp
string? desc   = WeatherError.PermissionDenied.GetErrorDescription();
string? reason = WeatherError.PermissionDenied.GetFailureReason();
string? help   = WeatherError.PermissionDenied.GetRecoverySuggestion();
```

## Known limitations

Some Swift APIs are not yet emitted by the generator and are therefore **not callable** from C#. Don't try to use them — they don't exist on the binding:

- **Query-based `weather(for:including:)` overloads.** Only `WeatherService.WeatherAsync(CLLocation)` is bound. The `WeatherQuery<T>` selectors (`.current`, `.hourly`, `.daily`, `.alerts`, `.minute`, `.availability`, `.changes`, `.historicalComparisons`) exist as types but the methods that consume them (the variadic `weather(for:including:)` family) are skipped (the generator reports *"closure or async in generic type member"* / *"parameter or return type not yet supported"*). Fetch the full `Weather` and read the property you need.
- **Historical statistics & summaries.** The result types (`DailyWeatherStatistics<T>`, `MonthlyWeatherStatistics<T>`, `HourlyWeatherStatistics<T>`, `DailyWeatherSummary<T>`, `DayTemperatureStatistics`, etc.) are fully emitted and implement `IReadOnlyList<T>`. However, the `WeatherService` entry-point methods that produce them (`dailyStatistics`, `monthlyStatistics`, `hourlyStatistics`, `dailySummary`) are not bound — they use Swift variadic generic parameter packs (`each ...` / `repeat each ...`) which have no C# equivalent. The types are accessible in the namespace but there is no callable service method to populate them.
- **Codable round-tripping of nested rich enums.** The synthesized `Codable` members on several value types are pruned by design (they depend on unresolvable existential `Encoder`/`Decoder` protocols). Most value types still expose hand-rolled `EncodeToJson()` / `DecodeFromJson(byte[])` helpers if you need JSON, but the per-property `encode`/`init(from:)` Swift members are not bound.

The validated test app (`apple-frameworks/WeatherKit/tests/Tests.cs`) exercises the metadata, enums, singletons, `Forecast<T>` projection, and the `GetAttributionAsync` async bridge. Real-data fetches require a live WeatherKit entitlement and are not exercised in CI.

## Memory & threading

- **Most WeatherKit types wrap Swift structs and implement `IDisposable`** (`Weather`, `CurrentWeather`, `DayWeather`, `HourWeather`, `WeatherAttribution`, `Wind`, `UVIndex`, `Forecast<T>`, `Measurement<T>`, …). For short-lived locals the finalizer cleans up, but `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

  ```csharp
  using var location = new CLLocation(lat, lon);
  using WeatherKit.Weather weather = await service.WeatherAsync(location);
  using var temp = weather.CurrentWeather.Temperature;
  double c = temp.Value;
  ```

- **`WeatherService` is a Swift class** with automatic ARC bridging; `Dispose()` is available but not required. Both `WeatherService` and `Weather` are `Sendable`, so instances may be shared across .NET threads without external synchronization.
- **Async hops.** `WeatherAsync` / `GetAttributionAsync` bridge Swift `async` to `Task<T>`; continuations resume on a thread-pool thread, so marshal back to your UI thread before touching UI. Both accept a trailing `CancellationToken` that cancels the in-flight Swift task.

## Reference links

- [Apple — WeatherKit](https://developer.apple.com/documentation/weatherkit)
- [Apple — WeatherService](https://developer.apple.com/documentation/weatherkit/weatherservice)
- [Apple — Weather (data bundle)](https://developer.apple.com/documentation/weatherkit/weather)
- [Apple — Get up-to-date weather information (WeatherKit overview)](https://developer.apple.com/weatherkit/)
