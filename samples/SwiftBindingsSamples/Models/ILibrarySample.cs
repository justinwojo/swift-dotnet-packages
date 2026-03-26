using UIKit;

namespace SwiftBindingsSamples.Models;

/// <summary>
/// Interface for library sample pages that demonstrate real-world usage of SwiftBindings libraries.
/// </summary>
public interface ILibrarySample
{
    string LibraryName { get; }
    string PackageName { get; }
    string Version { get; }
    string Description { get; }

    /// <summary>
    /// Create the sample UIView demonstrating real usage of the library.
    /// Called on the main thread after the view controller loads.
    /// </summary>
    UIView CreateSampleView(nfloat width);
}
