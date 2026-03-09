# SwiftBindings.Stripe.Issuing

Native Swift interop bindings for [StripeIssuing](https://github.com/stripe/stripe-ios.git), the Stripe card issuing module.

## Installation

```
dotnet add package SwiftBindings.Stripe.Issuing
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
