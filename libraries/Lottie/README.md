# Swift.Lottie

.NET bindings for [Lottie](https://github.com/airbnb/lottie-ios), Airbnb's library for rendering After Effects animations natively on iOS.

## Installation

```
dotnet add package Swift.Lottie
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

## Usage

```csharp
using Lottie;

// Access the shared configuration
var config = LottieConfiguration.Shared;

// Work with loop modes
var loopMode = LottieLoopMode.Loop;
```

## Building from Source

```bash
# 1. Build the xcframework from SPM
./build-xcframework.sh

# 2. Build the package (SDK generates bindings automatically)
dotnet build
```

## License

The bindings are MIT licensed. Lottie itself is Apache 2.0 licensed — see [Lottie's license](https://github.com/airbnb/lottie-ios/blob/master/LICENSE).
