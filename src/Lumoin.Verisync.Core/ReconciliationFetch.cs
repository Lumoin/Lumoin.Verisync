using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The items this side decoded but does not hold locally, requested from the peer. Each item is a fixed-width
/// identifier of the contract's item width; the peer answers a fetch with the matching elements.
/// </summary>
/// <remarks>
/// Decoded items are distinct by construction, so a duplicate item is a contract violation the constructor
/// fails closed on. The constructor copies every item, so a caller may reuse its buffers.
/// </remarks>
public sealed record ReconciliationFetch
{
    private ImmutableArray<ReadOnlyMemory<byte>> ItemBytes { get; }


    /// <summary>
    /// Initializes a fetch from the requested items, validating that the run is non-empty, has no empty item,
    /// shares one item length, and has no duplicate, and copying every item so the caller may reuse its buffers.
    /// </summary>
    /// <param name="items">The fixed-width items this side decoded but does not hold locally.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="items"/> is default or empty, when any item is empty, when the items do not
    /// all share the first item's length, or when two items are byte-equal.
    /// </exception>
    public ReconciliationFetch(ImmutableArray<ReadOnlyMemory<byte>> items)
    {
        if(items.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A fetch must carry at least one item.", nameof(items));
        }

        int itemLength = items[0].Length;
        if(itemLength == 0)
        {
            throw new ArgumentException("A fetch item cannot be empty.", nameof(items));
        }

        ImmutableArray<ReadOnlyMemory<byte>>.Builder copies = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>(items.Length);
        for(int i = 0; i < items.Length; i++)
        {
            ReadOnlyMemory<byte> item = items[i];
            if(item.Length != itemLength)
            {
                throw new ArgumentException("Every item in a fetch must share the first item's length.", nameof(items));
            }

            for(int j = 0; j < i; j++)
            {
                if(items[j].Span.SequenceEqual(item.Span))
                {
                    throw new ArgumentException("A fetch cannot carry duplicate items.", nameof(items));
                }
            }

            copies.Add(item.ToArray());
        }

        ItemBytes = copies.MoveToImmutable();
    }


    /// <summary>The fixed-width items this side decoded but does not hold locally, requested from the peer.</summary>
    public ImmutableArray<ReadOnlyMemory<byte>> Items => ItemBytes;


    /// <summary>Determines whether <paramref name="other"/> carries the same items by element-wise byte equality.</summary>
    /// <param name="other">The fetch to compare with.</param>
    /// <returns><see langword="true"/> when the items are element-wise byte-equal.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ReadOnlyMemory{T}"/>
    /// items by reference identity; fetch equality is element-wise byte-sequence equality.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationFetch? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(ItemBytes.Length != other.ItemBytes.Length)
        {
            return false;
        }

        for(int i = 0; i < ItemBytes.Length; i++)
        {
            if(!ItemBytes[i].Span.SequenceEqual(other.ItemBytes[i].Span))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach(ReadOnlyMemory<byte> item in ItemBytes)
        {
            hash.AddBytes(item.Span);
        }

        return hash.ToHashCode();
    }
}
