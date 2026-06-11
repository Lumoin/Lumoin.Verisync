using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class RgaTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoValues()
    {
        Assert.HasCount(0, Rga<string>.Empty.Values);
        Assert.AreEqual(0, Rga<string>.Empty.Count);
    }


    [TestMethod]
    public void InsertAtHeadAddsValue()
    {
        (Rga<string> rga, _) = Rga<string>.Empty.InsertAtHead("A", R1);

        string[] expected = ["A"];
        CollectionAssert.AreEqual(expected, rga.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterPlacesValueAfterPredecessor()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, _) = withA.InsertAfter(idA, "B", R1);

        string[] expected = ["A", "B"];
        CollectionAssert.AreEqual(expected, withB.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterRejectsUnknownPredecessor()
    {
        Dot foreign = new(R2, 5);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.Empty.InsertAfter(foreign, "X", R1));
    }


    [TestMethod]
    public void RemoveTombstonesElement()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        Rga<string> removed = withA.Remove(idA);

        Assert.HasCount(0, removed.Values);
        Assert.AreEqual(0, removed.Count);
    }


    [TestMethod]
    public void RemoveRejectsNullId()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Rga<string>.Empty.Remove(null!));
    }


    [TestMethod]
    public void InsertDoesNotMutateOriginal()
    {
        Rga<string> original = Rga<string>.Empty;
        _ = original.InsertAtHead("A", R1);

        Assert.HasCount(0, original.Values);
    }


    [TestMethod]
    public void RemoveDoesNotMutateOriginal()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        _ = withA.Remove(idA);

        string[] expected = ["A"];
        CollectionAssert.AreEqual(expected, withA.Values.ToArray());
    }


    [TestMethod]
    public void ConcurrentInsertsOrderByIdDescending()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, _) = withA.InsertAfter(idA, "B", R1);
        (Rga<string> withC, _) = withA.InsertAfter(idA, "C", R2);

        Rga<string> merged = withB.Merge(withC);

        //Truly concurrent inserts over the same observed state mint equal counters (both 2), so the
        //replica id breaks the tie deterministically: R2 orders above R1.
        string[] expected = ["A", "C", "B"];
        CollectionAssert.AreEqual(expected, merged.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterPlacesValueImmediatelyAfterPredecessorAcrossReplicas()
    {
        //R1 builds A then B after A. R2 merges that state and inserts C after A: C's identity must
        //dominate B's, or C would land behind B's whole subtree instead of immediately after A.
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, _) = withA.InsertAfter(idA, "B", R1);

        (Rga<string> merged, _) = Rga<string>.Empty.Merge(withB).InsertAfter(idA, "C", R2);

        string[] expected = ["A", "C", "B"];
        CollectionAssert.AreEqual(expected, merged.Values.ToArray());
        CollectionAssert.AreEqual(expected, withB.Merge(merged).Values.ToArray());
    }


    [TestMethod]
    public void MergeIsOrderIndependent()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, _) = withA.InsertAfter(idA, "B", R1);
        (Rga<string> withC, _) = withA.InsertAfter(idA, "C", R2);

        CollectionAssert.AreEqual(withB.Merge(withC).Values.ToArray(), withC.Merge(withB).Values.ToArray());
    }


    [TestMethod]
    public void TombstonePreservesOrderForLaterInserts()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, Dot idB) = withA.InsertAfter(idA, "B", R1);
        (Rga<string> withC, _) = withB.InsertAfter(idB, "C", R1);
        Rga<string> removed = withC.Remove(idB);
        (Rga<string> withD, _) = removed.InsertAfter(idB, "D", R1);

        //B is hidden but retained for ordering; D inserts after it with the higher counter.
        string[] expected = ["A", "D", "C"];
        CollectionAssert.AreEqual(expected, withD.Values.ToArray());
    }


    [TestMethod]
    public void EqualityHoldsForSameState()
    {
        (Rga<string> a, _) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> b, _) = Rga<string>.Empty.InsertAtHead("A", R1);

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void FromStateRejectsMissingPredecessor()
    {
        //A vertex points at a predecessor that is not itself a vertex.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), Dot(R2, 9), "A");
        var state = new RgaState<string>(context, [vertex], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsPredecessorCycle()
    {
        //Two vertices each name the other as predecessor: the order traversal never reaches a head.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var first = new RgaVertexEntry<string>(Dot(R1, 1), Dot(R1, 2), "A");
        var second = new RgaVertexEntry<string>(Dot(R1, 2), Dot(R1, 1), "B");
        var state = new RgaState<string>(context, [first, second], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateAcceptsUnknownTombstoneHarmlessly()
    {
        //A remove can be serialized separately from its vertex, so a tombstone for an absent dot is accepted
        //and affects neither Values nor Count.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var state = new RgaState<string>(context, [vertex], [Dot(R2, 7)]);

        Rga<string> reconstructed = Rga<string>.FromState(state);

        Assert.AreEqual(1, reconstructed.Count);
        string[] expected = ["A"];
        CollectionAssert.AreEqual(expected, reconstructed.Values.ToArray());
    }


    private static DotState Dot(ReplicaId replica, int counter) => new(Bytes(replica), counter);


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
