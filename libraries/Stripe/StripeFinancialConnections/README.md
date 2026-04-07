# SwiftBindings.Stripe.FinancialConnections

Native Swift interop bindings for [StripeFinancialConnections](https://github.com/stripe/stripe-ios.git), the Stripe financial account linking module.

## Installation

```
dotnet add package SwiftBindings.Stripe.FinancialConnections
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
