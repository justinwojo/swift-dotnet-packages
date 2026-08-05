# Lottie for .NET — Usage Guide

`SwiftBindings.Lottie` exposes Airbnb's [Lottie](https://github.com/airbnb/lottie-ios) — native playback of After Effects animations exported as JSON / `.lottie` — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

The headline fact for a .NET consumer: **`LottieAnimationView` is a real `UIKit.UIView` subclass**, so you add it to your view hierarchy exactly like any other view and drive it from C#. Everything below is grounded in the generated bindings for upstream Lottie **4.6.1** and mirrors calls exercised by the repo's on-device/simulator test app.

## Contents

- [Requirements & install](#requirements--install)
- [Swift → C# translation rules](#swift--c-translation-rules)
- [Quick start: play an animation in a UIKit screen](#quick-start-play-an-animation-in-a-uikit-screen)
- [Loading animations](#loading-animations)
- [Playback control](#playback-control)
- [Loop modes, speed, and scrubbing](#loop-modes-speed-and-scrubbing)
- [Configuration and rendering engine](#configuration-and-rendering-engine)
- [Dynamic properties: keypaths and value providers](#dynamic-properties-keypaths-and-value-providers)
- [Text, image, and font providers](#text-image-and-font-providers)
- [Caching](#caching)
- [Animated controls (button / switch)](#animated-controls-button--switch)
- [CALayer instead of UIView](#calayer-instead-of-uiview)
- [Memory & threading](#memory--threading)
- [Known limitations](#known-limitations)
- [Reference links](#reference-links)

## Requirements & install

```
dotnet add package SwiftBindings.Lottie
```

- .NET 10.0+
- **Target framework: `net10.0-ios`.** The package's assembly ships under `net10.0-ios26.0`, so your app's iOS workload must resolve to platform version ≥ 26.0. This is the compile-SDK pin, not your deployment minimum.
- **Deployment minimum: iOS 15.0** (`minIOS` in `library.json`). Set your app's real floor with `<SupportedOSPlatformVersion>`.
- macOS host for development.

```csharp
using Lottie;          // all Lottie types — module-level namespace, not Swift.Lottie
using Swift;           // Swift.CGRect / Swift.CGSize / Swift.CGPoint interop structs
using Swift.Runtime;   // ISwiftObject, SwiftObjectHelper, etc.
```

`Lottie` is a **single-product package** — one namespace, no sub-packages.

## Swift → C# translation rules

| Swift | C# | Rule |
|---|---|---|
| `import Lottie` | `using Lottie;` | module name becomes the namespace directly (not `Swift.Lottie`) |
| `LottieAnimation.named("x")` | `LottieAnimation.Named("x")` | failable static factories keep their name, PascalCased; failure is `null` |
| `var animationSpeed: CGFloat` | `AnimationSpeed` (`double`) | properties PascalCase; `CGFloat` → `double` |
| `func play(completion:)` | `Play(Action<bool>? completion = null)` | Swift closures become `System.Action<…>` / `System.Func<…>`, optional ones nullable with a default |
| `func play(fromFrame:toFrame:loopMode:completion:)` | **`PlayFromFrameToFrameLoopModeCompletion(double? fromFrame, double toFrame, …)`** | when two Swift overloads would collapse to the same C# signature, they are **named from their argument labels** — see [Playback control](#playback-control) |
| `enum LottieLoopMode { case repeat(Float) }` | class with `CaseTag` + `LottieLoopMode.Repeat(3f)` + `TryGetRepeat(out float)` | Swift enums **with payloads** project as a class: static factories for payload cases, static properties for payload-less cases, `Tag` to discriminate, `TryGetX(out …)` to unwrap |
| `enum LottieBackgroundBehavior: Int` | `enum LottieBackgroundBehavior : int` | payload-less, raw-valued Swift enums become ordinary C# enums |
| `CGRect` / `CGSize` / `CGPoint` args | `Swift.CGRect` etc. | cast from CoreGraphics: `(Swift.CGRect)new CoreGraphics.CGRect(…)` |
| `struct LottieColor` | `class LottieColor : ISwiftObject, ISwiftStruct, IDisposable` | Swift **structs** project as classes holding a value payload — see the value-semantics caveat below |
| `func loadedFrom(...) async` | `LoadedFromAsync(…, CancellationToken)` → `Task<T>` | Swift async gets an `…Async` C# `Task` overload |
| `@MainActor class LottieAnimationView` | `[SwiftMainActor]` + runtime main-thread assert | main-actor members throw if called off the UI thread |
| `[String]` / `[String: String]` | `IReadOnlyList<string>` / `IDictionary<string, string>` | collections marshal to BCL interfaces |

> **Value semantics don't survive the projection.** Swift structs (`LottieConfiguration`, `LottieColor`, `AnimationKeypath`, `LottieLoopMode`, `LottiePlaybackMode`) are C# *classes* wrapping a copied payload. Mutating a property on a value you read back from somewhere else changes only your copy:
>
> ```csharp
> LottieConfiguration.Shared.RenderingEngine = RenderingEngineOption.CoreAnimation; // ← no effect
> LottieConfiguration.Shared = new LottieConfiguration(RenderingEngineOption.CoreAnimation); // ← correct
> ```
>
> The SDK ships an `SB1003` analyzer warning that flags exactly this shape — a write through a
> struct-valued property — at compile time, so the silent-no-op case is now caught before you run.

## Quick start: play an animation in a UIKit screen

```csharp
using Lottie;
using UIKit;

public class AnimationViewController : UIViewController
{
    public override void ViewDidLoad()
    {
        base.ViewDidLoad();

        // 1. Load the animation (bundle resource "PlaneAnimation.json")
        LottieAnimation? animation = LottieAnimation.Named("PlaneAnimation");

        // 2. LottieAnimationView IS a UIView — build it, add it, constrain it
        var animView = new LottieAnimationView
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        animView.Animation = animation;
        animView.LoopMode = LottieLoopMode.Loop;
        animView.AnimationSpeed = 1.0;

        View!.AddSubview(animView);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            animView.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
            animView.CenterYAnchor.ConstraintEqualTo(View.CenterYAnchor),
            animView.WidthAnchor.ConstraintEqualTo(300),
            animView.HeightAnchor.ConstraintEqualTo(300),
        });

        // 3. Play
        animView.Play();
    }
}
```

`new LottieAnimationView((Swift.CGRect)new CoreGraphics.CGRect(0, 0, 300, 300))` is the frame-based alternative to the parameterless constructor.

## Loading animations

`LottieAnimation` is the parsed animation document; it is independent of any view and is **not** main-actor bound, so it is safe to load off the UI thread.

```csharp
// From the app bundle by name (no ".json" extension)
LottieAnimation? a1 = LottieAnimation.Named("PlaneAnimation");
LottieAnimation? a2 = LottieAnimation.Named("PlaneAnimation", NSBundle.MainBundle, subdirectory: "anims");

// From an absolute file path
string path = NSBundle.MainBundle.PathForResource("PlaneAnimation", "json")!;
LottieAnimation? a3 = LottieAnimation.Filepath(path);

// From raw JSON bytes (e.g. downloaded, or an embedded resource)
byte[] json = File.ReadAllBytes(path);
LottieAnimation a4 = LottieAnimation.From(json);
LottieAnimation a5 = LottieAnimation.From(json, DecodingStrategy.DictionaryBased);

// From an asset catalog data-set, or from a URL (async — null on failure)
LottieAnimation? a6 = LottieAnimation.Asset("PlaneAnimation");
LottieAnimation? a7 = await LottieAnimation.LoadedFromAsync(new NSUrl("https://example.com/anim.json"));
```

**A missing animation returns `null`, it does not throw.** Always null-check the result of `Named` / `Filepath` / `Asset`.

Animation metadata:

```csharp
double duration   = animation.Duration;     // seconds
double framerate  = animation.Framerate;    // fps
double startFrame = animation.StartFrame;
double endFrame   = animation.EndFrame;
IReadOnlyList<string> markers = animation.MarkerNames;

// Duration == (EndFrame - StartFrame) / Framerate, and frame/progress/time conversions:
double progress = animation.ProgressTime(frameTime: 30.0);
double frame    = animation.FrameTimeForProgress(0.5);         // Swift's frameTime(forProgress:)
double frameAtT = animation.FrameTimeForTime(1.5);             // Swift's frameTime(forTime:), seconds
double? markerProgress = animation.ProgressTime("myMarker");   // null if no such marker
```

### `.lottie` (dotLottie) files

```csharp
DotLottieFile file = await DotLottieFile.NamedAsync("MyBundledDotLottie");
// also: LoadedFromAsync(filepath), AssetAsync(name), LoadedFromAsync(NSUrl)

var view = new LottieAnimationView();
view.LoadAnimation(animationId: null, dotLottieFile: file);   // null → first animation
view.LoadAnimation(index: 0, dotLottieFile: file);

IReadOnlyList<DotLottieFile.Animation> animations = file.Animations;
LottieAnimation first = animations[0].AnimationValue;
```

Prefer the `…Async` (`Task`-returning) overloads. The synchronous forms live under
`DotLottieFile.SynchronouslyBlockingCurrentThread` and return a raw
`SwiftResult<DotLottieFile, ExistentialContainer1>` that is awkward to unwrap from C#.

## Playback control

```csharp
view.Play();                                   // whole animation, current LoopMode
view.Pause();
view.Stop();                                   // stop + reset to the start frame
bool playing = view.IsAnimationPlaying;

// Progress range (0.0 … 1.0) — Swift's play(fromProgress:toProgress:loopMode:completion:)
view.PlayFromProgressToProgressLoopModeCompletion(fromProgress: 0.0, toProgress: 0.5);
view.PlayFromProgressToProgressLoopModeCompletion(
    fromProgress: 0.0, toProgress: 1.0, loopMode: LottieLoopMode.AutoReverse);

// Frame range — Swift's play(fromFrame:toFrame:loopMode:completion:)
view.PlayFromFrameToFrameLoopModeCompletion(
    fromFrame: 0.0, toFrame: 30.0, loopMode: LottieLoopMode.PlayOnce);

// Markers authored in After Effects
view.Play("startMarker", "endMarker");
view.Play("singleMarker");
view.Play(new[] { "markerA", "markerB" });

// Completion callback — `finished` is false when playback was interrupted
view.LoopMode = LottieLoopMode.PlayOnce;
view.Play(finished => Console.WriteLine($"done: {finished}"));

// Notification when a new animation finishes loading into the view
view.AnimationLoaded = (v, loaded) => Console.WriteLine($"loaded {loaded.Duration}s");
```

The marker, playback-mode and completion-only forms all keep the plain name `Play` — only the
two *range* forms, which would otherwise land on the same C# signature, are named from their
Swift argument labels. The parameter names survive, so `fromProgress:` / `toFrame:` still read
the way the Swift call does.

### Playback modes

`LottiePlaybackMode` is the declarative form of the imperative `Play(...)` calls — useful when
you drive the view from state rather than from events.

```csharp
// Jump to a paused position without animating
view.SetPlaybackMode(LottiePlaybackMode.Paused(LottiePlaybackMode.PausedState.Progress(0.5)));
view.SetPlaybackMode(LottiePlaybackMode.Paused(LottiePlaybackMode.PausedState.Frame(30.0)));

// Start playing a declared range
view.Play(LottiePlaybackMode.Playing(
    LottiePlaybackMode.PlaybackMode.FromProgress(0.0, 1.0, LottieLoopMode.Loop)));

// Read back what the view is currently doing (null when nothing has been set)
LottiePlaybackMode? mode = view.CurrentPlaybackMode;
if (mode is not null && mode.Tag == LottiePlaybackMode.CaseTag.Paused
    && mode.TryGetPaused(out LottiePlaybackMode.PausedState paused)
    && paused.TryGetProgress(out double p))
{
    Console.WriteLine($"paused at {p:P0}");
}
```

## Loop modes, speed, and scrubbing

```csharp
view.LoopMode = LottieLoopMode.PlayOnce;
view.LoopMode = LottieLoopMode.Loop;
view.LoopMode = LottieLoopMode.AutoReverse;
view.LoopMode = LottieLoopMode.Repeat(3f);            // note: float, not double
view.LoopMode = LottieLoopMode.RepeatBackwards(2f);

// Unwrapping a payload case
if (view.LoopMode.TryGetRepeat(out float count)) { /* … */ }

view.AnimationSpeed = 1.5;                            // 1.0 = authored speed; negative = reverse

// Scrubbing — set progress or frame directly, then force a redraw
view.CurrentProgress = 0.5;      // 0.0 … 1.0
view.CurrentFrame    = 30.0;
view.ForceDisplayUpdate();

// While playing, the *presented* (Core Animation) values:
double liveFrame    = view.RealtimeAnimationFrame;
double liveProgress = view.RealtimeAnimationProgress;
```

Other view properties worth knowing:

| Member | Type | |
|---|---|---|
| `BackgroundBehavior` | `LottieBackgroundBehavior` | what happens when the app backgrounds — `Stop`, `Pause`, `PauseAndRestore`, `ForceFinish`, `ContinuePlaying` |
| `MaskAnimationToBounds` | `bool` | clip the animation to the view's bounds |
| `ShouldRasterizeWhenIdle` | `bool` | rasterize when paused (Main Thread engine only) |
| `RespectAnimationFrameRate` | `bool` | play at the authored fps instead of the display's |
| `ViewportFrame` | `Swift.CGRect?` | crop viewport |
| `CurrentRenderingEngine` | `RenderingEngine?` | which engine actually got used |
| `ReloadImages()` | — | re-fetch images from the image provider |

## Configuration and rendering engine

`LottieConfiguration` selects the rendering engine, decoding strategy, color space, and reduced-motion policy.

```csharp
// Global default for every LottieAnimationView created afterwards
LottieConfiguration.Shared = new LottieConfiguration(RenderingEngineOption.CoreAnimation);

// Per-view: pass a configuration at construction
var view = new LottieAnimationView(
    configuration: new LottieConfiguration(
        RenderingEngineOption.MainThread,
        DecodingStrategy.DictionaryBased),
    logger: LottieLogger.Shared);

LottieConfiguration current = view.Configuration;   // read-only on the view
```

`RenderingEngineOption` is a payload-carrying Swift enum:

| Member | Meaning |
|---|---|
| `RenderingEngineOption.Automatic` | Core Animation where supported, Main Thread fallback (Lottie's default) |
| `RenderingEngineOption.CoreAnimation` | force the Core Animation engine |
| `RenderingEngineOption.MainThread` | force the Main Thread engine |
| `RenderingEngineOption.Specific(RenderingEngine.CoreAnimation)` | explicit form; `TryGetSpecific(out RenderingEngine)` unwraps |

`ReducedMotionOption.StandardMotion` / `.ReducedMotion` / `.DisabledMotion` / `.SystemReducedMotionToggle` control accessibility behavior; `DecodingStrategy` is a plain enum (`DictionaryBased`, `LegacyCodable`).

## Dynamic properties: keypaths and value providers

Keypaths address nodes inside the animation ("Layer.Shape.Fill.Color"); `**` is the wildcard.
Discover them at runtime:

```csharp
view.LogHierarchyKeypaths();                                    // prints to the console
IReadOnlyList<string> all = view.GetAllHierarchyKeypaths();     // or get them as a list
```

Then override values:

```csharp
using Lottie;

var view = new LottieAnimationView { Animation = LottieAnimation.Named("PlaneAnimation") };

// Color
var color = new LottieColor(r: 1.0, g: 0.5, b: 0.25, a: 1.0);          // 0.0–1.0 channels
view.SetValueProvider(new ColorValueProvider(color), new AnimationKeypath("**.Color"));

// Opacity / any scalar
view.SetValueProvider(new FloatValueProvider(0.5), new AnimationKeypath("**.Opacity"));

// Position / size
view.SetValueProvider(
    new PointValueProvider((Swift.CGPoint)new CoreGraphics.CGPoint(50, 75)),
    new AnimationKeypath("**.Position"));
view.SetValueProvider(
    new SizeValueProvider((Swift.CGSize)new CoreGraphics.CGSize(100, 200)),
    new AnimationKeypath("**.Size"));

// Gradient
view.SetValueProvider(
    new GradientValueProvider(new[] { color, new LottieColor(0, 0, 1, 1) }),
    new AnimationKeypath("**.Gradient"));

// Remove an override again (a no-op if none was set)
view.RemoveValueProvider(new AnimationKeypath("**.Opacity"));
```

Every provider also has a **closure** constructor that recomputes per frame:

```csharp
var pulsing = new FloatValueProvider(frame => 0.5 + 0.5 * Math.Sin(frame / 10.0));
var cycling = new ColorValueProvider(frame => new LottieColor(frame % 1.0, 0.4, 0.8, 1.0));
```

Colors also convert from UIKit via an extension method: `UIColor.SystemRed.GetLottieColorValue()`.
The 0–255 form is `new LottieColor(255, 128, 64, 255, ColorFormatDenominator.TwoFiftyFive)`.

Building keypaths from parts, reading current values, and toggling nodes:

```csharp
var kp = new AnimationKeypath(new[] { "Layer", "Transform", "Position" });
string flat = kp.String;                    // "Layer.Transform.Position"
IReadOnlyList<string> keys = kp.Keys;

object? value    = view.GetValue(kp, atFrame: 30.0);
object? original = view.GetOriginalValue(kp, atFrame: null);
view.SetNodeIsEnabled(false, kp);
```

## Text, image, and font providers

```csharp
// Replace authored text layers by keypath name
view.TextProvider = new DictionaryTextProvider(new Dictionary<string, string>
{
    ["title"]    = "Hello World",
    ["subtitle"] = "From C#",
});
// or keep Lottie's authored text:
view.TextProvider = new DefaultTextProvider();

// Images referenced by the animation
view.ImageProvider = new BundleImageProvider(NSBundle.MainBundle, searchPath: null);
view.ImageProvider = new FilepathImageProvider(NSBundle.MainBundle.BundlePath);

// Fonts
view.FontProvider = new DefaultFontProvider();
```

## Caching

```csharp
DefaultAnimationCache cache = DefaultAnimationCache.SharedCache;   // process-wide LRU
cache.CacheSize = 50;

cache.SetAnimation(animation, "my-key");
LottieAnimation? hit = cache.Animation("my-key");                  // null on miss
cache.ClearCache();

// Or an isolated cache passed into a loader
var scoped = new DefaultAnimationCache();
LottieAnimation? a = LottieAnimation.Filepath(path, animationCache: scoped);
```

Loaders that take an `IAnimationCacheProvider?` accept `null` to bypass caching entirely.

## Animated controls (button / switch)

`AnimatedButton` and `AnimatedSwitch` derive from `AnimatedControl` (a `UIControl`), so they
drop into a UIKit hierarchy like the view does.

```csharp
var button = new AnimatedButton(LottieAnimation.Named("ButtonAnim"), LottieConfiguration.Shared);
button.SetPlayRange(fromProgress: 0.0, toProgress: 0.5, UIControlEvent.TouchUpInside);
button.PerformAction = () => Console.WriteLine("tapped");

var toggle = new AnimatedSwitch();
toggle.SetProgressForState(fromProgress: 0.0, toProgress: 1.0, forOnState: true);
toggle.StateUpdated = isOn => Console.WriteLine($"switch: {isOn}");
toggle.SetIsOn(true, animated: true);
bool on = toggle.IsOn;
```

## CALayer instead of UIView

`LottieAnimationLayer` is a `CoreAnimation.CALayer` with the same playback surface, for cases
where you want the animation inside an existing layer tree rather than as a subview.

```csharp
var layer = new LottieAnimationLayer(LottieConfiguration.Shared);
layer.Animation = LottieAnimation.Named("PlaneAnimation");
layer.AnimationSpeed = 1.5;
layer.LoopMode = LottieLoopMode.Loop;
layer.Play();
layer.Stop();
```

It exposes the same `Play` / `PlayFromProgressToProgressLoopModeCompletion` /
`PlayFromFrameToFrameLoopModeCompletion` / `Pause` / `SetPlaybackMode` / `CurrentProgress` /
`SetValueProvider` members as the view, plus `Pause(LottiePlaybackMode.PausedState)`.
Unlike the view, it can also be constructed straight from an animation
(`new LottieAnimationLayer(LottieAnimation.Named("PlaneAnimation"))`).

## Memory & threading

- **`LottieAnimationView` and its members are `@MainActor`-isolated.** The generated code asserts
  the platform main thread on ~40 members (`Play`, `SetValueProvider`, `GetValue`,
  `GetAllHierarchyKeypaths`, `ValueProviders`, …) via `MainActorGuard.AssertMainThread()`, so
  calling them off the UI thread trips the guard rather than silently misbehaving. Marshal with
  `InvokeOnMainThread(...)` / `MainThread.BeginInvokeOnMainThread(...)`.
- **`LottieAnimation`, `LottieColor`, `AnimationKeypath`, `DefaultAnimationCache` are not
  main-actor bound** — parse and cache animations off the UI thread, then hand them to the view
  on the main thread.
- **`using var` is the recommended deterministic-cleanup pattern** for value-type projections
  (`LottieColor`, `AnimationKeypath`, `LottieLoopMode`, `LottieConfiguration`,
  `DictionaryTextProvider`, the value providers) — e.g.
  `using var color = new LottieColor(1.0, 0.5, 0.0, 1.0);`. `Dispose` is safe on every generated
  type and double-`Dispose` is a no-op; the test app runs 100 create/dispose `LottieColor` cycles
  as a memory-pressure check.
- **Don't `using`-scope objects you hand to a long-lived view.** A `LottieAnimation` assigned to
  `view.Animation`, or a value provider passed to `SetValueProvider`, must outlive the view's use
  of it — hold them in a field instead.
- **Static singletons are cached** (`LottieLoopMode.Loop`, `DefaultAnimationCache.SharedCache`,
  `RenderingEngineOption.Automatic`); do not dispose them.
- `LottieAnimationView` / `LottieAnimationLayer` are `NSObject`-derived and follow normal
  UIKit/CoreAnimation lifetime rules, not `ISwiftObject` disposal.

## Known limitations

- **Colliding overloads are renamed from their Swift argument labels** — and those names changed
  in SDK 0.19.0. Where an older package exposed a numeric `2` suffix, the member now spells out
  the labels:

  | Swift | before | now |
  |---|---|---|
  | `play(fromProgress:toProgress:loopMode:completion:)` | `Play(double?, double, …)` | `PlayFromProgressToProgressLoopModeCompletion` |
  | `play(fromFrame:toFrame:loopMode:completion:)` | `Play2(double?, double, …)` | `PlayFromFrameToFrameLoopModeCompletion` |
  | `frameTime(forProgress:)` | `FrameTime(double)` | `FrameTimeForProgress` |
  | `frameTime(forTime:)` | `FrameTime2(double)` | `FrameTimeForTime` |

  (`CompatibleAnimationView` — the ObjC-compatibility shim — gets the same treatment with
  `PlayFromProgressToProgressCompletion` / `PlayFromFrameToFrameCompletion`.) This is a
  **source-breaking rename**: code written against an earlier package must be updated.
- **One `[Obsolete("SB0001")]` stub in the whole surface**:
  `DotLottieFile.LoadedFrom(byte[] data, string filename, DispatchQueue, handler)` has no
  `@_cdecl` wrapper and will not run. Use `DotLottieFile.LoadedFromAsync(data, filename, queue)`
  or one of the `NamedAsync` / `LoadedFromAsync` overloads instead.
- **The `LottieAnimationView` initializers that *load* an animation for you are
  `[Obsolete("SB0009")]` stubs that throw `NotSupportedException`** — the five that carry a
  trailing `completion` closure (the dotLottie by-name / by-filepath / by-URL forms, plus the
  remote-`NSUrl` one that reports its error through a closure). Their Swift signature carries an
  `Optional` wider than one machine word with no wrapper to pass it through memory. Construct the
  view plainly and load into it instead: `new LottieAnimationView()` + `view.Animation = …` or
  `view.LoadAnimation(animationId, file)`, with `DotLottieFile.NamedAsync` / `LoadedFromAsync`
  doing the loading. The closure-free initializers are unaffected —
  `new LottieAnimationView(name, bundle, subdirectory)`,
  `new LottieAnimationView(filePath, imageProvider, animationCache)`,
  `new LottieAnimationView(filePath, animationId, dotLottieCache)` and
  `new LottieAnimationView(url, animationId, dotLottieCache)` all work.
- **A C#-authored `ILottieURLSession` or `IAnyInterpolatable` is never called back.** Both
  interfaces carry `[Obsolete("SB0010")]`: none of their requirements is reverse-dispatchable, so
  implementing one in C# and handing it to Lottie compiles but never receives a call. *Consuming*
  a Swift-vended value through the interface — e.g. reading
  `LottieConfiguration.DefaultURLSession` — is unaffected. Use `LottieConfiguration`'s own URL
  session rather than supplying your own.
- **`LottieLogger` cannot be constructed with custom handlers.** Its only initializer takes four
  `@autoclosure` closures, a shape the bridge can't marshal, so it is an `[Obsolete("SB0005")]`
  stub that throws. Use `LottieLogger.Shared` / `LottieLogger.PrintToConsole`.
- **Some convenience arities are suppressed as ambiguous.** Where two Swift initializers reduce
  to the same C# arity once defaults are applied, the generator refuses both rather than picking
  one — e.g. there is no one-argument `new LottieAnimationView(animation)` (it would collide with
  `new LottieAnimationView(CGRect)`). Pass the fuller form, or set `view.Animation` after
  construction as the samples above do. `LottieAnimationLayer` is not affected and does accept
  `new LottieAnimationLayer(animation)`.
- **The synchronous dotLottie loaders return `SwiftResult<DotLottieFile, ExistentialContainer1>`**
  — the error side is an unwrapped Swift existential that is impractical to inspect from C#. Use
  the `Task`-returning overloads, which throw a `SwiftException` (carrying `DotLottieError`).
- **Swift struct value semantics are lost** — see [translation rules](#swift--c-translation-rules).
  Assign whole values (`LottieConfiguration.Shared = new LottieConfiguration(...)`) rather than
  mutating a property on a value you read back.
- **`ContentMode` is inherited from `LottieAnimationViewBase`, which re-declares UIKit's
  property.** It is not covered by the test app, and an earlier SDK generation could not make the
  setter take effect. The current bindings do route it through a real `@_cdecl` wrapper
  (`SBW_Set_Lottie_LottieAnimationViewBase_contentMode`), but verify it visually before relying
  on it; `ViewportFrame` / `MaskAnimationToBounds` are the tested alternatives for fitting.
- **The SwiftUI bridge types now ship, but are untested.** `LottieViewSession`,
  `LottieButtonSession` and `LottieSwitchSession` `P/Invoke` into a `LottieBridge` native library
  that earlier packages did not include; the package now builds and ships
  `LottieBridge.xcframework` alongside `Lottie.xcframework` and `LottieSwiftBindings.xcframework`,
  so the imports resolve. Each session wraps Lottie's SwiftUI view in a `UIViewController` you
  host yourself (`LottieViewSession.Create(animation)` → `session.ViewController`). Nothing in
  the repo's test app exercises this path yet, so treat it as unvalidated; `LottieAnimationView`
  from UIKit remains the supported route.
- **`ValueProviders` is get-only** (install via `SetValueProvider` / `RemoveValueProvider`), and
  **`GetValue` / `GetOriginalValue` return `object?`** — a boxed Swift existential whose runtime
  type depends on the addressed node; treat it as opaque unless you know the node type.

Beyond these, the test app covers loading, playback lifecycle, loop modes, speed, scrubbing,
value providers (float/size/point/color, including `SetValueProvider`), keypaths, caching,
`LottieAnimationLayer`, and the animated controls — with no permanently-skipped cases.

## Reference links

For full API semantics, consult the vendor's own documentation and translate using the rules above:

- [Lottie — official documentation (iOS)](https://lottie.airbnb.tech/#/ios) — Airbnb's unified Lottie docs
- [Lottie iOS — DocC API reference](https://swiftpackageindex.com/airbnb/lottie-ios/master/documentation/lottie) — generated Swift API reference for the exact types bound here
- [Supported After Effects features](https://lottie.airbnb.tech/#/supported-features) — what the Core Animation vs. Main Thread engines can render
- [airbnb/lottie-ios on GitHub](https://github.com/airbnb/lottie-ios) — upstream source (this package binds **4.6.1**)
