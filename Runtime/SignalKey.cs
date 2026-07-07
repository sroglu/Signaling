using System;

namespace PFound.Signaling
{
    /// <summary>
    /// Identifies a signal type. Handed to listeners so one callback can serve several signal
    /// types and branch on which fired. Value type, cheap to compare and use as a dictionary key.
    /// </summary>
    public readonly struct SignalKey : IEquatable<SignalKey>
    {
        private readonly Type _type;

        private SignalKey(Type type) { _type = type; }

        /// <summary>The key for signal type <typeparamref name="T"/>.</summary>
        public static SignalKey Create<T>() where T : SignalBase => new SignalKey(typeof(T));

        /// <summary>False for a default (uninitialized) key.</summary>
        public bool IsValid => _type != null;

        /// <summary>The signal's runtime type, or null for an invalid key.</summary>
        public Type SignalType => _type;

        public bool Equals(SignalKey other) => _type == other._type;
        public override bool Equals(object obj) => obj is SignalKey other && Equals(other);
        public override int GetHashCode() => _type?.GetHashCode() ?? 0;
        public override string ToString() => _type != null ? _type.Name : "<invalid>";
    }
}
