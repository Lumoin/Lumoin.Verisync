using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The dots whose entries the receiver must drop — the remove propagation a remove-aware session puts on the
/// wire. A drop carries only dots; the value is not needed to remove an entry the receiver already holds by
/// that dot. The receiver removes each present entry the dot names and lets its merged context dominate the
/// dot, so the removed entry never resurrects on a later reconcile.
/// </summary>
/// <remarks>
/// A validating-message record mirroring <see cref="ReconciliationFetch"/> and <see cref="ReconciliationElements{TElement}"/>.
/// The dots are held as-is — a <see cref="DotState"/> is immutable value data, so no copy is needed. Custom
/// <see cref="Equals(ReconciliationDrop)"/> and <see cref="GetHashCode"/> compare the dots order-independently,
/// as the set of (replica bytes, counter) pairs, because the synthesized record equality would compare the
/// <see cref="ImmutableArray{T}"/> by reference identity; the order-independent hash mirrors the style of
/// <see cref="DottedVersionVectorSet{T}.GetHashCode"/>.
/// </remarks>
public sealed record ReconciliationDrop
{
    /// <summary>
    /// Initializes a drop from its dots, validating that the run is non-empty, that every replica is the
    /// fixed identifier width, that every counter is at least one, and that no dot repeats.
    /// </summary>
    /// <param name="dots">The dots whose entries the receiver must drop.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="dots"/> is default or empty, when any dot's replica is not
    /// <see cref="ReplicaId.Size"/> bytes, when any counter is below one, or when two dots share a replica and
    /// a counter.
    /// </exception>
    public ReconciliationDrop(ImmutableArray<DotState> dots)
    {
        if(dots.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A drop must carry at least one dot.", nameof(dots));
        }

        for(int i = 0; i < dots.Length; i++)
        {
            DotState dot = dots[i];
            if(dot.Replica.Length != ReplicaId.Size)
            {
                throw new ArgumentException($"A drop dot's replica must be {ReplicaId.Size} bytes.", nameof(dots));
            }

            if(dot.Counter < 1)
            {
                throw new ArgumentException("A drop dot's counter must be at least one.", nameof(dots));
            }

            for(int j = 0; j < i; j++)
            {
                if(dots[j].Counter == dot.Counter && dots[j].Replica.AsSpan().SequenceEqual(dot.Replica.AsSpan()))
                {
                    throw new ArgumentException("A drop cannot carry duplicate dots.", nameof(dots));
                }
            }
        }

        Dots = dots;
    }


    /// <summary>The dots whose entries the receiver must drop.</summary>
    public ImmutableArray<DotState> Dots { get; }


    /// <summary>Determines whether <paramref name="other"/> carries the same dots as an order-independent set.</summary>
    /// <param name="other">The drop to compare with.</param>
    /// <returns><see langword="true"/> when the two carry the same set of (replica, counter) dots.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ImmutableArray{T}"/>
    /// by reference identity and each <see cref="DotState"/>'s replica bytes likewise; drop equality is
    /// order-independent set equality over the (replica bytes, counter) pairs. Both sides validate to a
    /// duplicate-free set, so equal lengths plus containment in one direction suffices.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationDrop? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(Dots.Length != other.Dots.Length)
        {
            return false;
        }

        foreach(DotState dot in Dots)
        {
            if(!ContainsDot(other.Dots, dot))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        //Order-independent over dots: each dot contributes the same combined hash regardless of position, so a
        //reorder of the same set hashes identically. The replica's bytes feed the hash, not its array identity.
        int dotsHash = 0;
        foreach(DotState dot in Dots)
        {
            var perDot = new HashCode();
            perDot.AddBytes(dot.Replica.AsSpan());
            perDot.Add(dot.Counter);
            dotsHash ^= perDot.ToHashCode();
        }

        return dotsHash;
    }


    private static bool ContainsDot(ImmutableArray<DotState> dots, DotState target)
    {
        foreach(DotState dot in dots)
        {
            if(dot.Counter == target.Counter && dot.Replica.AsSpan().SequenceEqual(target.Replica.AsSpan()))
            {
                return true;
            }
        }

        return false;
    }
}
