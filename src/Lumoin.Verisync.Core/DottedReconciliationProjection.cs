using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One pinned snapshot's present dotted entries projected once, at construction, into the items the
/// reconciliation contract encodes, alongside the reverse lookup a fetch, serve, or apply seam needs and the
/// causal context a later phase exchanges. The projection carries the cross-replica injectivity obligation:
/// two replicas project a shared dotted entry to an identical item, and distinct entries to distinct items, so
/// the recovered symmetric difference of two sides' item sets is exactly the entries whose presence differs.
/// </summary>
/// <typeparam name="T">The value type a dot tags.</typeparam>
/// <remarks>
/// <para>
/// The pinned canonical frame of a present entry, fed to the digest, is the replica bytes
/// (<see cref="ReplicaId.Size"/>), then the counter as an unsigned 64-bit little-endian word, then the canonical
/// value bytes. The frame commits to both the dot and the value: two honest replicas holding the same dot hold
/// the same value, so they agree; a dot or value disagreement yields different digests and surfaces as a
/// difference rather than masquerading. Only <see cref="ReconciliationItemDomain.ContentHash"/> is supported,
/// because the frame is variable-length before the digest.
/// </para>
/// <para>
/// Immutable after construction. It is not <see cref="IDisposable"/>: the framing scratch it rents is returned
/// before the constructor returns; only the produced items escape, as owned arrays the caller may keep.
/// </para>
/// </remarks>
public sealed class DottedReconciliationProjection<T>
{
    //The fixed frame header: the replica bytes followed by the counter as an unsigned 64-bit little-endian word.
    private const int FrameHeaderLength = ReplicaId.Size + sizeof(ulong);

    //The byte offset at which the canonical value bytes begin in the frame, past the replica and the counter.
    private const int FrameValueOffset = FrameHeaderLength;

    private Dictionary<ContentAddress, DottedEntry<T>> EntriesByItem { get; }


    /// <summary>
    /// Projects the present dotted entries of <paramref name="state"/> into the contract's items, building the
    /// reverse lookup and capturing the causal context, validating every entry's dot and digest width.
    /// </summary>
    /// <param name="state">The pinned dotted-version-vector-set state to project.</param>
    /// <param name="contract">The contract whose item width and domain the produced items must satisfy.</param>
    /// <param name="computeDigest">The digest committing a frame's bytes to its item.</param>
    /// <param name="canonicalizeValue">The pure canonicalization of a value into the bytes its frame commits to.</param>
    /// <param name="pool">
    /// The pool the framing scratch is rented from, tracking the memory; <see langword="null"/> rents a private
    /// heap-backed fallback instead, so the projection needs no pool to function.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="state"/>, <paramref name="contract"/>, <paramref name="computeDigest"/>, or
    /// <paramref name="canonicalizeValue"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="contract"/>'s domain is not <see cref="ReconciliationItemDomain.ContentHash"/>;
    /// when an entry's replica is not exactly <see cref="ReplicaId.Size"/> bytes; when an entry's counter is below
    /// one; when a digest's width differs from <paramref name="contract"/>'s item width; or when two entries
    /// produce the same item, which would XOR-cancel two distinct entries silently.
    /// </exception>
    public DottedReconciliationProjection(
        DottedVersionVectorSetState<T> state,
        ReconciliationContract contract,
        ComputeDigestDelegate computeDigest,
        CanonicalizeReconciliationValueDelegate<T> canonicalizeValue,
        MemoryPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(computeDigest);
        ArgumentNullException.ThrowIfNull(canonicalizeValue);

        if(contract.ItemDomain != ReconciliationItemDomain.ContentHash)
        {
            throw new ArgumentException("The dotted projection digests a variable-length frame, so only the content-hash domain is supported; the structural fixed-width dotted item is out of scope.", nameof(contract));
        }

        Contract = contract;
        Context = state.Context;

        var items = new List<ReadOnlyMemory<byte>>(state.Entries.Length);
        EntriesByItem = new Dictionary<ContentAddress, DottedEntry<T>>(state.Entries.Length);

        //The framing scratch is a single rental grown by re-rent when an entry's frame exceeds it, and returned
        //in the finally so the rental ledger balances on every path including a throw — the projection takes no
        //rental that outlives the constructor and so is not IDisposable.
        IMemoryOwner<byte> scratchOwner = Rent(FrameHeaderLength, pool);
        try
        {
            foreach(DottedEntry<T> entry in state.Entries)
            {
                if(entry.Replica.Length != ReplicaId.Size)
                {
                    throw new ArgumentException($"A dotted entry replica must be exactly {ReplicaId.Size} bytes; got {entry.Replica.Length}.", nameof(state));
                }

                if(entry.Counter < 1)
                {
                    throw new ArgumentException($"A dotted entry counter must be at least one; got {entry.Counter}.", nameof(state));
                }

                ReadOnlyMemory<byte> value = canonicalizeValue(entry.Value);
                int frameLength = FrameValueOffset + value.Length;
                if(scratchOwner.Memory.Length < frameLength)
                {
                    //Grow by re-rent: return the too-small scratch first, then rent the larger one, so a peak
                    //frame never holds two scratches at once and the ledger never carries an orphaned rental.
                    scratchOwner.Dispose();
                    scratchOwner = Rent(frameLength, pool);
                }

                Memory<byte> frame = scratchOwner.Memory[..frameLength];
                Span<byte> frameSpan = frame.Span;
                entry.Replica.AsSpan().CopyTo(frameSpan);
                BinaryPrimitives.WriteUInt64LittleEndian(frameSpan.Slice(ReplicaId.Size, sizeof(ulong)), (ulong)entry.Counter);
                value.Span.CopyTo(frameSpan[FrameValueOffset..]);

                ReadOnlyMemory<byte> digest = computeDigest(frame);
                if(digest.Length != contract.ItemWidth)
                {
                    throw new ArgumentException($"A dotted item digest must be exactly the contract's {contract.ItemWidth}-byte item width; got {digest.Length}.", nameof(computeDigest));
                }

                //The item is an owned copy, never a view over the scratch (overwritten by the next frame) or over
                //the digest delegate's returned memory (the caller may reuse its backing); .ToArray builds the
                //ReadOnlyMemory because a collection expression cannot construct one.
                ReadOnlyMemory<byte> item = digest.ToArray();
                var key = new ContentAddress(item);
                if(!EntriesByItem.TryAdd(key, entry))
                {
                    throw new ArgumentException("Two dotted entries produce the same item, which violates injectivity and would XOR-cancel two distinct entries silently.", nameof(state));
                }

                items.Add(item);
            }
        }
        finally
        {
            scratchOwner.Dispose();
        }

        Items = items;
    }


    /// <summary>The contract whose item width and domain the produced items satisfy.</summary>
    public ReconciliationContract Contract { get; }

    /// <summary>The causal context of the projected state, the same instance, shipped whole by a later phase.</summary>
    public VectorClockState Context { get; }

    /// <summary>The projected items, one per present entry, in <c>state.Entries</c> order, each the contract's item width.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Items { get; }

    /// <summary>The number of projected items, equal to the number of present entries in the state.</summary>
    public int Count => Items.Count;


    /// <summary>
    /// Resolves an item back to the dotted entry that produced it, by byte-sequence content, for a fetch, serve,
    /// or apply seam.
    /// </summary>
    /// <param name="item">The item bytes to resolve; a default or empty item resolves to nothing.</param>
    /// <param name="entry">
    /// When this method returns <see langword="true"/>, the originating entry carrying the replica bytes, counter,
    /// and value; otherwise <see langword="null"/>.
    /// </param>
    /// <returns><see langword="true"/> when an entry produced <paramref name="item"/>; otherwise <see langword="false"/>.</returns>
    public bool TryResolve(ReadOnlyMemory<byte> item, [NotNullWhen(true)] out DottedEntry<T>? entry)
    {
        if(item.IsEmpty)
        {
            entry = null;

            return false;
        }

        return EntriesByItem.TryGetValue(new ContentAddress(item), out entry);
    }


    private static IMemoryOwner<byte> Rent(int byteLength, MemoryPool<byte>? pool)
    {
        //A general pool may return an owner larger than requested; the framing slices only ever within the frame
        //length, so an over-sized owner is harmless. The heap fallback keeps the projection standalone-usable.
        return (pool?.Rent(byteLength)) ?? new ReconciliationHeapMemoryOwner(byteLength);
    }
}
