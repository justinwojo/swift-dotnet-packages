using Nuke.Common;
using Serilog;
using SwiftBindings.Build.Helpers;

partial class Build
{
    /// <summary>
    /// Smoke-test target. Walks <c>libraries/*/library.json</c>, prints library
    /// name, mode, product count, and whether any product is internal. Used
    /// during Session 1 validation to confirm <c>library.json</c> parsing
    /// covers all 8 libraries before any actual build runs.
    /// </summary>
    Target ListLibraries => _ => _
        .Description("Enumerate every library in libraries/ and print mode + product summary")
        .Executes(() =>
        {
            var libs = DiscoverLibraries().ToList();
            Log.Information("Found {Count} libraries:", libs.Count);

            foreach (var lib in libs)
            {
                try
                {
                    var config = LibraryConfigLoader.Load(LibraryConfigPath(lib));
                    var hasInternal = config.Products.Any(p => p.Internal);
                    Log.Information(
                        "  {Library,-12} mode={Mode,-7} products={Count,2}{Internal}",
                        lib,
                        config.Mode,
                        config.Products.Count,
                        hasInternal ? "  [has internal products]" : "");
                }
                catch (Exception ex)
                {
                    Log.Error("  {Library,-12} FAILED to load: {Message}", lib, ex.Message);
                    throw;
                }
            }
        });
}
