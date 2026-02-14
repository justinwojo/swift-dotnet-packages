# Swift.Nuke

.NET bindings for [Nuke](https://github.com/kean/Nuke), a powerful image loading and caching framework for Swift.

## Installation

```
dotnet add package Swift.Nuke
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

## Usage

```csharp
using Swift.Nuke;

// Create an image request
var request = new ImageRequest(url: "https://example.com/image.jpg");

// Use ImagePipeline for loading
var pipeline = ImagePipeline.Shared;
```

## Building from Source

```bash
# 1. Build the xcframework from SPM
./build-xcframework.sh

# 2. Generate C# bindings
./generate-bindings.sh

# 3. Build the package
dotnet build
```

## License

The bindings are MIT licensed. Nuke itself is MIT licensed — see [Nuke's license](https://github.com/kean/Nuke/blob/main/LICENSE).
