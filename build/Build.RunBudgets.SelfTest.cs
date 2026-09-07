using Nuke.Common;
using Serilog;

partial class Build
{
    /// <summary>
    /// Self-test for the run/launch budget resolvers in <c>Build.RunBudgets.cs</c>.
    ///
    /// <para>These are pure functions, but they decide whether a test app is allowed to finish
    /// speaking. A floor that silently stops applying — a renamed library, a table entry lost in
    /// a merge, a <c>--timeout</c> that lowers rather than raises — does not fail loudly; it
    /// shows up much later as a suite killed mid-run whose assertions never reported. So the
    /// properties are asserted directly rather than inferred from a green pipeline.</para>
    /// </summary>
    Target TestRunBudgets => _ => _
        .Description("Self-test for the per-library run budget + launch budget resolvers")
        .Executes(() =>
        {
            var failures = 0;

            void Check(string name, bool ok, string? detail = null)
            {
                if (ok) Log.Information("[ok]   {Name}", name);
                else { Log.Error("[FAIL] {Name}{Detail}", name, detail is null ? "" : ": " + detail); failures++; }
            }

            // ── 1. A floor raises a too-small request ────────────────────────
            // The StoreKit2 app awaits, in sequence: AppTransaction.shared (120s) + two finite
            // transaction sequences (120s each) + three 30s probes. The budget it runs under has
            // to clear that sum, or the process is killed mid-suite and the assertions those
            // bounds exist to make never report either way. Asserted against the sum rather than
            // against the table, so a floor edited down is caught here and not in a CI log.
            const int storeKitSequentialBoundsSeconds = 120 + 120 + 120 + 30 + 30 + 30;
            Check("[run-budget] StoreKit2 raises the CI default of 60s past the app's own bounds",
                ResolveRunBudgetSeconds("StoreKit2", 60) > storeKitSequentialBoundsSeconds,
                $"actual={ResolveRunBudgetSeconds("StoreKit2", 60)} bounds={storeKitSequentialBoundsSeconds}");
            // MapLibre's app waits up to 60s for its delegate callbacks to settle and then
            // runs behaviour probes bounded at 45s (snapshot) + 20s (source added) + 45s
            // (offline pack progress) + 15s (source removed) + 10s (region redelivery) + 45s
            // (a second map's style/load) before reporting. Asserted against that sum for the
            // same reason as StoreKit2's, and because CI already requests 60s — a floor at or
            // below the request is not a floor at all.
            const int mapLibreSequentialBoundsSeconds = 60 + 45 + 20 + 45 + 15 + 10 + 45;
            Check("[run-budget] MapLibre clears the app's own sequential waits",
                ResolveRunBudgetSeconds("MapLibre", 60) > mapLibreSequentialBoundsSeconds,
                $"actual={ResolveRunBudgetSeconds("MapLibre", 60)} bounds={mapLibreSequentialBoundsSeconds}");

            // ── 2. A floor never LOWERS an explicit request ──────────────────
            // An operator asking for more time gets it; the table is a floor, not a cap.
            Check("[run-budget] an explicit larger --timeout wins over the floor",
                ResolveRunBudgetSeconds("StoreKit2", 1800) == 1800,
                $"actual={ResolveRunBudgetSeconds("StoreKit2", 1800)}");
            Check("[run-budget] MapLibre with a larger request keeps the request",
                ResolveRunBudgetSeconds("MapLibre", 900) == 900,
                $"actual={ResolveRunBudgetSeconds("MapLibre", 900)}");

            // ── 3. A library with no entry is unchanged ──────────────────────
            Check("[run-budget] a library with no floor passes the request through",
                ResolveRunBudgetSeconds("Kingfisher", 60) == 60,
                $"actual={ResolveRunBudgetSeconds("Kingfisher", 60)}");
            Check("[run-budget] lookup is exact, not prefix/case-insensitive",
                ResolveRunBudgetSeconds("storekit2", 60) == 60 &&
                ResolveRunBudgetSeconds("StoreKit2Extra", 60) == 60,
                $"lower={ResolveRunBudgetSeconds("storekit2", 60)} suffixed={ResolveRunBudgetSeconds("StoreKit2Extra", 60)}");

            // ── 4. Launch budget ─────────────────────────────────────────────
            Check("[launch-budget] the default is used when nothing is requested",
                ResolveLaunchBudgetSeconds(DefaultLaunchBudgetSeconds) == DefaultLaunchBudgetSeconds,
                $"actual={ResolveLaunchBudgetSeconds(DefaultLaunchBudgetSeconds)}");
            Check("[launch-budget] a too-small request is floored",
                ResolveLaunchBudgetSeconds(1) == MinLaunchBudgetSeconds,
                $"actual={ResolveLaunchBudgetSeconds(1)}");
            Check("[launch-budget] a larger request is honoured",
                ResolveLaunchBudgetSeconds(300) == 300,
                $"actual={ResolveLaunchBudgetSeconds(300)}");
            Check("[launch-budget] this invocation's budget is at least the floor",
                LaunchBudgetSeconds >= MinLaunchBudgetSeconds,
                $"actual={LaunchBudgetSeconds}");

            // ── 5. The budgets fit inside the CI step envelope ───────────────
            // A CI attempt is install + launch + run + reporting, all inside the pipeline's
            // step deadline. The install is the part that yields when the deadline is tight,
            // so two things are asserted separately: the hard requirement that the budgets
            // plus the smallest install still fit (below this, a library's declared run
            // budget cannot be honoured at all), and the configuration requirement that the
            // full empirical install overhead fits too (below this, an install on a slow
            // runner is squeezed and may fail the attempt as an infrastructure error).
            foreach (var (library, floor) in LibraryRunBudgetSeconds)
            {
                var minimumAttempt = MinInstallCapSeconds + DefaultLaunchBudgetSeconds + floor + 60;
                Check($"[envelope] a {library} attempt with the smallest install fits the step deadline",
                    minimumAttempt <= StepTimeout,
                    $"attempt={minimumAttempt}s step-timeout={StepTimeout}s");

                var fullAttempt = InstallOverheadSeconds + DefaultLaunchBudgetSeconds + floor + 60;
                Check($"[envelope] a {library} attempt with a full-overhead install fits the step deadline",
                    fullAttempt <= StepTimeout,
                    $"attempt={fullAttempt}s step-timeout={StepTimeout}s");
            }

            // ── 6. The install cap the CI attempt runs under ────────────────
            // The install and the watch are sequential, so the cap is what the step budget
            // has left after the watcher's budgets are reserved — bounded above by the
            // empirical install overhead and below by the floor. These assert the live
            // resolver rather than a formula written out a second time here, so the checks
            // move with the arithmetic instead of freezing a retired version of it.
            const int watchBudget = DefaultLaunchBudgetSeconds + 600;   // launch + StoreKit2's floor
            Check("[install-cap] a long deadline caps the install at the empirical overhead",
                ResolveInstallCapSeconds(5000, watchBudget) == InstallOverheadSeconds,
                $"actual={ResolveInstallCapSeconds(5000, watchBudget)}");
            Check("[install-cap] a moderate deadline gives the install what the budgets leave",
                ResolveInstallCapSeconds(watchBudget + 200, watchBudget) == 200,
                $"actual={ResolveInstallCapSeconds(watchBudget + 200, watchBudget)}");
            Check("[install-cap] a spent deadline still leaves the install its floor",
                ResolveInstallCapSeconds(watchBudget - 500, watchBudget) == MinInstallCapSeconds,
                $"actual={ResolveInstallCapSeconds(watchBudget - 500, watchBudget)}");
            Check("[install-cap] the cap never reaches zero",
                ResolveInstallCapSeconds(0, watchBudget) > 0,
                $"actual={ResolveInstallCapSeconds(0, watchBudget)}");
            // The watcher's budgets are reserved first: whatever the install is capped at, the
            // launch and run budgets are handed over whole. That is the property that stopped a
            // slow install from shortening a library's declared run budget.
            Check("[install-cap] reserving the watch budget leaves the full run budget intact",
                ResolveInstallCapSeconds(watchBudget + InstallOverheadSeconds, watchBudget) == InstallOverheadSeconds,
                $"actual={ResolveInstallCapSeconds(watchBudget + InstallOverheadSeconds, watchBudget)}");

            if (failures > 0)
                throw new Exception($"{failures} run-budget self-test check(s) failed");

            Log.Information("Run-budget self-tests passed.");
        });
}
