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
        Assert.IsEmpty(Rga<string>.Empty.Values);
        Assert.AreEqual(0, Rga<string>.Empty.Count);
    }


    [TestMethod]
    public void InsertAtHeadAddsValue()
    {
        (Rga<string> rga, _) = Rga<string>.Empty.InsertAtHead("A", R1);

        string[] expected = ["A"];
        Assert.AreSequenceEqual(expected, rga.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterPlacesValueAfterPredecessor()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, _) = withA.InsertAfter(idA, "B", R1);

        string[] expected = ["A", "B"];
        Assert.AreSequenceEqual(expected, withB.Values.ToArray());
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
        Rga<string> removed = withA.Remove(idA, R1);

        Assert.IsEmpty(removed.Values);
        Assert.AreEqual(0, removed.Count);

        //The remove is a dotted event: it ticks R1's axis, so the context advances by one and the
        //remove-dot becomes stability-trackable.
        Assert.AreEqual(withA.CausalContext[R1] + 1, removed.CausalContext[R1]);
    }


    [TestMethod]
    public void RemoveRejectsNullId()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Rga<string>.Empty.Remove(null!, R1));
    }


    [TestMethod]
    public void InsertDoesNotMutateOriginal()
    {
        Rga<string> original = Rga<string>.Empty;
        _ = original.InsertAtHead("A", R1);

        Assert.IsEmpty(original.Values);
    }


    [TestMethod]
    public void RemoveDoesNotMutateOriginal()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        _ = withA.Remove(idA, R1);

        string[] expected = ["A"];
        Assert.AreSequenceEqual(expected, withA.Values.ToArray());
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
        Assert.AreSequenceEqual(expected, merged.Values.ToArray());
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
        Assert.AreSequenceEqual(expected, merged.Values.ToArray());
        Assert.AreSequenceEqual(expected, withB.Merge(merged).Values.ToArray());
    }


    [TestMethod]
    public void MergeIsOrderIndependent()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, _) = withA.InsertAfter(idA, "B", R1);
        (Rga<string> withC, _) = withA.InsertAfter(idA, "C", R2);

        Assert.AreSequenceEqual(withB.Merge(withC).Values.ToArray(), withC.Merge(withB).Values.ToArray());
    }


    [TestMethod]
    public void TombstonePreservesOrderForLaterInserts()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        (Rga<string> withB, Dot idB) = withA.InsertAfter(idA, "B", R1);
        (Rga<string> withC, _) = withB.InsertAfter(idB, "C", R1);
        Rga<string> removed = withC.Remove(idB, R1);
        (Rga<string> withD, _) = removed.InsertAfter(idB, "D", R1);

        //B is hidden but retained for ordering; D inserts after it with the higher counter.
        string[] expected = ["A", "D", "C"];
        Assert.AreSequenceEqual(expected, withD.Values.ToArray());
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
        //A remove can be serialized separately from its vertex, so a tombstone whose target is absent is
        //accepted (the orphan target is exempt from context-covers) as long as its remove-dot is covered by
        //the context. It affects neither Values nor Count.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 9)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var tombstone = new RgaTombstoneEntry(Dot(R2, 7), [Dot(R2, 9)]);
        var state = new RgaState<string>(context, [vertex], [tombstone]);

        Rga<string> reconstructed = Rga<string>.FromState(state);

        Assert.AreEqual(1, reconstructed.Count);
        string[] expected = ["A"];
        Assert.AreSequenceEqual(expected, reconstructed.Values.ToArray());
    }


    [TestMethod]
    public void FromStateRejectsDuplicateVertexId()
    {
        //Two vertices minting the same dot is a forged state: the last-wins indexer would silently drop one.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var first = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var second = new RgaVertexEntry<string>(Dot(R1, 1), null, "B");
        var state = new RgaState<string>(context, [first, second], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsDuplicateTombstoneTarget()
    {
        //Two tombstone entries for the same target cannot be merged into one map entry unambiguously.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 3)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var first = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 2)]);
        var second = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 3)]);
        var state = new RgaState<string>(context, [vertex], [first, second]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsDuplicateRemoveDotInAnEntry()
    {
        //A remove event mints one dot; the same dot appearing twice in one entry is forged.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var tombstone = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 2), Dot(R1, 2)]);
        var state = new RgaState<string>(context, [vertex], [tombstone]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsUncoveredVertexDot()
    {
        //Invariant CC: the context must cover every vertex insert-dot.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 5), null, "A");
        var state = new RgaState<string>(context, [vertex], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsUncoveredRemoveDot()
    {
        //Invariant CC: a remove-dot is never exempt from context-covers even when its target is present.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var tombstone = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 9)]);
        var state = new RgaState<string>(context, [vertex], [tombstone]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsRemoveDotCollidingWithAVertexId()
    {
        //Insert- and remove-dots are provably disjoint on an honest history; a remove-dot equal to a vertex
        //id is forged.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var first = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var second = new RgaVertexEntry<string>(Dot(R1, 2), Dot(R1, 1), "B");
        var tombstone = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 2)]);
        var state = new RgaState<string>(context, [first, second], [tombstone]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsNonPositiveCounters()
    {
        //Dots are minted from one; a zero or negative counter is not a value any replica produces.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var zeroVertex = new RgaVertexEntry<string>(Dot(R1, 0), null, "A");
        var zeroVertexState = new RgaState<string>(context, [zeroVertex], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(zeroVertexState));

        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var zeroRemoveDot = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 0)]);
        var zeroRemoveDotState = new RgaState<string>(context, [vertex], [zeroRemoveDot]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(zeroRemoveDotState));

        var zeroTarget = new RgaTombstoneEntry(Dot(R1, 0), [Dot(R2, 1)]);
        var zeroTargetContext = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 1)]);
        var zeroTargetState = new RgaState<string>(zeroTargetContext, [vertex], [zeroTarget]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(zeroTargetState));
    }


    [TestMethod]
    public void FromStateRejectsARemoveDotSharedByTwoTombstones()
    {
        //One remove event mints one dot for one target; the same dot hiding two distinct targets is forged.
        //This reaches the cross-entry guard, which the within-entry duplicate test cannot.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2), new ReplicaCounterEntry(Bytes(R2), 5)]);
        var first = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");
        var second = new RgaVertexEntry<string>(Dot(R1, 2), Dot(R1, 1), "B");
        var tombstoneA = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R2, 5)]);
        var tombstoneB = new RgaTombstoneEntry(Dot(R1, 2), [Dot(R2, 5)]);
        var state = new RgaState<string>(context, [first, second], [tombstoneA, tombstoneB]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsDefaultArraysAsAbsentFields()
    {
        //A deserializer that leaves an unset member default (the source-generated System.Text.Json path)
        //hands FromState default arrays for absent fields. An absent array is not the same statement as an
        //explicitly empty one — a legacy tombstone declares an empty remove-dot list — so each fails closed
        //instead of being reinterpreted or crashing on a Length read.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var vertex = new RgaVertexEntry<string>(Dot(R1, 1), null, "A");

        var defaultRemoveDots = new RgaTombstoneEntry(Dot(R1, 1), default);
        var defaultRemoveDotsState = new RgaState<string>(context, [vertex], [defaultRemoveDots]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(defaultRemoveDotsState));

        var defaultTombstonesState = new RgaState<string>(context, [vertex], default);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(defaultTombstonesState));

        var defaultVerticesState = new RgaState<string>(context, default, []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<string>.FromState(defaultVerticesState));
    }


    /// <summary>Equals must return false when only Vertices.Count differs; the per-entry loop only checks this instance's keys against the other, so it can never see the other's extra vertex on its own.</summary>
    [TestMethod]
    public void EqualsReturnsFalseWhenOnlyTheVertexCountDiffers()
    {
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var vertexOne = new RgaVertexEntry<int>(Dot(R1, 1), null, 1);
        var vertexTwo = new RgaVertexEntry<int>(Dot(R1, 2), Dot(R1, 1), 2);
        Rga<int> withOneVertex = Rga<int>.FromState(new RgaState<int>(context, [vertexOne], []));
        Rga<int> withTwoVertices = Rga<int>.FromState(new RgaState<int>(context, [vertexOne, vertexTwo], []));

        Assert.IsFalse(withOneVertex.Equals(withTwoVertices));
    }


    /// <summary>Removing an already-tombstoned element mints no new remove-dot and leaves the array unchanged.</summary>
    [TestMethod]
    public void RemoveIsIdempotentForAnAlreadyTombstonedElement()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        Rga<string> removedOnce = withA.Remove(idA, R1);
        Rga<string> removedTwice = removedOnce.Remove(idA, R1);

        Assert.AreEqual(removedOnce, removedTwice);
        Assert.AreEqual(removedOnce.CausalContext[R1], removedTwice.CausalContext[R1]);
    }


    /// <summary>Equals must return false when a shared tombstone target's remove-dot set differs, even though the tombstone count and every other vertex/context aggregate matches.</summary>
    [TestMethod]
    public void EqualsReturnsFalseWhenASharedTombstonesRemoveDotSetDiffers()
    {
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 3)]);
        var vertex = new RgaVertexEntry<int>(Dot(R1, 1), null, 1);
        var tombstoneA = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 2)]);
        var tombstoneB = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 3)]);
        Rga<int> left = Rga<int>.FromState(new RgaState<int>(context, [vertex], [tombstoneA]));
        Rga<int> right = Rga<int>.FromState(new RgaState<int>(context, [vertex], [tombstoneB]));

        Assert.IsFalse(left.Equals(right));
        Assert.AreNotEqual(left.GetHashCode(), right.GetHashCode());
    }


    /// <summary>Equals(null) must return false per IEquatable's contract.</summary>
    [TestMethod]
    public void EqualsReturnsFalseForNullOther()
    {
        Assert.IsFalse(Rga<string>.Empty.Equals(NullOfT()));
    }


    /// <summary>Returns a typed null so the strongly typed Equals overload is chosen over Equals(object).</summary>
    private static Rga<string>? NullOfT() => null;


    /// <summary>A tombstone target counter exactly at the positivity boundary (1) still loads successfully.</summary>
    [TestMethod]
    public void FromStateAcceptsATombstoneTargetCounterAtTheBoundary()
    {
        (Rga<string> withA, Dot idA) = Rga<string>.Empty.InsertAtHead("A", R1);
        Rga<string> removed = withA.Remove(idA, R1);

        Assert.AreEqual(1, idA.Counter);
        Rga<string> back = Rga<string>.FromState(removed.ToState());
        Assert.AreEqual(removed, back);
    }


    /// <summary>Equals must return false when only Tombstones.Count differs, with Vertices and CompactedPredecessors counts equal.</summary>
    [TestMethod]
    public void EqualsReturnsFalseWhenOnlyTheTombstoneCountDiffers()
    {
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var vertex = new RgaVertexEntry<int>(Dot(R1, 1), null, 1);
        var tombstone = new RgaTombstoneEntry(Dot(R1, 1), [Dot(R1, 2)]);
        Rga<int> withoutTombstone = Rga<int>.FromState(new RgaState<int>(context, [vertex], []));
        Rga<int> withTombstone = Rga<int>.FromState(new RgaState<int>(context, [vertex], [tombstone]));

        Assert.IsFalse(withoutTombstone.Equals(withTombstone));
    }


    /// <summary>Equals must return false the moment a shared-key vertex's value differs, even when the vertex, tombstone, and translation counts and the context all match.</summary>
    [TestMethod]
    public void EqualsReturnsFalseWhenASharedVertexValueDiffers()
    {
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var vertexOne = new RgaVertexEntry<int>(Dot(R1, 1), null, 1);
        var vertexTwo = new RgaVertexEntry<int>(Dot(R1, 1), null, 2);
        Rga<int> left = Rga<int>.FromState(new RgaState<int>(context, [vertexOne], []));
        Rga<int> right = Rga<int>.FromState(new RgaState<int>(context, [vertexTwo], []));

        Assert.IsFalse(left.Equals(right));
    }


    /// <summary>Remove's null-id guard reports ParamName "id", not the FrozenDictionary lookup's own key parameter.</summary>
    [TestMethod]
    public void RemoveRejectsNullIdWithTheCorrectParamName()
    {
        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() => Rga<string>.Empty.Remove(null!, R1));
        Assert.AreEqual("id", exception.ParamName);
    }


    /// <summary>
    /// Pins that InsertAfter's own null guard fires (ParamName "after"), not the downstream
    /// FrozenDictionary lookup's guard (which would report ParamName "key" instead).
    /// </summary>
    [TestMethod]
    public void InsertAfterRejectsNullPredecessorByParamName()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => Rga<string>.Empty.InsertAfter(null!, "X", R1));

        Assert.AreEqual("after", thrown.ParamName);
    }


    /// <summary>Merge rejects a null other operand with ArgumentNullException("other"), not the NullReferenceException the stale-replay check would throw if the guard were missing.</summary>
    [TestMethod]
    public void MergeRejectsNullOther()
    {
        (Rga<string> withA, _) = Rga<string>.Empty.InsertAtHead("A", R1);

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() => withA.Merge(null!));
        Assert.AreEqual("other", exception.ParamName);
    }


    /// <summary>Equals must return false when only the causal Context differs, with Vertices/Tombstones/CompactedPredecessors counts all equal.</summary>
    [TestMethod]
    public void EqualsReturnsFalseWhenOnlyTheContextDiffers()
    {
        var vertex = new RgaVertexEntry<int>(Dot(R1, 1), null, 1);
        var smallContext = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var largeContext = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 5)]);
        Rga<int> withSmallContext = Rga<int>.FromState(new RgaState<int>(smallContext, [vertex], []));
        Rga<int> withLargeContext = Rga<int>.FromState(new RgaState<int>(largeContext, [vertex], []));

        Assert.IsFalse(withSmallContext.Equals(withLargeContext));
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
