using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The element resolutions a peer returns when it answers a fetch: a run of item-to-element entries, one per
/// requested item. The entries share one item length, the contract's item width, and no item repeats.
/// </summary>
/// <typeparam name="TElement">The application element type the items identify.</typeparam>
/// <remarks>
/// The entries already hold copied item bytes, so this record carries them as-is; equality is element-wise
/// over the entries, which compare their item bytes and their elements.
/// </remarks>
public sealed record ReconciliationElements<TElement>
{
    /// <summary>
    /// Initializes a resolution set from its entries, validating that the run is non-empty, has no null entry,
    /// shares one item length, and has no duplicate item.
    /// </summary>
    /// <param name="entries">The item-to-element resolutions, one per requested item.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="entries"/> is default or empty, when any entry is <see langword="null"/>,
    /// when two entries share an item, or when the entries do not all share the first entry's item length.
    /// </exception>
    public ReconciliationElements(ImmutableArray<ReconciliationElementEntry<TElement>> entries)
    {
        if(entries.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An elements message must carry at least one entry.", nameof(entries));
        }

        ReconciliationElementEntry<TElement> first = entries[0];
        if(first is null)
        {
            throw new ArgumentException("An elements message cannot carry a null entry.", nameof(entries));
        }

        int itemLength = first.Item.Length;
        for(int i = 0; i < entries.Length; i++)
        {
            ReconciliationElementEntry<TElement> entry = entries[i];
            if(entry is null)
            {
                throw new ArgumentException("An elements message cannot carry a null entry.", nameof(entries));
            }

            if(entry.Item.Length != itemLength)
            {
                throw new ArgumentException("Every entry in an elements message must share the first entry's item length.", nameof(entries));
            }

            for(int j = 0; j < i; j++)
            {
                if(entries[j].Item.Span.SequenceEqual(entry.Item.Span))
                {
                    throw new ArgumentException("An elements message cannot carry duplicate items.", nameof(entries));
                }
            }
        }

        Entries = entries;
    }


    /// <summary>The item-to-element resolutions, one per requested item.</summary>
    public ImmutableArray<ReconciliationElementEntry<TElement>> Entries { get; }


    /// <summary>Determines whether <paramref name="other"/> carries element-wise equal entries.</summary>
    /// <param name="other">The resolution set to compare with.</param>
    /// <returns><see langword="true"/> when the entries are element-wise equal.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ImmutableArray{T}"/>
    /// by reference identity; elements equality is element-wise entry equality.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationElements<TElement>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(Entries.Length != other.Entries.Length)
        {
            return false;
        }

        for(int i = 0; i < Entries.Length; i++)
        {
            if(!Entries[i].Equals(other.Entries[i]))
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
        foreach(ReconciliationElementEntry<TElement> entry in Entries)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }
}
