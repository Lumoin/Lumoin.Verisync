using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A multi-value register: a write replaces every value the writer has observed, but concurrent writes
/// from different replicas are all retained until a later write or merge observes and supersedes them.
/// </summary>
/// <typeparam name="TValue">The type of the stored values.</typeparam>
/// <remarks>
/// <para>
/// A <see cref="MvRegister{TValue}"/> wraps a <see cref="DottedVersionVectorSet{T}"/>: a local
/// <see cref="Write(TValue, ReplicaId)"/> clears the values the register currently holds (it has
/// observed them) and adds the new value under a fresh dot, so locally the register reads as a single
/// value. <see cref="Merge(MvRegister{TValue})"/> keeps values whose dots neither side has superseded,
/// surfacing genuine concurrency as multiple values.
/// </para>
/// <para>
/// It is an immutable value; every operation returns a new register. The underlying causal merge is
/// commutative, associative, and idempotent.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class MvRegister<TValue>: IEquatable<MvRegister<TValue>>
{
    private DottedVersionVectorSet<TValue> Entries { get; }


    private MvRegister(DottedVersionVectorSet<TValue> entries)
    {
        Entries = entries;
    }


    /// <summary>An empty register that holds no values.</summary>
    public static MvRegister<TValue> Empty { get; } = new(DottedVersionVectorSet<TValue>.Empty);


    /// <summary>The current values: one value normally, or several when concurrent writes are unresolved.</summary>
    public IReadOnlyCollection<TValue> Values => Entries.Values;


    /// <summary>
    /// Returns a new register in which <paramref name="value"/> replaces every value this register has
    /// observed.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <param name="writer">The replica performing the write.</param>
    /// <returns>A new <see cref="MvRegister{TValue}"/>; this register is not modified.</returns>
    public MvRegister<TValue> Write(TValue value, ReplicaId writer)
    {
        return new MvRegister<TValue>(Entries.ClearValues().Add(writer, value));
    }


    /// <summary>
    /// Returns the merge of this register and <paramref name="other"/>, retaining all concurrent values.
    /// </summary>
    /// <param name="other">The register to merge with.</param>
    /// <returns>A new <see cref="MvRegister{TValue}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public MvRegister<TValue> Merge(MvRegister<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new MvRegister<TValue>(Entries.Merge(other.Entries));
    }


    /// <summary>
    /// Returns the serializable state of this register, for persistence or transfer.
    /// </summary>
    /// <returns>The register's state.</returns>
    public MvRegisterState<TValue> ToState() => new(Entries.ToState());


    /// <summary>
    /// Reconstructs a register from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed register.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    public static MvRegister<TValue> FromState(MvRegisterState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new MvRegister<TValue>(DottedVersionVectorSet<TValue>.FromState(state.Entries));
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] MvRegister<TValue>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Entries.Equals(other.Entries);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MvRegister<TValue> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => Entries.GetHashCode();


    private string DebuggerDisplay => $"MvRegister: {Entries.Count} value(s)";
}
