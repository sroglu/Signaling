using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PFound.Signaling
{
    /// <summary>
    /// Type-safe, deferred publish/subscribe bus. Signals are <see cref="Queue{T}"/>-d during a
    /// frame and delivered together by <see cref="EmitQueuedSignals"/>, in queue order, to each
    /// key's listeners (ordered). Queuing the same signal twice in one round is deduped. Listener
    /// add/remove during emit is deferred (takes effect next round); re-entrant emit is depth-
    /// guarded to break recursive storms; listeners bound to destroyed Unity objects are detected
    /// and dropped. Main-thread only.
    /// <para>
    /// Signals can be referenced by a compile-time type (the generic overloads) or by a runtime /
    /// serialized <see cref="SignalKey"/> (the by-key overloads), so they can be wired from the
    /// inspector with no static type parameter. Each queued signal captures its optional causer
    /// (originating object) for diagnostics; causer-aware handlers receive it. Adding the same
    /// callback twice for one key is detected, warned, and skipped. <see cref="Teardown"/> reports
    /// any listeners still registered at shutdown (leak audit) and clears the bus.
    /// </para>
    /// </summary>
    public sealed class SignalTracker
    {
        // Normalized listener: Original is the caller's delegate (identity for removal + the target
        // probed for a destroyed owner); Invoke is the causer-aware dispatch shim. A causer-less
        // callback is wrapped once at subscribe time so dispatch is uniform.
        private readonly struct Listener
        {
            public readonly Delegate Original;
            public readonly Action<SignalKey, Object> Invoke;
            public readonly int Order;
            public Listener(Delegate original, Action<SignalKey, Object> invoke, int order)
            {
                Original = original;
                Invoke = invoke;
                Order = order;
            }
        }

        private readonly struct QueuedSignal
        {
            public readonly SignalKey Key;
            public readonly Object Causer;
            public QueuedSignal(SignalKey key, Object causer) { Key = key; Causer = causer; }
        }

        private readonly Dictionary<SignalKey, List<Listener>> _listeners = new Dictionary<SignalKey, List<Listener>>();
        private readonly List<QueuedSignal> _queue = new List<QueuedSignal>();
        private readonly List<QueuedSignal> _emitBatch = new List<QueuedSignal>();
        private readonly List<Listener> _invokeBuffer = new List<Listener>();

        private const int MaxEmitDepth = 8; // re-entrant emit cooling: beyond this, stop and warn
        private int _emitDepth;

        /// <summary>When true, every emitted signal is logged with its causer (diagnostics; off by default to avoid log spam).</summary>
        public bool LogEmissions;

        /// <summary>True while any signal is waiting for the next <see cref="EmitQueuedSignals"/>.</summary>
        public bool HasQueuedSignals => _queue.Count > 0;

        // --- Subscribe -------------------------------------------------------------------------

        /// <summary>Subscribes <paramref name="callback"/> to signal <typeparamref name="T"/>. Lower <paramref name="order"/> runs first.</summary>
        public void AddListener<T>(Action<SignalKey> callback, int order = 0) where T : SignalBase
            => AddListener(SignalKey.Create<T>(), callback, order);

        /// <summary>Subscribes a causer-aware <paramref name="callback"/> to signal <typeparamref name="T"/>.</summary>
        public void AddListener<T>(Action<SignalKey, Object> callback, int order = 0) where T : SignalBase
            => AddListener(SignalKey.Create<T>(), callback, order);

        /// <summary>Subscribes <paramref name="callback"/> to the signal identified by <paramref name="key"/> (runtime/serialized key).</summary>
        public void AddListener(SignalKey key, Action<SignalKey> callback, int order = 0)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            Action<SignalKey, Object> invoke = (k, _) => callback(k);
            InternalAddListener(key, callback, invoke, order);
        }

        /// <summary>Subscribes a causer-aware <paramref name="callback"/> to the signal identified by <paramref name="key"/>.</summary>
        public void AddListener(SignalKey key, Action<SignalKey, Object> callback, int order = 0)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            InternalAddListener(key, callback, callback, order);
        }

        private void InternalAddListener(SignalKey key, Delegate original, Action<SignalKey, Object> invoke, int order)
        {
            if (!key.IsValid) throw new ArgumentException("Cannot add a listener for an invalid (unauthored) signal key.", nameof(key));

            if (!_listeners.TryGetValue(key, out var list))
            {
                list = new List<Listener>();
                _listeners[key] = list;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Original.Equals(original))
                {
                    Debug.LogWarning($"[Signaling] Duplicate listener for {key} ignored — the same callback is already subscribed.");
                    return;
                }
            }

            int index = list.Count; // stable insert: keep registration order among equal orders
            while (index > 0 && list[index - 1].Order > order) index--;
            list.Insert(index, new Listener(original, invoke, order));
        }

        // --- Unsubscribe -----------------------------------------------------------------------

        /// <summary>Unsubscribes <paramref name="callback"/> from signal <typeparamref name="T"/>.</summary>
        public void RemoveListener<T>(Action<SignalKey> callback) where T : SignalBase
            => RemoveListener(SignalKey.Create<T>(), callback);

        /// <summary>Unsubscribes a causer-aware <paramref name="callback"/> from signal <typeparamref name="T"/>.</summary>
        public void RemoveListener<T>(Action<SignalKey, Object> callback) where T : SignalBase
            => RemoveListener(SignalKey.Create<T>(), callback);

        /// <summary>Unsubscribes <paramref name="callback"/> from the signal identified by <paramref name="key"/>.</summary>
        public void RemoveListener(SignalKey key, Action<SignalKey> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            InternalRemoveListener(key, callback);
        }

        /// <summary>Unsubscribes a causer-aware <paramref name="callback"/> from the signal identified by <paramref name="key"/>.</summary>
        public void RemoveListener(SignalKey key, Action<SignalKey, Object> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            InternalRemoveListener(key, callback);
        }

        private void InternalRemoveListener(SignalKey key, Delegate original)
        {
            if (!_listeners.TryGetValue(key, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Original.Equals(original)) { list.RemoveAt(i); return; }
        }

        // --- Publish ---------------------------------------------------------------------------

        /// <summary>Queues signal <typeparamref name="T"/> for the next emit. Deduped per round.</summary>
        /// <param name="causer">Optional originating object, captured for diagnostics and passed to causer-aware handlers; may be null.</param>
        public void Queue<T>(Object causer) where T : SignalBase
            => Queue(SignalKey.Create<T>(), causer);

        /// <summary>Queues the signal identified by <paramref name="key"/> for the next emit. Deduped per round.</summary>
        /// <param name="causer">Optional originating object, captured for diagnostics; may be null.</param>
        public void Queue(SignalKey key, Object causer)
        {
            if (!key.IsValid) throw new ArgumentException("Cannot queue an invalid (unauthored) signal key.", nameof(key));
            for (int i = 0; i < _queue.Count; i++)
                if (_queue[i].Key.Equals(key)) return; // per-round dedupe; keep the first causer
            _queue.Add(new QueuedSignal(key, causer));
        }

        /// <summary>
        /// Delivers everything queued so far to its listeners, then empties the queue. Signals
        /// queued by a listener wait for the next call. Returns true if anything was delivered.
        /// </summary>
        public bool EmitQueuedSignals()
        {
            if (_queue.Count == 0) return false;
            if (_emitDepth >= MaxEmitDepth)
            {
                Debug.LogWarning($"[Signaling] Emit depth {_emitDepth} exceeded; suppressing recursive emit to avoid a storm.");
                return false;
            }

            _emitDepth++;
            try
            {
                _emitBatch.Clear();
                _emitBatch.AddRange(_queue); // snapshot: signals queued during emit go to next round
                _queue.Clear();

                for (int b = 0; b < _emitBatch.Count; b++)
                    Dispatch(_emitBatch[b].Key, _emitBatch[b].Causer);

                return true;
            }
            finally
            {
                _emitDepth--;
            }
        }

        private void Dispatch(SignalKey key, Object causer)
        {
            if (LogEmissions) Debug.Log($"[Signaling] Emitting {key} (causer: {(causer != null ? causer.ToString() : "none")}).");
            if (!_listeners.TryGetValue(key, out var list) || list.Count == 0) return;

            // Snapshot so add/remove during emit is deferred to the live list, not this pass.
            _invokeBuffer.Clear();
            _invokeBuffer.AddRange(list);

            for (int i = 0; i < _invokeBuffer.Count; i++)
            {
                var listener = _invokeBuffer[i];
                if (listener.Original.Target is Object target && target == null)
                {
                    Debug.LogWarning($"[Signaling] Dropping listener for {key} bound to a destroyed object (leaked subscription).");
                    RemoveDead(list, listener.Original);
                    continue;
                }
                listener.Invoke(key, causer);
            }
        }

        private static void RemoveDead(List<Listener> list, Delegate original)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Original.Equals(original)) { list.RemoveAt(i); return; }
        }

        // --- Introspection / teardown ----------------------------------------------------------

        /// <summary>Number of listeners currently registered for <paramref name="key"/>.</summary>
        public int ListenerCount(SignalKey key)
            => _listeners.TryGetValue(key, out var list) ? list.Count : 0;

        /// <summary>Total listeners registered across every signal.</summary>
        public int TotalListenerCount
        {
            get
            {
                int total = 0;
                foreach (var pair in _listeners) total += pair.Value.Count;
                return total;
            }
        }

        /// <summary>
        /// Shutdown audit: reports (as an error) any listeners still registered — a leaked
        /// subscription whose owner forgot to unsubscribe — then clears all listeners and the
        /// queue. Returns the number of leaked listeners found (0 = clean teardown).
        /// </summary>
        public int Teardown()
        {
            int leaked = 0;
            var report = new StringBuilder();
            foreach (var pair in _listeners)
            {
                int count = pair.Value.Count;
                if (count == 0) continue;
                leaked += count;
                report.Append($"\n  {pair.Key}: {count} listener(s) still registered");
            }

            if (leaked > 0)
                Debug.LogError($"[Signaling] Teardown found {leaked} leaked listener(s) — an owner did not unsubscribe:{report}");

            _listeners.Clear();
            _queue.Clear();
            _emitBatch.Clear();
            _invokeBuffer.Clear();
            return leaked;
        }
    }
}
