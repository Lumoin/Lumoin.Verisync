using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A positive-negative counter CRDT supporting both increment and decrement. It wraps two
/// <see cref="GCounter"/> values — one accumulating increments, one accumulating decrements — and
/// reports their difference.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="PNCounter"/> is an immutable value. Every operation returns a new counter and never
/// mutates the receiver. Because each half is a grow-only <see cref="GCounter"/>, the state-based
/// merge inherits the join-semilattice properties: it is commutative, associative, and idempotent, so
/// replicas converge regardless of the order in which they exchange state.
/// </para>
/// <para>
/// Increments and decrements take strictly positive amounts; a decrease is recorded by accumulating
/// into the negative half rather than by subtracting from the positive half, which is what keeps both
/// halves grow-only and the merge conflict-free.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class PNCounter: IEquatable<PNCounter>
{
    private GCounter Increments { get; }
    private GCounter Decrements { get; }


    private PNCounter(GCounter increments, GCounter decrements)
    {
        Increments = increments;
        Decrements = decrements;
    }


    /// <summary>An empty counter whose value is zero.</summary>
    public static PNCounter Empty { get; } = new(GCounter.Empty, GCounter.Empty);


    /// <summary>Gets the counter value: total increments minus total decrements. May be negative.</summary>
    public int Value => Increments.Value - Decrements.Value;


    /// <summary>
    /// Returns a new counter with <paramref name="replica"/>'s increment sub-counter increased by one.
    /// </summary>
    /// <param name="replica">The replica recording the increment.</param>
    /// <returns>A new <see cref="PNCounter"/>; this counter is not modified.</returns>
    public PNCounter Increment(ReplicaId replica) => Increment(replica, 1);


    /// <summary>
    /// Returns a new counter with <paramref name="replica"/>'s increment sub-counter increased by
    /// <paramref name="amount"/>.
    /// </summary>
    /// <param name="replica">The replica recording the increment.</param>
    /// <param name="amount">The positive amount to add.</param>
    /// <returns>A new <see cref="PNCounter"/>; this counter is not modified.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="amount"/> is less than or equal to zero.</exception>
    public PNCounter Increment(ReplicaId replica, int amount)
    {
        return new PNCounter(Increments.Increment(replica, amount), Decrements);
    }


    /// <summary>
    /// Returns a new counter with <paramref name="replica"/>'s decrement sub-counter increased by one.
    /// </summary>
    /// <param name="replica">The replica recording the decrement.</param>
    /// <returns>A new <see cref="PNCounter"/>; this counter is not modified.</returns>
    public PNCounter Decrement(ReplicaId replica) => Decrement(replica, 1);


    /// <summary>
    /// Returns a new counter with <paramref name="replica"/>'s decrement sub-counter increased by
    /// <paramref name="amount"/>.
    /// </summary>
    /// <param name="replica">The replica recording the decrement.</param>
    /// <param name="amount">The positive amount to subtract from the value.</param>
    /// <returns>A new <see cref="PNCounter"/>; this counter is not modified.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="amount"/> is less than or equal to zero.</exception>
    public PNCounter Decrement(ReplicaId replica, int amount)
    {
        return new PNCounter(Increments, Decrements.Increment(replica, amount));
    }


    /// <summary>
    /// Returns the merge of this counter and <paramref name="other"/>: the element-wise maximum of
    /// each half.
    /// </summary>
    /// <param name="other">The counter to merge with.</param>
    /// <returns>A new <see cref="PNCounter"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public PNCounter Merge(PNCounter other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new PNCounter(Increments.Merge(other.Increments), Decrements.Merge(other.Decrements));
    }


    /// <summary>
    /// Returns the serializable state of this counter, for persistence or transfer.
    /// </summary>
    /// <returns>The counter's state.</returns>
    public PNCounterState ToState() => new(Increments.ToState(), Decrements.ToState());


    /// <summary>
    /// Reconstructs a counter from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed counter.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    public static PNCounter FromState(PNCounterState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new PNCounter(GCounter.FromState(state.Increments), GCounter.FromState(state.Decrements));
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] PNCounter? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Increments.Equals(other.Increments) && Decrements.Equals(other.Decrements);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PNCounter other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Increments, Decrements);


    private string DebuggerDisplay => $"PNCounter: {Value} (+{Increments.Value} -{Decrements.Value})";
}
