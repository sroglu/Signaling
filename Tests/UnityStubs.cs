// Minimal UnityEngine shims so PFound.Signaling's runtime (which touches UnityEngine.Object
// fake-null liveness, Debug logging, and [SerializeField]) compiles and runs under mono/csc with
// no Unity present. Excluded from the real Unity build by the UNITY_5_3_OR_NEWER guard, so there
// is never a duplicate-type clash inside the editor.
#if !UNITY_5_3_OR_NEWER
using System.Collections.Generic;

namespace UnityEngine
{
    // Models Unity's "fake-null": a destroyed object compares == null via the overloaded operators.
    public class Object
    {
        private bool _alive = true;

        /// <summary>Test helper: simulate Unity destroying this object.</summary>
        public void MarkDestroyed() => _alive = false;

        private static bool IsNull(Object o) => ReferenceEquals(o, null) || !o._alive;

        public static bool operator ==(Object a, Object b)
            => IsNull(a) == IsNull(b) && (IsNull(a) || ReferenceEquals(a, b));

        public static bool operator !=(Object a, Object b) => !(a == b);

        public override bool Equals(object other)
            => other is Object o ? this == o : ReferenceEquals(other, null) && IsNull(this);

        public override int GetHashCode()
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

        public override string ToString() => _alive ? GetType().Name : "null";
    }

    public sealed class SerializeFieldAttribute : System.Attribute { }

    public static class Debug
    {
        public static readonly List<string> Logs = new List<string>();
        public static readonly List<string> Warnings = new List<string>();
        public static readonly List<string> Errors = new List<string>();

        public static void Log(object message) => Logs.Add(message?.ToString());
        public static void LogWarning(object message) => Warnings.Add(message?.ToString());
        public static void LogError(object message) => Errors.Add(message?.ToString());

        public static void Clear()
        {
            Logs.Clear();
            Warnings.Clear();
            Errors.Clear();
        }
    }
}
#endif
