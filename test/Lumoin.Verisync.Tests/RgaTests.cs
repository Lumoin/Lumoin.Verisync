using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

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

        //After A: B has the higher counter (2) than C (1), so B sorts first.
        string[] expected = ["A", "B", "C"];
        CollectionAssert.AreEqual(expected, merged.Values.ToArray());
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


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
