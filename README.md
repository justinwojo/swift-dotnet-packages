# Swift .NET Packages

NuGet packages providing .NET bindings for popular Swift libraries.

Bindings are generated using [swift-bindings](https://github.com/jwojiechowski/swift-bindings) and target .NET 10.0 on Apple platforms (iOS, macOS, tvOS).

## Repository Structure

```
libraries/          # One directory per bound library
  Nuke/
  CryptoSwift/
  ...
tests/              # Integration tests per library
  Nuke.Tests/
  ...
```

## Adding a New Library

Each library directory contains:
- A `.csproj` referencing the `Swift.Bindings.Sdk`
- A `build-xcframework.sh` script that fetches the library via SPM and builds the xcframework

## Building

```bash
# Build a specific library
cd libraries/Nuke
./build-xcframework.sh
dotnet build

# Run tests
dotnet test tests/Nuke.Tests
```

## License

MIT
