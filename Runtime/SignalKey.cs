using System;
using UnityEngine;

namespace PFound.Signaling
{
    /// <summary>
    /// Identifies a signal type. Handed to listeners so one callback can serve several signal
    /// types and branch on which fired, and used as the dictionary key inside a
    /// <see cref="SignalTracker"/>.
    /// <para>
    /// The identity is a serialized type string (the signal type's assembly-qualified name), so a
    /// key can be authored in the inspector via a dropdown of <see cref="SignalBase"/>-derived
    /// types (see the editor drawer) and wired without a compile-time generic parameter. A key
    /// built with <see cref="Create{T}"/> and a key picked in the inspector for the same type
    /// compare equal.
    /// </para>
    /// </summary>
    [Serializable]
    public struct SignalKey : IEquatable<SignalKey>
    {
        // Serialized so designer-authored keys survive Unity serialization; the assembly-qualified
        // name round-trips back to a Type without a scan and stays equal to Create<T>()'s key.
        [SerializeField] private string _typeName;

        private SignalKey(string typeName) { _typeName = typeName; }

        /// <summary>The key for signal type <typeparamref name="T"/>.</summary>
        public static SignalKey Create<T>() where T : SignalBase => new SignalKey(typeof(T).AssemblyQualifiedName);

        /// <summary>The key identifying <paramref name="signalType"/> (must derive from <see cref="SignalBase"/>).</summary>
        public static SignalKey FromType(Type signalType)
        {
            if (signalType == null) throw new ArgumentNullException(nameof(signalType));
            if (!typeof(SignalBase).IsAssignableFrom(signalType))
                throw new ArgumentException($"{signalType} does not derive from {nameof(SignalBase)}.", nameof(signalType));
            return new SignalKey(signalType.AssemblyQualifiedName);
        }

        /// <summary>False for a default (unauthored) key — nothing was selected.</summary>
        public bool IsValid => !string.IsNullOrEmpty(_typeName);

        /// <summary>The serialized identity string (assembly-qualified type name), or null/empty when unset.</summary>
        public string TypeName => _typeName;

        /// <summary>
        /// The signal's runtime type, or null when the key is unset / the type is no longer loaded.
        /// The load lookup is an external boundary, so a null result here is expected and allowed.
        /// </summary>
        public Type SignalType => string.IsNullOrEmpty(_typeName) ? null : Type.GetType(_typeName);

        public bool Equals(SignalKey other) => string.Equals(_typeName, other._typeName, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is SignalKey other && Equals(other);
        public override int GetHashCode() => _typeName != null ? _typeName.GetHashCode() : 0;

        public override string ToString()
        {
            if (string.IsNullOrEmpty(_typeName)) return "<invalid>";
            Type t = SignalType;
            return t != null ? t.Name : _typeName;
        }
    }
}
