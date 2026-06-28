using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

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
/// corrupt every membership key and cursor viewing the moved bytes — a silent wrong result — so the arena is
/// deliberately the opposite pattern: it only ever appends, leaving every prior block exactly where it is. A
/// slice it hands out stays byte-valid for the arena's whole life.
/// </para>
/// <para>
/// Each block is an <see cref="IMemoryOwner{T}"/> rental from the required <see cref="MemoryPool{T}"/>, so the
/// memory is pooled, tracked, and cleared on return. The arena owns its block rentals and releases them on
/// <see cref="Dispose"/>; it never disposes the injected pool.
/// </para>
/// <para>
/// No clear-on-rent is needed, unlike the cell buffer: every slot handed out is fully overwritten by the
/// append copy before it is returned, and a slot beyond the used count is never viewed, so a recycled-dirty
/// pooled segment can never be observed through a handed-out slice. The whole logical region of each block is
/// still cleared on <see cref="Dispose"/> as defence in depth so a recycled pool segment carries no stale
/// item bytes.
/// </para>
/// <para>
/// It is a mutable, single-threaded structure and is not safe for concurrent calls.
/// </para>
/// </remarks>
public sealed class ReconciliationItemArena: IDisposable
{
    //The smallest items a first block holds, keeping the common tiny session small while still amortizing the grow.
    private const int MinItemsPerBlock = 4;


    private MemoryPool<byte> Pool { get; }
    private int Stride { get; }
    private int HintItems { get; }
    private List<Block> Blocks { get; } = [];
    private int currentBlockUsed;
    private int currentBlockCapacity;
    private int totalCapacityItems;
    private bool disposed;


    /// <summary>
    /// Initializes an empty arena whose every slice is <paramref name="stride"/> bytes, renting its blocks
    /// lazily on the first <see cref="Append"/> from a quantized first-block capacity seeded from
    /// <paramref name="itemCapacityHint"/>; the total capacity then doubles a block at a time as items are
    /// appended.
    /// </summary>
    /// <param name="stride">The item width in bytes, in the inclusive range one through 1024; every slice handed out is exactly this long.</param>
    /// <param name="pool">The pool to rent the blocks from, tracking the memory. The arena never disposes it.</param>
    /// <param name="itemCapacityHint">
    /// A lower bound on the items the session will append; the first block's capacity is the smallest power of
    /// two at or above the larger of this and the minimum, pre-sizing past the doubling churn. Must not be negative.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="stride"/> is outside one through 1024, when <paramref name="itemCapacityHint"/>
    /// is negative, or when it rounds up to a first block whose backing would exceed the maximum array length.
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

        //Reject a hint whose first block backing would overflow a single allocation up front, so construction
        //fails fast with the hint's own name rather than the first append meeting the limit. The rounding is done
        //in 64-bit, so an out-of-range hint is caught by this widened bound check, never by a spinning doubling
        //loop. No rental happens here — only the validation is eager; the first block stays lazily rented.
        long firstBlock64 = NextPowerOfTwo(Math.Max(itemCapacityHint, MinItemsPerBlock));
        if(firstBlock64 * stride > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCapacityHint), itemCapacityHint, $"The item capacity hint of {itemCapacityHint} rounds up to a first block of {firstBlock64} items whose {stride}-byte-wide backing would exceed the maximum array length of {Array.MaxLength}.");
        }

        Stride = stride;
        Pool = pool;
        HintItems = itemCapacityHint;

        //Rent nothing here: the first block is rented lazily on the first append, so an arena constructed and
        //disposed with zero items rents nothing, and a never-renting constructor cannot leave a partial-
        //construction leak — which is why the encoder and decoder need no try/catch guard around building it.
    }


    /// <summary>The number of items appended so far, the index the next <see cref="Append"/> assigns.</summary>
    public int Count { get; private set; }


    /// <summary>
    /// Copies <paramref name="item"/> into the next free slot of the current block — growing by one block when
    /// the current block is full or none exists yet — and returns a view over the stored bytes. The view
    /// stays valid until <see cref="Dispose"/> because blocks are never relocated.
    /// </summary>
    /// <param name="item">The item bytes. Must be exactly <see cref="Stride"/> bytes.</param>
    /// <returns>A stable, never-moved view over the stored copy, exactly <see cref="Stride"/> bytes long.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the arena has been disposed.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="item"/>'s length differs from <see cref="Stride"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the arena is full and cannot grow without its backing exceeding the maximum array length.</exception>
    public ReadOnlyMemory<byte> Append(ReadOnlySpan<byte> item)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(item.Length != Stride)
        {
            throw new ArgumentException($"An item must be exactly {Stride} bytes.", nameof(item));
        }

        if(Blocks.Count == 0 || currentBlockUsed == currentBlockCapacity)
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
    /// Clears and disposes every block's rental, returning the rentals to the pool when one was injected. The
    /// call is idempotent; after it, <see cref="Append"/> throws <see cref="ObjectDisposedException"/>. The
    /// injected pool, if any, is never disposed — the arena owns its block rentals, not the pool.
    /// </summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;

        //Clear each block's logical region before releasing so a recycled pool segment carries no stale item
        //bytes (defence in depth, matching the cell buffer and the tagged-memory pattern), then return it.
        foreach(Block block in Blocks)
        {
            block.Owner.Memory.Span[..(block.ItemCapacity * Stride)].Clear();
            block.Owner.Dispose();
        }

        Blocks.Clear();
    }


    //The rented owner's ownership transfers into the Blocks list, which Dispose releases in full; the analyzer
    //cannot see ownership flow through a List.Add, so the lifetime is sound but not statically provable here.
    [SuppressMessage("Reliability", "CA2000", Justification = "The rented block owner transfers into the Blocks list and is disposed in Dispose; the insertion is exception-safe, so the owner is always either held by the list or released on the throwing path.")]
    private void Grow()
    {
        //The total capacity progresses b, 2b, 4b, 8b… so a session of N items uses O(log N) blocks at amortized
        //O(1) per append; appending a new block never touches the bytes of any existing block, which is the
        //whole point — every prior slice stays exactly where it was handed out.
        long newCapacity64 = Blocks.Count == 0
            ? NextPowerOfTwo(Math.Max(HintItems, MinItemsPerBlock))
            : totalCapacityItems;

        //The new block's byte length and the running total are computed in 64-bit and bound-checked before
        //narrowing, so a growth whose block backing or total capacity would exceed a single allocation throws
        //here rather than wrapping a rental size to a too-small block the later slices would overrun. The ctor
        //bounds the hint to keep the first block within range; this is the growth path's own guard.
        long blockBytes64 = newCapacity64 * Stride;
        long newTotal64 = totalCapacityItems + newCapacity64;
        if(blockBytes64 > Array.MaxLength || newTotal64 > Array.MaxLength)
        {
            throw new InvalidOperationException($"The item arena cannot grow by {newCapacity64} items: a block of {blockBytes64} bytes or a total backing of {newTotal64} bytes would exceed the maximum array length of {Array.MaxLength}.");
        }

        int newCapacity = (int)newCapacity64;

        //Rent first, then transfer ownership into the list exception-safely: if the insertion throws (only an
        //OutOfMemoryException while the list resizes its own backing), the just-rented owner is returned rather
        //than orphaned, so every rental still balances. This mirrors the cell buffer's paired-rent discipline.
        IMemoryOwner<byte> owner = Rent((int)blockBytes64);
        try
        {
            Blocks.Add(new Block(owner, newCapacity));
        }
        catch
        {
            owner.Dispose();

            throw;
        }

        currentBlockUsed = 0;
        currentBlockCapacity = newCapacity;
        totalCapacityItems = (int)newTotal64;
    }


    private IMemoryOwner<byte> Rent(int byteLength)
    {
        //A general pool may return an owner larger than requested; the arena only ever slices within
        //capacity * Stride, so an over-sized owner is harmless.
        return Pool.Rent(byteLength);
    }


    private static long NextPowerOfTwo(int value)
    {
        //Rounds up in 64-bit so the doubling cannot overflow into the sign flip that would spin this loop forever:
        //for any int value the result reaches at most 2^31, well within long, so the loop always terminates and
        //the caller's widened bound check rejects a capacity whose backing would not fit a single allocation.
        //value is at least MinItemsPerBlock here, so the result is always a positive power of two at or above it.
        long power = 1;
        while(power < value)
        {
            power *= 2;
        }

        return power;
    }


    //A non-compared internal carrier holding a rented block and its item-capacity; not a record struct because
    //the IMemoryOwner member would drag in reference equality the arena never wants.
    [SuppressMessage("Usage", "CA1815", Justification = "A non-compared internal carrier; blocks are never compared, only appended and disposed.")]
    private readonly struct Block(IMemoryOwner<byte> owner, int itemCapacity)
    {
        public IMemoryOwner<byte> Owner { get; } = owner;

        public int ItemCapacity { get; } = itemCapacity;
    }
}
