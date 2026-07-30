# Facebook binding test app

Validation app for the Facebook SDK bindings, exercising Core (Settings/AccessToken),
Login (LoginManager/LoginConfiguration), and Share (ShareLinkContent/ShareDialog).

Historical note: under SDK 0.16.0 this app could not build — Login/Share emitted no
bindings and Core/AEM failed to compile due to generator-internal blockers. All of
those were fixed upstream; as of SDK 0.18.0 the app builds and passes on both
iOS Simulator and device (see `artifacts/regression-validate-0.18.0.json`).

```bash
dotnet nuke BuildTestApp --library Facebook
dotnet nuke ValidateSim --library Facebook
```

The test csproj injects each binding's ObjC companion assembly via a build-time
target (`_InjectFacebookSwiftBindingArtifacts`) — see the comment in
`SwiftBindings.Facebook.Tests.csproj` for why an eval-time `<Import>` doesn't work
on a clean build.
