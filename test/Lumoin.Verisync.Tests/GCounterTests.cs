using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class GCounterTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasZeroValue()
    {
        Assert.AreEqual(0, GCounter.Empty.Value);
    }


    [TestMethod]
    public void IncrementAddsOneToValue()
    {
        GCounter counter = GCounter.Empty.Increment(R1);

        Assert.AreEqual(1, counter.Value);
    }


    [TestMethod]
    public void IncrementByAmountAddsThatAmount()
    {
        GCounter counter = GCounter.Empty.Increment(R1, 5);

        Assert.AreEqual(5, counter.Value);
    }


    [TestMethod]
    public void IncrementRejectsZeroAmount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GCounter.Empty.Increment(R1, 0));
    }


    [TestMethod]
    public void IncrementRejectsNegativeAmount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => GCounter.Empty.Increment(R1, -1));
    }


    [TestMethod]
    public void IncrementDoesNotMutateOriginal()
    {
        GCounter original = GCounter.Empty;
        _ = original.Increment(R1);

        Assert.AreEqual(0, original.Value);
    }


    [TestMethod]
    public void ValueIsSumOfCounters()
    {
        GCounter counter = GCounter.Empty.Increment(R1, 2).Increment(R2, 3);

        Assert.AreEqual(5, counter.Value);
    }


    [TestMethod]
    public void MergeIsElementWiseMax()
    {
        GCounter a = GCounter.Empty.Increment(R1, 2).Increment(R2, 1);
        GCounter b = GCounter.Empty.Increment(R1, 1).Increment(R2, 3);

        GCounter merged = a.Merge(b);

        Assert.AreEqual(5, merged.Value);
    }


    [TestMethod]
    public void MergeDoesNotMutateOperands()
    {
        GCounter a = GCounter.Empty.Increment(R1, 2);
        GCounter b = GCounter.Empty.Increment(R2, 3);

        _ = a.Merge(b);

        Assert.AreEqual(2, a.Value);
        Assert.AreEqual(3, b.Value);
    }


    [TestMethod]
    public void EqualityHoldsForSameIncrements()
    {
        GCounter a = GCounter.Empty.Increment(R1, 2).Increment(R2, 1);
        GCounter b = GCounter.Empty.Increment(R2, 1).Increment(R1, 2);

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void EqualityFailsForDifferentCounters()
    {
        GCounter a = GCounter.Empty.Increment(R1, 2);
        GCounter b = GCounter.Empty.Increment(R1, 3);

        Assert.AreNotEqual(a, b);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
