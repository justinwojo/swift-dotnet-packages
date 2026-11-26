# Toolchain pairings

One file per supported .NET + Xcode pairing. Each is the single record of that pairing for
this repository: `scripts/use-toolchain.sh` selects a shell's toolchain from it, the Nuke
harness reads it for paths, simulators and engines, and `Directory.Build.props` derives
the target frameworks and copy labels from the pairing name alone.

| Field | Meaning |
|---|---|
| `pairing` | the name; must match the file name |
| `sdkVersion`, `workloadVersion` | exact .NET SDK and workload set (written to `global.json`, roll-forward disabled) |
| `dotnetRoot` | the dotnet root holding that SDK (`~` expands) |
| `developerDirectory`, `xcodeVersion` | the Xcode the pairing builds and runs with (`DEVELOPER_DIR`) |
| `ciRunner` | the GitHub Actions runner image that carries this pairing's Xcode (`macos-26` for Xcode 26.3, `xcode-27` for Xcode 27.2) |
| `targetFramework`, `tfmPlatformVersion` | the build TFM (`net10.0-ios`, `net10.0-ios27.2`, `net11.0-ios27.2`); `tfmPlatformVersion` is empty on net10-xcode26 because an explicit 26.0 selects a workload pack that demands Xcode 26.0 exactly; must agree with `Directory.Build.props` |
| `platformLabel` | the platform version of the package copy (`lib/net10.0-ios26.0/`, `lib/net10.0-ios27.2/`, `lib/net11.0-ios27.2/`); the merge requires exactly these |
| `engines` | what the pairing's simulator, tvOS and Mac Catalyst apps run on: `mono` (.NET 10), or `coreclr-interpreter` (.NET 11 Debug) and `coreclr-r2r` (.NET 11 Release). The regression matrix runs one cell per engine and checks the built bundle proves it. |
| `simRuntimes`, `tvSimRuntimes` | simulator runtimes of that Xcode, preferred first |

The versions mirror the SwiftBindings SDK's own toolchain profiles; bump them together.
