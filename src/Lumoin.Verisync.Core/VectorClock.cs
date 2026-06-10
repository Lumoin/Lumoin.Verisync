using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Maps each <see cref="ReplicaId"/> to a monotonically increasing event counter, capturing the
/// happens-before partial order between replicas.
/// </summary>
/// <remarks>
/// <para>
/// A vector clock is an immutable value. <see cref="Increment(ReplicaId)"/> and
/// <see cref="Merge(VectorClock)"/> return new clocks and never mutate the receiver. An absent
/// replica is treated as having a counter of zero.
/// </para>
/// <para>
/// Keys are identified by <see cref="ReplicaId"/> byte-content equality, so distinct
/// <see cref="ReplicaId"/> instances carrying the same bytes address the same entry.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class VectorClock: IEquatable<VectorClock>
{
    private FrozenDictionary<ReplicaId, int> Counts { get; }


    private VectorClock(FrozenDictionary<ReplicaId, int> counts)
    {
        Counts = counts;
    }


    /// <summary>An empty clock in which every replica has a counter of zero.</summary>
    public static VectorClock Empty { get; } = new(FrozenDictionary<ReplicaId, int>.Empty);


    /// <summary>
    /// Gets the counter for <paramref name="replica"/>, or zero if the replica is absent.
    /// </summary>
    /// <param name="replica">The replica whose counter to read.</param>
    /// <returns>The counter value, or zero when the replica has no entry.</returns>
    [SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "A vector clock is keyed by replica identity; the ReplicaId-keyed lookup is the entire point.")]
    public int this[ReplicaId replica]
    {
        get
        {
            return Counts.TryGetValue(replica, out int value) ? value : 0;
        }
    }


    /// <summary>
    /// Returns a new clock with <paramref name="replica"/>'s counter increased by one.
    /// </summary>
    /// <param name="replica">The replica whose counter to advance.</param>
    /// <returns>A new <see cref="VectorClock"/>; this clock is not modified.</returns>
    /// <exception cref="OverflowException">
    /// Thrown when the increment would push <paramref name="replica"/>'s counter past
    /// <see cref="int.MaxValue"/>. The addition is checked so an overflow throws rather than wrapping: these
    /// dot counters feed <see cref="Rga{TValue}"/> and the dotted-version-vector types, and a wrapped
    /// (smaller) counter would corrupt causality and the element-wise max merge. This clock is not modified
    /// when the throw occurs.
    /// </exception>
    public VectorClock Increment(ReplicaId replica)
    {
        int next = checked((Counts.TryGetValue(replica, out int existing) ? existing : 0) + 1);

        var dict = new Dictionary<ReplicaId, int>(Counts.Count + 1);
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            dict[entry.Key] = entry.Value;
        }

        dict[replica] = next;

        return new VectorClock(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns a new clock with <paramref name="replica"/>'s counter advanced to one more than the
    /// largest counter in the clock, Lamport-style, so the new event's counter exceeds every counter
    /// this clock has observed.
    /// </summary>
    /// <param name="replica">The replica whose counter to advance.</param>
    /// <returns>A new <see cref="VectorClock"/>; this clock is not modified.</returns>
    /// <exception cref="OverflowException">
    /// Thrown when one more than the largest counter exceeds <see cref="int.MaxValue"/>. The addition is
    /// checked so an overflow throws rather than wrapping: these dot counters feed <see cref="Rga{TValue}"/>
    /// and the dotted-version-vector types, and a wrapped (smaller) counter would corrupt causality and the
    /// element-wise max merge. This clock is not modified when the throw occurs.
    /// </exception>
    public VectorClock IncrementPastAll(ReplicaId replica)
    {
        var dict = new Dictionary<ReplicaId, int>(Counts.Count + 1);
        int largest = 0;
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            dict[entry.Key] = entry.Value;
            if(entry.Value > largest)
            {
                largest = entry.Value;
            }
        }

        dict[replica] = checked(largest + 1);

        return new VectorClock(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns the element-wise maximum of this clock and <paramref name="other"/>.
    /// </summary>
    /// <param name="other">The clock to merge with.</param>
    /// <returns>A new <see cref="VectorClock"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public VectorClock Merge(VectorClock other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var dict = new Dictionary<ReplicaId, int>(Counts.Count + other.Counts.Count);
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            dict[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<ReplicaId, int> entry in other.Counts)
        {
            dict[entry.Key] = dict.TryGetValue(entry.Key, out int existing) && existing > entry.Value
                ? existing
                : entry.Value;
        }

        return new VectorClock(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Compares this clock with <paramref name="other"/> in the happens-before partial order.
    /// </summary>
    /// <param name="other">The clock to compare with.</param>
    /// <returns>
    /// <see cref="Causality.Before"/>, <see cref="Causality.After"/>, <see cref="Causality.Equal"/>,
    /// or <see cref="Causality.Concurrent"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public Causality Compare(VectorClock other)
    {
        ArgumentNullException.ThrowIfNull(other);

        bool greater = false;
        bool less = false;
        foreach(ReplicaId replica in UnionKeys(this, other))
        {
            int mine = this[replica];
            int theirs = other[replica];
            if(mine > theirs)
            {
                greater = true;
            }
            else if(mine < theirs)
            {
                less = true;
            }

            if(greater && less)
            {
                return Causality.Concurrent;
            }
        }

        return (greater, less) switch
        {
            (true, false) => Causality.After,
            (false, true) => Causality.Before,
            _ => Causality.Equal
        };
    }


    /// <summary>
    /// Returns the serializable state of this clock, for persistence or transfer.
    /// </summary>
    /// <returns>The clock's state.</returns>
    public VectorClockState ToState()
    {
        ImmutableArray<ReplicaCounterEntry>.Builder builder = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(Counts.Count);
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            builder.Add(new ReplicaCounterEntry(ImmutableArray.Create(entry.Key.AsSpan()), entry.Value));
        }

        return new VectorClockState(builder.ToImmutable());
    }


    /// <summary>
    /// Reconstructs a clock from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed clock.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if any entry has a negative count: a clock counter only ever increases from zero, so no honest
    /// history produces a negative counter and accepting it would corrupt causality and the max merge.
    /// </exception>
    /// <remarks>
    /// Zero-count entries are filtered out: an absent replica already has a counter of zero, so a stored
    /// zero carries no information. Keeping it would break the <see cref="Equals(VectorClock)"/>/
    /// <see cref="GetHashCode"/> contract, because <see cref="Equals(VectorClock)"/> compares over the union
    /// of replicas treating absent ones as zero while <see cref="GetHashCode"/> hashes only the stored
    /// entries — a stored zero would equal a clock without it yet hash differently.
    /// </remarks>
    public static VectorClock FromState(VectorClockState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dict = new Dictionary<ReplicaId, int>(state.Entries.Length);
        foreach(ReplicaCounterEntry entry in state.Entries)
        {
            if(entry.Count < 0)
            {
                throw new ArgumentException("A vector clock entry cannot be negative.", nameof(state));
            }

            if(entry.Count == 0)
            {
                continue;
            }

            dict[ReplicaId.FromSpan(entry.Replica.AsSpan())] = entry.Count;
        }

        return new VectorClock(dict.ToFrozenDictionary());
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] VectorClock? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        foreach(ReplicaId replica in UnionKeys(this, other))
        {
            if(this[replica] != other[replica])
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VectorClock other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        //Order-independent: a clock's entries have no inherent order, so combine per entry and XOR.
        int hash = 0;
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            hash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return hash;
    }


    private static HashSet<ReplicaId> UnionKeys(VectorClock left, VectorClock right)
    {
        var keys = new HashSet<ReplicaId>(left.Counts.Keys);
        keys.UnionWith(right.Counts.Keys);

        return keys;
    }


    private string DebuggerDisplay => Counts.Count == 0
        ? "VectorClock: []"
        : $"VectorClock: [{string.Join(", ", Counts.Select(entry => $"{entry.Key}={entry.Value}"))}]";
}
