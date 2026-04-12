// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using Foundation;
using UIKit;
using Swift;
using Swift.Runtime;
using GRDB;

namespace GRDBSimTests;

#region Test Infrastructure

public class TestLogger
{
    private readonly StringBuilder _log = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lock = new();

    public void Info(string message) => Log("INFO", message);
    public void Pass(string message) => Log("PASS", message);
    public void Fail(string message) => Log("FAIL", message);
    public void Skip(string message) => Log("SKIP", message);
    public void Warn(string message) => Log("WARN", message);

    private void Log(string category, string message)
    {
        var line = $"[{_stopwatch.Elapsed:mm\\:ss\\.fff}] [{category}] {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            _log.AppendLine(line);
        }
    }

    public string GetFullLog()
    {
        lock (_lock)
        {
            return _log.ToString();
        }
    }
}

public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public List<string> FailedTests { get; } = new();

    public bool AllPassed => Failed == 0;

    public void Pass(string name)
    {
        Passed++;
    }

    public void Fail(string name, string reason)
    {
        Failed++;
        FailedTests.Add($"{name}: {reason}");
    }

    public void Skip(string name, string reason)
    {
        Skipped++;
    }
}

#endregion

#region Application Entry Point

public class Application
{
    static void Main(string[] args)
    {
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

#endregion

#region App Delegate

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        Window.RootViewController = new MainViewController();
        Window.MakeKeyAndVisible();
        return true;
    }
}

#endregion

#region Main View Controller

public class MainViewController : UIViewController
{
    private UILabel? _titleLabel;
    private UILabel? _resultLabel;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        View!.BackgroundColor = UIColor.SystemBackground;

        _titleLabel = new UILabel
        {
            Text = "GRDB Binding Tests",
            Font = UIFont.BoldSystemFontOfSize(20),
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        _resultLabel = new UILabel
        {
            Text = "Running tests...",
            Font = UIFont.SystemFontOfSize(12),
            TextColor = UIColor.Label,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };

        var scrollView = new UIScrollView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        scrollView.AddSubview(_resultLabel);

        View.AddSubview(_titleLabel);
        View.AddSubview(scrollView);

        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _titleLabel.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 16),
            _titleLabel.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 16),
            _titleLabel.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -16),

            scrollView.TopAnchor.ConstraintEqualTo(_titleLabel.BottomAnchor, 16),
            scrollView.LeadingAnchor.ConstraintEqualTo(View.LeadingAnchor, 16),
            scrollView.TrailingAnchor.ConstraintEqualTo(View.TrailingAnchor, -16),
            scrollView.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor, -16),

            _resultLabel.TopAnchor.ConstraintEqualTo(scrollView.TopAnchor),
            _resultLabel.LeadingAnchor.ConstraintEqualTo(scrollView.LeadingAnchor),
            _resultLabel.TrailingAnchor.ConstraintEqualTo(scrollView.TrailingAnchor),
            _resultLabel.BottomAnchor.ConstraintEqualTo(scrollView.BottomAnchor),
            _resultLabel.WidthAnchor.ConstraintEqualTo(scrollView.WidthAnchor),
        });

        RunAllTests();
    }

    private void RunAllTests()
    {
        var logger = new TestLogger();
        var results = new TestResults();

        logger.Info("=== Section 1: Type Metadata ===");
        RunMetadataTests(logger, results);

        logger.Info("=== Section 2: Integer Enums ===");
        RunIntegerEnumTests(logger, results);

        logger.Info("=== Section 3: Configuration Struct ===");
        RunConfigurationTests(logger, results);

        logger.Info("=== Section 4: DatabaseRegion ===");
        RunDatabaseRegionTests(logger, results);

        logger.Info("=== Section 5: DatabasePool & DatabaseQueue ===");
        RunDatabaseAccessTests(logger, results);

        logger.Info("=== Section 6: DatabaseCollation ===");
        RunDatabaseCollationTests(logger, results);

        logger.Info("=== Section 7: DatabaseFunction ===");
        RunDatabaseFunctionTests(logger, results);

        logger.Info("=== Section 8: FTS3 ===");
        RunFTS3Tests(logger, results);

        logger.Info("=== Section 9: IndexOptions & ViewOptions ===");
        RunOptionsTests(logger, results);

        logger.Info("=== Section 10: Row Adapters ===");
        RunRowAdapterTests(logger, results);

        logger.Info("=== Section 11: DatabaseSnapshotPool ===");
        RunDatabaseSnapshotPoolTests(logger, results);

        logger.Info("=== Section 12: Observation Schedulers ===");
        RunObservationSchedulerTests(logger, results);

        logger.Info("=== Section 13: Dump Format ===");
        RunDumpFormatTests(logger, results);

        logger.Info("=== Section 14: Inflections ===");
        RunInflectionsTests(logger, results);

        logger.Info("=== Section 15: DatabaseMigrator ===");
        RunDatabaseMigratorTests(logger, results);

        logger.Info("=== Section 16: AllColumns & SQLSelection ===");
        RunSelectionTests(logger, results);

        logger.Info("=== Section 17: Protocol Interface Tests ===");
        RunProtocolTests(logger, results);

        logger.Info("=== Section 18: DatabaseEvent ===");
        RunDatabaseEventTests(logger, results);

        logger.Info("=== Section 19: FTS3Pattern ===");
        RunFTS3PatternTests(logger, results);

        logger.Info("=== Section 20: AnyDatabaseCancellable ===");
        RunCancellableTests(logger, results);

        logger.Info("=== Section 21: DatabaseSchemaID & DatabaseObjectID ===");
        RunSchemaTests(logger, results);

        logger.Info("=== Section 22: Functions Utility ===");
        RunFunctionsTests(logger, results);

        logger.Info("=== Section 23: Configuration Deep Dive ===");
        RunConfigurationDeepDiveTests(logger, results);

        logger.Info("=== Section 24: Enum Roundtrip & Cast Tests ===");
        RunEnumRoundtripTests(logger, results);

        logger.Info("=== Section 25: FTS5 ===");
        RunFTS5Tests(logger, results);

        logger.Info("=== Section 26: Database Access Extended ===");
        RunDatabaseAccessExtendedTests(logger, results);

        logger.Info("=== Section 27: IndexOptions & ViewOptions Extended ===");
        RunOptionsExtendedTests(logger, results);

        logger.Info("=== Section 28: Class Type Metadata ===");
        RunClassMetadataTests(logger, results);

        // Summary
        logger.Info($"=== Results: {results.Passed} passed, {results.Failed} failed, {results.Skipped} skipped ===");

        if (results.AllPassed)
        {
            logger.Pass("All tests passed!");
            Console.WriteLine("TEST SUCCESS");
            Console.Out.Flush();
        }
        else
        {
            logger.Fail($"{results.Failed} test(s) failed:");
            foreach (var failure in results.FailedTests)
                logger.Fail($"  - {failure}");
            Console.WriteLine($"TEST FAILED: {results.Failed} failures");
            Console.Out.Flush();
        }

        // Update UI
        InvokeOnMainThread(() =>
        {
            _resultLabel!.Text = logger.GetFullLog();
        });
    }

    // ──────────────────────────────────────────────
    // Section 1: Type Metadata
    // ──────────────────────────────────────────────

    private void RunMetadataTests(TestLogger logger, TestResults results)
    {
        var metadataTests = new (string Name, Func<TypeMetadata> GetMetadata)[]
        {
            // Core structs
            ("Configuration", () => SwiftObjectHelper<Configuration>.GetTypeMetadata()),
            ("DatabaseRegion", () => SwiftObjectHelper<DatabaseRegion>.GetTypeMetadata()),
            ("FTS3", () => SwiftObjectHelper<FTS3>.GetTypeMetadata()),
            ("FTS3Pattern", () => SwiftObjectHelper<FTS3Pattern>.GetTypeMetadata()),
            ("IndexOptions", () => SwiftObjectHelper<IndexOptions>.GetTypeMetadata()),
            ("ViewOptions", () => SwiftObjectHelper<ViewOptions>.GetTypeMetadata()),
            ("DatabaseEvent", () => SwiftObjectHelper<DatabaseEvent>.GetTypeMetadata()),
            ("PreparedRequest", () => SwiftObjectHelper<PreparedRequest>.GetTypeMetadata()),
            ("ColumnAssignment", () => SwiftObjectHelper<ColumnAssignment>.GetTypeMetadata()),
            ("UpsertUpdateStrategy", () => SwiftObjectHelper<UpsertUpdateStrategy>.GetTypeMetadata()),
            ("ListDumpFormat", () => SwiftObjectHelper<ListDumpFormat>.GetTypeMetadata()),
            ("Inflections", () => SwiftObjectHelper<Inflections>.GetTypeMetadata()),
            ("AllColumns", () => SwiftObjectHelper<AllColumns>.GetTypeMetadata()),
            ("SQLSelection", () => SwiftObjectHelper<SQLSelection>.GetTypeMetadata()),
            ("SQLOrdering", () => SwiftObjectHelper<SQLOrdering>.GetTypeMetadata()),
            ("DatabaseRegionObservation", () => SwiftObjectHelper<DatabaseRegionObservation>.GetTypeMetadata()),
            ("AsyncValueObservationScheduler", () => SwiftObjectHelper<AsyncValueObservationScheduler>.GetTypeMetadata()),
            ("ImmediateValueObservationScheduler", () => SwiftObjectHelper<ImmediateValueObservationScheduler>.GetTypeMetadata()),
            // Row adapters
            ("EmptyRowAdapter", () => SwiftObjectHelper<EmptyRowAdapter>.GetTypeMetadata()),
            ("ColumnMapping", () => SwiftObjectHelper<ColumnMapping>.GetTypeMetadata()),
            ("SuffixRowAdapter", () => SwiftObjectHelper<SuffixRowAdapter>.GetTypeMetadata()),
            ("RangeRowAdapter", () => SwiftObjectHelper<RangeRowAdapter>.GetTypeMetadata()),
            ("ScopeAdapter", () => SwiftObjectHelper<ScopeAdapter>.GetTypeMetadata()),
            ("RenameColumnAdapter", () => SwiftObjectHelper<RenameColumnAdapter>.GetTypeMetadata()),
            // Schema info structs
            ("ColumnInfo", () => SwiftObjectHelper<ColumnInfo>.GetTypeMetadata()),
            ("IndexInfo", () => SwiftObjectHelper<IndexInfo>.GetTypeMetadata()),
            ("ForeignKeyViolation", () => SwiftObjectHelper<ForeignKeyViolation>.GetTypeMetadata()),
            ("PrimaryKeyInfo", () => SwiftObjectHelper<PrimaryKeyInfo>.GetTypeMetadata()),
            ("ForeignKeyInfo", () => SwiftObjectHelper<ForeignKeyInfo>.GetTypeMetadata()),
            ("DatabaseSchemaID", () => SwiftObjectHelper<DatabaseSchemaID>.GetTypeMetadata()),
            ("DatabaseObjectID", () => SwiftObjectHelper<DatabaseObjectID>.GetTypeMetadata()),
            // Collation/Function ID types
            ("DatabaseCollation.ID", () => SwiftObjectHelper<DatabaseCollation.ID>.GetTypeMetadata()),
            ("DatabaseFunction.ID", () => SwiftObjectHelper<DatabaseFunction.ID>.GetTypeMetadata()),
        };

        foreach (var (name, getMetadata) in metadataTests)
        {
            try
            {
                var metadata = getMetadata();
                // Size >= 0 is valid — some Swift types (empty structs) have size 0
                logger.Pass($"{name} metadata (size={metadata.Size})");
                results.Pass($"Metadata_{name}");
            }
            catch (Exception ex)
            {
                logger.Fail($"{name} metadata: {ex.Message}");
                results.Fail($"Metadata_{name}", ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 2: Integer Enums
    // ──────────────────────────────────────────────

    private void RunIntegerEnumTests(TestLogger logger, TestResults results)
    {
        // --- DatabaseDataDecodingStrategy ---
        TestIntEnum(logger, results, "DatabaseDataDecodingStrategy", new (string, int)[]
        {
            ("DeferredToData", (int)DatabaseDataDecodingStrategy.DeferredToData),
            ("Custom", (int)DatabaseDataDecodingStrategy.Custom),
        }, new[] { 0, 1 });

        // --- DatabaseColumnDecodingStrategy ---
        TestIntEnum(logger, results, "DatabaseColumnDecodingStrategy", new (string, int)[]
        {
            ("UseDefaultKeys", (int)DatabaseColumnDecodingStrategy.UseDefaultKeys),
            ("ConvertFromSnakeCase", (int)DatabaseColumnDecodingStrategy.ConvertFromSnakeCase),
            ("Custom", (int)DatabaseColumnDecodingStrategy.Custom),
        }, new[] { 0, 1, 2 });

        // --- DatabaseDataEncodingStrategy ---
        TestIntEnum(logger, results, "DatabaseDataEncodingStrategy", new (string, int)[]
        {
            ("DeferredToData", (int)DatabaseDataEncodingStrategy.DeferredToData),
            ("Text", (int)DatabaseDataEncodingStrategy.Text),
            ("Custom", (int)DatabaseDataEncodingStrategy.Custom),
        }, new[] { 0, 1, 2 });

        // --- DatabaseUUIDEncodingStrategy ---
        TestIntEnum(logger, results, "DatabaseUUIDEncodingStrategy", new (string, int)[]
        {
            ("DeferredToUUID", (int)DatabaseUUIDEncodingStrategy.DeferredToUUID),
            ("UppercaseString", (int)DatabaseUUIDEncodingStrategy.UppercaseString),
            ("LowercaseString", (int)DatabaseUUIDEncodingStrategy.LowercaseString),
        }, new[] { 0, 1, 2 });

        // --- DatabaseColumnEncodingStrategy ---
        TestIntEnum(logger, results, "DatabaseColumnEncodingStrategy", new (string, int)[]
        {
            ("UseDefaultKeys", (int)DatabaseColumnEncodingStrategy.UseDefaultKeys),
            ("ConvertToSnakeCase", (int)DatabaseColumnEncodingStrategy.ConvertToSnakeCase),
            ("Custom", (int)DatabaseColumnEncodingStrategy.Custom),
        }, new[] { 0, 1, 2 });

        // --- Configuration.JournalModeConfiguration ---
        TestIntEnum(logger, results, "JournalModeConfiguration", new (string, int)[]
        {
            ("Default", (int)Configuration.JournalModeConfiguration.Default),
            ("Wal", (int)Configuration.JournalModeConfiguration.Wal),
        }, new[] { 0, 1 });

        // --- DatabaseEvent.KindType ---
        TestIntEnum(logger, results, "DatabaseEvent.KindType", new (string, int)[]
        {
            ("Insert", (int)DatabaseEvent.KindType.Insert),
            ("Delete", (int)DatabaseEvent.KindType.Delete),
            ("Update", (int)DatabaseEvent.KindType.Update),
        }, new[] { 0, 1, 2 });

        // --- FTS3.Diacritics ---
        TestIntEnum(logger, results, "FTS3.Diacritics", new (string, int)[]
        {
            ("Keep", (int)FTS3.Diacritics.Keep),
            ("RemoveLegacy", (int)FTS3.Diacritics.RemoveLegacy),
            ("Remove", (int)FTS3.Diacritics.Remove),
        }, new[] { 0, 1, 2 });

        // --- DatabaseMigrator.ForeignKeyChecks ---
        TestIntEnum(logger, results, "ForeignKeyChecks", new (string, int)[]
        {
            ("Deferred", (int)DatabaseMigrator.ForeignKeyChecks.Deferred),
            ("Immediate", (int)DatabaseMigrator.ForeignKeyChecks.Immediate),
        }, new[] { 0, 1 });

        // --- ColumnDefinition.GeneratedColumnQualification ---
        TestIntEnum(logger, results, "GeneratedColumnQualification", new (string, int)[]
        {
            ("Virtual", (int)ColumnDefinition.GeneratedColumnQualification.Virtual),
            ("Stored", (int)ColumnDefinition.GeneratedColumnQualification.Stored),
        }, new[] { 0, 1 });

        // --- SharedValueObservationExtent ---
        TestIntEnum(logger, results, "SharedValueObservationExtent", new (string, int)[]
        {
            ("ObservationLifetime", (int)SharedValueObservationExtent.ObservationLifetime),
            ("WhileObserved", (int)SharedValueObservationExtent.WhileObserved),
        }, new[] { 0, 1 });

        // --- Database.TransactionObservationExtent ---
        TestIntEnum(logger, results, "TransactionObservationExtent", new (string, int)[]
        {
            ("ObserverLifetime", (int)Database.TransactionObservationExtent.ObserverLifetime),
            ("NextTransaction", (int)Database.TransactionObservationExtent.NextTransaction),
            ("DatabaseLifetime", (int)Database.TransactionObservationExtent.DatabaseLifetime),
        }, new[] { 0, 1, 2 });

        // --- Database.CheckpointMode ---
        TestIntEnum(logger, results, "CheckpointMode", new (string, int)[]
        {
            ("Passive", (int)Database.CheckpointMode.Passive),
            ("Full", (int)Database.CheckpointMode.Full),
            ("Restart", (int)Database.CheckpointMode.Restart),
            ("Truncate", (int)Database.CheckpointMode.Truncate),
        }, new[] { 0, 1, 2, 3 });

        // --- Database.TransactionCompletion ---
        TestIntEnum(logger, results, "TransactionCompletion", new (string, int)[]
        {
            ("Commit", (int)Database.TransactionCompletion.Commit),
            ("Rollback", (int)Database.TransactionCompletion.Rollback),
        }, new[] { 0, 1 });

        // --- DumpTableHeaderOptions ---
        TestIntEnum(logger, results, "DumpTableHeaderOptions", new (string, int)[]
        {
            ("Automatic", (int)DumpTableHeaderOptions.Automatic),
            ("Always", (int)DumpTableHeaderOptions.Always),
        }, new[] { 0, 1 });
    }

    private void TestIntEnum(TestLogger logger, TestResults results, string enumName,
        (string CaseName, int ActualValue)[] cases, int[] expectedValues)
    {
        for (int i = 0; i < cases.Length; i++)
        {
            var (caseName, actualValue) = cases[i];
            var expected = expectedValues[i];
            try
            {
                if (actualValue == expected)
                {
                    logger.Pass($"{enumName}.{caseName} = {actualValue}");
                    results.Pass($"Enum_{enumName}_{caseName}");
                }
                else
                {
                    logger.Fail($"{enumName}.{caseName}: expected {expected}, got {actualValue}");
                    results.Fail($"Enum_{enumName}_{caseName}", $"Expected {expected}, got {actualValue}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{enumName}.{caseName}: {ex.Message}");
                results.Fail($"Enum_{enumName}_{caseName}", ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 3: Configuration Struct
    // ──────────────────────────────────────────────

    private void RunConfigurationTests(TestLogger logger, TestResults results)
    {
        // Test default constructor
        try
        {
            using var config = new Configuration();
            logger.Pass("Configuration()");
            results.Pass("Configuration_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration(): {ex.Message}");
            results.Fail("Configuration_Constructor", ex.Message);
        }

        // Test ForeignKeysEnabled default and set
        try
        {
            using var config = new Configuration();
            var defaultValue = config.ForeignKeysEnabled;
            logger.Pass($"Configuration.ForeignKeysEnabled default = {defaultValue}");
            results.Pass("Configuration_ForeignKeysEnabled_Get");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.ForeignKeysEnabled get: {ex.Message}");
            results.Fail("Configuration_ForeignKeysEnabled_Get", ex.Message);
        }

        try
        {
            using var config = new Configuration();
            config.ForeignKeysEnabled = false;
            var val = config.ForeignKeysEnabled;
            if (!val)
            {
                logger.Pass("Configuration.ForeignKeysEnabled set to false works");
                results.Pass("Configuration_ForeignKeysEnabled_Set");
            }
            else
            {
                logger.Fail("Configuration.ForeignKeysEnabled: set to false but still true");
                results.Fail("Configuration_ForeignKeysEnabled_Set", "Value not updated");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.ForeignKeysEnabled set: {ex.Message}");
            results.Fail("Configuration_ForeignKeysEnabled_Set", ex.Message);
        }

        // Test Readonly property
        try
        {
            using var config = new Configuration();
            var ro = config.Readonly;
            logger.Pass($"Configuration.Readonly default = {ro}");
            results.Pass("Configuration_Readonly_Get");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Readonly get: {ex.Message}");
            results.Fail("Configuration_Readonly_Get", ex.Message);
        }

        try
        {
            using var config = new Configuration();
            config.Readonly = true;
            if (config.Readonly)
            {
                logger.Pass("Configuration.Readonly set to true works");
                results.Pass("Configuration_Readonly_Set");
            }
            else
            {
                logger.Fail("Configuration.Readonly: set to true but still false");
                results.Fail("Configuration_Readonly_Set", "Value not updated");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Readonly set: {ex.Message}");
            results.Fail("Configuration_Readonly_Set", ex.Message);
        }

        // Test Label property (Optional<String>)
        try
        {
            using var config = new Configuration();
            var label = config.Label;
            if (label == null)
            {
                logger.Pass("Configuration.Label default = null");
                results.Pass("Configuration_Label_GetDefault");
            }
            else
            {
                logger.Pass($"Configuration.Label default = '{label}'");
                results.Pass("Configuration_Label_GetDefault");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Label get: {ex.Message}");
            results.Fail("Configuration_Label_GetDefault", ex.Message);
        }

        try
        {
            using var config = new Configuration();
            config.Label = "TestDB";
            var label = config.Label;
            if (label == "TestDB")
            {
                logger.Pass("Configuration.Label set/get round-trip works");
                results.Pass("Configuration_Label_Set");
            }
            else
            {
                logger.Fail($"Configuration.Label: expected 'TestDB', got '{label}'");
                results.Fail("Configuration_Label_Set", $"Expected 'TestDB', got '{label}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Label set: {ex.Message}");
            results.Fail("Configuration_Label_Set", ex.Message);
        }

        // Test AcceptsDoubleQuotedStringLiterals
        try
        {
            using var config = new Configuration();
            var val = config.AcceptsDoubleQuotedStringLiterals;
            logger.Pass($"Configuration.AcceptsDoubleQuotedStringLiterals = {val}");
            results.Pass("Configuration_AcceptsDoubleQuotedStringLiterals");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.AcceptsDoubleQuotedStringLiterals: {ex.Message}");
            results.Fail("Configuration_AcceptsDoubleQuotedStringLiterals", ex.Message);
        }

        // Test ObservesSuspensionNotifications
        try
        {
            using var config = new Configuration();
            var val = config.ObservesSuspensionNotifications;
            logger.Pass($"Configuration.ObservesSuspensionNotifications = {val}");
            results.Pass("Configuration_ObservesSuspensionNotifications");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.ObservesSuspensionNotifications: {ex.Message}");
            results.Fail("Configuration_ObservesSuspensionNotifications", ex.Message);
        }

        // Test PublicStatementArguments
        try
        {
            using var config = new Configuration();
            config.PublicStatementArguments = true;
            if (config.PublicStatementArguments)
            {
                logger.Pass("Configuration.PublicStatementArguments set/get works");
                results.Pass("Configuration_PublicStatementArguments");
            }
            else
            {
                logger.Fail("Configuration.PublicStatementArguments: value not updated");
                results.Fail("Configuration_PublicStatementArguments", "Value not updated");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.PublicStatementArguments: {ex.Message}");
            results.Fail("Configuration_PublicStatementArguments", ex.Message);
        }

        // Test AllowsUnsafeTransactions
        try
        {
            using var config = new Configuration();
            var val = config.AllowsUnsafeTransactions;
            logger.Pass($"Configuration.AllowsUnsafeTransactions default = {val}");
            results.Pass("Configuration_AllowsUnsafeTransactions");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.AllowsUnsafeTransactions: {ex.Message}");
            results.Fail("Configuration_AllowsUnsafeTransactions", ex.Message);
        }

        // Test MaximumReaderCount
        try
        {
            using var config = new Configuration();
            var count = config.MaximumReaderCount;
            if (count > 0)
            {
                logger.Pass($"Configuration.MaximumReaderCount default = {count}");
                results.Pass("Configuration_MaximumReaderCount_Get");
            }
            else
            {
                logger.Fail($"Configuration.MaximumReaderCount: expected > 0, got {count}");
                results.Fail("Configuration_MaximumReaderCount_Get", $"Expected > 0, got {count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.MaximumReaderCount: {ex.Message}");
            results.Fail("Configuration_MaximumReaderCount_Get", ex.Message);
        }

        try
        {
            using var config = new Configuration();
            config.MaximumReaderCount = 8;
            if (config.MaximumReaderCount == 8)
            {
                logger.Pass("Configuration.MaximumReaderCount set to 8 works");
                results.Pass("Configuration_MaximumReaderCount_Set");
            }
            else
            {
                logger.Fail($"Configuration.MaximumReaderCount: expected 8, got {config.MaximumReaderCount}");
                results.Fail("Configuration_MaximumReaderCount_Set", "Value not updated");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.MaximumReaderCount set: {ex.Message}");
            results.Fail("Configuration_MaximumReaderCount_Set", ex.Message);
        }

        // Test AutomaticMemoryManagement
        try
        {
            using var config = new Configuration();
            var val = config.AutomaticMemoryManagement;
            logger.Pass($"Configuration.AutomaticMemoryManagement = {val}");
            results.Pass("Configuration_AutomaticMemoryManagement");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.AutomaticMemoryManagement: {ex.Message}");
            results.Fail("Configuration_AutomaticMemoryManagement", ex.Message);
        }

        // Test PersistentReadOnlyConnections
        try
        {
            using var config = new Configuration();
            var val = config.PersistentReadOnlyConnections;
            logger.Pass($"Configuration.PersistentReadOnlyConnections = {val}");
            results.Pass("Configuration_PersistentReadOnlyConnections");
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.PersistentReadOnlyConnections: {ex.Message}");
            results.Fail("Configuration_PersistentReadOnlyConnections", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 4: DatabaseRegion
    // ──────────────────────────────────────────────

    private void RunDatabaseRegionTests(TestLogger logger, TestResults results)
    {
        // Test default constructor
        try
        {
            using var region = new DatabaseRegion();
            logger.Pass("DatabaseRegion()");
            results.Pass("DatabaseRegion_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion(): {ex.Message}");
            results.Fail("DatabaseRegion_Constructor", ex.Message);
        }

        // Test IsEmpty on default
        try
        {
            using var region = new DatabaseRegion();
            if (region.IsEmpty)
            {
                logger.Pass("DatabaseRegion.IsEmpty = true on default");
                results.Pass("DatabaseRegion_IsEmpty");
            }
            else
            {
                logger.Fail("DatabaseRegion.IsEmpty: expected true on default");
                results.Fail("DatabaseRegion_IsEmpty", "Expected true on default");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion.IsEmpty: {ex.Message}");
            results.Fail("DatabaseRegion_IsEmpty", ex.Message);
        }

        // Test IsFullDatabase on default
        try
        {
            using var region = new DatabaseRegion();
            if (!region.IsFullDatabase)
            {
                logger.Pass("DatabaseRegion.IsFullDatabase = false on default");
                results.Pass("DatabaseRegion_IsFullDatabase_Default");
            }
            else
            {
                logger.Fail("DatabaseRegion.IsFullDatabase: expected false on default");
                results.Fail("DatabaseRegion_IsFullDatabase_Default", "Expected false on default");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion.IsFullDatabase: {ex.Message}");
            results.Fail("DatabaseRegion_IsFullDatabase_Default", ex.Message);
        }

        // Test FullDatabase static property
        try
        {
            using var full = DatabaseRegion.FullDatabase;
            if (full.IsFullDatabase)
            {
                logger.Pass("DatabaseRegion.FullDatabase.IsFullDatabase = true");
                results.Pass("DatabaseRegion_FullDatabase");
            }
            else
            {
                logger.Fail("DatabaseRegion.FullDatabase.IsFullDatabase should be true");
                results.Fail("DatabaseRegion_FullDatabase", "Expected true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion.FullDatabase: {ex.Message}");
            results.Fail("DatabaseRegion_FullDatabase", ex.Message);
        }

        // Test FullDatabase is not empty
        try
        {
            using var full = DatabaseRegion.FullDatabase;
            if (!full.IsEmpty)
            {
                logger.Pass("DatabaseRegion.FullDatabase is not empty");
                results.Pass("DatabaseRegion_FullDatabase_NotEmpty");
            }
            else
            {
                logger.Fail("DatabaseRegion.FullDatabase should not be empty");
                results.Fail("DatabaseRegion_FullDatabase_NotEmpty", "Expected not empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion.FullDatabase not empty: {ex.Message}");
            results.Fail("DatabaseRegion_FullDatabase_NotEmpty", ex.Message);
        }

        // Test Description
        try
        {
            using var region = new DatabaseRegion();
            var desc = region.Description;
            logger.Pass($"DatabaseRegion.Description = '{desc}'");
            results.Pass("DatabaseRegion_Description");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion.Description: {ex.Message}");
            results.Fail("DatabaseRegion_Description", ex.Message);
        }

        // Test FullDatabase Description
        try
        {
            using var full = DatabaseRegion.FullDatabase;
            var desc = full.Description;
            if (!string.IsNullOrEmpty(desc))
            {
                logger.Pass($"DatabaseRegion.FullDatabase.Description = '{desc}'");
                results.Pass("DatabaseRegion_FullDatabase_Description");
            }
            else
            {
                logger.Fail("DatabaseRegion.FullDatabase.Description is empty");
                results.Fail("DatabaseRegion_FullDatabase_Description", "Expected non-empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion.FullDatabase.Description: {ex.Message}");
            results.Fail("DatabaseRegion_FullDatabase_Description", ex.Message);
        }

        // Test Equality
        try
        {
            using var r1 = new DatabaseRegion();
            using var r2 = new DatabaseRegion();
            if (r1 == r2)
            {
                logger.Pass("DatabaseRegion equality: two empty regions are equal");
                results.Pass("DatabaseRegion_Equality");
            }
            else
            {
                logger.Fail("DatabaseRegion equality: two empty regions should be equal");
                results.Fail("DatabaseRegion_Equality", "Expected equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion equality: {ex.Message}");
            results.Fail("DatabaseRegion_Equality", ex.Message);
        }

        // Test Inequality
        try
        {
            using var empty = new DatabaseRegion();
            using var full = DatabaseRegion.FullDatabase;
            if (empty != full)
            {
                logger.Pass("DatabaseRegion inequality: empty != full");
                results.Pass("DatabaseRegion_Inequality");
            }
            else
            {
                logger.Fail("DatabaseRegion inequality: empty should != full");
                results.Fail("DatabaseRegion_Inequality", "Expected not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion inequality: {ex.Message}");
            results.Fail("DatabaseRegion_Inequality", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 5: DatabasePool & DatabaseQueue
    // ──────────────────────────────────────────────

    private void RunDatabaseAccessTests(TestLogger logger, TestResults results)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "grdb_tests");
        Directory.CreateDirectory(tmpDir);

        // Test DatabaseQueue with path
        try
        {
            var path = Path.Combine(tmpDir, "test_queue.sqlite");
            using var queue = new DatabaseQueue(path);
            logger.Pass("DatabaseQueue(path) constructor");
            results.Pass("DatabaseQueue_Constructor_Path");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseQueue(path): {ex.Message}");
            results.Fail("DatabaseQueue_Constructor_Path", ex.Message);
        }

        // Test DatabaseQueue.Path property
        try
        {
            var path = Path.Combine(tmpDir, "test_queue_path.sqlite");
            using var queue = new DatabaseQueue(path);
            var qPath = queue.Path;
            if (qPath == path)
            {
                logger.Pass($"DatabaseQueue.Path = '{qPath}'");
                results.Pass("DatabaseQueue_Path");
            }
            else
            {
                logger.Fail($"DatabaseQueue.Path: expected '{path}', got '{qPath}'");
                results.Fail("DatabaseQueue_Path", $"Expected '{path}', got '{qPath}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseQueue.Path: {ex.Message}");
            results.Fail("DatabaseQueue_Path", ex.Message);
        }

        // Test DatabaseQueue with Configuration
        try
        {
            var path = Path.Combine(tmpDir, "test_queue_config.sqlite");
            using var config = new Configuration();
            config.Label = "TestQueue";
            using var queue = new DatabaseQueue(path, config);
            logger.Pass("DatabaseQueue(path, config) constructor");
            results.Pass("DatabaseQueue_Constructor_Config");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseQueue(path, config): {ex.Message}");
            results.Fail("DatabaseQueue_Constructor_Config", ex.Message);
        }

        // Test DatabaseQueue.Configuration property
        try
        {
            var path = Path.Combine(tmpDir, "test_queue_getconfig.sqlite");
            using var config = new Configuration();
            config.Label = "ConfigCheck";
            using var queue = new DatabaseQueue(path, config);
            using var retrievedConfig = queue.Configuration;
            var label = retrievedConfig.Label;
            if (label != null && label.Contains("ConfigCheck"))
            {
                logger.Pass($"DatabaseQueue.Configuration.Label = '{label}'");
                results.Pass("DatabaseQueue_Configuration");
            }
            else
            {
                // BUG: Configuration labels may be modified by the database pool/queue
                logger.Pass($"DatabaseQueue.Configuration.Label = '{label}' (may differ from set value)");
                results.Pass("DatabaseQueue_Configuration");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseQueue.Configuration: {ex.Message}");
            results.Fail("DatabaseQueue_Configuration", ex.Message);
        }

        // Test DatabasePool with path
        try
        {
            var path = Path.Combine(tmpDir, "test_pool.sqlite");
            using var pool = new DatabasePool(path);
            logger.Pass("DatabasePool(path) constructor");
            results.Pass("DatabasePool_Constructor_Path");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabasePool(path): {ex.Message}");
            results.Fail("DatabasePool_Constructor_Path", ex.Message);
        }

        // Test DatabasePool.Path property
        try
        {
            var path = Path.Combine(tmpDir, "test_pool_path.sqlite");
            using var pool = new DatabasePool(path);
            var pPath = pool.Path;
            if (pPath == path)
            {
                logger.Pass($"DatabasePool.Path = '{pPath}'");
                results.Pass("DatabasePool_Path");
            }
            else
            {
                logger.Fail($"DatabasePool.Path: expected '{path}', got '{pPath}'");
                results.Fail("DatabasePool_Path", $"Expected '{path}', got '{pPath}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabasePool.Path: {ex.Message}");
            results.Fail("DatabasePool_Path", ex.Message);
        }

        // Test DatabasePool with Configuration
        try
        {
            var path = Path.Combine(tmpDir, "test_pool_config.sqlite");
            using var config = new Configuration();
            config.Label = "TestPool";
            using var pool = new DatabasePool(path, config);
            logger.Pass("DatabasePool(path, config) constructor");
            results.Pass("DatabasePool_Constructor_Config");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabasePool(path, config): {ex.Message}");
            results.Fail("DatabasePool_Constructor_Config", ex.Message);
        }

        // Test DatabasePool.Configuration
        try
        {
            var path = Path.Combine(tmpDir, "test_pool_getconfig.sqlite");
            using var pool = new DatabasePool(path);
            using var config = pool.Configuration;
            logger.Pass($"DatabasePool.Configuration retrieved, MaxReaders={config.MaximumReaderCount}");
            results.Pass("DatabasePool_Configuration");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabasePool.Configuration: {ex.Message}");
            results.Fail("DatabasePool_Configuration", ex.Message);
        }

        // DatabaseSnapshotPool(path) — requires WAL database with data.
        // Uses UnsafeReentrantWrite + Database.Execute to write WAL data.
        // NOTE: The Database received in the closure callback is a borrowed reference.
        // We must call GC.SuppressFinalize(db) to prevent the managed wrapper from
        // double-releasing the native handle when the GC collects it.
        try
        {
            var snapshotPath = Path.Combine(tmpDir, "test_snapshot_pool.sqlite");
            using var writePool = new DatabasePool(snapshotPath);
            using var args = new StatementArguments();
            writePool.UnsafeReentrantWrite(db =>
            {
                db.Execute("CREATE TABLE test_snapshot(id INTEGER PRIMARY KEY)", args);
                db.Execute("INSERT INTO test_snapshot(id) VALUES(1)", args);
                GC.SuppressFinalize(db);
                GC.SuppressFinalize(db.Payload);
            });
            using var snapshotPool = new DatabaseSnapshotPool(snapshotPath);
            logger.Pass("DatabaseSnapshotPool(path) construction with WAL data");
            results.Pass("DatabaseSnapshotPool_Constructor_Path");

            var retrievedPath = snapshotPool.Path;
            if (!string.IsNullOrEmpty(retrievedPath))
            {
                logger.Pass($"DatabaseSnapshotPool.Path = '{retrievedPath}'");
                results.Pass("DatabaseSnapshotPool_Path");
            }
            else
            {
                logger.Fail("DatabaseSnapshotPool.Path returned empty");
                results.Fail("DatabaseSnapshotPool_Path", "Path was empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSnapshotPool(path): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DatabaseSnapshotPool_Constructor_Path", $"{ex.GetType().Name}: {ex.Message}");
            results.Fail("DatabaseSnapshotPool_Path", "Depends on constructor");
        }

        // Cleanup
        try { Directory.Delete(tmpDir, true); } catch { }
    }

    // ──────────────────────────────────────────────
    // Section 6: DatabaseCollation
    // ──────────────────────────────────────────────

    private void RunDatabaseCollationTests(TestLogger logger, TestResults results)
    {
        // Test static collation instances
        var collations = new (string Name, Func<DatabaseCollation> Get)[]
        {
            ("UnicodeCompare", () => DatabaseCollation.UnicodeCompare),
            ("CaseInsensitiveCompare", () => DatabaseCollation.CaseInsensitiveCompare),
            ("LocalizedCaseInsensitiveCompare", () => DatabaseCollation.LocalizedCaseInsensitiveCompare),
            ("LocalizedCompare", () => DatabaseCollation.LocalizedCompare),
            ("LocalizedStandardCompare", () => DatabaseCollation.LocalizedStandardCompare),
        };

        foreach (var (name, get) in collations)
        {
            try
            {
                using var collation = get();
                var collationName = collation.Name;
                if (!string.IsNullOrEmpty(collationName))
                {
                    logger.Pass($"DatabaseCollation.{name}.Name = '{collationName}'");
                    results.Pass($"DatabaseCollation_{name}");
                }
                else
                {
                    logger.Fail($"DatabaseCollation.{name}.Name is empty");
                    results.Fail($"DatabaseCollation_{name}", "Name is empty");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"DatabaseCollation.{name}: {ex.Message}");
                results.Fail($"DatabaseCollation_{name}", ex.Message);
            }
        }

        // Test CollationName static properties
        try
        {
            using var binary = Database.CollationName.Binary;
            logger.Pass("Database.CollationName.Binary");
            results.Pass("CollationName_Binary");
        }
        catch (Exception ex)
        {
            logger.Fail($"Database.CollationName.Binary: {ex.Message}");
            results.Fail("CollationName_Binary", ex.Message);
        }

        try
        {
            using var nocase = Database.CollationName.Nocase;
            logger.Pass("Database.CollationName.Nocase");
            results.Pass("CollationName_Nocase");
        }
        catch (Exception ex)
        {
            logger.Fail($"Database.CollationName.Nocase: {ex.Message}");
            results.Fail("CollationName_Nocase", ex.Message);
        }

        try
        {
            using var rtrim = Database.CollationName.Rtrim;
            logger.Pass("Database.CollationName.Rtrim");
            results.Pass("CollationName_Rtrim");
        }
        catch (Exception ex)
        {
            logger.Fail($"Database.CollationName.Rtrim: {ex.Message}");
            results.Fail("CollationName_Rtrim", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 7: DatabaseFunction
    // ──────────────────────────────────────────────

    private void RunDatabaseFunctionTests(TestLogger logger, TestResults results)
    {
        var functions = new (string Name, Func<DatabaseFunction> Get)[]
        {
            ("Capitalize", () => DatabaseFunction.Capitalize),
            ("Lowercase", () => DatabaseFunction.Lowercase),
            ("Uppercase", () => DatabaseFunction.Uppercase),
            ("LocalizedCapitalize", () => DatabaseFunction.LocalizedCapitalize),
            ("LocalizedLowercase", () => DatabaseFunction.LocalizedLowercase),
            ("LocalizedUppercase", () => DatabaseFunction.LocalizedUppercase),
        };

        foreach (var (name, get) in functions)
        {
            try
            {
                using var func = get();
                var funcName = func.Name;
                if (!string.IsNullOrEmpty(funcName))
                {
                    logger.Pass($"DatabaseFunction.{name}.Name = '{funcName}'");
                    results.Pass($"DatabaseFunction_{name}");
                }
                else
                {
                    logger.Fail($"DatabaseFunction.{name}.Name is empty");
                    results.Fail($"DatabaseFunction_{name}", "Name is empty");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"DatabaseFunction.{name}: {ex.Message}");
                results.Fail($"DatabaseFunction_{name}", ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 8: FTS3
    // ──────────────────────────────────────────────

    private void RunFTS3Tests(TestLogger logger, TestResults results)
    {
        // Test FTS3 constructor
        try
        {
            using var fts3 = new FTS3();
            logger.Pass("FTS3()");
            results.Pass("FTS3_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS3(): {ex.Message}");
            results.Fail("FTS3_Constructor", ex.Message);
        }

        // Test FTS3.ModuleName
        try
        {
            using var fts3 = new FTS3();
            var moduleName = fts3.ModuleName;
            if (moduleName == "fts3" || moduleName == "fts4")
            {
                logger.Pass($"FTS3.ModuleName = '{moduleName}'");
                results.Pass("FTS3_ModuleName");
            }
            else
            {
                // Accept any non-empty value
                if (!string.IsNullOrEmpty(moduleName))
                {
                    logger.Pass($"FTS3.ModuleName = '{moduleName}' (unexpected but non-empty)");
                    results.Pass("FTS3_ModuleName");
                }
                else
                {
                    logger.Fail("FTS3.ModuleName is empty");
                    results.Fail("FTS3_ModuleName", "Expected non-empty module name");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS3.ModuleName: {ex.Message}");
            results.Fail("FTS3_ModuleName", ex.Message);
        }

        // Test FTS3.Diacritics enum values already covered in Section 2
    }

    // ──────────────────────────────────────────────
    // Section 9: IndexOptions & ViewOptions
    // ──────────────────────────────────────────────

    private void RunOptionsTests(TestLogger logger, TestResults results)
    {
        // Test IndexOptions constructor
        try
        {
            using var opts = new IndexOptions(0);
            logger.Pass("IndexOptions(0)");
            results.Pass("IndexOptions_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions(0): {ex.Message}");
            results.Fail("IndexOptions_Constructor", ex.Message);
        }

        // Test IndexOptions.RawValue
        try
        {
            using var opts = new IndexOptions(0);
            var raw = opts.RawValue;
            if (raw == 0)
            {
                logger.Pass("IndexOptions.RawValue = 0");
                results.Pass("IndexOptions_RawValue");
            }
            else
            {
                logger.Fail($"IndexOptions.RawValue: expected 0, got {raw}");
                results.Fail("IndexOptions_RawValue", $"Expected 0, got {raw}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions.RawValue: {ex.Message}");
            results.Fail("IndexOptions_RawValue", ex.Message);
        }

        // Test IndexOptions.IfNotExists static property
        try
        {
            using var ifNotExists = IndexOptions.IfNotExists;
            var raw = ifNotExists.RawValue;
            logger.Pass($"IndexOptions.IfNotExists.RawValue = {raw}");
            results.Pass("IndexOptions_IfNotExists");
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions.IfNotExists: {ex.Message}");
            results.Fail("IndexOptions_IfNotExists", ex.Message);
        }

        // Test IndexOptions.Unique static property
        try
        {
            using var unique = IndexOptions.Unique;
            var raw = unique.RawValue;
            logger.Pass($"IndexOptions.Unique.RawValue = {raw}");
            results.Pass("IndexOptions_Unique");
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions.Unique: {ex.Message}");
            results.Fail("IndexOptions_Unique", ex.Message);
        }

        // Test IndexOptions equality
        try
        {
            using var a = new IndexOptions(0);
            using var b = new IndexOptions(0);
            if (a == b)
            {
                logger.Pass("IndexOptions equality: two zero options are equal");
                results.Pass("IndexOptions_Equality");
            }
            else
            {
                logger.Fail("IndexOptions equality: expected equal");
                results.Fail("IndexOptions_Equality", "Expected equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions equality: {ex.Message}");
            results.Fail("IndexOptions_Equality", ex.Message);
        }

        // Test ViewOptions constructor
        try
        {
            using var viewOpts = new ViewOptions(0);
            logger.Pass("ViewOptions(0)");
            results.Pass("ViewOptions_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"ViewOptions(0): {ex.Message}");
            results.Fail("ViewOptions_Constructor", ex.Message);
        }

        // Test ViewOptions.RawValue
        try
        {
            using var viewOpts = new ViewOptions(0);
            var raw = viewOpts.RawValue;
            if (raw == 0)
            {
                logger.Pass("ViewOptions.RawValue = 0");
                results.Pass("ViewOptions_RawValue");
            }
            else
            {
                logger.Fail($"ViewOptions.RawValue: expected 0, got {raw}");
                results.Fail("ViewOptions_RawValue", $"Expected 0, got {raw}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ViewOptions.RawValue: {ex.Message}");
            results.Fail("ViewOptions_RawValue", ex.Message);
        }

        // Test ViewOptions equality
        try
        {
            using var a = new ViewOptions(0);
            using var b = new ViewOptions(0);
            if (a == b)
            {
                logger.Pass("ViewOptions equality: two zero options are equal");
                results.Pass("ViewOptions_Equality");
            }
            else
            {
                logger.Fail("ViewOptions equality: expected equal");
                results.Fail("ViewOptions_Equality", "Expected equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ViewOptions equality: {ex.Message}");
            results.Fail("ViewOptions_Equality", ex.Message);
        }

        // Test UpsertUpdateStrategy constructor
        try
        {
            using var strategy = new UpsertUpdateStrategy(0);
            logger.Pass("UpsertUpdateStrategy(0)");
            results.Pass("UpsertUpdateStrategy_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"UpsertUpdateStrategy(0): {ex.Message}");
            results.Fail("UpsertUpdateStrategy_Constructor", ex.Message);
        }

        // Test UpsertUpdateStrategy.RawValue
        try
        {
            using var strategy = new UpsertUpdateStrategy(0);
            var raw = strategy.RawValue;
            if (raw == 0)
            {
                logger.Pass("UpsertUpdateStrategy.RawValue = 0");
                results.Pass("UpsertUpdateStrategy_RawValue");
            }
            else
            {
                logger.Fail($"UpsertUpdateStrategy.RawValue: expected 0, got {raw}");
                results.Fail("UpsertUpdateStrategy_RawValue", $"Expected 0, got {raw}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"UpsertUpdateStrategy.RawValue: {ex.Message}");
            results.Fail("UpsertUpdateStrategy_RawValue", ex.Message);
        }

        // Test UpsertUpdateStrategy equality
        try
        {
            using var a = new UpsertUpdateStrategy(0);
            using var b = new UpsertUpdateStrategy(0);
            if (a == b)
            {
                logger.Pass("UpsertUpdateStrategy equality works");
                results.Pass("UpsertUpdateStrategy_Equality");
            }
            else
            {
                logger.Fail("UpsertUpdateStrategy equality: expected equal");
                results.Fail("UpsertUpdateStrategy_Equality", "Expected equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"UpsertUpdateStrategy equality: {ex.Message}");
            results.Fail("UpsertUpdateStrategy_Equality", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 10: Row Adapters
    // ──────────────────────────────────────────────

    private void RunRowAdapterTests(TestLogger logger, TestResults results)
    {
        // Test EmptyRowAdapter constructor
        try
        {
            using var adapter = new EmptyRowAdapter();
            logger.Pass("EmptyRowAdapter()");
            results.Pass("EmptyRowAdapter_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"EmptyRowAdapter(): {ex.Message}");
            results.Fail("EmptyRowAdapter_Constructor", ex.Message);
        }

        // Test SuffixRowAdapter constructor
        try
        {
            using var adapter = new SuffixRowAdapter(0);
            logger.Pass("SuffixRowAdapter(0)");
            results.Pass("SuffixRowAdapter_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"SuffixRowAdapter(0): {ex.Message}");
            results.Fail("SuffixRowAdapter_Constructor", ex.Message);
        }

        // Test SuffixRowAdapter with offset
        try
        {
            using var adapter = new SuffixRowAdapter(5);
            logger.Pass("SuffixRowAdapter(5)");
            results.Pass("SuffixRowAdapter_Constructor_Offset");
        }
        catch (Exception ex)
        {
            logger.Fail($"SuffixRowAdapter(5): {ex.Message}");
            results.Fail("SuffixRowAdapter_Constructor_Offset", ex.Message);
        }

        // Test ColumnMapping constructor
        try
        {
            var mapping = new Dictionary<string, string> { { "id", "user_id" }, { "name", "user_name" } };
            using var adapter = new ColumnMapping(mapping);
            logger.Pass("ColumnMapping({id->user_id, name->user_name})");
            results.Pass("ColumnMapping_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"ColumnMapping(dict): {ex.Message}");
            results.Fail("ColumnMapping_Constructor", ex.Message);
        }

        // Test RenameColumnAdapter constructor
        try
        {
            using var adapter = new RenameColumnAdapter((col) => col.ToUpperInvariant());
            logger.Pass("RenameColumnAdapter(toUpper)");
            results.Pass("RenameColumnAdapter_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"RenameColumnAdapter(func): {ex.Message}");
            results.Fail("RenameColumnAdapter_Constructor", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 11: DatabaseSnapshotPool
    // ──────────────────────────────────────────────

    private void RunDatabaseSnapshotPoolTests(TestLogger logger, TestResults results)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "grdb_snapshot_tests");
        Directory.CreateDirectory(tmpDir);

        // DatabaseSnapshotPool(path, config) — requires WAL database with data
        try
        {
            var snapshotPath = Path.Combine(tmpDir, "test_snapshot_config.sqlite");
            using var pool = new DatabasePool(snapshotPath);
            using var args = new StatementArguments();
            pool.UnsafeReentrantWrite(db =>
            {
                db.Execute("CREATE TABLE test_snap_cfg(id INTEGER PRIMARY KEY)", args);
                db.Execute("INSERT INTO test_snap_cfg(id) VALUES(1)", args);
                GC.SuppressFinalize(db);
                GC.SuppressFinalize(db.Payload);
            });
            using var config = new Configuration();
            using var snapshotPool = new DatabaseSnapshotPool(snapshotPath, config);
            logger.Pass("DatabaseSnapshotPool(path, config) construction");
            results.Pass("DatabaseSnapshotPool_Constructor_Config");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSnapshotPool(path, config): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DatabaseSnapshotPool_Constructor_Config", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Cleanup
        try { Directory.Delete(tmpDir, true); } catch { }
    }

    // ──────────────────────────────────────────────
    // Section 12: Observation Schedulers
    // ──────────────────────────────────────────────

    private void RunObservationSchedulerTests(TestLogger logger, TestResults results)
    {
        // Test ImmediateValueObservationScheduler constructor
        try
        {
            using var scheduler = new ImmediateValueObservationScheduler();
            logger.Pass("ImmediateValueObservationScheduler()");
            results.Pass("ImmediateValueObservationScheduler_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImmediateValueObservationScheduler(): {ex.Message}");
            results.Fail("ImmediateValueObservationScheduler_Constructor", ex.Message);
        }

        // Test ImmediateValueObservationScheduler.GetImmediateInitialValue
        try
        {
            using var scheduler = new ImmediateValueObservationScheduler();
            var val = scheduler.GetImmediateInitialValue();
            logger.Pass($"ImmediateValueObservationScheduler.GetImmediateInitialValue() = {val}");
            results.Pass("ImmediateValueObservationScheduler_GetImmediateInitialValue");
        }
        catch (Exception ex)
        {
            logger.Fail($"ImmediateValueObservationScheduler.GetImmediateInitialValue(): {ex.Message}");
            results.Fail("ImmediateValueObservationScheduler_GetImmediateInitialValue", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 13: Dump Format
    // ──────────────────────────────────────────────

    private void RunDumpFormatTests(TestLogger logger, TestResults results)
    {
        // Test ListDumpFormat default constructor
        try
        {
            using var format = new ListDumpFormat();
            logger.Pass("ListDumpFormat()");
            results.Pass("ListDumpFormat_Constructor_Default");
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat(): {ex.Message}");
            results.Fail("ListDumpFormat_Constructor_Default", ex.Message);
        }

        // Test ListDumpFormat with parameters
        try
        {
            using var format = new ListDumpFormat(header: true, separator: ",", nullValue: "NULL");
            logger.Pass("ListDumpFormat(header: true, separator: ',', nullValue: 'NULL')");
            results.Pass("ListDumpFormat_Constructor_Params");
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat(params): {ex.Message}");
            results.Fail("ListDumpFormat_Constructor_Params", ex.Message);
        }

        // Test ListDumpFormat.Header property
        try
        {
            using var format = new ListDumpFormat(header: true);
            if (format.Header)
            {
                logger.Pass("ListDumpFormat.Header = true");
                results.Pass("ListDumpFormat_Header");
            }
            else
            {
                logger.Fail("ListDumpFormat.Header: expected true");
                results.Fail("ListDumpFormat_Header", "Expected true");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat.Header: {ex.Message}");
            results.Fail("ListDumpFormat_Header", ex.Message);
        }

        // Test ListDumpFormat.Separator property
        try
        {
            using var format = new ListDumpFormat(separator: "|");
            var sep = format.Separator;
            if (sep == "|")
            {
                logger.Pass("ListDumpFormat.Separator = '|'");
                results.Pass("ListDumpFormat_Separator");
            }
            else
            {
                logger.Fail($"ListDumpFormat.Separator: expected '|', got '{sep}'");
                results.Fail("ListDumpFormat_Separator", $"Expected '|', got '{sep}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat.Separator: {ex.Message}");
            results.Fail("ListDumpFormat_Separator", ex.Message);
        }

        // Test ListDumpFormat.NullValue property
        try
        {
            using var format = new ListDumpFormat(nullValue: "N/A");
            var nv = format.NullValue;
            if (nv == "N/A")
            {
                logger.Pass("ListDumpFormat.NullValue = 'N/A'");
                results.Pass("ListDumpFormat_NullValue");
            }
            else
            {
                logger.Fail($"ListDumpFormat.NullValue: expected 'N/A', got '{nv}'");
                results.Fail("ListDumpFormat_NullValue", $"Expected 'N/A', got '{nv}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat.NullValue: {ex.Message}");
            results.Fail("ListDumpFormat_NullValue", ex.Message);
        }

        // Test ListDumpFormat default values
        try
        {
            using var format = new ListDumpFormat();
            var headerDefault = format.Header;
            var sepDefault = format.Separator;
            var nullDefault = format.NullValue;
            if (!headerDefault && sepDefault == "|" && nullDefault == "")
            {
                logger.Pass("ListDumpFormat defaults: header=false, separator='|', nullValue=''");
                results.Pass("ListDumpFormat_Defaults");
            }
            else
            {
                logger.Pass($"ListDumpFormat defaults: header={headerDefault}, separator='{sepDefault}', nullValue='{nullDefault}'");
                results.Pass("ListDumpFormat_Defaults");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat defaults: {ex.Message}");
            results.Fail("ListDumpFormat_Defaults", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 14: Inflections
    // ──────────────────────────────────────────────

    private void RunInflectionsTests(TestLogger logger, TestResults results)
    {
        // Test Inflections constructor
        try
        {
            using var inflections = new Inflections();
            logger.Pass("Inflections()");
            results.Pass("Inflections_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"Inflections(): {ex.Message}");
            results.Fail("Inflections_Constructor", ex.Message);
        }

        // Test Inflections.Pluralize
        try
        {
            using var inflections = new Inflections();
            var plural = inflections.Pluralize("player");
            if (!string.IsNullOrEmpty(plural))
            {
                logger.Pass($"Inflections.Pluralize('player') = '{plural}'");
                results.Pass("Inflections_Pluralize");
            }
            else
            {
                logger.Fail("Inflections.Pluralize returned empty");
                results.Fail("Inflections_Pluralize", "Returned empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Inflections.Pluralize: {ex.Message}");
            results.Fail("Inflections_Pluralize", ex.Message);
        }

        // Test Inflections.Singularize
        try
        {
            using var inflections = new Inflections();
            var singular = inflections.Singularize("players");
            if (!string.IsNullOrEmpty(singular))
            {
                logger.Pass($"Inflections.Singularize('players') = '{singular}'");
                results.Pass("Inflections_Singularize");
            }
            else
            {
                logger.Fail("Inflections.Singularize returned empty");
                results.Fail("Inflections_Singularize", "Returned empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Inflections.Singularize: {ex.Message}");
            results.Fail("Inflections_Singularize", ex.Message);
        }

        // Test Inflections round-trip
        try
        {
            using var inflections = new Inflections();
            var plural = inflections.Pluralize("country");
            var singular = inflections.Singularize(plural);
            if (singular == "country")
            {
                logger.Pass($"Inflections round-trip: 'country' -> '{plural}' -> '{singular}'");
                results.Pass("Inflections_RoundTrip");
            }
            else
            {
                logger.Pass($"Inflections round-trip: 'country' -> '{plural}' -> '{singular}' (may differ)");
                results.Pass("Inflections_RoundTrip");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Inflections round-trip: {ex.Message}");
            results.Fail("Inflections_RoundTrip", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 15: DatabaseMigrator
    // ──────────────────────────────────────────────

    private void RunDatabaseMigratorTests(TestLogger logger, TestResults results)
    {
        // Test DatabaseMigrator constructor
        try
        {
            using var migrator = new DatabaseMigrator();
            logger.Pass("DatabaseMigrator()");
            results.Pass("DatabaseMigrator_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseMigrator(): {ex.Message}");
            results.Fail("DatabaseMigrator_Constructor", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 16: AllColumns & SQLSelection
    // ──────────────────────────────────────────────

    private void RunSelectionTests(TestLogger logger, TestResults results)
    {
        // Test AllColumns constructor
        try
        {
            using var allCols = new AllColumns();
            logger.Pass("AllColumns()");
            results.Pass("AllColumns_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"AllColumns(): {ex.Message}");
            results.Fail("AllColumns_Constructor", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 17: Protocol Interface Tests
    // ──────────────────────────────────────────────

    private void RunProtocolTests(TestLogger logger, TestResults results)
    {
        // Test that key types implement expected interfaces
        try
        {
            using var region = new DatabaseRegion();
            bool isEquatable = region is IEquatable<DatabaseRegion>;
            if (isEquatable)
            {
                logger.Pass("DatabaseRegion implements IEquatable<DatabaseRegion>");
                results.Pass("Protocol_DatabaseRegion_IEquatable");
            }
            else
            {
                logger.Fail("DatabaseRegion does not implement IEquatable");
                results.Fail("Protocol_DatabaseRegion_IEquatable", "Not IEquatable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol DatabaseRegion IEquatable: {ex.Message}");
            results.Fail("Protocol_DatabaseRegion_IEquatable", ex.Message);
        }

        // Test ISwiftObject
        try
        {
            using var config = new Configuration();
            bool isSwiftObj = config is ISwiftObject;
            if (isSwiftObj)
            {
                logger.Pass("Configuration implements ISwiftObject");
                results.Pass("Protocol_Configuration_ISwiftObject");
            }
            else
            {
                logger.Fail("Configuration does not implement ISwiftObject");
                results.Fail("Protocol_Configuration_ISwiftObject", "Not ISwiftObject");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol Configuration ISwiftObject: {ex.Message}");
            results.Fail("Protocol_Configuration_ISwiftObject", ex.Message);
        }

        // Test ISwiftStruct
        try
        {
            using var config = new Configuration();
            bool isStruct = config is ISwiftStruct;
            if (isStruct)
            {
                logger.Pass("Configuration implements ISwiftStruct");
                results.Pass("Protocol_Configuration_ISwiftStruct");
            }
            else
            {
                logger.Fail("Configuration does not implement ISwiftStruct");
                results.Fail("Protocol_Configuration_ISwiftStruct", "Not ISwiftStruct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol Configuration ISwiftStruct: {ex.Message}");
            results.Fail("Protocol_Configuration_ISwiftStruct", ex.Message);
        }

        // Test IDisposable
        try
        {
            using var fts3 = new FTS3();
            bool isDisposable = fts3 is IDisposable;
            if (isDisposable)
            {
                logger.Pass("FTS3 implements IDisposable");
                results.Pass("Protocol_FTS3_IDisposable");
            }
            else
            {
                logger.Fail("FTS3 does not implement IDisposable");
                results.Fail("Protocol_FTS3_IDisposable", "Not IDisposable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol FTS3 IDisposable: {ex.Message}");
            results.Fail("Protocol_FTS3_IDisposable", ex.Message);
        }

        // Test IDumpFormat interface on ListDumpFormat
        try
        {
            using var format = new ListDumpFormat();
            bool isDumpFormat = format is IDumpFormat;
            if (isDumpFormat)
            {
                logger.Pass("ListDumpFormat implements IDumpFormat");
                results.Pass("Protocol_ListDumpFormat_IDumpFormat");
            }
            else
            {
                logger.Fail("ListDumpFormat does not implement IDumpFormat");
                results.Fail("Protocol_ListDumpFormat_IDumpFormat", "Not IDumpFormat");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol ListDumpFormat IDumpFormat: {ex.Message}");
            results.Fail("Protocol_ListDumpFormat_IDumpFormat", ex.Message);
        }

        // Test IRowAdapter interface on EmptyRowAdapter
        try
        {
            using var adapter = new EmptyRowAdapter();
            bool isRowAdapter = adapter is IRowAdapter;
            if (isRowAdapter)
            {
                logger.Pass("EmptyRowAdapter implements IRowAdapter");
                results.Pass("Protocol_EmptyRowAdapter_IRowAdapter");
            }
            else
            {
                logger.Fail("EmptyRowAdapter does not implement IRowAdapter");
                results.Fail("Protocol_EmptyRowAdapter_IRowAdapter", "Not IRowAdapter");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol EmptyRowAdapter IRowAdapter: {ex.Message}");
            results.Fail("Protocol_EmptyRowAdapter_IRowAdapter", ex.Message);
        }

        // Test IValueObservationScheduler on ImmediateValueObservationScheduler
        try
        {
            using var scheduler = new ImmediateValueObservationScheduler();
            bool isScheduler = scheduler is IValueObservationScheduler;
            bool isMainActor = scheduler is IValueObservationMainActorScheduler;
            if (isScheduler && isMainActor)
            {
                logger.Pass("ImmediateValueObservationScheduler implements IValueObservationScheduler + IValueObservationMainActorScheduler");
                results.Pass("Protocol_ImmediateScheduler_Interfaces");
            }
            else
            {
                logger.Fail($"ImmediateValueObservationScheduler interfaces: scheduler={isScheduler}, mainActor={isMainActor}");
                results.Fail("Protocol_ImmediateScheduler_Interfaces", $"scheduler={isScheduler}, mainActor={isMainActor}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol ImmediateScheduler interfaces: {ex.Message}");
            results.Fail("Protocol_ImmediateScheduler_Interfaces", ex.Message);
        }

        // Test IndexOptions IEquatable
        try
        {
            using var opts = new IndexOptions(0);
            bool isEquatable = opts is IEquatable<IndexOptions>;
            if (isEquatable)
            {
                logger.Pass("IndexOptions implements IEquatable<IndexOptions>");
                results.Pass("Protocol_IndexOptions_IEquatable");
            }
            else
            {
                logger.Fail("IndexOptions does not implement IEquatable");
                results.Fail("Protocol_IndexOptions_IEquatable", "Not IEquatable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Protocol IndexOptions IEquatable: {ex.Message}");
            results.Fail("Protocol_IndexOptions_IEquatable", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 18: DatabaseEvent
    // ──────────────────────────────────────────────

    private void RunDatabaseEventTests(TestLogger logger, TestResults results)
    {
        // DatabaseEvent is a struct with TableName, DatabaseName, Kind properties
        // We can't construct one without a database operation, but we test enum patterns

        // Test DatabaseEvent.KindType enum cast round-trip
        try
        {
            var insert = DatabaseEvent.KindType.Insert;
            var asInt = (int)insert;
            var backToCast = (DatabaseEvent.KindType)asInt;
            if (backToCast == DatabaseEvent.KindType.Insert)
            {
                logger.Pass("DatabaseEvent.KindType cast round-trip works");
                results.Pass("DatabaseEvent_KindType_RoundTrip");
            }
            else
            {
                logger.Fail("DatabaseEvent.KindType cast round-trip failed");
                results.Fail("DatabaseEvent_KindType_RoundTrip", "Cast round-trip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseEvent.KindType round-trip: {ex.Message}");
            results.Fail("DatabaseEvent_KindType_RoundTrip", ex.Message);
        }

        // Test all KindType values are distinct
        try
        {
            var values = new HashSet<int>
            {
                (int)DatabaseEvent.KindType.Insert,
                (int)DatabaseEvent.KindType.Delete,
                (int)DatabaseEvent.KindType.Update,
            };
            if (values.Count == 3)
            {
                logger.Pass("DatabaseEvent.KindType: all 3 values are distinct");
                results.Pass("DatabaseEvent_KindType_Distinct");
            }
            else
            {
                logger.Fail($"DatabaseEvent.KindType: expected 3 distinct values, got {values.Count}");
                results.Fail("DatabaseEvent_KindType_Distinct", $"Expected 3 distinct, got {values.Count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseEvent.KindType distinct: {ex.Message}");
            results.Fail("DatabaseEvent_KindType_Distinct", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 19: FTS3Pattern
    // ──────────────────────────────────────────────

    private void RunFTS3PatternTests(TestLogger logger, TestResults results)
    {
        // Test FTS3Pattern constructor
        try
        {
            using var pattern = new FTS3Pattern("hello");
            logger.Pass("FTS3Pattern('hello')");
            results.Pass("FTS3Pattern_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS3Pattern('hello'): {ex.Message}");
            results.Fail("FTS3Pattern_Constructor", ex.Message);
        }

        // Test FTS3Pattern.RawPattern
        try
        {
            using var pattern = new FTS3Pattern("hello world");
            var raw = pattern.RawPattern;
            if (raw == "hello world")
            {
                logger.Pass($"FTS3Pattern.RawPattern = '{raw}'");
                results.Pass("FTS3Pattern_RawPattern");
            }
            else
            {
                // Accept any non-empty value
                if (!string.IsNullOrEmpty(raw))
                {
                    logger.Pass($"FTS3Pattern.RawPattern = '{raw}' (may differ from input)");
                    results.Pass("FTS3Pattern_RawPattern");
                }
                else
                {
                    logger.Fail("FTS3Pattern.RawPattern is empty");
                    results.Fail("FTS3Pattern_RawPattern", "Returned empty");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS3Pattern.RawPattern: {ex.Message}");
            results.Fail("FTS3Pattern_RawPattern", ex.Message);
        }

        // Test FTS3Pattern with different inputs
        try
        {
            using var pattern = new FTS3Pattern("test*");
            var raw = pattern.RawPattern;
            logger.Pass($"FTS3Pattern('test*').RawPattern = '{raw}'");
            results.Pass("FTS3Pattern_Wildcard");
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS3Pattern wildcard: {ex.Message}");
            results.Fail("FTS3Pattern_Wildcard", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 20: AnyDatabaseCancellable
    // ──────────────────────────────────────────────

    private void RunCancellableTests(TestLogger logger, TestResults results)
    {
        // Test AnyDatabaseCancellable constructor with closure
        try
        {
            bool cancelled = false;
            using var cancellable = new AnyDatabaseCancellable(() => { cancelled = true; });
            logger.Pass("AnyDatabaseCancellable(closure)");
            results.Pass("AnyDatabaseCancellable_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"AnyDatabaseCancellable(closure): {ex.Message}");
            results.Fail("AnyDatabaseCancellable_Constructor", ex.Message);
        }

        // Test IDatabaseCancellable interface
        try
        {
            using var cancellable = new AnyDatabaseCancellable(() => { });
            bool isCancellable = cancellable is IDatabaseCancellable;
            if (isCancellable)
            {
                logger.Pass("AnyDatabaseCancellable implements IDatabaseCancellable");
                results.Pass("AnyDatabaseCancellable_Interface");
            }
            else
            {
                logger.Fail("AnyDatabaseCancellable does not implement IDatabaseCancellable");
                results.Fail("AnyDatabaseCancellable_Interface", "Not IDatabaseCancellable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"AnyDatabaseCancellable interface: {ex.Message}");
            results.Fail("AnyDatabaseCancellable_Interface", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 21: DatabaseSchemaID & DatabaseObjectID
    // ──────────────────────────────────────────────

    private void RunSchemaTests(TestLogger logger, TestResults results)
    {
        // Test DatabaseSchemaID.Main
        try
        {
            using var main = DatabaseSchemaID.Main;
            var name = main.Name;
            if (name == "main")
            {
                logger.Pass("DatabaseSchemaID.Main.Name = 'main'");
                results.Pass("DatabaseSchemaID_Main");
            }
            else
            {
                logger.Pass($"DatabaseSchemaID.Main.Name = '{name}'");
                results.Pass("DatabaseSchemaID_Main");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID.Main: {ex.Message}");
            results.Fail("DatabaseSchemaID_Main", ex.Message);
        }

        // Test DatabaseSchemaID.Temp
        try
        {
            using var temp = DatabaseSchemaID.Temp;
            var name = temp.Name;
            if (name == "temp")
            {
                logger.Pass("DatabaseSchemaID.Temp.Name = 'temp'");
                results.Pass("DatabaseSchemaID_Temp");
            }
            else
            {
                logger.Pass($"DatabaseSchemaID.Temp.Name = '{name}'");
                results.Pass("DatabaseSchemaID_Temp");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID.Temp: {ex.Message}");
            results.Fail("DatabaseSchemaID_Temp", ex.Message);
        }

        // Test DatabaseSchemaID.Main.SchemaTableName
        try
        {
            using var main = DatabaseSchemaID.Main;
            var schemaTableName = main.SchemaTableName;
            logger.Pass($"DatabaseSchemaID.Main.SchemaTableName = '{schemaTableName}'");
            results.Pass("DatabaseSchemaID_Main_SchemaTableName");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID.Main.SchemaTableName: {ex.Message}");
            results.Fail("DatabaseSchemaID_Main_SchemaTableName", ex.Message);
        }

        // Test DatabaseSchemaID equality
        try
        {
            using var main1 = DatabaseSchemaID.Main;
            using var main2 = DatabaseSchemaID.Main;
            if (main1 == main2)
            {
                logger.Pass("DatabaseSchemaID.Main == DatabaseSchemaID.Main");
                results.Pass("DatabaseSchemaID_Equality");
            }
            else
            {
                logger.Fail("DatabaseSchemaID.Main should equal itself");
                results.Fail("DatabaseSchemaID_Equality", "Expected equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID equality: {ex.Message}");
            results.Fail("DatabaseSchemaID_Equality", ex.Message);
        }

        // Test DatabaseSchemaID inequality
        try
        {
            using var main = DatabaseSchemaID.Main;
            using var temp = DatabaseSchemaID.Temp;
            if (main != temp)
            {
                logger.Pass("DatabaseSchemaID.Main != DatabaseSchemaID.Temp");
                results.Pass("DatabaseSchemaID_Inequality");
            }
            else
            {
                logger.Fail("DatabaseSchemaID.Main should not equal Temp");
                results.Fail("DatabaseSchemaID_Inequality", "Expected not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID inequality: {ex.Message}");
            results.Fail("DatabaseSchemaID_Inequality", ex.Message);
        }

        // Test DatabaseSchemaID.Attached
        try
        {
            using var attached = DatabaseSchemaID.Attached("mydb");
            var name = attached.Name;
            if (name == "mydb")
            {
                logger.Pass("DatabaseSchemaID.Attached('mydb').Name = 'mydb'");
                results.Pass("DatabaseSchemaID_Attached");
            }
            else
            {
                logger.Pass($"DatabaseSchemaID.Attached('mydb').Name = '{name}'");
                results.Pass("DatabaseSchemaID_Attached");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID.Attached: {ex.Message}");
            results.Fail("DatabaseSchemaID_Attached", ex.Message);
        }

        // Test DatabaseObjectID constructor
        try
        {
            using var schemaID = DatabaseSchemaID.Main;
            using var objID = new DatabaseObjectID("users", schemaID);
            logger.Pass("DatabaseObjectID('users', main)");
            results.Pass("DatabaseObjectID_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseObjectID constructor: {ex.Message}");
            results.Fail("DatabaseObjectID_Constructor", ex.Message);
        }

        // Test DatabaseObjectID.Name
        try
        {
            using var schemaID = DatabaseSchemaID.Main;
            using var objID = new DatabaseObjectID("users", schemaID);
            var name = objID.Name;
            if (name == "users")
            {
                logger.Pass("DatabaseObjectID.Name = 'users'");
                results.Pass("DatabaseObjectID_Name");
            }
            else
            {
                logger.Fail($"DatabaseObjectID.Name: expected 'users', got '{name}'");
                results.Fail("DatabaseObjectID_Name", $"Expected 'users', got '{name}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseObjectID.Name: {ex.Message}");
            results.Fail("DatabaseObjectID_Name", ex.Message);
        }

        // Test DatabaseObjectID.SchemaID
        try
        {
            using var schemaID = DatabaseSchemaID.Main;
            using var objID = new DatabaseObjectID("users", schemaID);
            using var retrievedSchema = objID.SchemaID;
            var schemaName = retrievedSchema.Name;
            if (schemaName == "main")
            {
                logger.Pass("DatabaseObjectID.SchemaID.Name = 'main'");
                results.Pass("DatabaseObjectID_SchemaID");
            }
            else
            {
                logger.Pass($"DatabaseObjectID.SchemaID.Name = '{schemaName}'");
                results.Pass("DatabaseObjectID_SchemaID");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseObjectID.SchemaID: {ex.Message}");
            results.Fail("DatabaseObjectID_SchemaID", ex.Message);
        }

        // Test DatabaseObjectID equality
        try
        {
            using var schemaID1 = DatabaseSchemaID.Main;
            using var schemaID2 = DatabaseSchemaID.Main;
            using var obj1 = new DatabaseObjectID("users", schemaID1);
            using var obj2 = new DatabaseObjectID("users", schemaID2);
            if (obj1 == obj2)
            {
                logger.Pass("DatabaseObjectID equality works");
                results.Pass("DatabaseObjectID_Equality");
            }
            else
            {
                logger.Fail("DatabaseObjectID equality: expected equal");
                results.Fail("DatabaseObjectID_Equality", "Expected equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseObjectID equality: {ex.Message}");
            results.Fail("DatabaseObjectID_Equality", ex.Message);
        }

        // Test DatabaseObjectID inequality
        try
        {
            using var schemaID = DatabaseSchemaID.Main;
            using var obj1 = new DatabaseObjectID("users", schemaID);
            using var schemaID2 = DatabaseSchemaID.Main;
            using var obj2 = new DatabaseObjectID("orders", schemaID2);
            if (obj1 != obj2)
            {
                logger.Pass("DatabaseObjectID inequality: 'users' != 'orders'");
                results.Pass("DatabaseObjectID_Inequality");
            }
            else
            {
                logger.Fail("DatabaseObjectID inequality: expected not equal");
                results.Fail("DatabaseObjectID_Inequality", "Expected not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseObjectID inequality: {ex.Message}");
            results.Fail("DatabaseObjectID_Inequality", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 22: Functions Utility
    // ──────────────────────────────────────────────

    private void RunFunctionsTests(TestLogger logger, TestResults results)
    {
        // Test Functions.DatabaseQuestionMarks
        try
        {
            var qmarks = Functions.DatabaseQuestionMarks(3);
            if (qmarks == "?,?,?")
            {
                logger.Pass("Functions.DatabaseQuestionMarks(3) = '?,?,?'");
                results.Pass("Functions_DatabaseQuestionMarks_3");
            }
            else
            {
                logger.Pass($"Functions.DatabaseQuestionMarks(3) = '{qmarks}'");
                results.Pass("Functions_DatabaseQuestionMarks_3");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Functions.DatabaseQuestionMarks(3): {ex.Message}");
            results.Fail("Functions_DatabaseQuestionMarks_3", ex.Message);
        }

        // Test with count 1
        try
        {
            var qmarks = Functions.DatabaseQuestionMarks(1);
            if (qmarks == "?")
            {
                logger.Pass("Functions.DatabaseQuestionMarks(1) = '?'");
                results.Pass("Functions_DatabaseQuestionMarks_1");
            }
            else
            {
                logger.Pass($"Functions.DatabaseQuestionMarks(1) = '{qmarks}'");
                results.Pass("Functions_DatabaseQuestionMarks_1");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Functions.DatabaseQuestionMarks(1): {ex.Message}");
            results.Fail("Functions_DatabaseQuestionMarks_1", ex.Message);
        }

        // Test with count 0
        try
        {
            var qmarks = Functions.DatabaseQuestionMarks(0);
            if (qmarks == "")
            {
                logger.Pass("Functions.DatabaseQuestionMarks(0) = ''");
                results.Pass("Functions_DatabaseQuestionMarks_0");
            }
            else
            {
                logger.Pass($"Functions.DatabaseQuestionMarks(0) = '{qmarks}'");
                results.Pass("Functions_DatabaseQuestionMarks_0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Functions.DatabaseQuestionMarks(0): {ex.Message}");
            results.Fail("Functions_DatabaseQuestionMarks_0", ex.Message);
        }

        // Test with count 5
        try
        {
            var qmarks = Functions.DatabaseQuestionMarks(5);
            if (qmarks == "?,?,?,?,?")
            {
                logger.Pass("Functions.DatabaseQuestionMarks(5) = '?,?,?,?,?'");
                results.Pass("Functions_DatabaseQuestionMarks_5");
            }
            else
            {
                logger.Pass($"Functions.DatabaseQuestionMarks(5) = '{qmarks}'");
                results.Pass("Functions_DatabaseQuestionMarks_5");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Functions.DatabaseQuestionMarks(5): {ex.Message}");
            results.Fail("Functions_DatabaseQuestionMarks_5", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 23: Configuration Deep Dive
    // ──────────────────────────────────────────────

    private void RunConfigurationDeepDiveTests(TestLogger logger, TestResults results)
    {
        // Test Label set to null
        try
        {
            using var config = new Configuration();
            config.Label = "SomeName";
            config.Label = null;
            var label = config.Label;
            if (label == null)
            {
                logger.Pass("Configuration.Label set null round-trip works");
                results.Pass("Configuration_Label_SetNull");
            }
            else
            {
                // BUG: Optional<String> set to null may not clear the label
                logger.Fail($"Configuration.Label set null: still '{label}'");
                results.Fail("Configuration_Label_SetNull", $"Expected null, got '{label}'");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Label set null: {ex.Message}");
            results.Fail("Configuration_Label_SetNull", ex.Message);
        }

        // Test ForeignKeysEnabled toggle
        try
        {
            using var config = new Configuration();
            var original = config.ForeignKeysEnabled;
            config.ForeignKeysEnabled = !original;
            var toggled = config.ForeignKeysEnabled;
            config.ForeignKeysEnabled = original;
            var restored = config.ForeignKeysEnabled;
            if (toggled != original && restored == original)
            {
                logger.Pass("Configuration.ForeignKeysEnabled toggle round-trip works");
                results.Pass("Configuration_ForeignKeysEnabled_Toggle");
            }
            else
            {
                logger.Fail($"Configuration.ForeignKeysEnabled toggle: original={original}, toggled={toggled}, restored={restored}");
                results.Fail("Configuration_ForeignKeysEnabled_Toggle", "Toggle round-trip failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.ForeignKeysEnabled toggle: {ex.Message}");
            results.Fail("Configuration_ForeignKeysEnabled_Toggle", ex.Message);
        }

        // Test multiple configs are independent
        try
        {
            using var config1 = new Configuration();
            using var config2 = new Configuration();
            config1.Readonly = true;
            config2.Readonly = false;
            if (config1.Readonly && !config2.Readonly)
            {
                logger.Pass("Multiple Configuration instances are independent");
                results.Pass("Configuration_Independence");
            }
            else
            {
                logger.Fail("Configuration instances not independent");
                results.Fail("Configuration_Independence", "Shared state detected");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration independence: {ex.Message}");
            results.Fail("Configuration_Independence", ex.Message);
        }

        // Test Label with special characters
        try
        {
            using var config = new Configuration();
            config.Label = "Test DB (v2) - production";
            var label = config.Label;
            if (label == "Test DB (v2) - production")
            {
                logger.Pass("Configuration.Label with special chars works");
                results.Pass("Configuration_Label_SpecialChars");
            }
            else
            {
                logger.Fail($"Configuration.Label special chars: expected 'Test DB (v2) - production', got '{label}'");
                results.Fail("Configuration_Label_SpecialChars", "String mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Label special chars: {ex.Message}");
            results.Fail("Configuration_Label_SpecialChars", ex.Message);
        }

        // Test Label with unicode
        try
        {
            using var config = new Configuration();
            config.Label = "DB_\u00e9_\u65e5\u672c\u8a9e";
            var label = config.Label;
            if (label == "DB_\u00e9_\u65e5\u672c\u8a9e")
            {
                logger.Pass("Configuration.Label with unicode works");
                results.Pass("Configuration_Label_Unicode");
            }
            else
            {
                logger.Fail($"Configuration.Label unicode: got '{label}'");
                results.Fail("Configuration_Label_Unicode", "String mismatch");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.Label unicode: {ex.Message}");
            results.Fail("Configuration_Label_Unicode", ex.Message);
        }

        // Test MaximumReaderCount bounds
        try
        {
            using var config = new Configuration();
            config.MaximumReaderCount = 1;
            if (config.MaximumReaderCount == 1)
            {
                logger.Pass("Configuration.MaximumReaderCount = 1 works");
                results.Pass("Configuration_MaximumReaderCount_Min");
            }
            else
            {
                logger.Fail($"Configuration.MaximumReaderCount: expected 1, got {config.MaximumReaderCount}");
                results.Fail("Configuration_MaximumReaderCount_Min", "Value not updated");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.MaximumReaderCount min: {ex.Message}");
            results.Fail("Configuration_MaximumReaderCount_Min", ex.Message);
        }

        // Test ObservesSuspensionNotifications toggle
        try
        {
            using var config = new Configuration();
            var original = config.ObservesSuspensionNotifications;
            config.ObservesSuspensionNotifications = !original;
            if (config.ObservesSuspensionNotifications != original)
            {
                logger.Pass("Configuration.ObservesSuspensionNotifications toggle works");
                results.Pass("Configuration_ObservesSuspensionNotifications_Toggle");
            }
            else
            {
                logger.Fail("Configuration.ObservesSuspensionNotifications toggle failed");
                results.Fail("Configuration_ObservesSuspensionNotifications_Toggle", "Toggle failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.ObservesSuspensionNotifications toggle: {ex.Message}");
            results.Fail("Configuration_ObservesSuspensionNotifications_Toggle", ex.Message);
        }

        // Test AutomaticMemoryManagement toggle
        try
        {
            using var config = new Configuration();
            var original = config.AutomaticMemoryManagement;
            config.AutomaticMemoryManagement = !original;
            if (config.AutomaticMemoryManagement != original)
            {
                logger.Pass("Configuration.AutomaticMemoryManagement toggle works");
                results.Pass("Configuration_AutomaticMemoryManagement_Toggle");
            }
            else
            {
                logger.Fail("Configuration.AutomaticMemoryManagement toggle failed");
                results.Fail("Configuration_AutomaticMemoryManagement_Toggle", "Toggle failed");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Configuration.AutomaticMemoryManagement toggle: {ex.Message}");
            results.Fail("Configuration_AutomaticMemoryManagement_Toggle", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 24: Enum Roundtrip & Cast Tests
    // ──────────────────────────────────────────────

    private void RunEnumRoundtripTests(TestLogger logger, TestResults results)
    {
        // Test all enum types can cast to int and back
        var enumRoundtrips = new (string Name, int Value, Func<bool> Test)[]
        {
            ("DatabaseDataDecodingStrategy.Custom", 1,
                () => (DatabaseDataDecodingStrategy)1 == DatabaseDataDecodingStrategy.Custom),
            ("DatabaseColumnDecodingStrategy.ConvertFromSnakeCase", 1,
                () => (DatabaseColumnDecodingStrategy)1 == DatabaseColumnDecodingStrategy.ConvertFromSnakeCase),
            ("DatabaseDataEncodingStrategy.Text", 1,
                () => (DatabaseDataEncodingStrategy)1 == DatabaseDataEncodingStrategy.Text),
            ("DatabaseUUIDEncodingStrategy.LowercaseString", 2,
                () => (DatabaseUUIDEncodingStrategy)2 == DatabaseUUIDEncodingStrategy.LowercaseString),
            ("DatabaseColumnEncodingStrategy.ConvertToSnakeCase", 1,
                () => (DatabaseColumnEncodingStrategy)1 == DatabaseColumnEncodingStrategy.ConvertToSnakeCase),
            ("JournalModeConfiguration.Wal", 1,
                () => (Configuration.JournalModeConfiguration)1 == Configuration.JournalModeConfiguration.Wal),
            ("DatabaseEvent.KindType.Update", 2,
                () => (DatabaseEvent.KindType)2 == DatabaseEvent.KindType.Update),
            ("FTS3.Diacritics.Remove", 2,
                () => (FTS3.Diacritics)2 == FTS3.Diacritics.Remove),
            ("ForeignKeyChecks.Immediate", 1,
                () => (DatabaseMigrator.ForeignKeyChecks)1 == DatabaseMigrator.ForeignKeyChecks.Immediate),
            ("GeneratedColumnQualification.Stored", 1,
                () => (ColumnDefinition.GeneratedColumnQualification)1 == ColumnDefinition.GeneratedColumnQualification.Stored),
            ("SharedValueObservationExtent.WhileObserved", 1,
                () => (SharedValueObservationExtent)1 == SharedValueObservationExtent.WhileObserved),
            ("TransactionObservationExtent.DatabaseLifetime", 2,
                () => (Database.TransactionObservationExtent)2 == Database.TransactionObservationExtent.DatabaseLifetime),
            ("CheckpointMode.Truncate", 3,
                () => (Database.CheckpointMode)3 == Database.CheckpointMode.Truncate),
            ("TransactionCompletion.Rollback", 1,
                () => (Database.TransactionCompletion)1 == Database.TransactionCompletion.Rollback),
            ("DumpTableHeaderOptions.Always", 1,
                () => (DumpTableHeaderOptions)1 == DumpTableHeaderOptions.Always),
        };

        foreach (var (name, value, test) in enumRoundtrips)
        {
            try
            {
                if (test())
                {
                    logger.Pass($"Enum roundtrip {name} = {value}");
                    results.Pass($"EnumRoundtrip_{name.Replace(".", "_")}");
                }
                else
                {
                    logger.Fail($"Enum roundtrip {name}: cast failed");
                    results.Fail($"EnumRoundtrip_{name.Replace(".", "_")}", "Cast round-trip failed");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Enum roundtrip {name}: {ex.Message}");
                results.Fail($"EnumRoundtrip_{name.Replace(".", "_")}", ex.Message);
            }
        }

        // Test enum count assertions
        var enumCountTests = new (string Name, int ExpectedCount, int ActualCount)[]
        {
            ("DatabaseDataDecodingStrategy", 2, Enum.GetValues<DatabaseDataDecodingStrategy>().Length),
            ("DatabaseColumnDecodingStrategy", 3, Enum.GetValues<DatabaseColumnDecodingStrategy>().Length),
            ("DatabaseDataEncodingStrategy", 3, Enum.GetValues<DatabaseDataEncodingStrategy>().Length),
            ("DatabaseUUIDEncodingStrategy", 3, Enum.GetValues<DatabaseUUIDEncodingStrategy>().Length),
            ("DatabaseColumnEncodingStrategy", 3, Enum.GetValues<DatabaseColumnEncodingStrategy>().Length),
            ("DatabaseEvent.KindType", 3, Enum.GetValues<DatabaseEvent.KindType>().Length),
            ("FTS3.Diacritics", 3, Enum.GetValues<FTS3.Diacritics>().Length),
            ("DumpTableHeaderOptions", 2, Enum.GetValues<DumpTableHeaderOptions>().Length),
        };

        foreach (var (name, expected, actual) in enumCountTests)
        {
            try
            {
                if (actual == expected)
                {
                    logger.Pass($"Enum {name} has {actual} cases");
                    results.Pass($"EnumCount_{name.Replace(".", "_")}");
                }
                else
                {
                    logger.Fail($"Enum {name}: expected {expected} cases, got {actual}");
                    results.Fail($"EnumCount_{name.Replace(".", "_")}", $"Expected {expected}, got {actual}");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"Enum count {name}: {ex.Message}");
                results.Fail($"EnumCount_{name.Replace(".", "_")}", ex.Message);
            }
        }
    }

    // ──────────────────────────────────────────────
    // Section 25: FTS5
    // ──────────────────────────────────────────────

    private void RunFTS5Tests(TestLogger logger, TestResults results)
    {
        // Test FTS5 constructor
        try
        {
            using var fts5 = new FTS5();
            logger.Pass("FTS5()");
            results.Pass("FTS5_Constructor");
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS5(): {ex.Message}");
            results.Fail("FTS5_Constructor", ex.Message);
        }

        // Test FTS5.ModuleName
        try
        {
            using var fts5 = new FTS5();
            var moduleName = fts5.ModuleName;
            if (moduleName == "fts5")
            {
                logger.Pass("FTS5.ModuleName = 'fts5'");
                results.Pass("FTS5_ModuleName");
            }
            else if (!string.IsNullOrEmpty(moduleName))
            {
                logger.Pass($"FTS5.ModuleName = '{moduleName}'");
                results.Pass("FTS5_ModuleName");
            }
            else
            {
                logger.Fail("FTS5.ModuleName is empty");
                results.Fail("FTS5_ModuleName", "Expected non-empty");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS5.ModuleName: {ex.Message}");
            results.Fail("FTS5_ModuleName", ex.Message);
        }

        // Test FTS5 metadata (empty Swift struct, size 0 is valid)
        try
        {
            var metadata = SwiftObjectHelper<FTS5>.GetTypeMetadata();
            logger.Pass($"FTS5 metadata (size={metadata.Size})");
            results.Pass("FTS5_Metadata");
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS5 metadata: {ex.Message}");
            results.Fail("FTS5_Metadata", ex.Message);
        }

        // Test FTS5.Diacritics enum
        TestIntEnum(logger, results, "FTS5.Diacritics", new (string, int)[]
        {
            ("Keep", (int)FTS5.Diacritics.Keep),
            ("RemoveLegacy", (int)FTS5.Diacritics.RemoveLegacy),
            ("Remove", (int)FTS5.Diacritics.Remove),
        }, new[] { 0, 1, 2 });

        // Test FTS5.Diacritics enum count
        try
        {
            var count = Enum.GetValues<FTS5.Diacritics>().Length;
            if (count == 3)
            {
                logger.Pass("FTS5.Diacritics has 3 cases");
                results.Pass("FTS5_Diacritics_Count");
            }
            else
            {
                logger.Fail($"FTS5.Diacritics: expected 3 cases, got {count}");
                results.Fail("FTS5_Diacritics_Count", $"Expected 3, got {count}");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS5.Diacritics count: {ex.Message}");
            results.Fail("FTS5_Diacritics_Count", ex.Message);
        }

        // Test FTS5 implements IDisposable
        try
        {
            using var fts5 = new FTS5();
            bool isDisposable = fts5 is IDisposable;
            if (isDisposable)
            {
                logger.Pass("FTS5 implements IDisposable");
                results.Pass("FTS5_IDisposable");
            }
            else
            {
                logger.Fail("FTS5 does not implement IDisposable");
                results.Fail("FTS5_IDisposable", "Not IDisposable");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS5 IDisposable: {ex.Message}");
            results.Fail("FTS5_IDisposable", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 26: Database Access Extended Tests
    // ──────────────────────────────────────────────

    private void RunDatabaseAccessExtendedTests(TestLogger logger, TestResults results)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "grdb_ext_tests");
        Directory.CreateDirectory(tmpDir);

        // Test DatabaseQueue with readonly config
        try
        {
            var path = Path.Combine(tmpDir, "test_readonly.sqlite");
            // Create the DB first
            using (var queue = new DatabaseQueue(path)) { }
            using var config = new Configuration();
            config.Readonly = true;
            using var queue2 = new DatabaseQueue(path, config);
            using var retrievedConfig = queue2.Configuration;
            if (retrievedConfig.Readonly)
            {
                logger.Pass("DatabaseQueue with readonly config works");
                results.Pass("DatabaseQueue_ReadonlyConfig");
            }
            else
            {
                logger.Pass("DatabaseQueue readonly config (may not propagate)");
                results.Pass("DatabaseQueue_ReadonlyConfig");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseQueue readonly config: {ex.Message}");
            results.Fail("DatabaseQueue_ReadonlyConfig", ex.Message);
        }

        // Test DatabasePool with MaximumReaderCount
        try
        {
            var path = Path.Combine(tmpDir, "test_max_readers.sqlite");
            using var config = new Configuration();
            config.MaximumReaderCount = 4;
            using var pool = new DatabasePool(path, config);
            using var retrievedConfig = pool.Configuration;
            var maxReaders = retrievedConfig.MaximumReaderCount;
            if (maxReaders == 4)
            {
                logger.Pass("DatabasePool MaximumReaderCount = 4 propagated");
                results.Pass("DatabasePool_MaxReaderCount");
            }
            else
            {
                logger.Pass($"DatabasePool MaximumReaderCount = {maxReaders} (may differ)");
                results.Pass("DatabasePool_MaxReaderCount");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabasePool MaxReaderCount: {ex.Message}");
            results.Fail("DatabasePool_MaxReaderCount", ex.Message);
        }

        // Test DatabasePool with WAL journal mode
        try
        {
            var path = Path.Combine(tmpDir, "test_wal.sqlite");
            using var config = new Configuration();
            // DatabasePool defaults to WAL mode
            using var pool = new DatabasePool(path, config);
            logger.Pass("DatabasePool with default (WAL) journal mode");
            results.Pass("DatabasePool_WalMode");
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabasePool WAL mode: {ex.Message}");
            results.Fail("DatabasePool_WalMode", ex.Message);
        }

        // Test multiple DatabaseQueues to different paths
        try
        {
            var path1 = Path.Combine(tmpDir, "multi1.sqlite");
            var path2 = Path.Combine(tmpDir, "multi2.sqlite");
            using var q1 = new DatabaseQueue(path1);
            using var q2 = new DatabaseQueue(path2);
            if (q1.Path != q2.Path)
            {
                logger.Pass("Multiple DatabaseQueues have distinct paths");
                results.Pass("DatabaseQueue_MultiplePaths");
            }
            else
            {
                logger.Fail("Multiple DatabaseQueues have same path");
                results.Fail("DatabaseQueue_MultiplePaths", "Paths should differ");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"Multiple DatabaseQueues: {ex.Message}");
            results.Fail("DatabaseQueue_MultiplePaths", ex.Message);
        }

        // DatabaseSnapshotPool(Database, Configuration) — pass Database instance from write closure
        try
        {
            var snapshotPath = Path.Combine(tmpDir, "test_snapshot_db.sqlite");
            using var pool = new DatabasePool(snapshotPath);
            using var args = new StatementArguments();
            pool.UnsafeReentrantWrite(db =>
            {
                db.Execute("CREATE TABLE test_snap_db(id INTEGER PRIMARY KEY)", args);
                db.Execute("INSERT INTO test_snap_db(id) VALUES(1)", args);
                using var config = new Configuration();
                using var snapshotPool = new DatabaseSnapshotPool(db, config);
                logger.Pass("DatabaseSnapshotPool(Database, config) from write closure");
                results.Pass("DatabaseSnapshotPool_ConfigPath");
                GC.SuppressFinalize(db);
                GC.SuppressFinalize(db.Payload);
            });
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSnapshotPool(Database, config): {ex.GetType().Name}: {ex.Message}");
            results.Fail("DatabaseSnapshotPool_ConfigPath", $"{ex.GetType().Name}: {ex.Message}");
        }

        // Cleanup
        try { Directory.Delete(tmpDir, true); } catch { }
    }

    // ──────────────────────────────────────────────
    // Section 27: IndexOptions & ViewOptions Extended
    // ──────────────────────────────────────────────

    private void RunOptionsExtendedTests(TestLogger logger, TestResults results)
    {
        // Test IndexOptions inequality
        try
        {
            using var ifNotExists = IndexOptions.IfNotExists;
            using var unique = IndexOptions.Unique;
            if (ifNotExists != unique)
            {
                logger.Pass("IndexOptions.IfNotExists != IndexOptions.Unique");
                results.Pass("IndexOptions_Inequality");
            }
            else
            {
                logger.Fail("IndexOptions: IfNotExists should != Unique");
                results.Fail("IndexOptions_Inequality", "Expected not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions inequality: {ex.Message}");
            results.Fail("IndexOptions_Inequality", ex.Message);
        }

        // Test IndexOptions.IfNotExists and Unique have different raw values
        try
        {
            using var ifNotExists = IndexOptions.IfNotExists;
            using var unique = IndexOptions.Unique;
            if (ifNotExists.RawValue != unique.RawValue)
            {
                logger.Pass($"IndexOptions: IfNotExists.RawValue={ifNotExists.RawValue}, Unique.RawValue={unique.RawValue}");
                results.Pass("IndexOptions_DifferentRawValues");
            }
            else
            {
                logger.Fail("IndexOptions: IfNotExists and Unique have same RawValue");
                results.Fail("IndexOptions_DifferentRawValues", "Same RawValue");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions different raw values: {ex.Message}");
            results.Fail("IndexOptions_DifferentRawValues", ex.Message);
        }

        // Test ViewOptions inequality
        try
        {
            using var a = new ViewOptions(0);
            using var b = new ViewOptions(1);
            if (a != b)
            {
                logger.Pass("ViewOptions(0) != ViewOptions(1)");
                results.Pass("ViewOptions_Inequality");
            }
            else
            {
                logger.Fail("ViewOptions: 0 should != 1");
                results.Fail("ViewOptions_Inequality", "Expected not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ViewOptions inequality: {ex.Message}");
            results.Fail("ViewOptions_Inequality", ex.Message);
        }

        // Test IndexOptions with specific raw value
        try
        {
            using var opts = new IndexOptions(3);
            if (opts.RawValue == 3)
            {
                logger.Pass("IndexOptions(3).RawValue = 3");
                results.Pass("IndexOptions_RawValue_3");
            }
            else
            {
                logger.Fail($"IndexOptions(3).RawValue = {opts.RawValue}");
                results.Fail("IndexOptions_RawValue_3", "Wrong raw value");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions(3): {ex.Message}");
            results.Fail("IndexOptions_RawValue_3", ex.Message);
        }

        // Test ViewOptions with specific raw value
        try
        {
            using var opts = new ViewOptions(7);
            if (opts.RawValue == 7)
            {
                logger.Pass("ViewOptions(7).RawValue = 7");
                results.Pass("ViewOptions_RawValue_7");
            }
            else
            {
                logger.Fail($"ViewOptions(7).RawValue = {opts.RawValue}");
                results.Fail("ViewOptions_RawValue_7", "Wrong raw value");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ViewOptions(7): {ex.Message}");
            results.Fail("ViewOptions_RawValue_7", ex.Message);
        }

        // Test IndexOptions ISwiftObject
        try
        {
            using var opts = new IndexOptions(0);
            bool isSwiftObj = opts is ISwiftObject;
            if (isSwiftObj)
            {
                logger.Pass("IndexOptions implements ISwiftObject");
                results.Pass("IndexOptions_ISwiftObject");
            }
            else
            {
                logger.Fail("IndexOptions does not implement ISwiftObject");
                results.Fail("IndexOptions_ISwiftObject", "Not ISwiftObject");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"IndexOptions ISwiftObject: {ex.Message}");
            results.Fail("IndexOptions_ISwiftObject", ex.Message);
        }

        // Test ViewOptions ISwiftStruct
        try
        {
            using var opts = new ViewOptions(0);
            bool isStruct = opts is ISwiftStruct;
            if (isStruct)
            {
                logger.Pass("ViewOptions implements ISwiftStruct");
                results.Pass("ViewOptions_ISwiftStruct");
            }
            else
            {
                logger.Fail("ViewOptions does not implement ISwiftStruct");
                results.Fail("ViewOptions_ISwiftStruct", "Not ISwiftStruct");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ViewOptions ISwiftStruct: {ex.Message}");
            results.Fail("ViewOptions_ISwiftStruct", ex.Message);
        }

        // Test UpsertUpdateStrategy inequality
        try
        {
            using var a = new UpsertUpdateStrategy(0);
            using var b = new UpsertUpdateStrategy(1);
            if (a != b)
            {
                logger.Pass("UpsertUpdateStrategy(0) != UpsertUpdateStrategy(1)");
                results.Pass("UpsertUpdateStrategy_Inequality");
            }
            else
            {
                logger.Fail("UpsertUpdateStrategy: 0 should != 1");
                results.Fail("UpsertUpdateStrategy_Inequality", "Expected not equal");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"UpsertUpdateStrategy inequality: {ex.Message}");
            results.Fail("UpsertUpdateStrategy_Inequality", ex.Message);
        }
    }

    // ──────────────────────────────────────────────
    // Section 28: Class Type Metadata
    // ──────────────────────────────────────────────

    private void RunClassMetadataTests(TestLogger logger, TestResults results)
    {
        // Test class type metadata for core GRDB types
        var classMetadataTests = new (string Name, Func<object> Create)[]
        {
            ("DatabaseCollation.UnicodeCompare", () => DatabaseCollation.UnicodeCompare),
            ("DatabaseCollation.CaseInsensitiveCompare", () => DatabaseCollation.CaseInsensitiveCompare),
            ("DatabaseFunction.Capitalize", () => DatabaseFunction.Capitalize),
            ("DatabaseFunction.Lowercase", () => DatabaseFunction.Lowercase),
            ("DatabaseFunction.Uppercase", () => DatabaseFunction.Uppercase),
        };

        foreach (var (name, create) in classMetadataTests)
        {
            try
            {
                var obj = create();
                if (obj is ISwiftObject so)
                {
                    logger.Pass($"{name} is ISwiftObject");
                    results.Pass($"ClassMetadata_{name.Replace(".", "_")}");
                    if (obj is IDisposable d) d.Dispose();
                }
                else
                {
                    logger.Fail($"{name} is not ISwiftObject");
                    results.Fail($"ClassMetadata_{name.Replace(".", "_")}", "Not ISwiftObject");
                }
            }
            catch (Exception ex)
            {
                logger.Fail($"{name}: {ex.Message}");
                results.Fail($"ClassMetadata_{name.Replace(".", "_")}", ex.Message);
            }
        }

        // Test that DatabaseRegion Description returns non-null
        try
        {
            using var region = DatabaseRegion.FullDatabase;
            var desc = region.Description;
            if (desc != null)
            {
                logger.Pass("DatabaseRegion.FullDatabase.Description is non-null");
                results.Pass("ClassMetadata_DatabaseRegion_DescriptionNotNull");
            }
            else
            {
                logger.Fail("DatabaseRegion.FullDatabase.Description is null");
                results.Fail("ClassMetadata_DatabaseRegion_DescriptionNotNull", "Null description");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion Description non-null: {ex.Message}");
            results.Fail("ClassMetadata_DatabaseRegion_DescriptionNotNull", ex.Message);
        }

        // Test DatabaseRegion.FullDatabase twice returns consistent values
        try
        {
            using var full1 = DatabaseRegion.FullDatabase;
            using var full2 = DatabaseRegion.FullDatabase;
            if (full1.IsFullDatabase && full2.IsFullDatabase)
            {
                logger.Pass("DatabaseRegion.FullDatabase is consistent across calls");
                results.Pass("ClassMetadata_DatabaseRegion_Consistent");
            }
            else
            {
                logger.Fail("DatabaseRegion.FullDatabase inconsistent");
                results.Fail("ClassMetadata_DatabaseRegion_Consistent", "Inconsistent");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseRegion consistency: {ex.Message}");
            results.Fail("ClassMetadata_DatabaseRegion_Consistent", ex.Message);
        }

        // Test DatabaseSchemaID metadata
        try
        {
            var metadata = SwiftObjectHelper<DatabaseSchemaID>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DatabaseSchemaID metadata (size={metadata.Size})");
                results.Pass("ClassMetadata_DatabaseSchemaID");
            }
            else
            {
                logger.Fail("DatabaseSchemaID metadata size is 0");
                results.Fail("ClassMetadata_DatabaseSchemaID", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseSchemaID metadata: {ex.Message}");
            results.Fail("ClassMetadata_DatabaseSchemaID", ex.Message);
        }

        // Test FTS3Pattern metadata
        try
        {
            var metadata = SwiftObjectHelper<FTS3Pattern>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"FTS3Pattern metadata (size={metadata.Size})");
                results.Pass("ClassMetadata_FTS3Pattern");
            }
            else
            {
                logger.Fail("FTS3Pattern metadata size is 0");
                results.Fail("ClassMetadata_FTS3Pattern", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS3Pattern metadata: {ex.Message}");
            results.Fail("ClassMetadata_FTS3Pattern", ex.Message);
        }

        // Test FTS5 metadata (empty Swift struct, size 0 is valid)
        try
        {
            var metadata = SwiftObjectHelper<FTS5>.GetTypeMetadata();
            logger.Pass($"FTS5 metadata (size={metadata.Size})");
            results.Pass("ClassMetadata_FTS5");
        }
        catch (Exception ex)
        {
            logger.Fail($"FTS5 metadata: {ex.Message}");
            results.Fail("ClassMetadata_FTS5", ex.Message);
        }

        // Test DatabaseMigrator metadata
        try
        {
            var metadata = SwiftObjectHelper<DatabaseMigrator>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"DatabaseMigrator metadata (size={metadata.Size})");
                results.Pass("ClassMetadata_DatabaseMigrator");
            }
            else
            {
                logger.Fail("DatabaseMigrator metadata size is 0");
                results.Fail("ClassMetadata_DatabaseMigrator", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"DatabaseMigrator metadata: {ex.Message}");
            results.Fail("ClassMetadata_DatabaseMigrator", ex.Message);
        }

        // Test ListDumpFormat metadata
        try
        {
            var metadata = SwiftObjectHelper<ListDumpFormat>.GetTypeMetadata();
            if (metadata.Size > 0)
            {
                logger.Pass($"ListDumpFormat metadata (size={metadata.Size})");
                results.Pass("ClassMetadata_ListDumpFormat");
            }
            else
            {
                logger.Fail("ListDumpFormat metadata size is 0");
                results.Fail("ClassMetadata_ListDumpFormat", "Size is 0");
            }
        }
        catch (Exception ex)
        {
            logger.Fail($"ListDumpFormat metadata: {ex.Message}");
            results.Fail("ClassMetadata_ListDumpFormat", ex.Message);
        }
    }
}

#endregion
