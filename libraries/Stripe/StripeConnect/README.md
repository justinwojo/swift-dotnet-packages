# SwiftBindings.Stripe.Connect

Native Swift interop bindings for [StripeConnect](https://github.com/stripe/stripe-ios.git), the Stripe Connect platform module.

## Installation

```
dotnet add package SwiftBindings.Stripe.Connect
```

## Requirements

- .NET 10.0+
- iOS 15.0+
- Apple platform (macOS host for development)

## Building from Source

```bash
# One-time: install the pinned Nuke CLI from .config/dotnet-tools.json
dotnet tool restore

# Build all Stripe products end-to-end (xcframeworks + bindings + dotnet build)
dotnet nuke BuildLibrary --library Stripe --all-products
```

## License

The bindings are MIT licensed.
