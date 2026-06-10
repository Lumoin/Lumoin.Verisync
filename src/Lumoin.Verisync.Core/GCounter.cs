using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A grow-only counter CRDT. Each replica owns its own sub-counter that only increases; the value is
/// the sum of all sub-counters, and merge takes the element-wise maximum.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="GCounter"/> is an immutable value. <see cref="Increment(ReplicaId)"/>,
/// <see cref="Increment(ReplicaId, int)"/>, and <see cref="Merge(GCounter)"/> return new counters and
/// never mutate the receiver.
/// </para>
/// <para>
/// The state-based merge forms a join-semilattice: it is commutative, associative, and idempotent, so
/// replicas that exchange and merge state in any order converge to the same value.
/// </para>
/// <para>
/// A <see cref="GCounter"/> is structurally similar to a <see cref="VectorClock"/> — both map a
/// <see cref="ReplicaId"/> to a non-negative integer and merge by element-wise maximum — but the two
/// are deliberately distinct semantic types: a clock captures causality, a counter captures a tally.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GCounter: IEquatable<GCounter>
{
    private FrozenDictionary<ReplicaId, int> Counts { get; }


    private GCounter(FrozenDictionary<ReplicaId, int> counts)
    {
        Counts = counts;
    }


    /// <summary>An empty counter whose value is zero.</summary>
    public static GCounter Empty { get; } = new(FrozenDictionary<ReplicaId, int>.Empty);


    /// <summary>Gets the counter value: the sum of every replica's sub-counter.</summary>
    /// <exception cref="OverflowException">
    /// Thrown when the sum of the sub-counters exceeds <see cref="int.MaxValue"/>. The total is computed
    /// with checked arithmetic so an overflowing tally is reported rather than wrapping to a corrupt value.
    /// </exception>
    public int Value => Counts.Values.Sum();


    /// <summary>
    /// Returns a new counter with <paramref name="replica"/>'s sub-counter increased by one.
    /// </summary>
    /// <param name="replica">The replica whose sub-counter to advance.</param>
    /// <returns>A new <see cref="GCounter"/>; this counter is not modified.</returns>
    public GCounter Increment(ReplicaId replica) => Increment(replica, 1);


    /// <summary>
    /// Returns a new counter with <paramref name="replica"/>'s sub-counter increased by
    /// <paramref name="amount"/>.
    /// </summary>
    /// <param name="replica">The replica whose sub-counter to advance.</param>
    /// <param name="amount">The positive amount to add. A grow-only counter never decreases.</param>
    /// <returns>A new <see cref="GCounter"/>; this counter is not modified.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="amount"/> is less than or equal to zero.</exception>
    /// <exception cref="OverflowException">
    /// Thrown when the increment would push <paramref name="replica"/>'s sub-counter past
    /// <see cref="int.MaxValue"/>. The addition is checked so an overflow throws rather than wrapping: a
    /// wrapped (smaller) sub-counter is permanently rejected by the element-wise max merge, which would
    /// silently lose the increment and break monotonicity. This counter is not modified when the throw occurs.
    /// </exception>
    public GCounter Increment(ReplicaId replica, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(amount, 0);

        int next = checked((Counts.TryGetValue(replica, out int existing) ? existing : 0) + amount);

        var dict = new Dictionary<ReplicaId, int>(Counts.Count + 1);
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            dict[entry.Key] = entry.Value;
        }

        dict[replica] = next;

        return new GCounter(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns the element-wise maximum of this counter and <paramref name="other"/>.
    /// </summary>
    /// <param name="other">The counter to merge with.</param>
    /// <returns>A new <see cref="GCounter"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public GCounter Merge(GCounter other)
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

        return new GCounter(dict.ToFrozenDictionary());
    }


    /// <summary>
    /// Returns the serializable state of this counter, for persistence or transfer.
    /// </summary>
    /// <returns>The counter's state.</returns>
    public GCounterState ToState()
    {
        ImmutableArray<ReplicaCounterEntry>.Builder builder = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(Counts.Count);
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            builder.Add(new ReplicaCounterEntry(ImmutableArray.Create(entry.Key.AsSpan()), entry.Value));
        }

        return new GCounterState(builder.ToImmutable());
    }


    /// <summary>
    /// Reconstructs a counter from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed counter.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if any entry has a negative count: a grow-only sub-counter is never negative, so no honest
    /// history produces one and accepting it would corrupt the value and the max merge.
    /// </exception>
    /// <remarks>
    /// Zero-count entries are filtered out: an absent replica already means zero, so a stored zero carries
    /// no information. Keeping it would break the <see cref="Equals(GCounter)"/>/<see cref="GetHashCode"/>
    /// contract, because <see cref="Equals(GCounter)"/> compares over the union of replicas treating absent
    /// ones as zero while <see cref="GetHashCode"/> hashes only the stored entries — a stored zero would
    /// equal a counter without it yet hash differently.
    /// </remarks>
    public static GCounter FromState(GCounterState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var dict = new Dictionary<ReplicaId, int>(state.Entries.Length);
        foreach(ReplicaCounterEntry entry in state.Entries)
        {
            if(entry.Count < 0)
            {
                throw new ArgumentException("A grow-only counter entry cannot be negative.", nameof(state));
            }

            if(entry.Count == 0)
            {
                continue;
            }

            dict[ReplicaId.FromSpan(entry.Replica.AsSpan())] = entry.Count;
        }

        return new GCounter(dict.ToFrozenDictionary());
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] GCounter? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        var keys = new HashSet<ReplicaId>(Counts.Keys);
        keys.UnionWith(other.Counts.Keys);
        foreach(ReplicaId replica in keys)
        {
            int mine = Counts.TryGetValue(replica, out int a) ? a : 0;
            int theirs = other.Counts.TryGetValue(replica, out int b) ? b : 0;
            if(mine != theirs)
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GCounter other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        //Order-independent: a counter's entries have no inherent order, so combine per entry and XOR.
        int hash = 0;
        foreach(KeyValuePair<ReplicaId, int> entry in Counts)
        {
            hash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return hash;
    }


    private string DebuggerDisplay => Counts.Count == 0
        ? "GCounter: 0 []"
        : $"GCounter: {Value} [{string.Join(", ", Counts.Select(entry => $"{entry.Key}={entry.Value}"))}]";
}
