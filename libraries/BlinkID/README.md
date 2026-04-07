# SwiftBindings.BlinkID

Native Swift interop bindings for [BlinkID](https://github.com/BlinkID/blinkid-swift-package.git), Microblink's ID scanning library.

## Installation

```
dotnet add package SwiftBindings.BlinkID
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

## Building from Source

```bash
# One-time: install the pinned Nuke CLI from .config/dotnet-tools.json
dotnet tool restore

# Build the package end-to-end (xcframework + bindings + dotnet build)
dotnet nuke BuildLibrary --library BlinkID
```

## License

The bindings are MIT licensed.
