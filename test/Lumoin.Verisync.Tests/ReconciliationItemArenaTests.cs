using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Focused deterministic coverage of the append-only item arena in isolation: construction range validation,
/// the exact-width round-trip append contract and its wrong-length rejection, the central never-relocate
/// invariant that an early slice still reads its original bytes once the arena spans three blocks, disposal semantics,
/// the pooled growth zero-leak balance, and the full-overwrite-on-append soundness contract that needs no
/// clear-on-rent. The encoder and decoder behaviour that consumes the arena is covered by the existing suite,
/// which is the oracle.
/// </summary>
/// <remarks>
/// The pooled-rental test observes the library's process-global rental instruments, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> to keep their measurement totals free of rentals emitted by other
/// pool-using tests running concurrently -- the same isolation the other metric-observing suites use.
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class ReconciliationItemArenaTests
{
    [TestMethod]
    public void ConstructionRejectsArgumentsOutsideTheirRanges()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(0, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(1025, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(8, BaseMemoryPool.Shared, -1));
    }


    /// <summary>Every non-negative hint can presize the block table without renting item memory.</summary>
    [TestMethod]
    public void ConstructionAcceptsLargeHintsWithoutRenting()
    {
        using RentalAccountant accountant = new();
        using BaseMemoryPool pool = new();
        using ReconciliationItemArena largestHint = new(8, pool, itemCapacityHint: int.MaxValue);
        using ReconciliationItemArena widestStride = new(1024, pool, itemCapacityHint: (1 << 21) + 1);
        Assert.AreEqual(0, largestHint.Count);
        Assert.AreEqual(0, widestStride.Count);
        Assert.AreEqual(0L, accountant.Rented);
    }


    [TestMethod]
    public void AppendReturnsExactWidthSlicesThatRoundTrip()
    {
        const int Stride = 8;
        using ReconciliationItemArena arena = new(Stride, BaseMemoryPool.Shared);
        Assert.AreEqual(0, arena.Count);

        for(int n = 0; n < 5; n++)
        {
            //Each item carries a distinct, index-derived pattern across its full width.
            byte[] item = new byte[Stride];
            for(int b = 0; b < Stride; b++)
            {
                item[b] = (byte)((n * 31) + b);
            }

            ReadOnlyMemory<byte> slice = arena.Append(item);

            //The returned slice is exactly one stride wide and reads back the bytes just written.
            Assert.HasCount(Stride, slice.ToArray());
            for(int b = 0; b < Stride; b++)
            {
                Assert.AreEqual((byte)((n * 31) + b), slice.Span[b]);
            }

            Assert.AreEqual(n + 1, arena.Count);
        }

        //A span whose length is not exactly the stride is rejected, both shorter and longer.
        Assert.ThrowsExactly<ArgumentException>(() => arena.Append(new byte[Stride - 1]));
        Assert.ThrowsExactly<ArgumentException>(() => arena.Append(new byte[Stride + 1]));
    }


    /// <summary>Early slices retain their bytes after multiple fixed blocks are rented.</summary>
    [TestMethod]
    public void AppendedSlicesNeverMoveAcrossGrowth()
    {
        const int Stride = 8;
        const int Items = (2 * 65_536) + 1;

        //Appending into a third block must leave the held early slices at their original bytes.
        using ReconciliationItemArena arena = new(Stride, BaseMemoryPool.Shared, itemCapacityHint: 0);

        //Capture the live slices and an independent copy of the expected bytes for a spread of early indices
        //BEFORE the grows that come with the later appends.
        int[] earlyIndices = [0, 1, 2, 3, 4, 7, 11, 19];
        ReadOnlyMemory<byte>[] earlySlices = new ReadOnlyMemory<byte>[earlyIndices.Length];
        byte[][] earlyExpected = new byte[earlyIndices.Length][];

        int next = 0;
        int captured = 0;
        while(next <= earlyIndices[^1])
        {
            byte[] item = BuildItem(Stride, next);
            ReadOnlyMemory<byte> slice = arena.Append(item);
            if(captured < earlyIndices.Length && next == earlyIndices[captured])
            {
                earlySlices[captured] = slice;
                earlyExpected[captured] = item;
                captured++;
            }

            next++;
        }

        //Drive the arena across two block boundaries into a third block.
        for(; next < Items; next++)
        {
            _ = arena.Append(BuildItem(Stride, next));
        }

        Assert.AreEqual(Items, arena.Count);

        //Every captured early slice still reads its original bytes byte-for-byte, proving its block was never
        //relocated by any of the intervening grows.
        for(int e = 0; e < earlyIndices.Length; e++)
        {
            ReadOnlyMemory<byte> slice = earlySlices[e];
            byte[] expected = earlyExpected[e];
            Assert.HasCount(Stride, slice.ToArray());
            for(int b = 0; b < Stride; b++)
            {
                Assert.AreEqual(expected[b], slice.Span[b]);
            }
        }

        //A freshly captured slice for a late index in the last block also reads correctly.
        const int LateIndex = Items - 1;
        ReadOnlyMemory<byte> lateSlice = arena.Append(BuildItem(Stride, LateIndex));
        byte[] lateExpected = BuildItem(Stride, LateIndex);
        for(int b = 0; b < Stride; b++)
        {
            Assert.AreEqual(lateExpected[b], lateSlice.Span[b]);
        }
    }


    [TestMethod]
    public void DisposalClearsAndThrowsAndIsIdempotent()
    {
        ReconciliationItemArena arena = new(8, BaseMemoryPool.Shared);
        _ = arena.Append(new byte[8]);

        arena.Dispose();

        //After disposal an append rejects use rather than touching a released backing.
        Assert.ThrowsExactly<ObjectDisposedException>(() => arena.Append(new byte[8]));

        //A second dispose is a silent no-op; the guarded flag absorbs it without a double-return to a pool.
        arena.Dispose();
    }


    /// <summary>Disposing an arena returns every fixed block rented across growth.</summary>
    [TestMethod]
    public void PooledGrowthLeavesNoActiveRentals()
    {
        const int Stride = 8;
        const int Items = (2 * 65_536) + 1;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            //The corpus fills two blocks and starts a third on the pooled path.
            ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

            for(int n = 0; n < Items; n++)
            {
                ReadOnlyMemory<byte> slice = arena.Append(BuildItem(Stride, n));

                //Confirm the round-trip on append so the test proves the pooled blocks carried the items, not
                //merely that the rental ledger balanced.
                for(int b = 0; b < Stride; b++)
                {
                    Assert.AreEqual((byte)((n * 31) + b), slice.Span[b]);
                }
            }

            Assert.AreEqual(Items, arena.Count);

            //Dispose the arena before reading the totals so every return measurement has flushed to the listener.
            arena.Dispose();
        }

        //Disposal returns every block, so the net active gauge balances to zero; the corpus spans exactly three
        //blocks, so the arena rented exactly three and returned exactly as many as it took.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.AreEqual(3L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public void FreshArenaOnDirtyRecycledSegmentsReadsTheWrittenBytesNotStale()
    {
        const int Stride = 8;
        const int InitialCapacity = 4;

        //A pool that hands out genuinely dirty (0xFF-filled) blocks on every rent, independent of any real pool's
        //clear policy. The arena does not clear on rent; it full-overwrites each slot on append, so every
        //returned slice must read the written bytes, never the stale 0xFF of the rented block. (A real pool that
        //clears recycled memory, as BaseMemoryPool does on return, would weaken this check; the dirty stub does
        //not, so a removed full-overwrite would read 0xFF and fail.)
        using DirtyMemoryPool pool = new();

        using ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

        for(int n = 0; n < InitialCapacity; n++)
        {
            byte[] item = BuildItem(Stride, n);
            ReadOnlyMemory<byte> slice = arena.Append(item);
            for(int b = 0; b < Stride; b++)
            {
                Assert.AreEqual(item[b], slice.Span[b]);
            }
        }
    }


    /// <summary>
    /// Builds a deterministic, index-derived item of the given width without System.Random (CA5394): a simple
    /// index-and-position pattern that is distinct per index and fills the whole stride.
    /// </summary>
    private static byte[] BuildItem(int stride, int index)
    {
        byte[] item = new byte[stride];
        for(int b = 0; b < stride; b++)
        {
            item[b] = (byte)((index * 31) + b);
        }

        return item;
    }


    /// <summary>Each grow rents another block of the same item capacity.</summary>
    [TestMethod]
    public void GrowthKeepsEveryBlockAtTheSameCapacity()
    {
        const int Stride = 8;
        const int Items = (3 * 65_536) + 1;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using var meter = new Meter(BaseMemoryPoolMetrics.MeterName);
            List<int> segmentSizes = [];
            using BaseMemoryPool pool = new(meter, capacityStrategy: size =>
            {
                segmentSizes.Add(size);

                return 1;
            });
            using ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

            for(int n = 0; n < Items; n++)
            {
                _ = arena.Append(BuildItem(Stride, n));
            }

            Assert.AreEqual(Items, arena.Count);
            Assert.HasCount(4, segmentSizes);
            foreach(int size in segmentSizes)
            {
                Assert.AreEqual(65_536 * Stride, size);
            }

            arena.Dispose();
        }

        Assert.AreEqual(4L, accountant.Rented);
    }


    /// <summary>The first block holds exactly 65,536 items before another rental is needed.</summary>
    [TestMethod]
    public void ZeroHintFirstBlockHoldsExactly65536ItemsBeforeGrowing()
    {
        const int Stride = 8;
        const int FloorItems = 65_536;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();
            ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

            for(int n = 0; n < FloorItems; n++)
            {
                _ = arena.Append(BuildItem(Stride, n));
            }

            Assert.AreEqual(1L, accountant.Rented);

            Assert.AreEqual(FloorItems, arena.Count);
            _ = arena.Append(BuildItem(Stride, FloorItems));

            Assert.AreEqual(2L, accountant.Rented);
            Assert.AreEqual(FloorItems + 1, arena.Count);

            arena.Dispose();
        }
    }


    /// <summary>Disposal clears full blocks and the used last-block region while leaving unused bytes untouched.</summary>
    [TestMethod]
    public void DisposalClearsOnlyEachBlocksUsedRegionEvenWhenThePoolDoesNot()
    {
        const int Stride = 8;
        using DirtyMemoryPool pool = new();
        using ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

        byte[] item = new byte[Stride];
        for(int b = 0; b < Stride; b++)
        {
            item[b] = (byte)(b + 1);
        }

        ReadOnlyMemory<byte> first = arena.Append(item);
        for(int index = 1; index < 65_536; index++)
        {
            _ = arena.Append(item);
        }

        ReadOnlyMemory<byte> last = arena.Append(item);
        Assert.IsTrue(MemoryMarshal.TryGetArray(first, out ArraySegment<byte> firstBlock));
        Assert.IsTrue(MemoryMarshal.TryGetArray(last, out ArraySegment<byte> lastBlock));
        Assert.IsNotNull(firstBlock.Array);
        Assert.IsNotNull(lastBlock.Array);

        //Sanity: the slice reads the written, non-zero bytes before disposal.
        Assert.IsTrue(first.Span.SequenceEqual(item));
        Assert.IsTrue(last.Span.SequenceEqual(item));

        arena.Dispose();

        //The dirty pool never clears on return, so these checks observe the arena's own clearing boundaries.
        Assert.AreEqual(-1, firstBlock.Array.AsSpan().IndexOfAnyExcept((byte)0));
        Assert.AreEqual(-1, lastBlock.Array.AsSpan(0, Stride).IndexOfAnyExcept((byte)0));
        Assert.AreEqual(-1, lastBlock.Array.AsSpan(Stride).IndexOfAnyExcept((byte)0xFF));
    }


    /// <summary>Pins that the stride lower bound is inclusive: exactly one byte constructs without throwing.</summary>
    [TestMethod]
    public void ConstructionAcceptsTheMinimumStrideOfOneByte()
    {
        using ReconciliationItemArena arena = new(1, BaseMemoryPool.Shared);
        Assert.AreEqual(0, arena.Count);
    }


    /// <summary>A large hint rents nothing at construction and one fixed block on the first append.</summary>
    [TestMethod]
    public void LargeHintLeavesTheFirstBlockLazyAndFixedSize()
    {
        const int Stride = 8;
        const int Hint = 200_000;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using var meter = new Meter(BaseMemoryPoolMetrics.MeterName);
            List<int> segmentSizes = [];
            using BaseMemoryPool pool = new(meter, capacityStrategy: size =>
            {
                segmentSizes.Add(size);

                return 1;
            });
            using ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: Hint);
            Assert.AreEqual(0L, accountant.Rented);
            Assert.IsEmpty(segmentSizes);

            _ = arena.Append(BuildItem(Stride, 0));

            Assert.AreEqual(1, arena.Count);
            Assert.AreEqual(1L, accountant.Rented);
            Assert.HasCount(1, segmentSizes);
            Assert.AreEqual(65_536 * Stride, segmentSizes[0]);
        }

        Assert.AreEqual(1L, accountant.Rented);
        Assert.AreEqual(1L, accountant.Returned);
    }


    /// <summary>Every append index returns the original memory view across a block boundary.</summary>
    [TestMethod]
    public void ItemAtReturnsAppendMemoryAcrossABlockBoundary()
    {
        const int Stride = 4;
        const int Items = 65_540;
        using ReconciliationItemArena arena = new(Stride, BaseMemoryPool.Shared);
        ReadOnlyMemory<byte>[] appended = new ReadOnlyMemory<byte>[Items];
        for(int index = 0; index < Items; index++)
        {
            appended[index] = arena.Append(BuildItem(Stride, index));
        }

        for(int index = 0; index < Items; index++)
        {
            Assert.AreEqual(appended[index], arena.ItemAt(index));
        }
    }


    /// <summary>The next append index is outside the readable range, including for an empty arena.</summary>
    [TestMethod]
    public void ItemAtRejectsCount()
    {
        using ReconciliationItemArena arena = new(4, BaseMemoryPool.Shared);
        ArgumentOutOfRangeException empty = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => arena.ItemAt(arena.Count));
        Assert.AreEqual("index", empty.ParamName);

        _ = arena.Append(new byte[4]);

        ArgumentOutOfRangeException populated = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => arena.ItemAt(arena.Count));
        Assert.AreEqual("index", populated.ParamName);
    }


    /// <summary>A negative append index is rejected with its parameter name.</summary>
    [TestMethod]
    public void ItemAtRejectsNegativeIndex()
    {
        using ReconciliationItemArena arena = new(4, BaseMemoryPool.Shared);
        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => arena.ItemAt(-1));
        Assert.AreEqual("index", exception.ParamName);
    }


    /// <summary>Disposal prevents index lookup before any released block can be accessed.</summary>
    [TestMethod]
    public void ItemAtRejectsUseAfterDispose()
    {
        using ReconciliationItemArena arena = new(4, BaseMemoryPool.Shared);
        _ = arena.Append(new byte[4]);
        arena.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => arena.ItemAt(0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => arena.ItemAt(-1));
        Assert.ThrowsExactly<ObjectDisposedException>(() => arena.ItemAt(arena.Count));
    }


    /// <summary>Pins that a null pool is rejected by its own null guard at construction, not a later NullReferenceException from the first use of the unset pool.</summary>
    [TestMethod]
    public void ConstructionRejectsANullPool()
    {
        ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new ReconciliationItemArena(8, null!));
        Assert.AreEqual("pool", ex.ParamName);
    }
}
