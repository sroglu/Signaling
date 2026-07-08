// Standalone mono/csc runner for PFound.Signaling — no Unity, no NUnit. Exit code 0 = all green.
//   csc -nologo -warn:0 -out:/tmp/pf_sig.exe Assets/PFound/Signaling/Runtime/*.cs Assets/PFound/Signaling/Tests/*.cs && mono /tmp/pf_sig.exe
// Excluded from the Unity build (the stubs it relies on are UNITY_5_3_OR_NEWER-guarded).
#if !UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using PFound.Signaling;

namespace PFound.Signaling.Tests
{
    public sealed class SigA : SignalBase { }
    public sealed class SigB : SignalBase { }
    public sealed class SigC : SignalBase { }

    // A subscriber that lives on a fake UnityEngine.Object so the destroyed-owner drop can be exercised.
    internal sealed class FakeOwner : UnityEngine.Object
    {
        public int Count;
        public void OnSignal(SignalKey key) => Count++;
    }

    internal static class Kit
    {
        public static int Passed;
        public static int Failed;

        public static void Check(bool cond, string name)
        {
            if (cond) Passed++;
            else { Failed++; Console.WriteLine("  FAIL: " + name); }
        }

        public static void Throws<TEx>(Action a, string name) where TEx : Exception
        {
            try { a(); Failed++; Console.WriteLine("  FAIL (no throw): " + name); }
            catch (TEx) { Passed++; }
            catch (Exception e) { Failed++; Console.WriteLine("  FAIL (wrong ex " + e.GetType().Name + "): " + name); }
        }

        public static int Summary()
        {
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine("PFound.Signaling: passed=" + Passed + " failed=" + Failed);
            return Failed == 0 ? 0 : 1;
        }
    }

    internal static class Program
    {
        public static int Main()
        {
            ExistingBehaviorPreserved();
            ByKeyApi();
            CauserDiagnostics();
            DuplicateListenerDetection();
            LeakedListenerAudit();
            SerializableSignalKey();
            return Kit.Summary();
        }

        // --- Regression guard: keep the shipped behavior intact -------------------------------

        private static void ExistingBehaviorPreserved()
        {
            var bus = new SignalTracker();

            // ordered listeners: lower order first, equal order keeps registration order
            var log = new StringBuilder();
            bus.AddListener<SigA>(_ => log.Append("b"), order: 5);
            bus.AddListener<SigA>(_ => log.Append("a"), order: -1);
            bus.AddListener<SigA>(_ => log.Append("c"), order: 5);
            bus.Queue<SigA>(null);
            Kit.Check(bus.EmitQueuedSignals(), "emit returns true when something delivered");
            Kit.Check(log.ToString() == "abc", "listeners run in order (a<-1, then b,c at 5 in reg order)");

            // per-round dedupe: queue twice, deliver once
            int hits = 0;
            var bus2 = new SignalTracker();
            bus2.AddListener<SigB>(_ => hits++);
            bus2.Queue<SigB>(null);
            bus2.Queue<SigB>(null);
            bus2.EmitQueuedSignals();
            Kit.Check(hits == 1, "queuing the same signal twice in a round delivers once");

            // deferred: a signal queued during emit waits for the next pump
            var bus3 = new SignalTracker();
            int aCount = 0, bCount = 0;
            bus3.AddListener<SigA>(_ => { aCount++; bus3.Queue<SigB>(null); });
            bus3.AddListener<SigB>(_ => bCount++);
            bus3.Queue<SigA>(null);
            bus3.EmitQueuedSignals();
            Kit.Check(aCount == 1 && bCount == 0, "signal queued during emit is deferred to next round");
            bus3.EmitQueuedSignals();
            Kit.Check(bCount == 1, "deferred signal delivers on the next pump");

            // empty queue -> false
            Kit.Check(!new SignalTracker().EmitQueuedSignals(), "emit returns false with an empty queue");

            // dead-target drop: destroyed owner's listener is skipped and removed
            var bus4 = new SignalTracker();
            var owner = new FakeOwner();
            bus4.AddListener<SigC>(owner.OnSignal);
            owner.MarkDestroyed();
            bus4.Queue<SigC>(null);
            bus4.EmitQueuedSignals();
            Kit.Check(owner.Count == 0, "listener on a destroyed owner is not invoked");
            Kit.Check(bus4.ListenerCount(SignalKey.Create<SigC>()) == 0, "destroyed-owner listener is removed");
        }

        // --- Gap 1: by-key subscribe / queue / remove ----------------------------------------

        private static void ByKeyApi()
        {
            var bus = new SignalTracker();
            SignalKey key = SignalKey.Create<SigA>();

            int hits = 0;
            Action<SignalKey> cb = _ => hits++;

            // subscribe by key, publish by key
            bus.AddListener(key, cb);
            bus.Queue(key, null);
            bus.EmitQueuedSignals();
            Kit.Check(hits == 1, "by-key subscribe + by-key queue delivers");

            // by-key key equals the generic key: generic queue reaches a by-key listener
            bus.Queue<SigA>(null);
            bus.EmitQueuedSignals();
            Kit.Check(hits == 2, "generic queue reaches a by-key listener (keys unify)");

            // remove by key
            bus.RemoveListener(key, cb);
            bus.Queue(key, null);
            bus.EmitQueuedSignals();
            Kit.Check(hits == 2, "by-key remove stops delivery");

            // a by-key listener also receives a generic-typed subscription's signal path
            int hits2 = 0;
            bus.AddListener<SigB>(_ => hits2++);
            bus.Queue(SignalKey.Create<SigB>(), null);
            bus.EmitQueuedSignals();
            Kit.Check(hits2 == 1, "generic subscribe reached via by-key queue (keys unify both ways)");

            // invalid (unauthored) key is rejected at the boundary
            Kit.Throws<ArgumentException>(() => bus.AddListener(default(SignalKey), _ => { }), "AddListener rejects an invalid key");
            Kit.Throws<ArgumentException>(() => bus.Queue(default(SignalKey), null), "Queue rejects an invalid key");
        }

        // --- Gap 2: causer captured + surfaced -----------------------------------------------

        private static void CauserDiagnostics()
        {
            var bus = new SignalTracker();
            var causer = new FakeOwner();

            UnityEngine.Object received = null;
            int hits = 0;
            bus.AddListener<SigA>((key, c) => { hits++; received = c; });

            bus.Queue<SigA>(causer);
            bus.EmitQueuedSignals();
            Kit.Check(hits == 1 && ReferenceEquals(received, causer), "causer-aware handler receives the queued causer");

            // first causer wins under per-round dedupe
            var other = new FakeOwner();
            received = null;
            bus.Queue<SigA>(causer);
            bus.Queue<SigA>(other);
            bus.EmitQueuedSignals();
            Kit.Check(ReferenceEquals(received, causer), "dedupe keeps the first causer");

            // optional emit logging surfaces the causer
            UnityEngine.Debug.Clear();
            bus.LogEmissions = true;
            bus.Queue<SigA>(causer);
            bus.EmitQueuedSignals();
            Kit.Check(UnityEngine.Debug.Logs.Count == 1 && UnityEngine.Debug.Logs[0].Contains("causer"),
                "LogEmissions logs the emit with its causer");
        }

        // --- Gap 3: duplicate listener warn + skip -------------------------------------------

        private static void DuplicateListenerDetection()
        {
            var bus = new SignalTracker();
            int hits = 0;
            Action<SignalKey> cb = _ => hits++;

            UnityEngine.Debug.Clear();
            bus.AddListener<SigA>(cb);
            bus.AddListener<SigA>(cb); // duplicate
            Kit.Check(bus.ListenerCount(SignalKey.Create<SigA>()) == 1, "duplicate add is skipped (one listener kept)");
            Kit.Check(UnityEngine.Debug.Warnings.Count == 1, "duplicate add warns once");

            bus.Queue<SigA>(null);
            bus.EmitQueuedSignals();
            Kit.Check(hits == 1, "duplicate listener fires only once");
        }

        // --- Gap 4: leaked-listener audit on teardown ----------------------------------------

        private static void LeakedListenerAudit()
        {
            var bus = new SignalTracker();
            bus.AddListener<SigA>(_ => { });
            bus.AddListener<SigB>(_ => { });
            bus.AddListener<SigB>(_ => { }, order: 1);

            UnityEngine.Debug.Clear();
            int leaked = bus.Teardown();
            Kit.Check(leaked == 3, "Teardown reports the count of still-registered listeners");
            Kit.Check(UnityEngine.Debug.Errors.Count == 1, "Teardown logs an error when listeners leak");
            Kit.Check(bus.TotalListenerCount == 0, "Teardown clears all listeners");

            // a properly-unsubscribed bus reports zero leaks and logs nothing
            var clean = new SignalTracker();
            Action<SignalKey> cb = _ => { };
            clean.AddListener<SigA>(cb);
            clean.RemoveListener<SigA>(cb);
            UnityEngine.Debug.Clear();
            Kit.Check(clean.Teardown() == 0, "clean teardown reports zero leaks");
            Kit.Check(UnityEngine.Debug.Errors.Count == 0, "clean teardown logs no error");
        }

        // --- Gap 5: serializable SignalKey identity ------------------------------------------

        private static void SerializableSignalKey()
        {
            SignalKey a = SignalKey.Create<SigA>();
            SignalKey a2 = SignalKey.FromType(typeof(SigA));
            SignalKey b = SignalKey.Create<SigB>();

            Kit.Check(a.Equals(a2), "Create<T>() and FromType(typeof(T)) produce equal keys");
            Kit.Check(a.GetHashCode() == a2.GetHashCode(), "equal keys share a hash code");
            Kit.Check(!a.Equals(b), "different signal types produce different keys");
            Kit.Check(a.IsValid && !default(SignalKey).IsValid, "IsValid distinguishes authored from default keys");
            Kit.Check(a.SignalType == typeof(SigA), "SignalType round-trips from the stored type name");
            Kit.Check(a.ToString() == "SigA", "ToString shows the short type name");
            Kit.Check(!string.IsNullOrEmpty(a.TypeName), "authored key exposes a serialized type name");

            Kit.Throws<ArgumentException>(() => SignalKey.FromType(typeof(string)), "FromType rejects a non-SignalBase type");
            Kit.Throws<ArgumentNullException>(() => SignalKey.FromType(null), "FromType rejects null");
        }
    }
}
#endif
