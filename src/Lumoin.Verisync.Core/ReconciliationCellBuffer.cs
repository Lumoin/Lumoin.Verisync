using System;
using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A dense, append-only, fixed-width cell store backing the reconciliation kernel's coded cells. Every cell
/// is a pair of constant-width fields — a <see cref="SumWidth"/>-byte sum and a <see cref="ChecksumWidth"/>-byte
/// checksum — laid out row-major in two contiguous backing owners, so cell <c>i</c> occupies a single
/// slice of each backing. The contiguous layout is what the vectorized XOR fold walks; one buffer is shared
/// by the encoder's produced cells and the decoder's absorbed cells, replacing the per-cell array allocation.
/// </summary>
/// <remarks>
/// <para>
/// The two backings are <see cref="IMemoryOwner{T}"/> rentals from the required <see cref="MemoryPool{T}"/>,
/// so the buffer's memory is pooled, tracked, and cleared on return. The buffer owns its rentals and releases
/// them on <see cref="Dispose"/>; it never disposes the injected pool. It only ever grows, by renting the
/// next power-of-two and copying the live bytes across, and is never written beyond <see cref="Count"/>
/// except through the spans <see cref="SumAt(int)"/> and <see cref="ChecksumAt(int)"/> hand out.
/// </para>
/// <para>
/// The buffer's contract is that a freshly appended cell is all zero across both fields — the encoder folds
/// into a zero cell and the decoder copies a raw symbol in then folds decoded items out, both assuming
/// zero-initialized cells. A pooled segment is NOT zero-filled on rent, so the load-bearing clear after every
/// rent restores that contract: the whole logical span on the initial rent, and the region beyond the copied
/// live bytes after a grow. The logical capacity is authoritative — a pool may return an over-sized owner, so
/// the buffer always slices within <c>Capacity * width</c> and never trusts the owner's own length.
/// </para>
/// <para>
/// It is a mutable, single-threaded structure and is not safe for concurrent calls. A span a reader holds is
/// valid only until the next <see cref="Append"/> (which may move the backing) or <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class ReconciliationCellBuffer: IDisposable
{
    //The smallest initial capacity, keeping the common tiny session small while still amortizing the grow.
    private const int MinCapacity = 4;


    private IMemoryOwner<byte> SumOwner { get; set; }
    private IMemoryOwner<byte> ChecksumOwner { get; set; }
    private MemoryPool<byte> Pool { get; }
    private int Capacity { get; set; }
    private bool disposed;


    /// <summary>
    /// Initializes an empty cell buffer whose cells are <paramref name="sumWidth"/> sum bytes and
    /// <paramref name="checksumWidth"/> checksum bytes wide, renting both backings at a quantized initial
    /// capacity seeded from <paramref name="cellCapacityHint"/> that grows by doubling as cells are appended.
    /// </summary>
    /// <param name="sumWidth">The sum field width in bytes, in the inclusive range one through 1024.</param>
    /// <param name="checksumWidth">The checksum field width in bytes, in the inclusive range one through eight.</param>
    /// <param name="pool">The pool to rent the backings from, tracking the memory. The buffer never disposes it.</param>
    /// <param name="cellCapacityHint">
    /// A lower bound on the cells the session will touch; the initial capacity is the smallest power of two at
    /// or above the larger of this and the minimum, pre-sizing past the doubling churn. Must not be negative.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="sumWidth"/> is outside one through 1024, when <paramref name="checksumWidth"/>
    /// is outside one through eight, when <paramref name="cellCapacityHint"/> is negative, or when it rounds up
    /// to a capacity whose backing would exceed the maximum array length.
    /// </exception>
    public ReconciliationCellBuffer(int sumWidth, int checksumWidth, MemoryPool<byte> pool, int cellCapacityHint = 0)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if(sumWidth is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(sumWidth), sumWidth, "The sum width must be between one and 1024 bytes.");
        }

        if(checksumWidth is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(checksumWidth), checksumWidth, "The checksum width must be between one and eight bytes.");
        }

        if(cellCapacityHint < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCapacityHint), cellCapacityHint, "The cell capacity hint cannot be negative.");
        }

        SumWidth = sumWidth;
        ChecksumWidth = checksumWidth;
        Pool = pool;

        //Round the hint up to the next power of two in 64-bit and reject one whose backing would exceed a single
        //allocation before narrowing back to int. The widened rounding cannot overflow into a spinning loop, and
        //bounding the widened product keeps every later capacity * width and index * width multiply provably
        //within int range. A hint past this bound is the caller's, so it surfaces with the hint's own name here
        //rather than as an overflow deeper down.
        long capacity64 = NextPowerOfTwo(Math.Max(cellCapacityHint, MinCapacity));
        if(capacity64 * sumWidth > Array.MaxLength || capacity64 * checksumWidth > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCapacityHint), cellCapacityHint, $"The cell capacity hint of {cellCapacityHint} rounds up to a capacity of {capacity64} whose backing at {sumWidth} sum and {checksumWidth} checksum bytes per cell would exceed the maximum array length of {Array.MaxLength}.");
        }

        int capacity = (int)capacity64;
        int sumBytes = capacity * sumWidth;
        int checksumBytes = capacity * checksumWidth;
        Capacity = capacity;

        //Rent both backings at the logical size, exception-safely: if the second rent throws, the first is
        //returned, so a failed construction leaks no rental. Then clear the whole logical span — a pooled
        //segment is not zero-filled on rent, so this restores the all-zero-fresh-cell contract every appended
        //cell relies on.
        IMemoryOwner<byte> sumOwner = Rent(sumBytes);
        IMemoryOwner<byte> checksumOwner;
        try
        {
            checksumOwner = Rent(checksumBytes);
        }
        catch
        {
            sumOwner.Dispose();

            throw;
        }

        SumOwner = sumOwner;
        ChecksumOwner = checksumOwner;
        SumOwner.Memory.Span[..sumBytes].Clear();
        ChecksumOwner.Memory.Span[..checksumBytes].Clear();
    }


    /// <summary>The sum field width in bytes, the length of every span <see cref="SumAt(int)"/> returns.</summary>
    public int SumWidth { get; }

    /// <summary>The checksum field width in bytes, the length of every span <see cref="ChecksumAt(int)"/> returns.</summary>
    public int ChecksumWidth { get; }

    /// <summary>The number of cells appended so far, which is the index <see cref="Append"/> assigns next.</summary>
    public int Count { get; private set; }


    /// <summary>
    /// Appends a new cell and returns its index. The buffer doubles both backings when full; the new cell's
    /// sum and checksum bytes are guaranteed to be all zero, so a caller may fold straight into the returned
    /// spans without clearing them first.
    /// </summary>
    /// <returns>The index of the newly appended cell, equal to the prior <see cref="Count"/>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the buffer has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the buffer is full and cannot grow without its backing exceeding the maximum array length.</exception>
    public int Append()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(Count == Capacity)
        {
            //Grow by renting the next power-of-two, copying the live bytes across, clearing the newly-exposed
            //region (the pool does not zero recycled segments), and disposing the old owners. The new cell at
            //the returned index lies in the just-cleared region, so it is zero — the fresh-cell contract holds.
            //The doubled capacity and both byte lengths are computed in 64-bit and bound-checked before narrowing,
            //so a growth whose backing would exceed a single allocation throws here rather than wrapping a rental
            //size to a too-small buffer the later slices would overrun. The ctor bounds the hint to keep this
            //unreachable in practice; this is the growth path's own guard.
            long grown64 = (long)Capacity * 2;
            long grownSumBytes64 = grown64 * SumWidth;
            long grownChecksumBytes64 = grown64 * ChecksumWidth;
            if(grownSumBytes64 > Array.MaxLength || grownChecksumBytes64 > Array.MaxLength)
            {
                throw new InvalidOperationException($"The cell buffer cannot grow to {grown64} cells: its backing at {SumWidth} sum and {ChecksumWidth} checksum bytes per cell would exceed the maximum array length of {Array.MaxLength}.");
            }

            int grown = (int)grown64;
            int grownSumBytes = (int)grownSumBytes64;
            int grownChecksumBytes = (int)grownChecksumBytes64;

            //Rent the new pair exception-safely too: a throwing second rent disposes the first and leaves the
            //buffer's existing owners untouched, so neither the new nor the old rentals leak.
            IMemoryOwner<byte> grownSumOwner = Rent(grownSumBytes);
            IMemoryOwner<byte> grownChecksumOwner;
            try
            {
                grownChecksumOwner = Rent(grownChecksumBytes);
            }
            catch
            {
                grownSumOwner.Dispose();

                throw;
            }

            int liveSumBytes = Count * SumWidth;
            int liveChecksumBytes = Count * ChecksumWidth;

            SumOwner.Memory.Span[..liveSumBytes].CopyTo(grownSumOwner.Memory.Span);
            ChecksumOwner.Memory.Span[..liveChecksumBytes].CopyTo(grownChecksumOwner.Memory.Span);

            grownSumOwner.Memory.Span[liveSumBytes..grownSumBytes].Clear();
            grownChecksumOwner.Memory.Span[liveChecksumBytes..grownChecksumBytes].Clear();

            SumOwner.Dispose();
            ChecksumOwner.Dispose();

            SumOwner = grownSumOwner;
            ChecksumOwner = grownChecksumOwner;
            Capacity = grown;
        }

        int index = Count;
        Count++;

        return index;
    }


    /// <summary>
    /// Returns the sum field of the cell at <paramref name="index"/> as a writable span over the backing. The
    /// span has length <see cref="SumWidth"/> and is valid only until the next <see cref="Append"/> or
    /// <see cref="Dispose"/>, which may move or release the backing.
    /// </summary>
    /// <param name="index">The cell index, in the range zero through <see cref="Count"/> exclusive.</param>
    /// <returns>The cell's sum field over the contiguous backing.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the buffer has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the appended range.</exception>
    public Span<byte> SumAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The cell index must be within the appended range.");
        }

        return SumOwner.Memory.Span.Slice(index * SumWidth, SumWidth);
    }


    /// <summary>
    /// Returns the checksum field of the cell at <paramref name="index"/> as a writable span over the backing.
    /// The span has length <see cref="ChecksumWidth"/> and is valid only until the next <see cref="Append"/> or
    /// <see cref="Dispose"/>, which may move or release the backing.
    /// </summary>
    /// <param name="index">The cell index, in the range zero through <see cref="Count"/> exclusive.</param>
    /// <returns>The cell's checksum field over the contiguous backing.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the buffer has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is outside the appended range.</exception>
    public Span<byte> ChecksumAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if(index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The cell index must be within the appended range.");
        }

        return ChecksumOwner.Memory.Span.Slice(index * ChecksumWidth, ChecksumWidth);
    }


    /// <summary>
    /// Clears and disposes both rented backings, returning the rentals to the pool when one was injected. The
    /// call is idempotent; after it, <see cref="Append"/>, <see cref="SumAt(int)"/>, and
    /// <see cref="ChecksumAt(int)"/> throw <see cref="ObjectDisposedException"/>. The injected pool, if any, is
    /// never disposed — the buffer owns its rentals, not the pool.
    /// </summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;

        //Clear the logical span before releasing so a recycled pool segment carries no stale bytes, then
        //release the rentals; the pool clears returned segments too, so this is defence in depth on both paths.
        SumOwner.Memory.Span[..(Capacity * SumWidth)].Clear();
        ChecksumOwner.Memory.Span[..(Capacity * ChecksumWidth)].Clear();

        SumOwner.Dispose();
        ChecksumOwner.Dispose();
    }


    private IMemoryOwner<byte> Rent(int byteLength)
    {
        //A general pool may return an owner larger than requested; the buffer treats the logical capacity as
        //authoritative and only ever slices within Capacity * width, so an over-sized owner is harmless.
        return Pool.Rent(byteLength);
    }


    private static long NextPowerOfTwo(int value)
    {
        //Rounds up in 64-bit so the doubling cannot overflow into the sign flip that would spin this loop forever:
        //for any int value the result reaches at most 2^31, well within long, so the loop always terminates and
        //the caller's widened bound check rejects a capacity whose backing would not fit a single allocation.
        //value is at least MinCapacity here, so the result is always a positive power of two at or above it.
        long power = 1;
        while(power < value)
        {
            power *= 2;
        }

        return power;
    }
}
