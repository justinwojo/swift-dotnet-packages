# SwiftBindings.Facebook.CoreBasics

.NET bindings for **FBSDKCoreKit_Basics**, the Objective-C kernel module of the
[Facebook iOS SDK](https://github.com/facebook/facebook-ios-sdk). This is a low-level
dependency of the other Facebook kits; you usually depend on
`SwiftBindings.Facebook.Core`, `.Login`, or `.Share` rather than this package directly.

> 📖 **[Full usage guide →](https://github.com/justinwojo/swift-dotnet-packages/wiki/Facebook)** — the five-package map, SDK setup, and the flows built on top of this module; its own surface (Base64, JSON/type coercion, crash handler) is covered under "FBSDKCoreKit_Basics utilities".

Built from the official prebuilt `FBSDKCoreKit_Basics.xcframework` (dynamic) at upstream v18.1.0.

Targets .NET 10 on iOS.

## Documentation

- [**Usage guide** (wiki)](https://github.com/justinwojo/swift-dotnet-packages/wiki/Facebook) — full C# walkthrough of the binding surface
- [Facebook SDK for iOS](https://developers.facebook.com/docs/ios/) — vendor documentation
