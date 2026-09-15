using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Focused deterministic coverage of the append-only item arena in isolation: construction range validation,
/// the exact-width round-trip append contract and its wrong-length rejection, the central never-relocate
/// invariant that an early slice still reads its original bytes after many block grows, disposal semantics,
/// the pooled growth zero-leak balance, and the full-overwrite-on-append soundness contract that needs no
/// clear-on-rent. The encoder and decoder behaviour that consumes the arena is covered by the existing suite,
/// which is the oracle.
/// </summary>
/// <remarks>
/// The pooled-rental test observes the library's process-global rental instruments, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> to keep their measurement totals free of rentals emitted by other
/// pool-using tests running concurrently — the same isolation the other metric-observing suites use.
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


    [TestMethod]
    public void ConstructionRejectsHintsThatWouldOverflowTheBacking()
    {
        //A hint near int's range would, in the unguarded power-of-two rounding, double past 2^30 into the sign
        //flip and then to zero and spin forever; the 64-bit rounding instead reaches 2^31 and the widened bound
        //check rejects it with the hint's own name. The arena validates eagerly at construction WITHOUT renting,
        //so the rejection is allocation-free and its lazy-first-block property still holds.
        ArgumentOutOfRangeException farPastInt = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(8, BaseMemoryPool.Shared, itemCapacityHint: int.MaxValue));
        Assert.AreEqual("itemCapacityHint", farPastInt.ParamName);

        //A hint far below int's range is still rejected when its power-of-two first block times the stride
        //overflows: 2^21 + 1 rounds up to 2^22, and at the maximum stride of 1024 bytes that block is 2^32 bytes,
        //past the maximum array length. The widened product fires here rather than wrapping the int rental size.
        ArgumentOutOfRangeException widthOverflow = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(1024, BaseMemoryPool.Shared, itemCapacityHint: (1 << 21) + 1));
        Assert.AreEqual("itemCapacityHint", widthOverflow.ParamName);

        //A modest hint at a small stride still constructs and stays empty until the first append: the rejection is
        //a ceiling on pathological hints, not a regression of the ordinary pre-sizing path.
        using ReconciliationItemArena ok = new(8, BaseMemoryPool.Shared, itemCapacityHint: 1000);
        Assert.AreEqual(0, ok.Count);
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


    [TestMethod]
    public void AppendedSlicesNeverMoveAcrossGrowth()
    {
        const int Stride = 8;
        const int Items = 1000;

        //A zero hint pins the smallest first block at four items, so a thousand appends force several block
        //grows; if any grow relocated stored bytes the held early slices below would read moved-or-recycled
        //bytes.
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

        //Drive the arena well past several block grows.
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


    [TestMethod]
    public void PooledGrowthLeavesNoActiveRentals()
    {
        const int Stride = 8;
        const int Items = 1000;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            //A zero hint pins the small initial block, so a thousand items force several doubling block rents on
            //the pooled path, each renting an additional block and never relocating a prior one.
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

        //Disposal returns every grown block, so the net active gauge balances to zero and the arena rented at
        //least its initial block plus a block per grow, returning exactly as many as it took.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
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


    /// <summary>Pins that only the very first grow sizes from the hint floor; every later grow doubles the running total capacity instead of repeating the small first-block size.</summary>
    [TestMethod]
    public void GrowthDoublesTheRunningCapacityAfterTheFirstBlock()
    {
        const int Stride = 8;
        const int Items = 1000;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();
            ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

            for(int n = 0; n < Items; n++)
            {
                _ = arena.Append(BuildItem(Stride, n));
            }

            Assert.AreEqual(Items, arena.Count);
            arena.Dispose();
        }

        //A zero hint pins the first block at four items; doubling the running total thereafter (4, 8, 16, ..., 1024)
        //reaches 1000 items in nine block rents. A broken grow that re-sized every later block from the hint floor
        //instead of doubling the running total would need roughly Items / 4 rents to hold the same 1000 items.
        Assert.IsLessThan(20L, accountant.Rented);
    }


    /// <summary>Pins that the zero-hint first block holds exactly the documented four-item floor, not one power of two further.</summary>
    [TestMethod]
    public void ZeroHintFirstBlockHoldsExactlyFourItemsBeforeGrowing()
    {
        const int Stride = 8;
        const int FloorItems = 4;

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

            //A fifth append overflows the four-item first block and forces a second rent; a first block rounded up
            //to eight (the next power of two past four) would still have room and rent only once.
            _ = arena.Append(BuildItem(Stride, FloorItems));

            Assert.AreEqual(2L, accountant.Rented);

            arena.Dispose();
        }
    }


    /// <summary>Pins that Dispose clears each block's whole logical region in defence in depth, even when the injected pool's own rental does not clear on return.</summary>
    [TestMethod]
    public void DisposalClearsEachBlocksLogicalRegionEvenWhenThePoolDoesNot()
    {
        const int Stride = 8;
        using DirtyMemoryPool pool = new();
        ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: 0);

        byte[] item = new byte[Stride];
        for(int b = 0; b < Stride; b++)
        {
            item[b] = (byte)(b + 1);
        }

        ReadOnlyMemory<byte> slice = arena.Append(item);

        //Sanity: the slice reads the written, non-zero bytes before disposal.
        Assert.IsFalse(slice.Span.SequenceEqual(new byte[Stride]));

        arena.Dispose();

        //Disposal clears the block's logical region in defence in depth: the dirty pool's own owner does not clear
        //on return, so only the arena's own explicit clear could zero these bytes.
        Assert.IsTrue(slice.Span.SequenceEqual(new byte[Stride]));
    }


    /// <summary>Pins that the stride lower bound is inclusive: exactly one byte constructs without throwing.</summary>
    [TestMethod]
    public void ConstructionAcceptsTheMinimumStrideOfOneByte()
    {
        using ReconciliationItemArena arena = new(1, BaseMemoryPool.Shared);
        Assert.AreEqual(0, arena.Count);
    }


    /// <summary>Pins that a hint above the block floor pre-sizes the first block to the hint, not the floor.</summary>
    [TestMethod]
    public void ConstructionPreSizesTheFirstBlockFromAHintAboveTheFloor()
    {
        const int Stride = 8;
        const int Hint = 100;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();
            ReconciliationItemArena arena = new(Stride, pool, itemCapacityHint: Hint);

            for(int n = 0; n < Hint; n++)
            {
                _ = arena.Append(BuildItem(Stride, n));
            }

            Assert.AreEqual(Hint, arena.Count);
            arena.Dispose();
        }

        //The first block's capacity is the smallest power of two at or above the hint (128 for a hint of 100), so
        //appending exactly the hinted count fits in the single rented first block with no grow.
        Assert.AreEqual(1L, accountant.Rented);
    }


    /// <summary>Pins that a null pool is rejected by its own null guard at construction, not a later NullReferenceException from the first use of the unset pool.</summary>
    [TestMethod]
    public void ConstructionRejectsANullPool()
    {
        ArgumentNullException ex = Assert.ThrowsExactly<ArgumentNullException>(() => new ReconciliationItemArena(8, null!));
        Assert.AreEqual("pool", ex.ParamName);
    }
}
