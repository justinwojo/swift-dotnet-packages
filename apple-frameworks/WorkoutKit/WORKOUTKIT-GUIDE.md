# WorkoutKit for .NET — Usage Guide

`SwiftBindings.Apple.WorkoutKit` exposes Apple's [WorkoutKit](https://developer.apple.com/documentation/workoutkit) framework to C# through .NET 10's native Swift interop — direct Swift calls, not Objective-C proxy wrappers. WorkoutKit lets you *compose* structured workouts (warmups, interval blocks, goals, alerts), wrap them in a plan, and *schedule* them to a paired Apple Watch. This guide maps the Swift workflow to the generated C# surface.

The mental model: build a workout (`CustomWorkout`, `SingleGoalWorkout`, `PacerWorkout`, or `SwimBikeRunWorkout`) out of steps and goals → wrap it in a `WorkoutPlan` → schedule it with `WorkoutScheduler`. Activity types and locations come from HealthKit (`HealthKit.HKWorkoutActivityType`, `HealthKit.HKWorkoutSessionLocationType`).

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start: a custom interval workout](#quick-start-a-custom-interval-workout)
- [Goals](#goals)
- [Steps and interval blocks](#steps-and-interval-blocks)
- [Alerts](#alerts)
- [Workout types](#workout-types)
- [Plans](#plans)
- [Scheduling](#scheduling)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+, macOS 26.2+, Mac Catalyst 26.2+
- macOS host for development
- A paired Apple Watch with your companion watchOS app installed for scheduling to actually deliver

```
dotnet add package SwiftBindings.Apple.WorkoutKit
```

```csharp
using WorkoutKit;
```

Many WorkoutKit types also need HealthKit and Foundation unit types, so you will typically also reference `HealthKit` and `Foundation`/`Swift.Foundation`.

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `func schedule(_:at:) async throws` | `ScheduleAsync(workout, at, ct)` | `async` methods gain `Async`; argument labels mostly drop, keyword labels (`at:`) keep a parameter name |
| `WorkoutScheduler.shared` | `WorkoutScheduler.Shared` (static property) | singletons are PascalCase static properties |
| `enum WorkoutGoal { case distance(...) }` | `WorkoutGoal.Distance(...)` static factory + `.Tag` / `.CaseTag` | Swift enums-with-payload project to a class: static factories to build, discriminate with `Tag`/`CaseTag`. Only the compound `PoolSwimDistanceWithTime` case gets a `TryGet*` unpacker; simple cases are read via the factory + `Tag` |
| `enum IntervalStep.Purpose` | `IntervalStep.PurposeType` (plain `int` enum) | payload-free nested enums become C# enums; `Purpose` → `PurposeType` to avoid clashing with the `Purpose` property |
| `step.displayName` | `step.DisplayName` | properties are PascalCase |
| `static func supports(activity:)` | `SupportsActivity(activity)` | static support-query helpers keep a descriptive name |

Every `*Async` method also accepts a trailing `CancellationToken` (defaulted).

## Quick start: a custom interval workout

```csharp
using WorkoutKit;
using HealthKit;

// 1. A goal: run for 5 minutes. WorkoutGoal.Time takes a value + a Foundation
//    duration unit (from the Foundation binding — e.g. an NSUnitDuration instance).
WorkoutGoal fiveMin = WorkoutGoal.Time(5, durationUnit);   // durationUnit: Foundation.NSUnitDuration

// 2. A step toward that goal.
var workStep = new WorkoutStep(fiveMin);

// 3. Wrap the step in an interval block, repeated 4 times.
var block = new IntervalBlock(
    new[] { new IntervalStep(IntervalStep.PurposeType.Work, fiveMin) },
    iterations: 4);

// 4. A custom workout. activity/location are HealthKit enums (from the
//    HealthKit binding) — pick the running activity + outdoor location values.
var workout = new CustomWorkout(
    activity: runningActivity,        // HealthKit.HKWorkoutActivityType
    location: outdoorLocation,        // HealthKit.HKWorkoutSessionLocationType
    displayName: "4×5 min run",
    warmup: workStep,
    blocks: new[] { block });

// 5. Wrap in a plan.
var plan = new WorkoutPlan(WorkoutPlan.WorkoutType.Custom(workout), Guid.NewGuid());

// 6. Schedule it (requires authorization + a paired Watch — see "Scheduling").
var when = /* a Swift.Foundation.DateComponents */;
await WorkoutScheduler.Shared.ScheduleAsync(plan, when);
```

## Goals

`WorkoutGoal` is a projected Swift enum. Build one with a static factory; read one back with `.Tag` and `TryGet*`.

| Factory / member | Signature | |
|---|---|---|
| `WorkoutGoal.Distance` | `WorkoutGoal Distance(double value0, Foundation.NSUnitLength unitLength)` | distance goal |
| `WorkoutGoal.Time` | `WorkoutGoal Time(double value0, Foundation.NSUnitDuration unitDuration)` | duration goal |
| `WorkoutGoal.Energy` | `WorkoutGoal Energy(double value0, Foundation.NSUnitEnergy unitEnergy)` | active-energy goal |
| `WorkoutGoal.Open` | `WorkoutGoal Open` (static property) | open/unbounded goal |

```csharp
public enum CaseTag : uint { Distance = 0, Time = 1, Energy = 2, PoolSwimDistanceWithTime = 3, Open = 4 }
```

```csharp
// lengthUnit is a Foundation.NSUnitLength (from the Foundation binding).
var goal = WorkoutGoal.Distance(5, lengthUnit);

if (goal.Tag == WorkoutGoal.CaseTag.Distance) { /* … */ }
```

> `WorkoutGoal.Distance`/`Time`/`Energy` take a `double` plus a Foundation unit type (`NSUnitLength` / `NSUnitDuration` / `NSUnitEnergy`). Those unit types come from the Foundation binding, not WorkoutKit — refer to its surface for the available unit instances.

Reading payloads back uses `TryGet*`. The pool-swim case (`CaseTag.PoolSwimDistanceWithTime`) has **no factory** but is readable:

```csharp
public bool TryGetPoolSwimDistanceWithTime(
    out Swift.Foundation.Measurement<Foundation.NSUnitLength> distance,
    out Swift.Foundation.Measurement<Foundation.NSUnitDuration> time);
```

> Only `Distance`, `Time`, `Energy`, and `Open` have constructors/factories in the binding. `PoolSwimDistanceWithTime` can be inspected (via `Tag` / `TryGet…`) but not constructed from C#.

## Steps and interval blocks

### `WorkoutStep`

A single segment of work toward a goal, with an optional alert and display name.

```csharp
public WorkoutStep();
public WorkoutStep(WorkoutGoal goal);
public WorkoutStep(WorkoutGoal goal, IWorkoutAlert? alert = null);
public WorkoutStep(WorkoutGoal goal, IWorkoutAlert? alert = null, string? displayName = null);
```

Members: `Goal` (`WorkoutGoal`), `Alert` (`IWorkoutAlert?`), `DisplayName` (`string?`).

### `IntervalStep`

A step with a *purpose* — work or recovery — used inside interval blocks.

```csharp
public IntervalStep(IntervalStep.PurposeType purpose);
public IntervalStep(IntervalStep.PurposeType purpose, WorkoutStep step);
public IntervalStep(IntervalStep.PurposeType purpose, WorkoutGoal goal, IWorkoutAlert? alert = null);

public enum PurposeType : int { Work = 0, Recovery = 1 }
```

Members: `Purpose` (`PurposeType`), `Step` (`WorkoutStep`).

### `IntervalBlock`

A repeated sequence of interval steps.

```csharp
public IntervalBlock();
public IntervalBlock(IEnumerable<IntervalStep> steps);
public IntervalBlock(IEnumerable<IntervalStep> steps, nint iterations = 1);
```

Members: `Steps` (`IReadOnlyList<IntervalStep>`), `Iterations` (`int`).

## Alerts

Alerts conform to the `IWorkoutAlert` interface and can be attached to steps. The binding emits these alert types (all implement `IWorkoutAlert` and value equality):

| Type | Constructor(s) | Key members |
|---|---|---|
| `HeartRateRangeAlert` | *(no public ctor — inspect only)* | `TargetQuantityLowerBound`, `TargetQuantityUpperBound` (`HealthKit.HKQuantity`) |
| `HeartRateZoneAlert` | `HeartRateZoneAlert(nint zone)` | `Zone` (`int`) |
| `CadenceRangeAlert` | *(no public ctor)* | `Metric`, `TargetQuantityLowerBound`, `TargetQuantityUpperBound` |
| `CadenceThresholdAlert` | `CadenceThresholdAlert(Measurement<NSUnitFrequency> target)` | `Target`, `Metric`, `TargetQuantity` |
| `PowerRangeAlert` | *(no public ctor)* | `Metric`, `TargetQuantityLowerBound`, `TargetQuantityUpperBound` |
| `PowerThresholdAlert` | `PowerThresholdAlert(Measurement<NSUnitPower> target)`, `(target, WorkoutAlertMetric metric)` | `Target`, `Metric`, `TargetQuantity` |
| `PowerZoneAlert` | `PowerZoneAlert(nint zone)` | `Zone`, `Metric` |
| `SpeedRangeAlert` | *(no public ctor)* | `Metric`, `TargetQuantityLowerBound`, `TargetQuantityUpperBound` |
| `SpeedThresholdAlert` | `SpeedThresholdAlert(Measurement<NSUnitSpeed> target, WorkoutAlertMetric metric)` | `Target`, `Metric`, `TargetQuantity` |

```csharp
public enum WorkoutAlertMetric : int { Current = 0, Average = 1 }
```

```csharp
var alert = new HeartRateZoneAlert(zone: 3);
var step = new WorkoutStep(WorkoutGoal.Open, alert);
```

> The four **range** alert types (`HeartRateRangeAlert`, `CadenceRangeAlert`, `PowerRangeAlert`, `SpeedRangeAlert`) expose no public constructor in this binding — you can read their bounds/metric off instances you obtain, but cannot build them from C#. The threshold and zone alerts above are constructible.

You can ask whether a workout supports a given alert with `CustomWorkout.SupportsAlert(IWorkoutAlert alert, HealthKit.HKWorkoutActivityType activity, HealthKit.HKWorkoutSessionLocationType location = Unknown)`.

## Workout types

Four concrete workout shapes feed a plan:

### `CustomWorkout`

Fully structured: warmup, interval blocks, cooldown.

```csharp
public CustomWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location);
public CustomWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location, string? displayName);
public CustomWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location, string? displayName, WorkoutStep? warmup);
public CustomWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location, string? displayName, WorkoutStep? warmup, IEnumerable<IntervalBlock> blocks);
public CustomWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location, string? displayName, WorkoutStep? warmup, IEnumerable<IntervalBlock> blocks, WorkoutStep? cooldown = null);
```

Members: `Activity`, `Location`, `DisplayName`, `Warmup` (`WorkoutStep?`), `Blocks` (`IReadOnlyList<IntervalBlock>`), `Cooldown` (`WorkoutStep?`). Static helpers: `SupportsActivity(activity)`, `SupportsAlert(…)`, `SupportsGoal(goal, activity, location = Unknown)`.

### `SingleGoalWorkout`

A workout with one goal.

```csharp
public SingleGoalWorkout(HKWorkoutActivityType activity);
public SingleGoalWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location);
public SingleGoalWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location, HKWorkoutSwimmingLocationType swimmingLocation);
public SingleGoalWorkout(HKWorkoutActivityType activity, HKWorkoutSessionLocationType location, HKWorkoutSwimmingLocationType swimmingLocation, WorkoutGoal goal);
```

Members: `Activity`, `Location`, `SwimmingLocation`, `Goal`. Static helpers: `SupportsActivity`, `SupportsGoal`.

### `PacerWorkout`

Cover a distance in a target time.

```csharp
public PacerWorkout(
    HKWorkoutActivityType activity,
    HKWorkoutSessionLocationType location,
    Swift.Foundation.Measurement<Foundation.NSUnitLength> distance,
    Swift.Foundation.Measurement<Foundation.NSUnitDuration> time);
```

Members: `Activity`, `Location`, `Distance`, `Time`. Static helper: `SupportsActivity`.

### `SwimBikeRunWorkout`

A multisport (triathlon-style) workout built from ordered activities.

```csharp
public SwimBikeRunWorkout(IEnumerable<SwimBikeRunWorkout.Activity> activities, string? displayName = null);
```

Members: `Activities` (`IReadOnlyList<SwimBikeRunWorkout.Activity>`), `DisplayName`. Static helper: `SupportsActivityOrdering(IEnumerable<Activity>)`.

`SwimBikeRunWorkout.Activity` is a projected enum — build with factories, read with `Tag`/`TryGet*`:

```csharp
public enum CaseTag : uint { Swimming = 0, Cycling = 1, Running = 2 }

Activity.Swimming(HKWorkoutSwimmingLocationType …);
Activity.Cycling(HKWorkoutSessionLocationType …);
Activity.Running(HKWorkoutSessionLocationType …);
// TryGetSwimming/TryGetCycling/TryGetRunning(out …) to read back
```

## Plans

`WorkoutPlan` wraps any of the four workout types via the nested `WorkoutPlan.WorkoutType` projected enum:

```csharp
public WorkoutPlan(WorkoutPlan.WorkoutType workout, System.Guid id);
public WorkoutPlan(byte[] data);   // reconstruct from a serialized representation
```

Members: `Workout` (`WorkoutPlan.WorkoutType`), `Id` (`System.Guid`), `DataRepresentation` (`byte[]` — serialize a plan for storage/transfer).

`WorkoutPlan.WorkoutType` factories and cases:

```csharp
public enum CaseTag : uint { Goal = 0, Custom = 1, Pacer = 2, SwimBikeRun = 3 }

WorkoutType.Goal(SingleGoalWorkout …);
WorkoutType.Custom(CustomWorkout …);
WorkoutType.Pacer(PacerWorkout …);
WorkoutType.SwimBikeRun(SwimBikeRunWorkout …);
// TryGetGoal/TryGetCustom/TryGetPacer/TryGetSwimBikeRun(out …) to read back
// .Activity exposes the resolved HKWorkoutActivityType
```

```csharp
var plan = new WorkoutPlan(WorkoutPlan.WorkoutType.Pacer(pacer), Guid.NewGuid());

// Persist / restore:
byte[] data = plan.DataRepresentation;
var roundtripped = new WorkoutPlan(data);
```

## Scheduling

`WorkoutScheduler` delivers plans to the paired Apple Watch. It is exercised end to end by the bindings.

| Member | Signature | |
|---|---|---|
| `WorkoutScheduler.Shared` | static property | the scheduler singleton |
| `WorkoutScheduler.IsSupported` | `static bool` | whether scheduling is available on this device |
| `WorkoutScheduler.MaxAllowedScheduledWorkoutCount` | `static int` | upper bound on concurrently scheduled workouts |
| `RequestAuthorizationAsync` | `Task<WorkoutScheduler.AuthorizationStateType> RequestAuthorizationAsync(CancellationToken ct = default)` | request permission |
| `GetAuthorizationStateAsync` | `Task<WorkoutScheduler.AuthorizationStateType> GetAuthorizationStateAsync(CancellationToken ct = default)` | current permission state |
| `ScheduleAsync` | `Task ScheduleAsync(WorkoutPlan workout, Swift.Foundation.DateComponents at, CancellationToken ct = default)` | schedule a plan |
| `RemoveAsync` | `Task RemoveAsync(WorkoutPlan workout, Swift.Foundation.DateComponents at, CancellationToken ct = default)` | remove one scheduled workout |
| `RemoveAllWorkoutsAsync` | `Task RemoveAllWorkoutsAsync(CancellationToken ct = default)` | clear all |
| `MarkCompleteAsync` | `Task MarkCompleteAsync(WorkoutPlan workout, Swift.Foundation.DateComponents at, CancellationToken ct = default)` | mark a scheduled workout done |
| `GetScheduledWorkoutsAsync` | `Task<IReadOnlyList<ScheduledWorkoutPlan>> GetScheduledWorkoutsAsync(CancellationToken ct = default)` | list scheduled workouts |

```csharp
var scheduler = WorkoutScheduler.Shared;

if (WorkoutScheduler.IsSupported)
{
    var state = await scheduler.RequestAuthorizationAsync();
    if (state == WorkoutScheduler.AuthorizationStateType.Authorized)
    {
        await scheduler.ScheduleAsync(plan, when);
        IReadOnlyList<ScheduledWorkoutPlan> scheduled =
            await scheduler.GetScheduledWorkoutsAsync();
    }
}
```

```csharp
public enum AuthorizationStateType : long
{
    NotDetermined = 0, Restricted = 1, Denied = 2, Authorized = 3
}
```

A `ScheduledWorkoutPlan` pairs a plan with a date:

```csharp
public ScheduledWorkoutPlan(WorkoutPlan plan, Swift.Foundation.DateComponents date);
```

Members: `Plan` (`WorkoutPlan`), `Date` (`Swift.Foundation.DateComponents`), `Complete` (`bool`).

`StateError` is the scheduling failure enum: `WatchNotPaired = 0`, `WorkoutApplicationNotInstalled = 1`. The async scheduling calls are `throws` in Swift, so failures surface as `Swift.Runtime.SwiftException`.

> **HealthKit-backed writes are out of scope.** The scheduler schedules plans, but mutating HealthKit data models through this binding is not supported in this SDK version. Scheduling only delivers when a paired Apple Watch with your companion app is present.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. For short-lived locals the finalizer cleans up, but `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op.

```csharp
using var workout = new SingleGoalWorkout(runningActivity);   // runningActivity: HealthKit.HKWorkoutActivityType
```

- **`WorkoutGoal.Open`** is a cached singleton — don't wrap it in `using` (Dispose on it is a no-op, but treat it as shared).
- **Several struct wrappers are `Sendable`** in Swift (`ScheduledWorkoutPlan`, `WorkoutGoal`, …) — the binding marks them `[SwiftSendable]`, so instances may be shared across .NET threads without external locking.
- **Async + cancellation.** All scheduler operations are `*Async` with a trailing `CancellationToken`; await off the UI thread and marshal back before touching UI.
- **Platform availability.** Many WorkoutKit types carry `[SupportedOSPlatform]` annotations (iOS 17+, macOS 15+, watchOS 10+). The compiler will flag a call that isn't valid for your target — check IntelliSense availability hints.

## Reference links

- [Apple — WorkoutKit framework](https://developer.apple.com/documentation/workoutkit)
- [Apple — Building workouts with WorkoutKit](https://developer.apple.com/documentation/workoutkit/building-workouts-with-workoutkit)
- [Apple — Scheduling workouts with WorkoutKit](https://developer.apple.com/documentation/workoutkit/scheduling-workouts-with-workoutkit)
