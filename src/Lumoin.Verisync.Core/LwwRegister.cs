using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A last-writer-wins register: a single value tagged with the <see cref="Timestamp"/> and
/// <see cref="ReplicaId"/> of the write that produced it. Merge keeps the value with the higher
/// (timestamp, writer) pair.
/// </summary>
/// <typeparam name="TValue">The type of the stored value.</typeparam>
/// <remarks>
/// <para>
/// A <see cref="LwwRegister{TValue}"/> is an immutable value. <see cref="Write(TValue, Timestamp, ReplicaId)"/>
/// and <see cref="Merge(LwwRegister{TValue})"/> return new registers and never mutate the receiver.
/// </para>
/// <para>
/// Merge is commutative, associative, and idempotent under the assumption that a given
/// (<see cref="Timestamp"/>, writer) pair identifies a single write: timestamps are compared first and
/// the writing <see cref="ReplicaId"/> breaks ties, so the outcome is independent of merge order.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class LwwRegister<TValue>: IEquatable<LwwRegister<TValue>>
{
    private readonly TValue? value;


    private LwwRegister(bool hasValue, TValue? value, Timestamp timestamp, ReplicaId? writer)
    {
        HasValue = hasValue;
        this.value = value;
        Timestamp = timestamp;
        Writer = writer;
    }


    /// <summary>An empty register that holds no value and loses to any written register on merge.</summary>
    public static LwwRegister<TValue> Empty { get; } = new(false, default, default, null);


    /// <summary>Whether the register currently holds a value.</summary>
    public bool HasValue { get; }


    /// <summary>The stored value.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the register holds no value.</exception>
    public TValue Value => HasValue ? value! : throw new InvalidOperationException("The register holds no value.");


    /// <summary>The timestamp of the current value, or the default timestamp when empty.</summary>
    public Timestamp Timestamp { get; }


    /// <summary>The replica that wrote the current value, or <see langword="null"/> when empty.</summary>
    public ReplicaId? Writer { get; }


    /// <summary>
    /// Returns a new register holding <paramref name="value"/>, stamped with <paramref name="timestamp"/>
    /// and <paramref name="writer"/>.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="timestamp">The write timestamp, typically obtained from a <see cref="TimeProvider"/> at a higher level.</param>
    /// <param name="writer">The replica performing the write.</param>
    /// <returns>A new <see cref="LwwRegister{TValue}"/>; this register is not modified.</returns>
    public LwwRegister<TValue> Write(TValue value, Timestamp timestamp, ReplicaId writer)
    {
        return new LwwRegister<TValue>(true, value, timestamp, writer);
    }


    /// <summary>
    /// Returns a new register holding <paramref name="value"/>, stamped with a timestamp read from
    /// <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="writer">The replica performing the write.</param>
    /// <param name="timeProvider">The clock used to stamp this single write.</param>
    /// <returns>A new <see cref="LwwRegister{TValue}"/>; this register is not modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public LwwRegister<TValue> Write(TValue value, ReplicaId writer, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return Write(value, new Timestamp(timeProvider.GetUtcNow().UtcTicks), writer);
    }


    /// <summary>
    /// Returns the merge of this register and <paramref name="other"/>: the value with the higher
    /// (timestamp, writer) pair. An empty register loses to any register that holds a value.
    /// </summary>
    /// <param name="other">The register to merge with.</param>
    /// <returns>A new <see cref="LwwRegister{TValue}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public LwwRegister<TValue> Merge(LwwRegister<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if(!HasValue)
        {
            return other;
        }

        if(!other.HasValue)
        {
            return this;
        }

        int comparison = Timestamp.CompareTo(other.Timestamp);
        if(comparison == 0)
        {
            comparison = Writer!.Value.CompareTo(other.Writer!.Value);
        }

        return comparison >= 0 ? this : other;
    }


    /// <summary>
    /// Returns the serializable state of this register, for persistence or transfer.
    /// </summary>
    /// <returns>The register's state.</returns>
    public LwwRegisterState<TValue> ToState()
    {
        return HasValue
            ? new LwwRegisterState<TValue>(true, value, Timestamp.UtcTicks, ImmutableArray.Create(Writer!.Value.AsSpan()))
            : new LwwRegisterState<TValue>(false, default, 0, ImmutableArray<byte>.Empty);
    }


    /// <summary>
    /// Reconstructs a register from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed register.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    public static LwwRegister<TValue> FromState(LwwRegisterState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if(!state.HasValue)
        {
            return Empty;
        }

        return new LwwRegister<TValue>(true, state.Value, new Timestamp(state.UtcTicks), ReplicaId.FromSpan(state.Writer.AsSpan()));
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] LwwRegister<TValue>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(HasValue != other.HasValue)
        {
            return false;
        }

        if(!HasValue)
        {
            return true;
        }

        return Timestamp.Equals(other.Timestamp)
            && Writer!.Value.Equals(other.Writer!.Value)
            && EqualityComparer<TValue>.Default.Equals(value, other.value);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LwwRegister<TValue> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return HasValue
            ? HashCode.Combine(Timestamp, Writer, value)
            : 0;
    }


    private string DebuggerDisplay => HasValue
        ? $"LwwRegister: {value} @ {Timestamp.UtcTicks}"
        : "LwwRegister: (empty)";
}
