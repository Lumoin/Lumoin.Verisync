using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A dense, append-only, fixed-stride store of item bytes that hands out stable, never-moved
/// <see cref="ReadOnlyMemory{T}"/> slices. It backs the reconciliation kernel's per-add item copies in the
/// encoder and its decoded items in the decoder, holding the bytes that the kernel's
/// <c>ContentAddress</c> membership keys and walk cursors view without a copy of their own.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ReconciliationCellBuffer"/>, which grows by copying its live bytes into a larger backing,
/// this arena grows by renting an additional block and never relocates a stored item. A relocation would
/// corrupt every membership key and cursor viewing the moved bytes -- a silent wrong result -- so the arena is
/// deliberately the opposite pattern: it only ever appends, leaving every prior block exactly where it is. A
/// slice it hands out stays byte-valid for the arena's whole life.
/// </para>
/// <para>
/// Each block is an <see cref="IMemoryOwner{T}"/> rental from the required <see cref="MemoryPool{T}"/>, so the
/// memory is pooled, tracked, and cleared on return. The arena owns its block rentals and releases them on
/// <see cref="Dispose"/>; it never disposes the injected pool.
/// </para>
/// <para>
/// Every block holds 65,536 items and is rented only when needed. No rental grows with the set; the item
/// capacity hint presizes only the block table.
/// </para>
/// <para>
/// No clear-on-rent is needed, unlike the cell buffer: every slot handed out is fully overwritten by the
/// append copy before it is returned, and a slot beyond the used count is never viewed, so a recycled-dirty
/// pooled segment can never be observed through a handed-out slice. The used region of each block is
/// still cleared on <see cref="Dispose"/> as defence in depth so a recycled pool segment carries no stale
/// item bytes.
/// </para>
/// <para>
/// It is a mutable, single-threaded structure and is not safe for concurrent calls.
/// </para>
/// </remarks>
public sealed class ReconciliationItemArena: IDisposable
{
    /// <summary>The shift that maps an item index to its block index.</summary>
    private const int ItemsPerBlockShift = 16;

    /// <summary>The number of items held by every block.</summary>
    private const int ItemsPerBlock = 1 << ItemsPerBlockShift;


    private MemoryPool<byte> Pool { get; }
    private int Stride { get; }
    /// <summary>The rented blocks in append order.</summary>
    private List<Block> Blocks { get; }
    private int currentBlockUsed;
    private bool disposed;


    /// <summary>
    /// Initializes an empty arena whose every slice is <paramref name="stride"/> bytes; blocks of 65,536 items
    /// are rented as items are appended and the constructor rents nothing.
    /// </summary>
    /// <param name="stride">The item width in bytes, in the inclusive range one through 1024; every slice handed out is exactly this long.</param>
    /// <param name="pool">The pool to rent the blocks from, tracking the memory. The arena never disposes it.</param>
    /// <param name="itemCapacityHint">
    /// A lower bound on the items the session will append, used only to presize the block table. Must not be negative.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="stride"/> is outside one through 1024 or when <paramref name="itemCapacityHint"/>
    /// is negative.
    /// </exception>
    public ReconciliationItemArena(int stride, MemoryPool<byte> pool, int itemCapacityHint = 0)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if(stride is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "The item stride must be between one and 1024 bytes.");
        }

        if(itemCapacityHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCapacityHint), itemCapacityHint, "The item capacity hint cannot be negative.");
        }

        Stride = stride;
        Pool = pool;
        Blocks = new List<Block>((int)(((long)itemCapacityHint + ItemsPerBlock - 1) >> ItemsPerBlockShift));
    }


    /// <summary>The number of items appended so far, the index the next <see cref="Append"/> assigns.</summary>
    public int Count { get; private set; }


    /// <summary>
    /// Copies <paramref name="item"/> into the next free slot of the current block -- growing by one block when
    /// the current block is full or none exists yet -- and returns a view over the stored bytes. The view
    /// stays valid until <see cref="Dispose"/> because blocks are never relocated.
    /// </summary>
    /// <param name="item">The item bytes. Must be exactly <see cref="Stride"/> bytes.</param>
    /// <returns>A stable, never-moved view over the stored copy, exactly <see cref="Stride"/> bytes long.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the arena has been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="item"/>'s length differs from <see cref="Stride"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Count"/> has reached <see cref="Array.MaxLength"/>.</exception>
    public ReadOnlyMemory<byte> Append(ReadOnlySpan<byte> item)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(item.Length != Stride)
        {
            throw new ArgumentException($"An item must be exactly {Stride} bytes.", nameof(item));
        }

        if(Count == Array.MaxLength)
        {
            throw new InvalidOperationException($"The item arena cannot hold more than {Array.MaxLength} items.");
        }

        if(Blocks.Count == 0 || currentBlockUsed == ItemsPerBlock)
        {
            Grow();
        }

        Block block = Blocks[^1];
        int slot = currentBlockUsed;
        Memory<byte> destination = block.Owner.Memory.Slice(slot * Stride, Stride);

        //The slot is fully overwritten here, so a recycled pooled segment can never be observed through the
        //returned slice; no clear-on-rent is needed, unlike the cell buffer.
        item.CopyTo(destination.Span);

        currentBlockUsed++;
        Count++;

        return destination;
    }


    /// <summary>
    /// Returns the stable view of the item appended at <paramref name="index"/>, in append order.
    /// </summary>
    /// <param name="index">The zero-based append index of the item.</param>
    /// <returns>The same memory that <see cref="Append"/> returned for the item, valid until <see cref="Dispose"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="index"/> is outside the appended range.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the arena has been disposed.</exception>
    public ReadOnlyMemory<byte> ItemAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The item index must be within the appended range.");
        }

        Block block = Blocks[index >> ItemsPerBlockShift];
        int slot = index & (ItemsPerBlock - 1);

        return block.Owner.Memory.Slice(slot * Stride, Stride);
    }


    /// <summary>
    /// Clears every block's used region and returns every rental to the pool. The call is idempotent; after it,
    /// <see cref="Append"/> and <see cref="ItemAt"/> throw <see cref="ObjectDisposedException"/>. The injected
    /// pool is never disposed.
    /// If a component's release throws, every remaining component is still released and the first failure propagates afterwards.
    /// </summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;
        Exception? failure = null;

        for(int index = 0; index < Blocks.Count; index++)
        {
            Block block = Blocks[index];
            int used = index == Blocks.Count - 1 ? currentBlockUsed : ItemsPerBlock;
            ReconciliationRental.Release(block.Owner, used * Stride, ref failure);
        }

        try
        {
            Blocks.Clear();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        if(failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }


    /// <summary>Rents one block of 65,536 items without relocating any previously stored item.</summary>
    [SuppressMessage("Reliability", "CA2000", Justification = "The list's capacity is ensured before the rental, so the insertion cannot fail after the owner exists and Dispose releases every held block.")]
    private void Grow()
    {
        //Ensure the list's capacity before renting the block so insertion cannot fail after the owner exists.
        Blocks.EnsureCapacity(Blocks.Count + 1);
        IMemoryOwner<byte> owner = Rent(ItemsPerBlock * Stride);
        Blocks.Add(new Block(owner));

        currentBlockUsed = 0;
    }


    /// <summary>Rents a backing region of at least the requested byte length from the injected pool.</summary>
    private IMemoryOwner<byte> Rent(int byteLength)
    {
        //A general pool may return an owner larger than requested; the arena only ever slices within the
        //ItemsPerBlock * Stride bytes of a block, so an over-sized owner is harmless.

        return Pool.Rent(byteLength);
    }


    /// <summary>Owns one fixed-size item block.</summary>
    /// <param name="owner">The rental released when the arena is disposed.</param>
    [SuppressMessage("Usage", "CA1815", Justification = "A non-compared internal carrier; blocks are never compared, only appended and disposed.")]
    private readonly struct Block(IMemoryOwner<byte> owner)
    {
        /// <summary>The rental that backs the block's item bytes.</summary>
        public IMemoryOwner<byte> Owner { get; } = owner;
    }
}
