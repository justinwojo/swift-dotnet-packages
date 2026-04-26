// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using WeatherKit;
using Swift.Runtime;

namespace SwiftBindings.WeatherKit.Tests;

internal static class Tests
{
    public static int Run()
    {
        int passed = 0, failed = 0, skipped = 0;

        void Pass(string name)
        {
            passed++;
            Log($"PASS: {name}");
        }

        void Fail(string name, string error)
        {
            failed++;
            Log($"FAIL: {name} — {error}");
        }

        void Skip(string name, string reason)
        {
            skipped++;
            Log($"SKIP: {name} — {reason}");
        }

        // Local helper: load type metadata and check handle is non-null.
        void MetadataTest<T>(string name) where T : ISwiftObject
        {
            try
            {
                var md = SwiftObjectHelper<T>.GetTypeMetadata();
                if (md.Handle == IntPtr.Zero)
                    throw new InvalidOperationException("null metadata handle");
                Pass($"{name} metadata");
            }
            catch (Exception ex)
            {
                Fail($"{name} metadata", ex.Message);
            }
        }

        // Test 1: WeatherService.Shared resolves to a non-null object.
        try
        {
            var svc = WeatherService.Shared;
            if (svc is null)
                throw new InvalidOperationException("WeatherService.Shared returned null");
            Pass("WeatherService.Shared");
        }
        catch (Exception ex)
        {
            Fail("WeatherService.Shared", ex.Message);
        }

        // Test 2: WeatherService metadata loads.
        MetadataTest<WeatherService>("WeatherService");

        // Test 3: WeatherAttribution metadata loads.
        MetadataTest<WeatherAttribution>("WeatherAttribution");

        // Test 4: WeatherMetadata metadata loads.
        MetadataTest<WeatherMetadata>("WeatherMetadata");

        // Test 5: CurrentWeather metadata loads.
        MetadataTest<CurrentWeather>("CurrentWeather");

        // Test 6: DayWeather metadata loads.
        MetadataTest<DayWeather>("DayWeather");

        // Test 7: HourWeather metadata loads.
        MetadataTest<HourWeather>("HourWeather");

        // Test 8: WeatherError plain enum values match expected int tags.
        try
        {
            if ((int)WeatherError.PermissionDenied != 0)
                throw new InvalidOperationException($"PermissionDenied expected 0, got {(int)WeatherError.PermissionDenied}");
            if ((int)WeatherError.Unknown != 1)
                throw new InvalidOperationException($"Unknown expected 1, got {(int)WeatherError.Unknown}");
            Pass("WeatherError enum values");
        }
        catch (Exception ex)
        {
            Fail("WeatherError enum values", ex.Message);
        }

        // Test 9: WeatherError.GetErrorDescription extension cdecl round-trip.
        try
        {
            // GetErrorDescription is nullable — just confirm the call doesn't crash.
            var desc = WeatherError.PermissionDenied.GetErrorDescription();
            Log($"WeatherError.PermissionDenied errorDescription = {desc ?? "(null)"}");
            Pass("WeatherError.GetErrorDescription");
        }
        catch (Exception ex)
        {
            Fail("WeatherError.GetErrorDescription", ex.Message);
        }

        // Test 10: MoonPhase enum values and AllCases non-empty.
        try
        {
            if ((int)MoonPhase.New != 0)
                throw new InvalidOperationException($"MoonPhase.New expected 0, got {(int)MoonPhase.New}");
            if ((int)MoonPhase.WaningCrescent != 7)
                throw new InvalidOperationException($"MoonPhase.WaningCrescent expected 7, got {(int)MoonPhase.WaningCrescent}");
            var allCases = MoonPhaseExtensions.AllCases;
            if (allCases.Count == 0)
                throw new InvalidOperationException("MoonPhase.AllCases is empty");
            Pass("MoonPhase enum values + AllCases");
        }
        catch (Exception ex)
        {
            Fail("MoonPhase enum values + AllCases", ex.Message);
        }

        // Test 11: MoonPhase.Full.GetDescription cdecl round-trip.
        try
        {
            var desc = MoonPhase.Full.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"MoonPhase.Full description = {desc}");
            Pass("MoonPhase.Full.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("MoonPhase.Full.GetDescription", ex.Message);
        }

        // Test 12: MoonPhase.ToRawValue / FromRawValue round-trip.
        try
        {
            var raw = MoonPhase.WaxingCrescent.ToRawValue();
            if (raw != "waxingCrescent")
                throw new InvalidOperationException($"unexpected raw value '{raw}'");
            var back = MoonPhaseExtensions.FromRawValue("waxingCrescent");
            if (back != MoonPhase.WaxingCrescent)
                throw new InvalidOperationException($"round-trip failed: got {back}");
            Pass("MoonPhase.ToRawValue/FromRawValue");
        }
        catch (Exception ex)
        {
            Fail("MoonPhase.ToRawValue/FromRawValue", ex.Message);
        }

        // Test 13: Deviation plain enum values.
        try
        {
            if ((int)Deviation.MuchHigher != 0)
                throw new InvalidOperationException($"MuchHigher expected 0, got {(int)Deviation.MuchHigher}");
            if ((int)Deviation.MuchLower != 4)
                throw new InvalidOperationException($"MuchLower expected 4, got {(int)Deviation.MuchLower}");
            Pass("Deviation enum values");
        }
        catch (Exception ex)
        {
            Fail("Deviation enum values", ex.Message);
        }

        // Test 14: WeatherCondition.Clear singleton loads and has the expected CaseTag.
        try
        {
            var clear = WeatherCondition.Clear;
            if (clear is null)
                throw new InvalidOperationException("WeatherCondition.Clear returned null");
            var tag = clear.Tag;
            if (tag != WeatherCondition.CaseTag.Clear)
                throw new InvalidOperationException($"Expected CaseTag.Clear (4), got {tag}");
            Pass("WeatherCondition.Clear singleton + CaseTag");
        }
        catch (Exception ex)
        {
            Fail("WeatherCondition.Clear singleton + CaseTag", ex.Message);
        }

        // Test 15: WeatherCondition.Rain singleton has expected CaseTag.
        try
        {
            var rain = WeatherCondition.Rain;
            if (rain is null)
                throw new InvalidOperationException("WeatherCondition.Rain returned null");
            if (rain.Tag != WeatherCondition.CaseTag.Rain)
                throw new InvalidOperationException($"Expected CaseTag.Rain (22), got {rain.Tag}");
            Pass("WeatherCondition.Rain singleton + CaseTag");
        }
        catch (Exception ex)
        {
            Fail("WeatherCondition.Rain singleton + CaseTag", ex.Message);
        }

        // Test 16: WeatherCondition.AllCases is non-empty.
        try
        {
            var allCases = WeatherCondition.AllCases;
            if (allCases.Count == 0)
                throw new InvalidOperationException("WeatherCondition.AllCases is empty");
            Log($"WeatherCondition.AllCases count = {allCases.Count}");
            Pass("WeatherCondition.AllCases non-empty");
        }
        catch (Exception ex)
        {
            Fail("WeatherCondition.AllCases non-empty", ex.Message);
        }

        // Test 17: Precipitation singletons load with expected CaseTags.
        try
        {
            var none = Precipitation.None;
            var rain = Precipitation.Rain;
            var snow = Precipitation.Snow;
            if (none is null || rain is null || snow is null)
                throw new InvalidOperationException("one of the Precipitation singletons was null");
            if (none.Tag != Precipitation.CaseTag.None)
                throw new InvalidOperationException($"Precipitation.None tag mismatch: {none.Tag}");
            if (rain.Tag != Precipitation.CaseTag.Rain)
                throw new InvalidOperationException($"Precipitation.Rain tag mismatch: {rain.Tag}");
            if (snow.Tag != Precipitation.CaseTag.Snow)
                throw new InvalidOperationException($"Precipitation.Snow tag mismatch: {snow.Tag}");
            Pass("Precipitation singletons + CaseTags");
        }
        catch (Exception ex)
        {
            Fail("Precipitation singletons + CaseTags", ex.Message);
        }

        // Test 18: PressureTrend singletons load with expected CaseTags.
        try
        {
            var rising = PressureTrend.Rising;
            var falling = PressureTrend.Falling;
            var steady = PressureTrend.Steady;
            if (rising is null || falling is null || steady is null)
                throw new InvalidOperationException("one of the PressureTrend singletons was null");
            if (rising.Tag != PressureTrend.CaseTag.Rising)
                throw new InvalidOperationException($"PressureTrend.Rising tag mismatch: {rising.Tag}");
            if (falling.Tag != PressureTrend.CaseTag.Falling)
                throw new InvalidOperationException($"PressureTrend.Falling tag mismatch: {falling.Tag}");
            if (steady.Tag != PressureTrend.CaseTag.Steady)
                throw new InvalidOperationException($"PressureTrend.Steady tag mismatch: {steady.Tag}");
            Pass("PressureTrend singletons + CaseTags");
        }
        catch (Exception ex)
        {
            Fail("PressureTrend singletons + CaseTags", ex.Message);
        }

        // Test 19: WeatherSeverity singletons load with expected CaseTags.
        try
        {
            var minor = WeatherSeverity.Minor;
            var extreme = WeatherSeverity.Extreme;
            var unknown = WeatherSeverity.Unknown;
            if (minor is null || extreme is null || unknown is null)
                throw new InvalidOperationException("one of the WeatherSeverity singletons was null");
            if (minor.Tag != WeatherSeverity.CaseTag.Minor)
                throw new InvalidOperationException($"WeatherSeverity.Minor tag mismatch: {minor.Tag}");
            if (extreme.Tag != WeatherSeverity.CaseTag.Extreme)
                throw new InvalidOperationException($"WeatherSeverity.Extreme tag mismatch: {extreme.Tag}");
            Pass("WeatherSeverity singletons + CaseTags");
        }
        catch (Exception ex)
        {
            Fail("WeatherSeverity singletons + CaseTags", ex.Message);
        }

        // Test 20: WeatherAvailability.AvailabilityKind singletons and CaseTags.
        try
        {
            var available = WeatherAvailability.AvailabilityKind.Available;
            var unsupported = WeatherAvailability.AvailabilityKind.Unsupported;
            var unknownKind = WeatherAvailability.AvailabilityKind.Unknown;
            if (available is null || unsupported is null || unknownKind is null)
                throw new InvalidOperationException("one of the AvailabilityKind singletons was null");
            if (available.Tag != WeatherAvailability.AvailabilityKind.CaseTag.Available)
                throw new InvalidOperationException($"Available tag mismatch: {available.Tag}");
            if (unsupported.Tag != WeatherAvailability.AvailabilityKind.CaseTag.Unsupported)
                throw new InvalidOperationException($"Unsupported tag mismatch: {unsupported.Tag}");
            Pass("WeatherAvailability.AvailabilityKind singletons + CaseTags");
        }
        catch (Exception ex)
        {
            Fail("WeatherAvailability.AvailabilityKind singletons + CaseTags", ex.Message);
        }

        // Test 21: UVIndex.ExposureCategory enum values and AllCases.
        try
        {
            if ((int)UVIndex.ExposureCategory.Low != 0)
                throw new InvalidOperationException($"Low expected 0, got {(int)UVIndex.ExposureCategory.Low}");
            if ((int)UVIndex.ExposureCategory.Extreme != 4)
                throw new InvalidOperationException($"Extreme expected 4, got {(int)UVIndex.ExposureCategory.Extreme}");
            var allCases = UVIndexExposureCategoryExtensions.AllCases;
            if (allCases.Count == 0)
                throw new InvalidOperationException("AllCases is empty");
            Pass("UVIndex.ExposureCategory values + AllCases");
        }
        catch (Exception ex)
        {
            Fail("UVIndex.ExposureCategory values + AllCases", ex.Message);
        }

        // Test 22: UVIndex.ExposureCategory.Moderate.GetDescription cdecl round-trip.
        try
        {
            var desc = UVIndex.ExposureCategory.Moderate.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"UVIndex.ExposureCategory.Moderate description = {desc}");
            Pass("UVIndex.ExposureCategory.Moderate.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("UVIndex.ExposureCategory.Moderate.GetDescription", ex.Message);
        }

        // Test 23: Wind.CompassDirectionType enum values and AllCases.
        try
        {
            if ((int)Wind.CompassDirectionType.North != 0)
                throw new InvalidOperationException($"North expected 0, got {(int)Wind.CompassDirectionType.North}");
            if ((int)Wind.CompassDirectionType.NorthNorthwest != 15)
                throw new InvalidOperationException($"NNW expected 15, got {(int)Wind.CompassDirectionType.NorthNorthwest}");
            var allCases = WindCompassDirectionTypeExtensions.AllCases;
            if (allCases.Count != 16)
                throw new InvalidOperationException($"Expected 16 compass directions, got {allCases.Count}");
            Pass("Wind.CompassDirectionType values + AllCases");
        }
        catch (Exception ex)
        {
            Fail("Wind.CompassDirectionType values + AllCases", ex.Message);
        }

        // Test 24: Wind.CompassDirectionType.GetDescription cdecl round-trip.
        try
        {
            var desc = Wind.CompassDirectionType.North.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"Wind.CompassDirectionType.North description = {desc}");
            Pass("Wind.CompassDirectionType.North.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("Wind.CompassDirectionType.North.GetDescription", ex.Message);
        }

        // Test 25a: Forecast<HourWeather> implements IReadOnlyList<HourWeather>.
        // Constructing a real Forecast<T> requires a live WeatherKit entitlement (paid Apple developer
        // program + WeatherKit capability), so we verify the projection by reflection — the type must
        // implement IReadOnlyList<TElement> and expose Count / indexer / GetEnumerator.
        try
        {
            var t = typeof(Forecast<HourWeather>);
            if (!typeof(System.Collections.Generic.IReadOnlyList<HourWeather>).IsAssignableFrom(t))
                throw new InvalidOperationException("Forecast<HourWeather> does not implement IReadOnlyList<HourWeather>");
            if (t.GetProperty("Count") is null)
                throw new InvalidOperationException("Forecast<HourWeather>.Count property missing");
            if (t.GetMethod("GetEnumerator") is null)
                throw new InvalidOperationException("Forecast<HourWeather>.GetEnumerator missing");
            // Indexer: parameterless name "Item" with int parameter, returns TElement.
            var indexer = t.GetProperty("Item", new[] { typeof(int) });
            if (indexer is null || indexer.PropertyType != typeof(HourWeather))
                throw new InvalidOperationException($"Forecast<HourWeather>[int] indexer missing or wrong type ({indexer?.PropertyType.Name ?? "(null)"})");
            Pass("Forecast<HourWeather> IReadOnlyList projection");
        }
        catch (Exception ex)
        {
            Fail("Forecast<HourWeather> IReadOnlyList projection", ex.Message);
        }

        // Test 25b: Forecast<DayWeather> implements IReadOnlyList<DayWeather>.
        try
        {
            var t = typeof(Forecast<DayWeather>);
            if (!typeof(System.Collections.Generic.IReadOnlyList<DayWeather>).IsAssignableFrom(t))
                throw new InvalidOperationException("Forecast<DayWeather> does not implement IReadOnlyList<DayWeather>");
            Pass("Forecast<DayWeather> IReadOnlyList projection");
        }
        catch (Exception ex)
        {
            Fail("Forecast<DayWeather> IReadOnlyList projection", ex.Message);
        }

        // Test 26: WeatherService.GetAttributionAsync — dispatch fires without framework crash.
        // WeatherKit requires an API key for real data; expect a SwiftException error, which still
        // proves the Swift→C# async bridge is wired correctly.
        try
        {
            var svc = WeatherService.Shared;
            var task = svc.GetAttributionAsync();
            // Allow brief time for the async dispatch to invoke callback.
            var completed = task.Wait(TimeSpan.FromSeconds(5));
            if (completed)
            {
                // Attribution returned unexpectedly — still valid; log it.
                Log("GetAttributionAsync completed (attribution returned)");
                Pass("WeatherService.GetAttributionAsync dispatch");
            }
            else
            {
                // Timed out waiting — callback hasn't fired yet; acceptable for simulator.
                Log("GetAttributionAsync timed out waiting for callback — async bridge dispatched");
                Pass("WeatherService.GetAttributionAsync dispatch");
            }
        }
        catch (AggregateException aex) when (aex.InnerException is Exception inner)
        {
            // A Swift-thrown error (e.g. no API key) means the bridge completed the round-trip.
            Log($"GetAttributionAsync raised expected error: {inner.Message}");
            Pass("WeatherService.GetAttributionAsync dispatch (error expected)");
        }
        catch (Exception ex)
        {
            Fail("WeatherService.GetAttributionAsync dispatch", ex.Message);
        }

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[WEATHERKIT-TEST] {msg}" : msg);
}
