namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of a <see cref="PNCounter"/>: its increment and decrement halves. Obtain it
/// with <see cref="PNCounter.ToState"/> and reconstruct with <see cref="PNCounter.FromState"/>.
/// </summary>
/// <param name="Increments">The serialized increment half.</param>
/// <param name="Decrements">The serialized decrement half.</param>
public sealed record PNCounterState(GCounterState Increments, GCounterState Decrements);
