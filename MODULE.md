# Signaling

## Purpose
A type-safe, deferred publish/subscribe signal bus. Signals are payload-free — the type *is* the
message. Publishers `Queue<T>` during a frame; a single `EmitQueuedSignals()` pump delivers the whole
batch to listeners in queue order.

## Assemblies

| Assembly | Path | Notes |
|---|---|---|
| `PFound.Signaling` | `Runtime/PFound.Signaling.asmdef` | `autoReferenced: false`; references the engine only for the destroyed-owner check and the warning log |

## Dependencies
- Engine only: `UnityEngine.Object` (destroyed-listener detection) and `Debug.LogWarning`.
- No other PFound module, no third-party package, no scripting define.

## Key Types

### `PFound.Signaling`
- **`SignalTracker`** — the bus (instance-based). Holds listeners per `SignalKey`, the queue, and the
  emit machinery.
- **`SignalBase`** (abstract) — marker base for signal types. Payload-free: the type identifies the
  message.
- **`SignalKey`** (readonly struct, `IEquatable<SignalKey>`) — value-type identity of a signal type,
  handed to listeners so one callback can serve several signals and branch on which fired.

## Public API

`SignalTracker`:
```csharp
void AddListener<T>(Action<SignalKey> callback, int order = 0) where T : SignalBase;  // lower order runs first
void RemoveListener<T>(Action<SignalKey> callback) where T : SignalBase;
void Queue<T>(UnityEngine.Object causer) where T : SignalBase;   // deduped per round; causer = diagnostics only, may be null
bool EmitQueuedSignals();                                        // deliver everything queued, empty queue, return true if anything delivered
```

`SignalKey`:
```csharp
static SignalKey Create<T>() where T : SignalBase;
bool IsValid { get; }        // false for a default (uninitialized) key
Type SignalType { get; }     // the signal's runtime type, or null for an invalid key
```

## Model

- **Signals are types.** Declare a signal by deriving from `SignalBase`; listeners receive a
  `SignalKey` identifying which type fired.
- **Deferred delivery.** `Queue<T>` records the signal (deduped per round); nothing runs until
  `EmitQueuedSignals()`. Signals a listener queues during emit wait for the NEXT pump — the current
  batch is snapshotted, then the queue is cleared before dispatch.
- **Ordered listeners.** Lower `order` runs first; equal orders keep registration order (stable
  insert).
- **Safe under fire.** Listener add/remove during emit is deferred to the live list (the dispatch
  loop iterates a snapshot, so the change takes effect next round); re-entrant emit is depth-guarded
  (`MaxEmitDepth = 8`) to break recursive storms with a warning; a listener whose delegate target is
  a destroyed `UnityEngine.Object` is detected and dropped with a warning (leaked-subscription
  guard).

## Setup / wiring

Instance-based, no scene object — `new SignalTracker()`. The consumer owns the instance (keep it in
your bootstrap, or register it in `DependencyContainer`) and **must call `EmitQueuedSignals()` once
per frame from a driver** — nothing pumps it for you. There is no static/singleton accessor: share
the one instance you construct.

```csharp
public sealed class LevelLoaded : SignalBase { }

var signals = new SignalTracker();
signals.AddListener<LevelLoaded>(OnLevelLoaded);

// pump it every frame — e.g. via LoopScheduler:
LoopScheduler.RegisterUpdateLoop(() => signals.EmitQueuedSignals(), host);

// publish:
signals.Queue<LevelLoaded>(causer: null);   // delivered on the next pump
```

The `EmitQueuedSignals()` driver is the load-bearing wiring decision: pick a single per-frame call
site (a `LoopScheduler` Update callback, a host `MonoBehaviour.Update`, or your own frame loop). Two
pumps per frame means two delivery rounds; no pump means signals accumulate and never fire.
Main-thread only.

## File Structure
```
Signaling/
  README.md
  MODULE.md
  Runtime/
    PFound.Signaling.asmdef
    SignalTracker.cs   # the bus: listeners, queue, deferred emit, re-entrancy + destroyed-owner guards
    SignalBase.cs      # marker base for signal types
    SignalKey.cs       # value-type signal identity
```

## Downstream Dependents
- **`PFound.HubApp`** — the mini-game host and its services (`ProfileService`, `StickerService`,
  `PhotoAlbumService`, `BadgeService`) publish/consume gameplay signals
  (`MiniGameStartedSignal`, `ProfileSelectedSignal`, `PhotoCapturedSignal`, `BadgePersistedSignal`,
  and others under `HubApp/Runtime/MiniGame/Signals/`).
- **`PFound.GuidedOnboardingFlow`** — `WaitForSignalStep` gates tutorial progression on a signal;
  wired via `TutorialInstaller` / `TutorialRuntimeServices`.

## Limitations / Known Gaps
- **Main-thread only.** No locking; `Queue`/`Add`/`Remove`/`EmitQueuedSignals` must run on the main
  thread.
- **Payload-free by design.** A signal carries no data — the type is the whole message. Pass state
  out-of-band (read shared state, or model distinct payloads as distinct signal types).
- **Needs an external pump.** Nothing ticks `EmitQueuedSignals()` for you; forget the driver and
  signals never deliver.
- **Per-round dedupe.** Queuing the same type twice before a pump delivers it once — you cannot fire
  the same signal N times within one round.
- **`causer` is diagnostics only.** It is accepted for future diagnostics but not currently surfaced
  to listeners or logged.
