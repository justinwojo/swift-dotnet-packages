# LiveCommunicationKit for .NET — Usage Guide

`SwiftBindings.Apple.LiveCommunicationKit` exposes Apple's [LiveCommunicationKit](https://developer.apple.com/documentation/livecommunicationkit) framework — the modern VoIP / Live Communication API (CallKit's successor for VoIP "conversations") — to C# through .NET 10's native Swift interop. These are direct Swift calls, not Objective-C proxy wrappers.

This is an orientation guide: it walks the few entry points you build a VoIP integration around (the conversation manager, conversations, and the action types) and is honest about what requires a real app capability and a delegate. Every type, method, and enum case below is verbatim from the generated C#.

## Contents

- [Requirements & install](#requirements--install)
- [Naming conventions](#naming-conventions)
- [Quick start](#quick-start)
- [The conversation manager](#the-conversation-manager)
- [Conversations](#conversations)
- [Actions](#actions)
- [Handles & capabilities](#handles--capabilities)
- [Telephony & history](#telephony--history)
- [Known limitations](#known-limitations)
- [Memory & threading](#memory--threading)
- [Reference links](#reference-links)

## Requirements & install

- .NET 10.0+
- iOS 26.2+, macOS 26.2+, Mac Catalyst 26.2+
- macOS host for development
- The LiveCommunicationKit / VoIP capability enabled on the app, plus a configured `Info.plist`

```
dotnet add package SwiftBindings.Apple.LiveCommunicationKit
```

```csharp
using LiveCommunicationKit;
```

> The co-located test app only exercises type metadata and plain enum round-trips, and only under `#if IOS`. Conversation lifecycle calls need a real app with the VoIP capability and a wired-up delegate. Treat live behavior as device-only.

## Naming conventions

| Swift | C# | Rule |
|---|---|---|
| `func perform(...) async throws` | `PerformAsync(...)` | `async` methods gain an `Async` suffix and return `Task`/`Task<T>` |
| `obj.localMember` | `obj.LocalMember` | properties are PascalCase |
| nested `struct Configuration` / `enum State` | `ConversationManager.ConfigurationType`, `Conversation.StateType` | nested types that collide with a member get a `Type` suffix |
| `static var sharedInstance` | `…SharedInstance` | Swift type-level singletons become static C# properties |
| `protocol ConversationManagerDelegate` | `interface IConversationManagerDelegate` | Swift protocols project to `I`-prefixed C# interfaces |
| `OptionSet` (`Conversation.Capabilities`) | static singletons + `==`/`!=`, not a flags enum | combine/compare with the provided members and operators |

Every async method also accepts a trailing `CancellationToken` (defaulted).

## Quick start

You build the manager from a configuration, set a delegate, then report incoming/outgoing conversations and perform actions.

```csharp
using LiveCommunicationKit;

// 1. Describe your VoIP app's capabilities
using var config = new ConversationManager.ConfigurationType(
    ringtoneName: null,
    iconTemplateImageData: null,
    maximumConversationGroups: 1,
    maximumConversationsPerConversationGroup: 5,
    includesConversationInRecents: true,
    supportsVideo: true,
    supportedHandleTypes: new HashSet<Handle.Kind> { Handle.Kind.PhoneNumber, Handle.Kind.Generic });

// 2. Create the manager
using var manager = new ConversationManager(config);

// 3. Report an incoming call so the system shows the call UI
using var remote = new Handle(Handle.Kind.PhoneNumber, "+15551234567", displayName: "Alex");
using var update = new Conversation.Update(localMember: null,
    members: new HashSet<Handle> { remote });
await manager.ReportNewIncomingConversationAsync(Guid.NewGuid(), update);
```

## The conversation manager

`ConversationManager` is the central object. It is constructed from a `ConfigurationType`:

```csharp
public partial class ConversationManager
{
    public ConversationManager(ConversationManager.ConfigurationType configuration);

    public IReadOnlyList<Conversation> Conversations { get; }
    public IReadOnlyList<ConversationAction> PendingActions { get; }
    public ConversationManager.ConfigurationType Configuration { get; }
    public IConversationManagerDelegate? Delegate { get; set; }

    public Task PerformAsync(IEnumerable<ConversationAction> actions, CancellationToken ct = default);
    public Task ReportNewIncomingConversationAsync(Guid uuid, Conversation.Update update, CancellationToken ct = default);
    public void ReportConversationEvent(Conversation.Event @event, Conversation conversation);
    public static Task ReportNewIncomingVoIPPushPayloadAsync(IDictionary<Swift.AnyHashable, object> payload, CancellationToken ct = default);
    public void Invalidate();
}
```

`ConfigurationType` has two constructors (the second adds `supportsAudioTranslation`):

```csharp
new ConversationManager.ConfigurationType(
    string? ringtoneName, byte[]? iconTemplateImageData,
    nint maximumConversationGroups, nint maximumConversationsPerConversationGroup,
    bool includesConversationInRecents, bool supportsVideo,
    IReadOnlySet<Handle.Kind> supportedHandleTypes);

new ConversationManager.ConfigurationType(
    /* …same… */, bool supportsAudioTranslation);
```

Wire up an `IConversationManagerDelegate` to receive callbacks. Its members:

```csharp
public interface IConversationManagerDelegate
{
    void ConversationManager(ConversationManager manager, Conversation conversation);
    void ConversationManagerDidBegin(ConversationManager manager);
    void ConversationManagerDidReset(ConversationManager manager);
    void ConversationManager(ConversationManager manager, ConversationAction action);
    void ConversationManager(ConversationManager manager, AVFoundation.AVAudioSession audioSession);
}
```

## Conversations

`Conversation` is the call object the system tracks:

```csharp
public partial class Conversation
{
    public Guid Uuid { get; }
    public Conversation.StateType State { get; }   // Idle, Joining, Joined, Paused, Leaving, Left
    public Handle? LocalMember { get; }
    public string DebugDescription { get; }
}
```

`Conversation.Update` describes the participants/capabilities of a conversation you report:

```csharp
new Conversation.Update(
    Handle? localMember = null,
    IReadOnlySet<Handle>? members = null,
    IReadOnlySet<Handle>? activeRemoteMembers = null,
    Conversation.Capabilities? capabilities = null);
```

`Conversation.Event` is a projected Swift enum (discriminate with `.Tag` against `Conversation.Event.CaseTag`: `ConversationUpdated`, `ConversationStartedConnecting`, `ConversationConnected`, `ConversationEnded`, …). `Conversation.EndedReason` (`Failed`, `RemoteEnded`, `Unanswered`, `JoinedElsewhere`, `DeclinedElsewhere`) explains a finished call.

## Actions

All conversation operations are modeled as action structs; submit them with `manager.PerformAsync(...)`. Each is constructed with the target conversation's UUID plus operation-specific arguments:

| Action | Constructor |
|---|---|
| `StartConversationAction` | `new(Guid conversationUUID, IEnumerable<Handle> handles, bool isVideo)` |
| `JoinConversationAction` | `new(Guid conversationUUID)` |
| `EndConversationAction` | `new(Guid conversationUUID)` |
| `MergeConversationAction` | `new(Guid conversationUUID, Guid conversationUUIDToMergeWith)` |
| `UnmergeConversationAction` | `new(Guid conversationUUID)` |
| `MuteConversationAction` | `new(Guid conversationUUID, bool isMuted)` |
| `PauseConversationAction` | `new(Guid conversationUUID, bool isPaused)` |
| `PlayToneAction` | `new(Guid conversationUUID, string digits, PlayToneAction.ToneType tone)` |
| `SetTranslatingAction` | `new(Guid conversationID, bool isTranslating, Swift.Foundation.Locale.Language localLanguage, Swift.Foundation.Locale.Language remoteLanguage)` |
| `StartCellularConversationAction` | `new(Handle handle, CellularService? cellularService = null)` or `new(ConversationHistoryManager.RecentConversation recentConversation)` |

```csharp
using var end = new EndConversationAction(conversationUuid);
await manager.PerformAsync(new[] { end });
```

The action types share a `ConversationAction` base exposing `Uuid`, `ConversationUUID`, `State` (a projected enum — `Failed`/`Idle`/`Running`/`Complete`), and `TimeoutDate`.

`PlayToneAction.ToneType` = `Single` / `SoftPause` / `HardPause`.
`SetTranslatingAction.TranslationEngine` = `Default` / `Custom`.

## Handles & capabilities

A `Handle` identifies a participant:

```csharp
using var handle = new Handle(Handle.Kind.PhoneNumber, "+15551234567", displayName: "Alex");
// Handle.Kind: Generic, PhoneNumber, EmailAddress
string value = handle.Value;
string display = handle.DisplayName;
```

`Conversation.Capabilities` is an OptionSet-style value type with static members (`Pausing`, `Merging`, `Unmerging`, `Video`, `PlayingTones`) and `==`/`!=` operators — combine and compare with those, not with C# flag enums.

## Telephony & history

Two singleton managers are exposed via static `SharedInstance` accessors:

```csharp
var telephony = TelephonyConversationManager.SharedInstance;
await telephony.StartCellularConversationAsync(
    new StartCellularConversationAction(handle));

var history = ConversationHistoryManager.SharedInstance;
await history.MarkConversationAsReadAsync(recentConversation);
await history.MarkConversationsAsReadAsync(recentConversations);
```

`ConversationHistoryManager.RecentConversation` carries `StatusType` and `DirectionType` enums and a `DecodeFromJson(byte[])` helper; `CellularService` (with `Id` and `Label`) describes an available cellular line.

## Known limitations

- **Live behavior is device + capability gated.** Reporting conversations and performing actions requires the VoIP/LiveCommunicationKit capability on the app and a running system call UI. The bindings construct fine, but the validated surface here is metadata + enum values only.
- **The delegate is fully wired.** `IConversationManagerDelegate` is backed by a generated `ConversationManagerDelegateProxy` with vtable dispatch for all five methods. Implement the interface in C# and assign it to `manager.Delegate`; Swift will call back through the proxy. Test on a physical device with the VoIP capability to exercise the full round-trip.
- **No constructor on the singleton managers.** `TelephonyConversationManager` and `ConversationHistoryManager` are reached only through `SharedInstance`.
- **Three niche APIs are not bound.** A second overload of `pendingConversationActions` (returning an opaque placeholder type), `ConversationHistoryManager.makeMessage` (unsupported placeholder return type), and the predicate-based `ConversationHistoryManager.recentConversations` overload (requires `Foundation.Predicate<…>` which references SwiftUI/Combine) are commented out in the generated bindings. The primary `PendingActions`, `MarkConversationAsReadAsync`, and `MarkConversationsAsReadAsync` APIs are fully bound.
- **Synthesized `Codable` members pruned.** Several value types implement Swift's `Codable` protocol; the `encode(to:)` and `init(from:)` members are intentionally omitted because `Encoder`/`Decoder` are unresolvable existential protocols. Use `ConversationHistoryManager.RecentConversation.DecodeFromJson(byte[])` for JSON round-tripping where the framework provides it.

## Memory & threading

Generated types implement `ISwiftObject` / `IDisposable`. The action and configuration types wrap Swift structs; `Conversation`, `ConversationManager`, and the singleton managers are class wrappers. `using var` is the recommended pattern for deterministic cleanup — `Dispose` is safe on every generated type and double-Dispose is a no-op. Do not dispose the `SharedInstance` singletons.

```csharp
using var config = new ConversationManager.ConfigurationType(/* … */);
using var manager = new ConversationManager(config);
```

Async `PerformAsync` / `Report*Async` calls continue on a thread-pool thread; marshal back to your UI thread before touching UI.

## Reference links

- [Apple — LiveCommunicationKit](https://developer.apple.com/documentation/livecommunicationkit) — upstream documentation
