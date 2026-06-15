using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One resolved pairing of a fixed-width item to the element it identifies, the unit a peer returns when it
/// answers a fetch. The item is the contract-width identifier; the element is the application state it stands
/// for.
/// </summary>
/// <typeparam name="TElement">The application element type the item identifies.</typeparam>
/// <remarks>
/// The constructor copies the item, so a caller may reuse its buffer. The element is compared with
/// <see cref="EqualityComparer{T}.Default"/>, so entry equality reflects the application's own element equality.
/// </remarks>
public sealed record ReconciliationElementEntry<TElement>
{
    private byte[] ItemBytes { get; }


    /// <summary>
    /// Initializes an entry from a fixed-width item and the element it identifies, validating that the item is
    /// non-empty and the element is non-null, and copying the item so the caller may reuse its buffer.
    /// </summary>
    /// <param name="item">The fixed-width identifier of the contract's item width.</param>
    /// <param name="element">The application element the item identifies.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="item"/> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="element"/> is <see langword="null"/>.</exception>
    public ReconciliationElementEntry(ReadOnlyMemory<byte> item, TElement element)
    {
        if(item.IsEmpty)
        {
            throw new ArgumentException("An element entry's item cannot be empty.", nameof(item));
        }

        ArgumentNullException.ThrowIfNull(element);

        ItemBytes = item.ToArray();
        Element = element;
    }


    /// <summary>The fixed-width identifier of the contract's item width.</summary>
    public ReadOnlyMemory<byte> Item => ItemBytes;

    /// <summary>The application element the item identifies.</summary>
    public TElement Element { get; }


    /// <summary>Determines whether <paramref name="other"/> has the same item bytes and an equal element.</summary>
    /// <param name="other">The entry to compare with.</param>
    /// <returns><see langword="true"/> when the item bytes match and the elements are equal.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ReadOnlyMemory{T}"/>
    /// item by reference identity; entry equality is the item bytes plus default element equality.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationElementEntry<TElement>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return ItemBytes.AsSpan().SequenceEqual(other.ItemBytes) && EqualityComparer<TElement>.Default.Equals(Element, other.Element);
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(ItemBytes);
        hash.Add(Element);

        return hash.ToHashCode();
    }
}
