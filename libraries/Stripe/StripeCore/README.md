# SwiftBindings.Stripe.Core

Native Swift interop bindings for [StripeCore](https://github.com/stripe/stripe-ios.git), the Stripe core networking and shared infrastructure module.

## Installation

```
dotnet add package SwiftBindings.Stripe.Core
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

## Building from Source

```bash
# Build all Stripe products end-to-end (xcframeworks + bindings + dotnet build)
./build.sh BuildLibrary --library Stripe --all-products
```

## License

The bindings are MIT licensed.
