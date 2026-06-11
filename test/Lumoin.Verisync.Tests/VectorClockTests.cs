using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class VectorClockTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoEntries()
    {
        Assert.AreEqual(0, VectorClock.Empty[R1]);
        Assert.AreEqual(0, VectorClock.Empty[R2]);
    }


    [TestMethod]
    public void IndexerReturnsZeroForUnknownReplica()
    {
        VectorClock clock = VectorClock.Empty.Increment(R1);

        Assert.AreEqual(0, clock[R2]);
    }


    [TestMethod]
    public void IncrementAddsOneToCounter()
    {
        VectorClock once = VectorClock.Empty.Increment(R1);
        VectorClock twice = once.Increment(R1);

        Assert.AreEqual(1, once[R1]);
        Assert.AreEqual(2, twice[R1]);
    }


    [TestMethod]
    public void IncrementPastAllAdvancesPastTheLargestEntry()
    {
        VectorClock clock = VectorClock.Empty.Increment(R1).Increment(R1).Increment(R1);

        VectorClock advanced = clock.IncrementPastAll(R2);

        Assert.AreEqual(4, advanced[R2]);
        Assert.AreEqual(3, advanced[R1]);
    }


    [TestMethod]
    public void IncrementPastAllOnEmptyStartsAtOne()
    {
        Assert.AreEqual(1, VectorClock.Empty.IncrementPastAll(R1)[R1]);
    }


    [TestMethod]
    public void IncrementDoesNotMutateOriginal()
    {
        VectorClock original = VectorClock.Empty;
        _ = original.Increment(R1);

        Assert.AreEqual(0, original[R1]);
    }


    [TestMethod]
    public void MergeIsElementWiseMax()
    {
        VectorClock a = VectorClock.Empty.Increment(R1).Increment(R1).Increment(R2);
        VectorClock b = VectorClock.Empty.Increment(R1).Increment(R2).Increment(R2).Increment(R2);

        VectorClock merged = a.Merge(b);

        Assert.AreEqual(2, merged[R1]);
        Assert.AreEqual(3, merged[R2]);
    }


    [TestMethod]
    public void MergeDoesNotMutateOperands()
    {
        VectorClock a = VectorClock.Empty.Increment(R1);
        VectorClock b = VectorClock.Empty.Increment(R2);

        _ = a.Merge(b);

        Assert.AreEqual(1, a[R1]);
        Assert.AreEqual(0, a[R2]);
        Assert.AreEqual(0, b[R1]);
        Assert.AreEqual(1, b[R2]);
    }


    [TestMethod]
    public void CompareReturnsEqualForIdenticalClocks()
    {
        VectorClock a = VectorClock.Empty.Increment(R1);
        VectorClock b = VectorClock.Empty.Increment(R1);

        Assert.AreEqual(Causality.Equal, a.Compare(b));
    }


    [TestMethod]
    public void CompareReturnsBeforeForStrictlyLess()
    {
        VectorClock a = VectorClock.Empty.Increment(R1);
        VectorClock b = a.Increment(R1);

        Assert.AreEqual(Causality.Before, a.Compare(b));
    }


    [TestMethod]
    public void CompareReturnsAfterForStrictlyGreater()
    {
        VectorClock a = VectorClock.Empty.Increment(R1);
        VectorClock b = a.Increment(R1);

        Assert.AreEqual(Causality.After, b.Compare(a));
    }


    [TestMethod]
    public void CompareReturnsConcurrentWhenNeitherDominates()
    {
        VectorClock a = VectorClock.Empty.Increment(R1);
        VectorClock b = VectorClock.Empty.Increment(R2);

        Assert.AreEqual(Causality.Concurrent, a.Compare(b));
    }


    [TestMethod]
    public void IncrementThrowsOnOverflowAndLeavesOriginalUnchanged()
    {
        VectorClock atMax = VectorClock.FromState(new VectorClockState([new ReplicaCounterEntry(Bytes(R1), int.MaxValue)]));

        Assert.ThrowsExactly<OverflowException>(() => atMax.Increment(R1));

        Assert.AreEqual(int.MaxValue, atMax[R1]);
    }


    [TestMethod]
    public void IncrementPastAllThrowsOnOverflowAndLeavesOriginalUnchanged()
    {
        VectorClock atMax = VectorClock.FromState(new VectorClockState([new ReplicaCounterEntry(Bytes(R1), int.MaxValue)]));

        Assert.ThrowsExactly<OverflowException>(() => atMax.IncrementPastAll(R2));

        Assert.AreEqual(int.MaxValue, atMax[R1]);
        Assert.AreEqual(0, atMax[R2]);
    }


    [TestMethod]
    public void FromStateRejectsNegativeCount()
    {
        var state = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), -1)]);

        Assert.ThrowsExactly<ArgumentException>(() => VectorClock.FromState(state));
    }


    [TestMethod]
    public void FromStateFiltersZeroCountEntry()
    {
        //A stored zero must be dropped so the result equals one built without the entry and hashes identically.
        var withZero = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2), new ReplicaCounterEntry(Bytes(R2), 0)]);
        VectorClock reconstructed = VectorClock.FromState(withZero);
        VectorClock expected = VectorClock.Empty.Increment(R1).Increment(R1);

        Assert.AreEqual(expected, reconstructed);
        Assert.AreEqual(expected.GetHashCode(), reconstructed.GetHashCode());
    }


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
