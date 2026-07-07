using System;
using System.Collections.Generic;
using UnityEngine;

namespace PFound.Signaling
{
    /// <summary>
    /// Type-safe, deferred publish/subscribe bus. Signals are <see cref="Queue{T}"/>-d during a
    /// frame and delivered together by <see cref="EmitQueuedSignals"/>, in queue order, to each
    /// type's listeners (ordered). Queuing the same type twice in one round is deduped. Listener
    /// add/remove during emit is deferred (takes effect next round); re-entrant emit is depth-
    /// guarded to break recursive storms; listeners bound to destroyed Unity objects are detected
    /// and dropped. Main-thread only.
    /// </summary>
    public sealed class SignalTracker
    {
        private readonly struct Listener
        {
            public readonly Action<SignalKey> Callback;
            public readonly int Order;
            public Listener(Action<SignalKey> callback, int order) { Callback = callback; Order = order; }
        }

        private readonly Dictionary<SignalKey, List<Listener>> _listeners = new Dictionary<SignalKey, List<Listener>>();
        private readonly List<SignalKey> _queue = new List<SignalKey>();
        private readonly List<SignalKey> _emitBatch = new List<SignalKey>();
        private readonly List<Listener> _invokeBuffer = new List<Listener>();

        private const int MaxEmitDepth = 8; // re-entrant emit cooling: beyond this, stop and warn
        private int _emitDepth;

        /// <summary>Subscribes <paramref name="callback"/> to signal <typeparamref name="T"/>. Lower <paramref name="order"/> runs first.</summary>
        public void AddListener<T>(Action<SignalKey> callback, int order = 0) where T : SignalBase
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var key = SignalKey.Create<T>();
            if (!_listeners.TryGetValue(key, out var list))
            {
                list = new List<Listener>();
                _listeners[key] = list;
            }
            int i = list.Count; // stable insert: keep registration order among equal orders
            while (i > 0 && list[i - 1].Order > order) i--;
            list.Insert(i, new Listener(callback, order));
        }

        /// <summary>Unsubscribes <paramref name="callback"/> from signal <typeparamref name="T"/>.</summary>
        public void RemoveListener<T>(Action<SignalKey> callback) where T : SignalBase
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!_listeners.TryGetValue(SignalKey.Create<T>(), out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Callback == callback) { list.RemoveAt(i); return; }
        }

        /// <summary>Queues signal <typeparamref name="T"/> for the next emit. Deduped per round.</summary>
        /// <param name="causer">Optional originating Unity object (used only for diagnostics); may be null.</param>
        public void Queue<T>(UnityEngine.Object causer) where T : SignalBase
        {
            var key = SignalKey.Create<T>();
            if (!_queue.Contains(key)) _queue.Add(key);
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
                    Dispatch(_emitBatch[b]);

                return true;
            }
            finally
            {
                _emitDepth--;
            }
        }

        private void Dispatch(SignalKey key)
        {
            if (!_listeners.TryGetValue(key, out var list) || list.Count == 0) return;

            // Snapshot so add/remove during emit is deferred to the live list, not this pass.
            _invokeBuffer.Clear();
            _invokeBuffer.AddRange(list);

            for (int i = 0; i < _invokeBuffer.Count; i++)
            {
                var callback = _invokeBuffer[i].Callback;
                if (callback.Target is UnityEngine.Object target && target == null)
                {
                    Debug.LogWarning($"[Signaling] Dropping listener for {key} bound to a destroyed object (leaked subscription).");
                    RemoveDead(list, callback);
                    continue;
                }
                callback(key);
            }
        }

        private static void RemoveDead(List<Listener> list, Action<SignalKey> callback)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Callback == callback) { list.RemoveAt(i); return; }
        }
    }
}
