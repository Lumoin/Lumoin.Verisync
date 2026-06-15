using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The initiator's partition of a decoded difference: the items it must fetch from the peer because it lacks
/// them locally, the entries it must push to the peer because it holds them and the peer never observed them,
/// and the dots it must drop locally because it holds them and the peer's context proves it observed and
/// removed them. An empty resolution — all three lists empty — is the quiescent outcome of reconciling two
/// equal snapshots.
/// </summary>
/// <typeparam name="TElement">The application element type carried by a pushed entry.</typeparam>
/// <remarks>
/// Unlike the wire records an empty resolution is legal, because quiescence is a normal outcome the host must
/// be able to express, and so an empty local-drop list is legal even though an empty <see cref="ReconciliationDrop"/>
/// is not. The fetch items are copied at construction so a caller may reuse its buffers; equality is element-wise
/// over the fetch items and push entries and order-independent over the local drops, because a drop set has no
/// inherent order. The local drops are produced at decode time; the push-drops the peer must honour are produced
/// later, from the fetch answer, and so are not a field here.
/// </remarks>
public sealed record ReconciliationDifferenceResolution<TElement>
{
    private ImmutableArray<ReadOnlyMemory<byte>> FetchItems { get; }


    /// <summary>
    /// Initializes a resolution from the items to fetch and the entries to push, with no local drops, validating
    /// that neither array is default, that no fetch item is empty, that neither list repeats an item, that each
    /// list shares one item width, and that no push entry is <see langword="null"/>, and copying every fetch item.
    /// </summary>
    /// <param name="fetch">The fixed-width items the initiator lacks and must request from the peer.</param>
    /// <param name="push">The element entries the initiator holds and must send to the peer.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when either array is default, when a fetch item is empty, when either list carries a duplicate
    /// item, when a list mixes item widths, or when a push entry is <see langword="null"/>.
    /// </exception>
    public ReconciliationDifferenceResolution(ImmutableArray<ReadOnlyMemory<byte>> fetch, ImmutableArray<ReconciliationElementEntry<TElement>> push)
        : this(fetch, push, ImmutableArray<DotState>.Empty)
    {
    }


    /// <summary>
    /// Initializes a resolution from the items to fetch, the entries to push, and the dots to drop locally,
    /// validating that neither the fetch nor the push array is default, that no fetch item is empty, that neither
    /// list repeats an item, that each list shares one item width, that no push entry is <see langword="null"/>,
    /// and that the local drops are not default and every dot is a well-formed, non-repeating dot — though an
    /// empty local-drop list is accepted, as quiescence — and copying every fetch item.
    /// </summary>
    /// <param name="fetch">The fixed-width items the initiator lacks and must request from the peer.</param>
    /// <param name="push">The element entries the initiator holds and must send to the peer.</param>
    /// <param name="localDrops">The dots the initiator holds that the peer observed and removed, to drop locally.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when the fetch or push array is default, when a fetch item is empty, when either list carries a
    /// duplicate item, when a list mixes item widths, when a push entry is <see langword="null"/>, when
    /// <paramref name="localDrops"/> is default, when a drop dot's replica is not <see cref="ReplicaId.Size"/>
    /// bytes, when a drop dot's counter is below one, or when two drop dots share a replica and a counter.
    /// </exception>
    public ReconciliationDifferenceResolution(ImmutableArray<ReadOnlyMemory<byte>> fetch, ImmutableArray<ReconciliationElementEntry<TElement>> push, ImmutableArray<DotState> localDrops)
    {
        if(fetch.IsDefault)
        {
            throw new ArgumentException("A resolution's fetch list cannot be default.", nameof(fetch));
        }

        if(push.IsDefault)
        {
            throw new ArgumentException("A resolution's push list cannot be default.", nameof(push));
        }

        if(localDrops.IsDefault)
        {
            throw new ArgumentException("A resolution's local-drop list cannot be default.", nameof(localDrops));
        }

        FetchItems = CopyFetch(fetch);
        ValidatePush(push);
        ValidateLocalDrops(localDrops);
        Push = push;
        LocalDrops = localDrops;
    }


    /// <summary>The fixed-width items the initiator lacks and must request from the peer; empty when none.</summary>
    public ImmutableArray<ReadOnlyMemory<byte>> Fetch => FetchItems;

    /// <summary>The element entries the initiator holds and must send to the peer; empty when none.</summary>
    public ImmutableArray<ReconciliationElementEntry<TElement>> Push { get; }

    /// <summary>The dots the initiator holds that the peer observed and removed, to drop locally; empty when none.</summary>
    public ImmutableArray<DotState> LocalDrops { get; }


    /// <summary>The quiescent resolution with all three lists empty, shared because it carries no state.</summary>
    public static ReconciliationDifferenceResolution<TElement> Empty { get; } = new(ImmutableArray<ReadOnlyMemory<byte>>.Empty, ImmutableArray<ReconciliationElementEntry<TElement>>.Empty, ImmutableArray<DotState>.Empty);


    /// <summary>Determines whether <paramref name="other"/> carries the same fetch items, push entries, and local drops.</summary>
    /// <param name="other">The resolution to compare with.</param>
    /// <returns><see langword="true"/> when the fetch items are byte-equal and the push entries equal element-wise and the local drops match as a set.</returns>
    /// <remarks>
    /// The synthesized record equality is replaced because it would compare the <see cref="ImmutableArray{T}"/>
    /// members by reference identity; resolution equality is byte-sequence equality over the fetch items,
    /// element-wise entry equality over the push entries, and order-independent set equality over the local drops.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] ReconciliationDifferenceResolution<TElement>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(FetchItems.Length != other.FetchItems.Length || Push.Length != other.Push.Length || LocalDrops.Length != other.LocalDrops.Length)
        {
            return false;
        }

        for(int i = 0; i < FetchItems.Length; i++)
        {
            if(!FetchItems[i].Span.SequenceEqual(other.FetchItems[i].Span))
            {
                return false;
            }
        }

        for(int i = 0; i < Push.Length; i++)
        {
            if(!Push[i].Equals(other.Push[i]))
            {
                return false;
            }
        }

        foreach(DotState dot in LocalDrops)
        {
            if(!ContainsDot(other.LocalDrops, dot))
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
        foreach(ReadOnlyMemory<byte> item in FetchItems)
        {
            hash.AddBytes(item.Span);
        }

        foreach(ReconciliationElementEntry<TElement> entry in Push)
        {
            hash.Add(entry);
        }

        //Order-independent over the local drops: each dot contributes the same combined hash regardless of
        //position, so a reorder of the same set hashes identically. The replica's bytes feed the hash, not its
        //array identity.
        int dropsHash = 0;
        foreach(DotState dot in LocalDrops)
        {
            var perDot = new HashCode();
            perDot.AddBytes(dot.Replica.AsSpan());
            perDot.Add(dot.Counter);
            dropsHash ^= perDot.ToHashCode();
        }

        hash.Add(dropsHash);

        return hash.ToHashCode();
    }


    private static ImmutableArray<ReadOnlyMemory<byte>> CopyFetch(ImmutableArray<ReadOnlyMemory<byte>> fetch)
    {
        if(fetch.IsEmpty)
        {
            return ImmutableArray<ReadOnlyMemory<byte>>.Empty;
        }

        int itemLength = fetch[0].Length;
        if(itemLength == 0)
        {
            throw new ArgumentException("A fetch item cannot be empty.", nameof(fetch));
        }

        ImmutableArray<ReadOnlyMemory<byte>>.Builder copies = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>(fetch.Length);
        for(int i = 0; i < fetch.Length; i++)
        {
            ReadOnlyMemory<byte> item = fetch[i];
            if(item.Length != itemLength)
            {
                throw new ArgumentException("Every fetch item must share the first item's width.", nameof(fetch));
            }

            for(int j = 0; j < i; j++)
            {
                if(fetch[j].Span.SequenceEqual(item.Span))
                {
                    throw new ArgumentException("A resolution cannot carry duplicate fetch items.", nameof(fetch));
                }
            }

            copies.Add(item.ToArray());
        }

        return copies.MoveToImmutable();
    }


    private static void ValidatePush(ImmutableArray<ReconciliationElementEntry<TElement>> push)
    {
        if(push.IsEmpty)
        {
            return;
        }

        ReconciliationElementEntry<TElement> first = push[0];
        if(first is null)
        {
            throw new ArgumentException("A resolution cannot carry a null push entry.", nameof(push));
        }

        int itemLength = first.Item.Length;
        for(int i = 0; i < push.Length; i++)
        {
            ReconciliationElementEntry<TElement> entry = push[i];
            if(entry is null)
            {
                throw new ArgumentException("A resolution cannot carry a null push entry.", nameof(push));
            }

            if(entry.Item.Length != itemLength)
            {
                throw new ArgumentException("Every push entry must share the first entry's item width.", nameof(push));
            }

            for(int j = 0; j < i; j++)
            {
                if(push[j].Item.Span.SequenceEqual(entry.Item.Span))
                {
                    throw new ArgumentException("A resolution cannot carry duplicate push items.", nameof(push));
                }
            }
        }
    }


    private static void ValidateLocalDrops(ImmutableArray<DotState> localDrops)
    {
        if(localDrops.IsEmpty)
        {
            return;
        }

        for(int i = 0; i < localDrops.Length; i++)
        {
            DotState dot = localDrops[i];
            if(dot.Replica.Length != ReplicaId.Size)
            {
                throw new ArgumentException($"A local-drop dot's replica must be {ReplicaId.Size} bytes.", nameof(localDrops));
            }

            if(dot.Counter < 1)
            {
                throw new ArgumentException("A local-drop dot's counter must be at least one.", nameof(localDrops));
            }

            for(int j = 0; j < i; j++)
            {
                if(localDrops[j].Counter == dot.Counter && localDrops[j].Replica.AsSpan().SequenceEqual(dot.Replica.AsSpan()))
                {
                    throw new ArgumentException("A resolution cannot carry duplicate local-drop dots.", nameof(localDrops));
                }
            }
        }
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
