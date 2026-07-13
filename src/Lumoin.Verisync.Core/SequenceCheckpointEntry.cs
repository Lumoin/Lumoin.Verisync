using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One element of a dotted checkpoint: the removed-or-visible element's serialized identity paired with its
/// value. Dots make a checkpoint unambiguous under repeated values and strengthen the commitment, since the
/// digest then covers WHICH elements the projection captured, not merely their values in order.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Dot">The serialized identity of the projected element.</param>
/// <param name="Value">The element's value.</param>
/// <remarks>
/// Unlike the other serializable state records, this one carries CUSTOM VALUE EQUALITY: the container's
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}.Equals(CheckpointedSequence{TSequence, TValue, TAnchor})"/>
/// and the checkpoint law suite compare checkpoints for logical equality, and the synthesized record equality
/// would compare the <see cref="DotState.Replica"/> <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// by reference identity rather than by content — the same landmine <see cref="ReconciliationDrop"/> handles.
/// <see cref="Equals(SequenceCheckpointEntry{TValue})"/> compares the dot's counter, the replica bytes by
/// content, and the value through <see cref="EqualityComparer{T}.Default"/>; <see cref="GetHashCode"/> folds the
/// replica bytes with the counter and the value the same way.
/// </remarks>
public sealed record SequenceCheckpointEntry<TValue>(DotState Dot, TValue Value)
{
    /// <summary>Determines whether <paramref name="other"/> carries the same dot and value.</summary>
    /// <param name="other">The entry to compare with.</param>
    /// <returns><see langword="true"/> when the dots' counters and replica bytes match and the values are equal.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the dot's replica bytes by array
    /// identity; checkpoint equality is value equality over the (replica bytes, counter, value) content.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] SequenceCheckpointEntry<TValue>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Dot.Counter == other.Dot.Counter
            && Dot.Replica.AsSpan().SequenceEqual(other.Dot.Replica.AsSpan())
            && EqualityComparer<TValue>.Default.Equals(Value, other.Value);
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        //The replica's bytes feed the hash, not its array identity, so an entry hashes by content the same way
        //it compares.
        var hash = new HashCode();
        hash.AddBytes(Dot.Replica.AsSpan());
        hash.Add(Dot.Counter);
        hash.Add(Value);

        return hash.ToHashCode();
    }
}
