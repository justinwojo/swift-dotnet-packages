// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using WeatherKit;
using Swift.Runtime;

namespace SwiftBindings.WeatherKit.Tests;

internal static class Tests
{
    /// <summary>
    /// Awaits <paramref name="task"/> with a hard upper bound. Returns false when the bound
    /// elapsed first; the caller must treat that as a failure, never as a pass. The await is a
    /// real await (never <c>Task.Wait</c>/<c>GetAwaiter().GetResult()</c>) so a continuation that
    /// is affine to the UIKit main thread can run instead of deadlocking against a blocked runner.
    /// </summary>
    internal static async Task<bool> CompletesWithin(Task task, TimeSpan budget)
    {
        var timeout = Task.Delay(budget);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(true);
        if (!ReferenceEquals(winner, task))
            return false;
        await task.ConfigureAwait(true); // rethrow the real exception, not an AggregateException
        return true;
    }

    /// <summary>
    /// Bounded await that yields the result. Throws <see cref="TimeoutException"/> when the budget
    /// elapses, so a hung async bridge fails the test instead of being logged as a pass.
    /// </summary>
    internal static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan budget, string what)
    {
        var timeout = Task.Delay(budget);
        var winner = await Task.WhenAny(task, timeout).ConfigureAwait(true);
        if (!ReferenceEquals(winner, task))
            throw new TimeoutException($"{what} did not complete within {budget.TotalSeconds:0.#}s");
        return await task.ConfigureAwait(true);
    }

    public static async Task<int> RunAsync()
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

        // Test 23: Wind.CompassDirectionKind enum values and AllCases.
        try
        {
            if ((int)Wind.CompassDirectionKind.North != 0)
                throw new InvalidOperationException($"North expected 0, got {(int)Wind.CompassDirectionKind.North}");
            if ((int)Wind.CompassDirectionKind.NorthNorthwest != 15)
                throw new InvalidOperationException($"NNW expected 15, got {(int)Wind.CompassDirectionKind.NorthNorthwest}");
            var allCases = WindCompassDirectionKindExtensions.AllCases;
            if (allCases.Count != 16)
                throw new InvalidOperationException($"Expected 16 compass directions, got {allCases.Count}");
            Pass("Wind.CompassDirectionKind values + AllCases");
        }
        catch (Exception ex)
        {
            Fail("Wind.CompassDirectionKind values + AllCases", ex.Message);
        }

        // Test 24: Wind.CompassDirectionKind.GetDescription cdecl round-trip.
        try
        {
            var desc = Wind.CompassDirectionKind.North.GetDescription();
            if (string.IsNullOrEmpty(desc))
                throw new InvalidOperationException("empty description");
            Log($"Wind.CompassDirectionKind.North description = {desc}");
            Pass("Wind.CompassDirectionKind.North.GetDescription");
        }
        catch (Exception ex)
        {
            Fail("Wind.CompassDirectionKind.North.GetDescription", ex.Message);
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

        // Test 26: WeatherService.attribution — real bounded await asserting a documented outcome.
        //
        // Exactly three outcomes are accepted, and each one carries an assertion that could fail:
        //   (a) success  — the returned WeatherAttribution must expose readable fields:
        //                  a non-empty serviceName, a non-empty legalAttributionText, and
        //                  absolute legalPageURL / squareMarkURL values;
        //   (b) a *typed* WeatherKit error — SwiftException<WeatherError>, i.e. the framework's own
        //       documented error category (WeatherError.PermissionDenied is what a host without the
        //       com.apple.developer.weatherkit entitlement reports; WeatherError.Unknown is the only
        //       other declared case). The typed payload must actually be one of those two cases.
        //   (c) a NAMED environment skip, and only for the one environment condition that is not a
        //       binding defect: no network reachability in the simulator (NSURLErrorDomain
        //       -1009/-1005/-1004). The message must match that signature.
        // A timeout is a FAILURE (the previous version passed on it), and any other exception —
        // including an untyped SwiftException that is not the offline signature — is a FAILURE.
        try
        {
            var svc = WeatherService.Shared;
            var attribution = await WithTimeout(
                svc.GetAttributionAsync(),
                TimeSpan.FromSeconds(30),
                "WeatherService.attribution").ConfigureAwait(true);

            if (attribution is null)
                throw new InvalidOperationException("GetAttributionAsync returned a null WeatherAttribution");

            var serviceName = attribution.ServiceName;
            var legalText = attribution.LegalAttributionText;
            var legalPage = attribution.LegalPageURL;
            var squareMark = attribution.SquareMarkURL;

            if (string.IsNullOrWhiteSpace(serviceName))
                throw new InvalidOperationException("WeatherAttribution.serviceName was empty");
            if (string.IsNullOrWhiteSpace(legalText))
                throw new InvalidOperationException("WeatherAttribution.legalAttributionText was empty");
            if (legalPage is null || string.IsNullOrWhiteSpace(legalPage.AbsoluteString))
                throw new InvalidOperationException("WeatherAttribution.legalPageURL was null/empty");
            if (squareMark is null || string.IsNullOrWhiteSpace(squareMark.AbsoluteString))
                throw new InvalidOperationException("WeatherAttribution.squareMarkURL was null/empty");

            Log($"attribution.serviceName = '{serviceName}'");
            Log($"attribution.legalPageURL = {legalPage.AbsoluteString}");
            Log($"attribution.legalAttributionText = '{legalText}'");
            Pass("WeatherService.attribution returns a readable WeatherAttribution");
        }
        catch (SwiftException<WeatherError> typed)
        {
            // (b) The documented WeatherKit error category. Assert the payload really is one of the
            // two declared cases — a typed exception carrying an out-of-range tag is a marshalling bug.
            var err = typed.Error;
            if (err != WeatherError.PermissionDenied && err != WeatherError.Unknown)
            {
                Fail("WeatherService.attribution error category",
                     $"typed WeatherError out of the declared range (permissionDenied=0, unknown=1): {(int)err}");
            }
            else if (string.IsNullOrWhiteSpace(typed.Message))
            {
                Fail("WeatherService.attribution error category",
                     "SwiftException<WeatherError> carried an empty message");
            }
            else
            {
                Log($"attribution reported typed WeatherError.{err} — {typed.Message}");
                Pass($"WeatherService.attribution reports typed WeatherError.{err}");
            }
        }
        catch (SwiftException sex) when (IsNoNetwork(sex.Message))
        {
            // (c) The one named environment condition. Narrow: URL-loading offline codes only.
            Skip("WeatherService.attribution",
                 $"simulator has no network reachability (NSURLErrorDomain offline signature): {sex.Message}");
        }
        catch (Exception ex)
        {
            Fail("WeatherService.attribution", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Test 27 (Skip): WeatherService.WeatherAsync(CLLocation) — the real weather fetch.
        // The binding surface exists (Task<Weather> WeatherAsync(CLLocation, CancellationToken)),
        // but the call requires the com.apple.developer.weatherkit entitlement + a provisioned
        // WeatherKit API key + network; in the autonomous CLI simulator it resolves to a
        // WeatherDaemon/authentication error rather than real data, so an assertion here would
        // prove only dispatch — which this suite deliberately does not count as coverage.
        // First-class documented Skip; revisit with a keyed, provisioned test host that has
        // the entitlement, then assert a real Weather value (e.g. CurrentWeather.Temperature).
        Skip("WeatherService.WeatherAsync(CLLocation) real fetch",
            "needs com.apple.developer.weatherkit entitlement + API key + network; not autonomous-CLI-sim runnable (revisit under entitlement follow-up)");

        // Summary
        Log($"Results: {passed} passed, {failed} failed, {skipped} skipped");
        if (failed == 0)
            Log("TEST SUCCESS", prefixed: false);
        else
            Log($"TEST FAILED: {failed} failures", prefixed: false);
        return failed;
    }

    /// <summary>
    /// True only for the URL-loading system's offline/unreachable error codes. Deliberately narrow:
    /// this is the single environment condition allowed to convert a WeatherKit network call into a
    /// named skip. Anything else (auth, entitlement, decode, bridge) must fail the test.
    /// </summary>
    private static bool IsNoNetwork(string message) =>
        message.Contains("-1009", StringComparison.Ordinal)          // NSURLErrorNotConnectedToInternet
        || message.Contains("-1005", StringComparison.Ordinal)       // NSURLErrorNetworkConnectionLost
        || message.Contains("-1004", StringComparison.Ordinal)       // NSURLErrorCannotConnectToHost
        || message.Contains("The Internet connection appears to be offline", StringComparison.OrdinalIgnoreCase)
        || message.Contains("A data connection is not currently allowed", StringComparison.OrdinalIgnoreCase);

    internal static void Log(string msg, bool prefixed = true) =>
        Console.WriteLine(prefixed ? $"[WEATHERKIT-TEST] {msg}" : msg);
}
