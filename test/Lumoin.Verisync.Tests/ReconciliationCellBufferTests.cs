using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Focused deterministic coverage of the flat cell buffer in isolation: construction range validation,
/// the append/access/zero-init contract, per-cell independence and exact span widths, growth integrity
/// across the doubling resize past the initial capacity, the additive span constructor of
/// <see cref="ReconciliationSymbol"/> against the existing memory constructor, disposal semantics, the
/// pooled growth zero-leak invariant, and the clear-on-rent soundness contract. The encoder and decoder
/// behaviour that consumes the buffer is covered by the existing suite, which is the oracle.
/// </summary>
/// <remarks>
/// The pooled-rental tests observe the library's process-global rental instruments, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> to keep their measurement totals free of rentals emitted by other
/// pool-using tests running concurrently — the same isolation the other metric-observing suites use.
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class ReconciliationCellBufferTests
{
    [TestMethod]
    public void ConstructionRejectsWidthsOutsideTheirRanges()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCellBuffer(0, 8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCellBuffer(1025, 8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCellBuffer(8, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCellBuffer(8, 9));
    }


    [TestMethod]
    public void ConstructionRejectsHintsThatWouldOverflowTheBacking()
    {
        //A hint near int's range would, in the unguarded power-of-two rounding, double past 2^30 into the sign
        //flip and then to zero and spin forever; the 64-bit rounding instead reaches 2^31 and the widened bound
        //check below rejects it with the hint's own name, allocation-free, proving the rounding can no longer hang.
        ArgumentOutOfRangeException farPastInt = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCellBuffer(1024, 8, null, cellCapacityHint: int.MaxValue));
        Assert.AreEqual("cellCapacityHint", farPastInt.ParamName);

        //A hint far below int's range is still rejected when its power-of-two capacity times the field width
        //overflows: 2^21 + 1 rounds up to 2^22, and at the maximum sum width of 1024 bytes that backing is 2^32
        //bytes, past the maximum array length. The check uses a widened product, so it fires here at construction
        //rather than wrapping the int rental size to a too-small buffer the later slices would overrun.
        ArgumentOutOfRangeException widthOverflow = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationCellBuffer(1024, 8, null, cellCapacityHint: (1 << 21) + 1));
        Assert.AreEqual("cellCapacityHint", widthOverflow.ParamName);

        //A modest hint at small widths still constructs and stays empty: the rejection is a ceiling on pathological
        //hints, not a regression of the ordinary pre-sizing path.
        using ReconciliationCellBuffer ok = new(8, 4, null, cellCapacityHint: 1000);
        Assert.AreEqual(0, ok.Count);
    }


    [TestMethod]
    public void AppendReturnsConsecutiveIndicesAndYieldsZeroedCells()
    {
        using ReconciliationCellBuffer buffer = new(8, 4);
        Assert.AreEqual(0, buffer.Count);

        for(int expected = 0; expected < 5; expected++)
        {
            int index = buffer.Append();
            Assert.AreEqual(expected, index);
            Assert.AreEqual(expected + 1, buffer.Count);

            //A freshly appended cell is guaranteed all zero across both fields.
            Span<byte> sum = buffer.SumAt(index);
            for(int b = 0; b < sum.Length; b++)
            {
                Assert.AreEqual((byte)0, sum[b]);
            }

            Span<byte> checksum = buffer.ChecksumAt(index);
            for(int b = 0; b < checksum.Length; b++)
            {
                Assert.AreEqual((byte)0, checksum[b]);
            }
        }

        //Access outside the half-open range of populated cells is rejected on both accessors.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.SumAt(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.SumAt(buffer.Count));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.ChecksumAt(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => buffer.ChecksumAt(buffer.Count));
    }


    [TestMethod]
    public void CellsAreIndependentAndSpansHaveExactWidths()
    {
        const int SumWidth = 8;
        const int ChecksumWidth = 4;
        using ReconciliationCellBuffer buffer = new(SumWidth, ChecksumWidth);
        for(int n = 0; n < 5; n++)
        {
            _ = buffer.Append();
        }

        Assert.AreEqual(SumWidth, buffer.SumWidth);
        Assert.AreEqual(ChecksumWidth, buffer.ChecksumWidth);

        //Each accessor returns a span of exactly the configured width.
        Assert.HasCount(SumWidth, buffer.SumAt(2).ToArray());
        Assert.HasCount(ChecksumWidth, buffer.ChecksumAt(2).ToArray());

        //Write a distinct, non-zero pattern into the sum of one cell only.
        const int Target = 2;
        Span<byte> targetSum = buffer.SumAt(Target);
        for(int b = 0; b < targetSum.Length; b++)
        {
            targetSum[b] = (byte)(0x80 + b);
        }

        //Every other cell's sum and every cell's checksum remain untouched zero.
        for(int i = 0; i < buffer.Count; i++)
        {
            if(i != Target)
            {
                Span<byte> otherSum = buffer.SumAt(i);
                for(int b = 0; b < otherSum.Length; b++)
                {
                    Assert.AreEqual((byte)0, otherSum[b]);
                }
            }

            Span<byte> checksum = buffer.ChecksumAt(i);
            for(int b = 0; b < checksum.Length; b++)
            {
                Assert.AreEqual((byte)0, checksum[b]);
            }
        }
    }


    [TestMethod]
    public void GrowthPreservesPriorCellContents()
    {
        const int SumWidth = 8;
        const int ChecksumWidth = 4;
        const int Cells = 1000;
        using ReconciliationCellBuffer buffer = new(SumWidth, ChecksumWidth);

        //Append every cell first so that no held span survives an intervening grow.
        for(int k = 0; k < Cells; k++)
        {
            int index = buffer.Append();
            Assert.AreEqual(k, index);
        }

        Assert.AreEqual(Cells, buffer.Count);

        //Second pass: write an index-derived pattern into each cell, fetching spans fresh after all appends.
        for(int k = 0; k < Cells; k++)
        {
            Span<byte> sum = buffer.SumAt(k);
            for(int b = 0; b < sum.Length; b++)
            {
                sum[b] = (byte)((k * 31) + b);
            }

            Span<byte> checksum = buffer.ChecksumAt(k);
            for(int b = 0; b < checksum.Length; b++)
            {
                checksum[b] = (byte)((k * 17) + b + 7);
            }
        }

        //Third pass: every cell reads back its own pattern, proving the doubling grow kept prior contents.
        for(int k = 0; k < Cells; k++)
        {
            Span<byte> sum = buffer.SumAt(k);
            for(int b = 0; b < sum.Length; b++)
            {
                Assert.AreEqual((byte)((k * 31) + b), sum[b]);
            }

            Span<byte> checksum = buffer.ChecksumAt(k);
            for(int b = 0; b < checksum.Length; b++)
            {
                Assert.AreEqual((byte)((k * 17) + b + 7), checksum[b]);
            }
        }
    }


    [TestMethod]
    public void SymbolSpanConstructorMatchesTheMemoryConstructor()
    {
        byte[] sum = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        byte[] checksum = [0x11, 0x22, 0x33, 0x44];

        ReconciliationSymbol fromMemory = new(sum, checksum);
        ReconciliationSymbol fromSpan = new(sum.AsSpan(), checksum.AsSpan());

        //Records compare by value through the type's custom byte-sequence equality.
        Assert.AreEqual(fromMemory, fromSpan);

        //The span constructor enforces the same validation as the memory constructor.
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbol(ReadOnlySpan<byte>.Empty, checksum.AsSpan()));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbol(sum.AsSpan(), new byte[9].AsSpan()));
    }


    [TestMethod]
    public void DisposalThrowsOnAccessAndIsIdempotent()
    {
        ReconciliationCellBuffer buffer = new(8, 4);
        _ = buffer.Append();

        buffer.Dispose();

        //After disposal every public mutator and reader rejects use rather than touching a released backing.
        Assert.ThrowsExactly<ObjectDisposedException>(() => buffer.Append());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = buffer.SumAt(0).Length);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = buffer.ChecksumAt(0).Length);

        //A second dispose is a silent no-op; the guarded flag absorbs it without a double-return to a pool.
        buffer.Dispose();
    }


    [TestMethod]
    public void PooledGrowthPreservesContentsAndLeavesNoActiveRentals()
    {
        const int SumWidth = 8;
        const int ChecksumWidth = 4;
        const int Cells = 1000;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            //A zero hint pins the small initial capacity, so a thousand cells force several doubling grows on
            //the pooled path, each renting a larger pair of backings and disposing the prior pair.
            ReconciliationCellBuffer buffer = new(SumWidth, ChecksumWidth, pool, cellCapacityHint: 0);

            //Append every cell first so that no held span survives an intervening grow.
            for(int k = 0; k < Cells; k++)
            {
                int index = buffer.Append();
                Assert.AreEqual(k, index);
            }

            Assert.AreEqual(Cells, buffer.Count);

            //Write an index-derived pattern into each cell, fetching spans fresh after all appends.
            for(int k = 0; k < Cells; k++)
            {
                Span<byte> sum = buffer.SumAt(k);
                for(int b = 0; b < sum.Length; b++)
                {
                    sum[b] = (byte)((k * 31) + b);
                }

                Span<byte> checksum = buffer.ChecksumAt(k);
                for(int b = 0; b < checksum.Length; b++)
                {
                    checksum[b] = (byte)((k * 17) + b + 7);
                }
            }

            //Every cell reads back its own pattern, proving the doubling grow copied prior contents intact.
            for(int k = 0; k < Cells; k++)
            {
                Span<byte> sum = buffer.SumAt(k);
                for(int b = 0; b < sum.Length; b++)
                {
                    Assert.AreEqual((byte)((k * 31) + b), sum[b]);
                }

                Span<byte> checksum = buffer.ChecksumAt(k);
                for(int b = 0; b < checksum.Length; b++)
                {
                    Assert.AreEqual((byte)((k * 17) + b + 7), checksum[b]);
                }
            }

            //Dispose the buffer before reading the totals so every return measurement has flushed to the listener.
            buffer.Dispose();
        }

        //Disposal returns every grown pair, so the net active gauge balances to zero and the buffer rented at
        //least its initial pair plus a pair per grow, returning exactly as many as it took.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public void FreshCellsAreZeroWhenRentingADirtyRecycledSegment()
    {
        const int SumWidth = 8;
        const int ChecksumWidth = 4;
        const int InitialCapacity = 4;

        //A pool that hands out genuinely dirty (0xFF-filled) segments on every rent, independent of any real
        //pool's clear policy. The cell buffer must zero each rented segment itself — the all-zero-fresh-cell
        //contract the encoder and decoder fold relies on — so a freshly appended cell must read all-zero even
        //though the backing it was rented over was non-zero. (A real pool that clears recycled memory, as
        //BaseMemoryPool does on return, would mask a missing clear here; the dirty stub does not.)
        using DirtyMemoryPool pool = new();

        using ReconciliationCellBuffer buffer = new(SumWidth, ChecksumWidth, pool, cellCapacityHint: 0);

        for(int k = 0; k < InitialCapacity; k++)
        {
            int index = buffer.Append();

            //Every freshly appended cell must read all-zero across both fields, proving the buffer cleared the
            //dirty rented segment on rent. If the initial clear were missing the cells would carry stale 0xFF.
            Span<byte> sum = buffer.SumAt(index);
            for(int b = 0; b < sum.Length; b++)
            {
                Assert.AreEqual((byte)0, sum[b]);
            }

            Span<byte> checksum = buffer.ChecksumAt(index);
            for(int b = 0; b < checksum.Length; b++)
            {
                Assert.AreEqual((byte)0, checksum[b]);
            }
        }
    }


}
