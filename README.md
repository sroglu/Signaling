# PFound.Signaling

A type-safe, deferred publish/subscribe signal bus. Signals are payload-free — the type *is* the
message. Publishers `Queue<T>` during a frame; a single `EmitQueuedSignals()` pump delivers the batch
to listeners in queue order. Instance-based: you own a `SignalTracker` and drive its pump.

## Quick reference

```csharp
public sealed class LevelLoaded : SignalBase { }

var signals = new SignalTracker();
signals.AddListener<LevelLoaded>(OnLevelLoaded);

// pump once per frame — nothing does this for you:
LoopScheduler.RegisterUpdateLoop(() => signals.EmitQueuedSignals(), host);

signals.Queue<LevelLoaded>(causer: null);   // delivered on the next pump
```

## Dependencies

Engine only (destroyed-owner check + warning log). `autoReferenced:false`.

## Docs

Deep reference: [MODULE.md](MODULE.md) — signal model, ordered/deferred/re-entrancy semantics, full
API, the per-frame `EmitQueuedSignals()` driver requirement, and downstream dependents.
