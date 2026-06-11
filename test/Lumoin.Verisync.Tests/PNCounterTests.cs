using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class PNCounterTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasZeroValue()
    {
        Assert.AreEqual(0, PNCounter.Empty.Value);
    }


    [TestMethod]
    public void IncrementAddsToValue()
    {
        Assert.AreEqual(1, PNCounter.Empty.Increment(R1).Value);
    }


    [TestMethod]
    public void DecrementSubtractsFromValue()
    {
        Assert.AreEqual(-1, PNCounter.Empty.Decrement(R1).Value);
    }


    [TestMethod]
    public void IncrementThenDecrementNets()
    {
        PNCounter counter = PNCounter.Empty.Increment(R1, 5).Decrement(R1, 2);

        Assert.AreEqual(3, counter.Value);
    }


    [TestMethod]
    public void ValueCanBeNegative()
    {
        PNCounter counter = PNCounter.Empty.Decrement(R1, 3);

        Assert.AreEqual(-3, counter.Value);
    }


    [TestMethod]
    public void IncrementRejectsZeroAmount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PNCounter.Empty.Increment(R1, 0));
    }


    [TestMethod]
    public void IncrementRejectsNegativeAmount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PNCounter.Empty.Increment(R1, -1));
    }


    [TestMethod]
    public void DecrementRejectsZeroAmount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PNCounter.Empty.Decrement(R1, 0));
    }


    [TestMethod]
    public void DecrementRejectsNegativeAmount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PNCounter.Empty.Decrement(R1, -1));
    }


    [TestMethod]
    public void IncrementDoesNotMutateOriginal()
    {
        PNCounter original = PNCounter.Empty;
        _ = original.Increment(R1);

        Assert.AreEqual(0, original.Value);
    }


    [TestMethod]
    public void DecrementDoesNotMutateOriginal()
    {
        PNCounter original = PNCounter.Empty;
        _ = original.Decrement(R1);

        Assert.AreEqual(0, original.Value);
    }


    [TestMethod]
    public void MergeCombinesBothHalves()
    {
        PNCounter a = PNCounter.Empty.Increment(R1, 3);
        PNCounter b = PNCounter.Empty.Decrement(R2, 1);

        PNCounter merged = a.Merge(b);

        Assert.AreEqual(2, merged.Value);
    }


    [TestMethod]
    public void MergeTakesElementWiseMaxPerReplica()
    {
        PNCounter a = PNCounter.Empty.Increment(R1, 3);
        PNCounter b = PNCounter.Empty.Increment(R1, 5);

        PNCounter merged = a.Merge(b);

        Assert.AreEqual(5, merged.Value);
    }


    [TestMethod]
    public void MergeDoesNotMutateOperands()
    {
        PNCounter a = PNCounter.Empty.Increment(R1, 3);
        PNCounter b = PNCounter.Empty.Decrement(R2, 1);

        _ = a.Merge(b);

        Assert.AreEqual(3, a.Value);
        Assert.AreEqual(-1, b.Value);
    }


    [TestMethod]
    public void EqualityHoldsForSameOperations()
    {
        PNCounter a = PNCounter.Empty.Increment(R1, 2).Decrement(R2, 1);
        PNCounter b = PNCounter.Empty.Decrement(R2, 1).Increment(R1, 2);

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void EqualityFailsForDifferentCounters()
    {
        PNCounter a = PNCounter.Empty.Increment(R1, 2);
        PNCounter b = PNCounter.Empty.Decrement(R1, 2);

        Assert.AreNotEqual(a, b);
    }


    [TestMethod]
    public void IncrementAndDecrementOfSameMagnitudeAreDistinctStates()
    {
        PNCounter incThenDec = PNCounter.Empty.Increment(R1, 1).Decrement(R1, 1);

        //Value nets to zero, but the state is not the empty counter: both halves carry history.
        Assert.AreEqual(0, incThenDec.Value);
        Assert.AreNotEqual(PNCounter.Empty, incThenDec);
    }


    [TestMethod]
    public void IncrementInheritsGCounterOverflowGuard()
    {
        //PNCounter wraps two GCounters, so the checked-arithmetic guard carries over to its increment half.
        PNCounter atMax = PNCounter.Empty.Increment(R1, int.MaxValue);

        Assert.ThrowsExactly<OverflowException>(() => atMax.Increment(R1, 1));

        Assert.AreEqual(int.MaxValue, atMax.Value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
