# PFound.Signaling

Type-safe, deferred publish/subscribe signal bus. Signals are payload-free — the type *is* the
message. Publishers `Queue<T>` during a frame; a single `EmitQueuedSignals()` pump delivers the
batch to listeners in queue order. Instance-based: you own a `SignalTracker` and drive its pump.

## Model

- **Signals are types.** Declare a signal by deriving from `SignalBase`; listeners receive a
  `SignalKey` identifying which type fired, so one callback can serve several signals and branch.
- **Deferred delivery.** `Queue<T>` records the signal (deduped per round); nothing runs until
  `EmitQueuedSignals()`. Signals a listener queues during emit wait for the next pump.
- **Ordered listeners.** Lower `order` runs first; equal orders keep registration order.
- **Safe under fire.** Listener add/remove during emit is deferred to the next round; re-entrant
  emit is depth-guarded (max 8) to break recursive storms; a listener bound to a destroyed
  `UnityEngine.Object` is detected and dropped with a warning.

## Public API (`SignalTracker`)

- `AddListener<T>(Action<SignalKey> callback, int order = 0)` / `RemoveListener<T>(callback)` —
  subscribe/unsubscribe (`T : SignalBase`).
- `Queue<T>(UnityEngine.Object causer)` — queue a signal for the next emit (`causer` is diagnostics
  only, may be `null`).
- `bool EmitQueuedSignals()` — deliver everything queued so far, then empty the queue; returns
  `true` if anything was delivered.
- `SignalKey.Create<T>()`, `SignalKey.IsValid`, `SignalKey.SignalType` — the identity handed to
  listeners.

## Setup / wiring

Instance-based, no scene object — `new SignalTracker()`. The consumer owns the instance (keep it in
your bootstrap, or register it in `DependencyContainer`) and **must call `EmitQueuedSignals()` once
per frame from a driver** — nothing pumps it for you.

```csharp
var signals = new SignalTracker();
signals.AddListener<LevelLoaded>(OnLevelLoaded);

// pump it every frame — e.g. via LoopScheduler:
LoopScheduler.RegisterUpdateLoop(() => signals.EmitQueuedSignals(), host);

// publish:
signals.Queue<LevelLoaded>(causer: null);   // delivered on the next pump
```

Main-thread only. There is no static/singleton accessor — share the one instance you construct.

## Layout

`Runtime/` — `SignalBase` (marker base for signals), `SignalKey` (value-type identity), `SignalTracker`
(the bus). Assembly `PFound.Signaling` (`autoReferenced:false`; references the engine for the
destroyed-owner check and warning log).
