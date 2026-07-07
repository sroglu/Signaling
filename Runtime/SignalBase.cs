namespace PFound.Signaling
{
    /// <summary>
    /// Marker base for signal types dispatched through a <see cref="SignalTracker"/>. Signals are
    /// payload-free: the type itself is the message, identified to listeners by a <see cref="SignalKey"/>.
    /// </summary>
    public abstract class SignalBase { }
}
