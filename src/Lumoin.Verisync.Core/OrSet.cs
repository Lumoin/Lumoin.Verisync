using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// An observed-remove set: elements can be added and removed concurrently, and a removal only affects
/// the additions the remover has observed. A concurrent addition the remover did not observe survives,
/// so an add and a concurrent remove of the same element resolve to the element being present.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
/// <para>
/// An <see cref="OrSet{T}"/> wraps a <see cref="DottedVersionVectorSet{T}"/>: each
/// <see cref="Add(T, ReplicaId)"/> tags the element with a fresh dot, and <see cref="Remove(T)"/>
/// drops the dots the set currently holds for that element while keeping the causal context, so removed
/// additions do not resurrect on merge. It is an immutable value; every operation returns a new set, and
/// <see cref="Merge(OrSet{T})"/> is commutative, associative, and idempotent.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class OrSet<T>: IEquatable<OrSet<T>>
{
    private DottedVersionVectorSet<T> Entries { get; }


    private OrSet(DottedVersionVectorSet<T> entries)
    {
        Entries = entries;
    }


    /// <summary>An empty set.</summary>
    public static OrSet<T> Empty { get; } = new(DottedVersionVectorSet<T>.Empty);


    /// <summary>The distinct elements currently in the set.</summary>
    public IReadOnlyCollection<T> Elements => Entries.Values;


    /// <summary>Whether <paramref name="element"/> is currently in the set.</summary>
    /// <param name="element">The element to test for.</param>
    /// <returns><see langword="true"/> if the element is present; otherwise <see langword="false"/>.</returns>
    public bool Contains(T element) => Entries.Values.Contains(element);


    /// <summary>
    /// Returns a new set with <paramref name="element"/> added under a fresh dot for <paramref name="replica"/>.
    /// </summary>
    /// <param name="element">The element to add.</param>
    /// <param name="replica">The replica performing the add.</param>
    /// <returns>A new <see cref="OrSet{T}"/>; this set is not modified.</returns>
    public OrSet<T> Add(T element, ReplicaId replica)
    {
        return new OrSet<T>(Entries.Add(replica, element));
    }


    /// <summary>
    /// Returns a new set with <paramref name="element"/> removed, affecting only the additions this set
    /// has observed.
    /// </summary>
    /// <param name="element">The element to remove.</param>
    /// <returns>A new <see cref="OrSet{T}"/>, or this set if the element was not present.</returns>
    public OrSet<T> Remove(T element)
    {
        return new OrSet<T>(Entries.RemoveValue(element));
    }


    /// <summary>
    /// Returns the merge of this set and <paramref name="other"/>.
    /// </summary>
    /// <param name="other">The set to merge with.</param>
    /// <returns>A new <see cref="OrSet{T}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public OrSet<T> Merge(OrSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new OrSet<T>(Entries.Merge(other.Entries));
    }


    /// <summary>
    /// Returns the serializable state of this set, for persistence or transfer.
    /// </summary>
    /// <returns>The set's state.</returns>
    public OrSetState<T> ToState() => new(Entries.ToState());


    /// <summary>
    /// Reconstructs a set from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed set.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    public static OrSet<T> FromState(OrSetState<T> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new OrSet<T>(DottedVersionVectorSet<T>.FromState(state.Set));
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] OrSet<T>? other)
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
    public override bool Equals(object? obj) => obj is OrSet<T> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => Entries.GetHashCode();


    private string DebuggerDisplay => $"OrSet: {Entries.Values.Count} element(s)";
}
