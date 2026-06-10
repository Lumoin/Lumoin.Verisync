using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A causal set of values, each tagged with the <see cref="Dot"/> of the event that introduced it,
/// together with a <see cref="VectorClock"/> context summarizing every event the set has observed.
/// </summary>
/// <typeparam name="T">The type of the tagged values.</typeparam>
/// <remarks>
/// <para>
/// This is the shared primitive behind concurrency-preserving CRDTs: the multi-value register keeps
/// concurrent values, and (in a later wave) the observed-remove set keeps concurrent adds. It is an
/// immutable value; every operation returns a new set.
/// </para>
/// <para>
/// The causal <see cref="Merge(DottedVersionVectorSet{T})"/> retains a dotted entry when the other
/// side either still holds the same dot or has not yet observed it (its context has not advanced past
/// the dot). Entries the other side observed and dropped do not resurrect, because the merged context
/// still dominates their dots.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class DottedVersionVectorSet<T>: IEquatable<DottedVersionVectorSet<T>>
{
    private FrozenDictionary<Dot, T> Entries { get; }


    private DottedVersionVectorSet(VectorClock context, FrozenDictionary<Dot, T> entries)
    {
        Context = context;
        Entries = entries;
    }


    /// <summary>An empty set: an empty context and no entries.</summary>
    public static DottedVersionVectorSet<T> Empty { get; } = new(VectorClock.Empty, FrozenDictionary<Dot, T>.Empty);


    /// <summary>The causal context: every event this set has observed.</summary>
    public VectorClock Context { get; }


    /// <summary>The number of dotted entries currently retained.</summary>
    public int Count => Entries.Count;


    /// <summary>The distinct values currently retained.</summary>
    public IReadOnlyCollection<T> Values => Entries.Values.Distinct().ToArray();


    /// <summary>
    /// Returns a new set with <paramref name="value"/> added under a fresh dot for
    /// <paramref name="replica"/>. Existing entries are retained.
    /// </summary>
    /// <param name="replica">The replica introducing the value.</param>
    /// <param name="value">The value to add.</param>
    /// <returns>A new <see cref="DottedVersionVectorSet{T}"/>; this set is not modified.</returns>
    public DottedVersionVectorSet<T> Add(ReplicaId replica, T value)
    {
        VectorClock advanced = Context.Increment(replica);
        var dot = new Dot(replica, advanced[replica]);

        var dict = new Dictionary<Dot, T>(Entries.Count + 1);
        foreach(KeyValuePair<Dot, T> entry in Entries)
        {
            dict[entry.Key] = entry.Value;
        }

        dict[dot] = value;

        return new DottedVersionVectorSet<T>(advanced, dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new set with all currently observed values removed but the context retained, so the
    /// removed values do not resurrect on a later merge.
    /// </summary>
    /// <returns>A new <see cref="DottedVersionVectorSet{T}"/>, or this set if it is already empty of values.</returns>
    public DottedVersionVectorSet<T> ClearValues()
    {
        if(Entries.Count == 0)
        {
            return this;
        }

        return new DottedVersionVectorSet<T>(Context, FrozenDictionary<Dot, T>.Empty);
    }


    /// <summary>
    /// Returns a new set with every entry whose value equals <paramref name="value"/> removed, keeping
    /// the context so the removed entries do not resurrect on a later merge. This is the observed-remove
    /// operation: only entries the caller currently holds are removed; concurrent additions of the same
    /// value that this set has not observed are unaffected.
    /// </summary>
    /// <param name="value">The value whose entries to remove.</param>
    /// <returns>A new <see cref="DottedVersionVectorSet{T}"/>, or this set if no entry held the value.</returns>
    public DottedVersionVectorSet<T> RemoveValue(T value)
    {
        var dict = new Dictionary<Dot, T>(Entries.Count);
        bool removed = false;
        foreach(KeyValuePair<Dot, T> entry in Entries)
        {
            if(EqualityComparer<T>.Default.Equals(entry.Value, value))
            {
                removed = true;
            }
            else
            {
                dict[entry.Key] = entry.Value;
            }
        }

        if(!removed)
        {
            return this;
        }

        return new DottedVersionVectorSet<T>(Context, dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns the causal merge of this set and <paramref name="other"/>.
    /// </summary>
    /// <param name="other">The set to merge with.</param>
    /// <returns>A new <see cref="DottedVersionVectorSet{T}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public DottedVersionVectorSet<T> Merge(DottedVersionVectorSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        VectorClock mergedContext = Context.Merge(other.Context);
        var dict = new Dictionary<Dot, T>(Entries.Count + other.Entries.Count);

        foreach(KeyValuePair<Dot, T> entry in Entries)
        {
            if(other.Entries.ContainsKey(entry.Key) || other.Context[entry.Key.Replica] < entry.Key.Counter)
            {
                dict[entry.Key] = entry.Value;
            }
        }

        foreach(KeyValuePair<Dot, T> entry in other.Entries)
        {
            if(Entries.ContainsKey(entry.Key) || Context[entry.Key.Replica] < entry.Key.Counter)
            {
                dict[entry.Key] = entry.Value;
            }
        }

        return new DottedVersionVectorSet<T>(mergedContext, dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns the serializable state of this set, for persistence or transfer.
    /// </summary>
    /// <returns>The set's state.</returns>
    public DottedVersionVectorSetState<T> ToState()
    {
        ImmutableArray<DottedEntry<T>>.Builder builder = ImmutableArray.CreateBuilder<DottedEntry<T>>(Entries.Count);
        foreach(KeyValuePair<Dot, T> entry in Entries)
        {
            builder.Add(new DottedEntry<T>(ImmutableArray.Create(entry.Key.Replica.AsSpan()), entry.Key.Counter, entry.Value));
        }

        return new DottedVersionVectorSetState<T>(Context.ToState(), builder.ToImmutable());
    }


    /// <summary>
    /// Reconstructs a set from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed set.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if any dot violates the maintained invariant that the context dominates the set's own dots: a
    /// dot counter must be at least one (a dot is minted by advancing the context past zero) and must not
    /// exceed the context entry for its replica (every retained dot is an event the context has observed).
    /// No honest history produces a dot outside this range, and accepting one would let a later merge fail
    /// to dominate the dot and so misjudge whether the value was observed and dropped.
    /// </exception>
    public static DottedVersionVectorSet<T> FromState(DottedVersionVectorSetState<T> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        VectorClock context = VectorClock.FromState(state.Context);
        var dict = new Dictionary<Dot, T>(state.Entries.Length);
        foreach(DottedEntry<T> entry in state.Entries)
        {
            if(entry.Counter < 1)
            {
                throw new ArgumentException("A dot counter must be at least one.", nameof(state));
            }

            ReplicaId replica = ReplicaId.FromSpan(entry.Replica.AsSpan());
            if(entry.Counter > context[replica])
            {
                throw new ArgumentException("A dot counter cannot exceed its replica's context entry.", nameof(state));
            }

            dict[new Dot(replica, entry.Counter)] = entry.Value;
        }

        return new DottedVersionVectorSet<T>(context, dict.ToFrozenDictionary());
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] DottedVersionVectorSet<T>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(!Context.Equals(other.Context) || Entries.Count != other.Entries.Count)
        {
            return false;
        }

        foreach(KeyValuePair<Dot, T> entry in Entries)
        {
            if(!other.Entries.TryGetValue(entry.Key, out T? otherValue)
                || !EqualityComparer<T>.Default.Equals(entry.Value, otherValue))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DottedVersionVectorSet<T> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        //Order-independent over entries; combine with the context hash.
        int entriesHash = 0;
        foreach(KeyValuePair<Dot, T> entry in Entries)
        {
            entriesHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return HashCode.Combine(Context, entriesHash);
    }


    private string DebuggerDisplay => $"DVVSet: {Entries.Count} dot(s), context {Context}";
}
