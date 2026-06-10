using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Deterministic, hand-built coverage of <see cref="Rga{TValue}"/> waterline compaction: ghost-based
/// retention, dropped-tombstone translation, run-length state round-trips, and the fail-closed guards.
/// Frontiers are raised over exactly the dots meant to be stable; checkpoints are the expected stable
/// visible values.
/// </summary>
[TestClass]
internal sealed class RgaCompactionTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    //A stable, childless, non-head-anchored tombstone drops; values stay; its dot serves the nearest
    //retained ancestor and an insert after the translated dot lands right after that ancestor.
    [TestMethod]
    public void StableChildlessTombstoneDropsAndTranslatesToItsRetainedAncestor()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));

        (Rga<int> inserted, _) = compacted.InsertAfter(compacted.TranslateAnchor(idB)!, 9, R2);
        int[] expectedAfterInsert = [1, 9];
        CollectionAssert.AreEqual(expectedAfterInsert, inserted.Values.ToArray());
    }


    //A stable tombstone with an unstable child is kept as a ghost; the child's recorded predecessor is
    //unchanged and the visible order is preserved.
    [TestMethod]
    public void StableTombstoneWithUnstableChildIsKept()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withParent, Dot idParent) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withChild, Dot idChild) = withParent.InsertAfter(idParent, 3, R1);
        Rga<int> removed = withChild.Remove(idParent);

        //The parent is stable and tombstoned; the child stays above the frontier and keeps the parent alive.
        VectorClock frontier = FrontierCovering(idA, idParent);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1, 3];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idParent, compacted.TranslateAnchor(idParent));

        Dot childPredecessor = PredecessorOf(compacted, idChild);
        Assert.AreEqual(idParent, childPredecessor);
    }


    //A head-anchored stable childless tombstone is retained — Dot cannot express the head, so there is no
    //translation target — and it still anchors inserts directly.
    [TestMethod]
    public void HeadAnchoredStableChildlessTombstoneIsRetained()
    {
        (Rga<int> withHead, Dot idHead) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> removed = withHead.Remove(idHead);

        VectorClock frontier = FrontierCovering(idHead);
        ImmutableArray<int> checkpoint = [];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        //The tombstone survives: it still maps to itself and still anchors a direct insert.
        Assert.AreEqual(idHead, compacted.TranslateAnchor(idHead));

        (Rga<int> inserted, _) = compacted.InsertAfter(idHead, 9, R2);
        int[] expected = [9];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    //A chain of two stable tombstones (t2 inserted after t1) drops together; t2 resolves through to t1's
    //retained predecessor in the same pass.
    [TestMethod]
    public void ChainOfTwoStableTombstonesDropsAndComposesInOnePass()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withT1, Dot idT1) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withT2, Dot idT2) = withT1.InsertAfter(idT1, 3, R1);
        Rga<int> removed = withT2.Remove(idT1).Remove(idT2);

        VectorClock frontier = FrontierCovering(idA, idT1, idT2);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idA, compacted.TranslateAnchor(idT1));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idT2));
    }


    //A (frontier, checkpoint) pair that disagrees with the stable visible content fails closed.
    [TestMethod]
    public void CheckpointMismatchThrows()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> wrongCheckpoint = [1];

        Assert.ThrowsExactly<InvalidOperationException>(() => withB.Compact(frontier, wrongCheckpoint));
    }


    //Re-compacting at the same (frontier, checkpoint) yields a sequence equal to the first compaction.
    [TestMethod]
    public void RecompactingAtTheSameWaterlineIsANoOp()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> once = removed.Compact(frontier, checkpoint);

        Rga<int> twice = once.Compact(frontier, checkpoint);

        Assert.AreEqual(once, twice);
    }


    //Two successive compactions at increasing frontiers: a dot dropped in the first still translates after
    //the second, by map composition.
    [TestMethod]
    public void TwoSuccessiveCompactionsStillTranslateAFirstGenerationDot()
    {
        //idB and idC are both children of idA so idB is childless and drops cleanly in the first generation
        //while idC is still above the frontier.
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idA, 3, R1);
        Rga<int> removed = withC.Remove(idB);

        //First compaction folds the childless stable tombstone at idB but leaves idC above the frontier.
        VectorClock firstFrontier = FrontierCovering(idA, idB);
        ImmutableArray<int> firstCheckpoint = [1];
        Rga<int> first = removed.Compact(firstFrontier, firstCheckpoint);

        //Second compaction at a strictly higher frontier folds idC too (and tombstones it first).
        Rga<int> secondInput = first.Remove(idC);
        VectorClock secondFrontier = FrontierCovering(idA, idB, idC);
        ImmutableArray<int> secondCheckpoint = [1];
        Rga<int> second = secondInput.Compact(secondFrontier, secondCheckpoint);

        //The dot folded away in the first generation is still translatable after the second.
        Assert.IsNotNull(second.TranslateAnchor(idB));
    }


    //Merging a compacted state with an uncompacted laggard resurrects the tombstone, values converge, and a
    //repeat compaction drops it again.
    [TestMethod]
    public void MergingACompactedStateWithAnUncompactedLaggardResurrectsThenDropsAgain()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> laggard = withB.Remove(idB);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = laggard.Compact(frontier, checkpoint);

        //The laggard still carries the dropped tombstone's vertex and tombstone; the union brings them
        //back, so the merge converges to the same visible content as the laggard.
        Rga<int> merged = compacted.Merge(laggard);
        int[] expectedValues = [1];
        CollectionAssert.AreEqual(expectedValues, merged.Values.ToArray());
        CollectionAssert.AreEqual(laggard.Values.ToArray(), merged.Values.ToArray());

        //A repeat compaction at the same waterline drops it again, back to the compacted state.
        Rga<int> recompacted = merged.Compact(frontier, checkpoint);
        CollectionAssert.AreEqual(expectedValues, recompacted.Values.ToArray());
        Assert.AreEqual(compacted, recompacted);
    }


    //ToRunState/FromRunState round-trips a compacted instance to an equal Rga, a typed chained run
    //coalesces into a single RgaRunEntry, and a removal span coalesces into a single RgaTombstoneSpan.
    [TestMethod]
    public void RunStateRoundTripsAndCoalescesRunsAndSpans()
    {
        //Four consecutive same-replica chained inserts form one maximal run; two are then removed, forming
        //one coalesced span.
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        (Rga<int> withD, Dot idD) = withC.InsertAfter(idC, 4, R1);
        Rga<int> removed = withD.Remove(idB).Remove(idC);

        //Compact a separate dropped tombstone to exercise the round-trip on a compacted instance.
        (Rga<int> tombWith, Dot idTomb) = withD.InsertAfter(idD, 5, R1);
        Rga<int> tombRemoved = tombWith.Remove(idTomb);
        VectorClock tombFrontier = FrontierCovering(idA, idB, idC, idD, idTomb);
        ImmutableArray<int> tombCheckpoint = [1, 2, 3, 4];
        Rga<int> compacted = tombRemoved.Compact(tombFrontier, tombCheckpoint);

        RgaRunState<int> compactedState = compacted.ToRunState();
        Assert.AreEqual(compacted, Rga<int>.FromRunState(compactedState));

        //The four chained inserts coalesce into a single run; the two consecutive removals into a single span.
        RgaRunState<int> runState = removed.ToRunState();
        Assert.HasCount(1, runState.Runs);
        int[] expectedRunValues = [1, 2, 3, 4];
        CollectionAssert.AreEqual(expectedRunValues, runState.Runs[0].Values.ToArray());
        Assert.IsNull(runState.Runs[0].Predecessor);
        Assert.HasCount(1, runState.TombstoneSpans);
        Assert.AreEqual(idB.Counter, runState.TombstoneSpans[0].FromCounter);
        Assert.AreEqual(idC.Counter, runState.TombstoneSpans[0].ToCounter);
        Assert.AreEqual(removed, Rga<int>.FromRunState(runState));
    }


    //ToState fails closed on an instance carrying translations, but still serializes a never-compacted one.
    [TestMethod]
    public void ToStateThrowsOnTranslationsButWorksOnANeverCompactedInstance()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        Assert.ThrowsExactly<InvalidOperationException>(() => compacted.ToState());

        //A never-compacted instance still round-trips through the v1 state shape.
        Assert.AreEqual(withB, Rga<int>.FromState(withB.ToState()));
    }


    //TranslateAnchor: identity for a live dot, the map for a dropped dot, null for an unknown dot.
    [TestMethod]
    public void TranslateAnchorServesLiveDroppedAndUnknownDots()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);
        Dot unknown = new(R2, 99);

        Assert.AreEqual(idA, compacted.TranslateAnchor(idA));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));
        Assert.IsNull(compacted.TranslateAnchor(unknown));
    }


    //FromRunState validation: a dangling translation target, an invalid span, empty run values, and
    //duplicate dots across runs each fail closed.
    [TestMethod]
    public void FromRunStateValidatesItsInput()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaRunEntry<int> headRun = new(DotStateOf(new Dot(R1, 1)), null, [1]);

        //A translation whose target is not a vertex breaks servability.
        RgaTranslationEntry danglingTranslation = new(DotStateOf(new Dot(R1, 5)), DotStateOf(new Dot(R2, 7)));
        RgaRunState<int> danglingTarget = new(context, [headRun], [], [danglingTranslation]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(danglingTarget));

        //A span with ToCounter below FromCounter is invalid.
        RgaRunState<int> invalidSpan = new(context, [headRun], [new RgaTombstoneSpan(Bytes(R1), 3, 2)], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(invalidSpan));

        //A span with FromCounter below one is invalid.
        RgaRunState<int> belowOneSpan = new(context, [headRun], [new RgaTombstoneSpan(Bytes(R1), 0, 1)], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(belowOneSpan));

        //An empty run cannot expand into any vertex.
        RgaRunState<int> emptyRun = new(context, [new RgaRunEntry<int>(DotStateOf(new Dot(R1, 1)), null, [])], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(emptyRun));

        //Two runs minting the same dot collide.
        RgaRunEntry<int> duplicateRun = new(DotStateOf(new Dot(R1, 1)), null, [2]);
        RgaRunState<int> duplicateDots = new(context, [headRun, duplicateRun], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(duplicateDots));
    }


    private static Dot PredecessorOf(Rga<int> sequence, Dot id)
    {
        foreach(RgaVertexEntry<int> entry in sequence.ToState().Vertices)
        {
            if(entry.Id.Counter == id.Counter && ReplicaId.FromSpan(entry.Id.Replica.AsSpan()).Equals(id.Replica))
            {
                Assert.IsNotNull(entry.Predecessor);

                return new Dot(ReplicaId.FromSpan(entry.Predecessor!.Replica.AsSpan()), entry.Predecessor.Counter);
            }
        }

        throw new InvalidOperationException("The vertex was not found.");
    }


    private static DotState DotStateOf(Dot dot) => new(Bytes(dot.Replica), dot.Counter);


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static VectorClock FrontierCovering(params Dot[] dots)
    {
        VectorClock frontier = VectorClock.Empty;
        foreach(Dot dot in dots)
        {
            while(frontier[dot.Replica] < dot.Counter)
            {
                frontier = frontier.Increment(dot.Replica);
            }
        }

        return frontier;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
