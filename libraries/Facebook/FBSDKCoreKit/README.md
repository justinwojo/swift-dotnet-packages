# SwiftBindings.Facebook.Core

.NET bindings for **FBSDKCoreKit** from the
[Facebook iOS SDK](https://github.com/facebook/facebook-ios-sdk). Provides the core SDK
surface: `AccessToken`, `Settings`, `Profile`, `AppEvents`, `GraphRequest`,
`ApplicationDelegate`, and authentication types. Required by the Login and Share kits.

> 📖 **[Full usage guide →](https://github.com/justinwojo/swift-dotnet-packages/wiki/Facebook)** — SDK initialization (Info.plist + app delegate), App Events, Facebook Login, Graph API requests, and sharing — plus the ObjC-vs-Swift naming rule that decides what each type is called in C#.

Built from the official prebuilt `FBSDKCoreKit.xcframework` (dynamic) at upstream v18.1.0.

Targets .NET 10 on iOS.

## Documentation

- [**Usage guide** (wiki)](https://github.com/justinwojo/swift-dotnet-packages/wiki/Facebook) — full C# walkthrough of the binding surface
- [Facebook SDK for iOS](https://developers.facebook.com/docs/ios/) — vendor documentation
- [Getting Started with the iOS SDK](https://developers.facebook.com/docs/ios/getting-started) — `Info.plist` keys and app-delegate wiring
