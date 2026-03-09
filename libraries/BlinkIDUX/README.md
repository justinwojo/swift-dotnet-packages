# SwiftBindings.BlinkIDUX

Native Swift interop bindings for [BlinkIDUX](https://github.com/BlinkID/blinkid-ios.git), the BlinkID scanning UX components.

## Installation

```
dotnet add package SwiftBindings.BlinkIDUX
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

## Building from Source

```bash
# 1. Build the xcframework from SPM
./build-xcframework.sh

# 2. Build the package (SDK generates bindings automatically)
dotnet build
```

## License

The bindings are MIT licensed.
