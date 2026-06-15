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
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(1025));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(8, null, -1));
    }


    [TestMethod]
    public void ConstructionRejectsHintsThatWouldOverflowTheBacking()
    {
        //A hint near int's range would, in the unguarded power-of-two rounding, double past 2^30 into the sign
        //flip and then to zero and spin forever; the 64-bit rounding instead reaches 2^31 and the widened bound
        //check rejects it with the hint's own name. The arena validates eagerly at construction WITHOUT renting,
        //so the rejection is allocation-free and its lazy-first-block property still holds.
        ArgumentOutOfRangeException farPastInt = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(8, null, itemCapacityHint: int.MaxValue));
        Assert.AreEqual("itemCapacityHint", farPastInt.ParamName);

        //A hint far below int's range is still rejected when its power-of-two first block times the stride
        //overflows: 2^21 + 1 rounds up to 2^22, and at the maximum stride of 1024 bytes that block is 2^32 bytes,
        //past the maximum array length. The widened product fires here rather than wrapping the int rental size.
        ArgumentOutOfRangeException widthOverflow = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationItemArena(1024, null, itemCapacityHint: (1 << 21) + 1));
        Assert.AreEqual("itemCapacityHint", widthOverflow.ParamName);

        //A modest hint at a small stride still constructs and stays empty until the first append: the rejection is
        //a ceiling on pathological hints, not a regression of the ordinary pre-sizing path.
        using ReconciliationItemArena ok = new(8, null, itemCapacityHint: 1000);
        Assert.AreEqual(0, ok.Count);
    }


    [TestMethod]
    public void AppendReturnsExactWidthSlicesThatRoundTrip()
    {
        const int Stride = 8;
        using ReconciliationItemArena arena = new(Stride);
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
        using ReconciliationItemArena arena = new(Stride, null, itemCapacityHint: 0);

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
        ReconciliationItemArena arena = new(8);
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


    //Builds a deterministic, index-derived item of the given width without System.Random (CA5394): a simple
    //index-and-position pattern that is distinct per index and fills the whole stride.
    private static byte[] BuildItem(int stride, int index)
    {
        byte[] item = new byte[stride];
        for(int b = 0; b < stride; b++)
        {
            item[b] = (byte)((index * 31) + b);
        }

        return item;
    }


}
