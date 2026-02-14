# Swift.StripePaymentSheet

.NET bindings for [StripePaymentSheet](https://github.com/stripe/stripe-ios-spm.git), a Swift library.

## Installation

```
dotnet add package Swift.StripePaymentSheet
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

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

The bindings are MIT licensed.
