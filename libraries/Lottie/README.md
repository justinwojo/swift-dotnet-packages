# Swift.Lottie

.NET bindings for [Lottie](https://github.com/airbnb/lottie-ios.git), a Swift library.

## Installation

```
dotnet add package Swift.Lottie
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
