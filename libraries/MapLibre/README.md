# SwiftBindings.MapLibre

.NET bindings for [MapLibre Native iOS](https://github.com/maplibre/maplibre-native) — an
open-source map rendering SDK. MapLibre's public iOS API (`MLNMapView` and the `MLN*`
family) is Objective-C/Objective-C++ over a C++ rendering core, distributed as the
`MapLibre.dynamic.xcframework` release asset.

> 📖 **[Full usage guide →](https://github.com/justinwojo/swift-dotnet-packages/wiki/MapLibre)** — Objective-C→C# naming, map view + delegate wiring, camera, annotations, runtime styling with `NSExpression`, feature queries, offline packs and snapshots.

## Source

- Upstream: `https://github.com/maplibre/maplibre-native` (`ios-v6.28.0`)
- Distribution: `MapLibre.dynamic.xcframework.zip` (zip mode)
- Module: pure Objective-C clang module (`framework module MapLibre`, umbrella `MapLibre.h`)

## Documentation

- [**Usage guide** (wiki)](https://github.com/justinwojo/swift-dotnet-packages/wiki/MapLibre) — full C# walkthrough of the binding surface
- [MapLibre Native for iOS — API reference](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre/)
- [Getting started (iOS)](https://maplibre.org/maplibre-native/ios/latest/documentation/maplibre-native-for-ios/gettingstarted)
- [MapLibre Style Spec](https://maplibre.org/maplibre-style-spec/)
