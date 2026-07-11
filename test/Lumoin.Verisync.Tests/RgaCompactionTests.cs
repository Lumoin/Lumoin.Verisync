using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Deterministic, hand-built coverage of <see cref="Rga{TValue}"/> waterline compaction under dotted
/// removes: certified-remove retention, dropped-tombstone translation, run-length state round-trips over
/// legacy tombstones, and the fail-closed guards. A drop now requires a certified remove-dot, so drop-site
/// frontiers are the removed state's own <see cref="Rga{TValue}.CausalContext"/> (which covers the
/// remove-dots); partial dot-derived frontiers stay only where the test deliberately leaves state above the
/// line. Checkpoints are the certified projection at the frontier, which includes a locally tombstoned
/// element whose remove is not yet certified.
/// </summary>
[TestClass]
internal sealed class RgaCompactionTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    //A stable, childless, non-head-anchored tombstone whose remove is certified drops; values stay; its dot
    //serves the nearest retained ancestor and an insert after the translated dot lands right after it.
    [TestMethod]
    public void StableChildlessTombstoneDropsAndTranslatesToItsRetainedAncestor()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB, R1);

        //The removed state's context covers the remove-dot, so the frontier certifies the remove.
        VectorClock frontier = removed.CausalContext;
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));

        (Rga<int> inserted, _) = compacted.InsertAfter(compacted.TranslateAnchor(idB)!, 9, R2);
        int[] expectedAfterInsert = [1, 9];
        CollectionAssert.AreEqual(expectedAfterInsert, inserted.Values.ToArray());
    }


    //A stable tombstone with an unstable child is kept as a ghost regardless of certification; the child's
    //recorded predecessor is unchanged. The certified projection includes the ghost, whose remove is not yet
    //certified at the partial frontier.
    [TestMethod]
    public void StableTombstoneWithUnstableChildIsKept()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withParent, Dot idParent) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withChild, Dot idChild) = withParent.InsertAfter(idParent, 3, R1);
        Rga<int> removed = withChild.Remove(idParent, R1);

        //The parent is stable and tombstoned; the child stays above this partial frontier and keeps it alive.
        //The parent's remove is not certified here, so the certified projection includes the parent's value.
        VectorClock frontier = FrontierCovering(idA, idParent);
        ImmutableArray<int> checkpoint = [1, 2];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1, 3];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idParent, compacted.TranslateAnchor(idParent));

        Dot childPredecessor = PredecessorOf(compacted, idChild);
        Assert.AreEqual(idParent, childPredecessor);
    }


    //A head-anchored stable childless tombstone is retained even when its remove is certified — Dot cannot
    //express the head, so there is no translation target — and it still anchors inserts directly.
    [TestMethod]
    public void HeadAnchoredStableChildlessTombstoneIsRetained()
    {
        (Rga<int> withHead, Dot idHead) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> removed = withHead.Remove(idHead, R1);

        //The frontier certifies the remove, yet the head-anchored clause still retains the tombstone.
        VectorClock frontier = removed.CausalContext;
        ImmutableArray<int> checkpoint = [];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        //The tombstone survives: it still maps to itself and still anchors a direct insert.
        Assert.AreEqual(idHead, compacted.TranslateAnchor(idHead));

        (Rga<int> inserted, _) = compacted.InsertAfter(idHead, 9, R2);
        int[] expected = [9];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    //A chain of two stable tombstones (t2 inserted after t1), both removes certified, drops together; t2
    //resolves through to t1's retained predecessor in the same pass.
    [TestMethod]
    public void ChainOfTwoStableTombstonesDropsAndComposesInOnePass()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withT1, Dot idT1) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withT2, Dot idT2) = withT1.InsertAfter(idT1, 3, R1);
        Rga<int> removed = withT2.Remove(idT1, R1).Remove(idT2, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idA, compacted.TranslateAnchor(idT1));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idT2));
    }


    //A (frontier, checkpoint) pair that disagrees with the certified projection fails closed.
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
        Rga<int> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<int> checkpoint = [1];
        Rga<int> once = removed.Compact(frontier, checkpoint);

        Rga<int> twice = once.Compact(frontier, checkpoint);

        Assert.AreEqual(once, twice);
    }


    //Two successive compactions at increasing frontiers: a dot dropped in the first still translates after
    //the second, by map composition. The first remove is minted before the surviving sibling is inserted, so
    //a frontier can certify that remove while the sibling stays above the line.
    [TestMethod]
    public void TwoSuccessiveCompactionsStillTranslateAFirstGenerationDot()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removedB = withB.Remove(idB, R1);
        (Rga<int> withC, Dot idC) = removedB.InsertAfter(idA, 3, R1);

        //First compaction certifies idB's remove and folds the childless stable tombstone; idC is minted
        //after the remove, so idB's remove-dot sits below idC's insert and idC stays above the frontier.
        VectorClock firstFrontier = removedB.CausalContext;
        ImmutableArray<int> firstCheckpoint = [1];
        Rga<int> first = withC.Compact(firstFrontier, firstCheckpoint);

        //Second compaction at a strictly higher frontier certifies idC's remove and folds it too.
        Rga<int> secondInput = first.Remove(idC, R1);
        VectorClock secondFrontier = secondInput.CausalContext;
        ImmutableArray<int> secondCheckpoint = [1];
        Rga<int> second = secondInput.Compact(secondFrontier, secondCheckpoint);

        //The dot folded away in the first generation is still translatable after the second.
        Assert.IsNotNull(second.TranslateAnchor(idB));
    }


    //Merging a compacted state with an uncompacted laggard that HOLDS the dotted tombstone resurrects the
    //ghost with its tombstone (the detector stays quiet), values converge, and a repeat compaction drops it.
    [TestMethod]
    public void MergingACompactedStateWithAnUncompactedLaggardResurrectsThenDropsAgain()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> laggard = withB.Remove(idB, R1);

        VectorClock frontier = laggard.CausalContext;
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = laggard.Compact(frontier, checkpoint);

        //The laggard still carries the dropped vertex AND its dotted tombstone, so the merge re-enters the
        //ghost hidden (never live) and the stale-replay detector must not fire in either direction.
        Rga<int> merged = compacted.Merge(laggard);
        int[] expectedValues = [1];
        CollectionAssert.AreEqual(expectedValues, merged.Values.ToArray());
        CollectionAssert.AreEqual(laggard.Values.ToArray(), merged.Values.ToArray());
        CollectionAssert.AreEqual(expectedValues, laggard.Merge(compacted).Values.ToArray());

        //A repeat compaction at the same waterline drops it again, back to the compacted state.
        Rga<int> recompacted = merged.Compact(frontier, checkpoint);
        CollectionAssert.AreEqual(expectedValues, recompacted.Values.ToArray());
        Assert.AreEqual(compacted, recompacted);
    }


    //A typed chained run coalesces into a single RgaRunEntry and a per-replica removal range into a single
    //RgaTombstoneSpan. The fixture is rebuilt through FromState with LEGACY tombstones (empty remove-dots) so
    //ToRunState — which refuses dotted removes in slice 1 — still serializes it.
    [TestMethod]
    public void RunStateCoalescesRunsAndSpans()
    {
        //Four consecutive same-replica chained inserts, with the middle two tombstoned as legacy entries.
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 4)]);
        RgaVertexEntry<int> vertexA = new(DotStateOf(new Dot(R1, 1)), null, 1);
        RgaVertexEntry<int> vertexB = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)), 2);
        RgaVertexEntry<int> vertexC = new(DotStateOf(new Dot(R1, 3)), DotStateOf(new Dot(R1, 2)), 3);
        RgaVertexEntry<int> vertexD = new(DotStateOf(new Dot(R1, 4)), DotStateOf(new Dot(R1, 3)), 4);
        RgaTombstoneEntry legacyB = new(DotStateOf(new Dot(R1, 2)), []);
        RgaTombstoneEntry legacyC = new(DotStateOf(new Dot(R1, 3)), []);
        RgaState<int> state = new(context, [vertexA, vertexB, vertexC, vertexD], [legacyB, legacyC]);
        Rga<int> removed = Rga<int>.FromState(state);

        RgaRunState<int> runState = removed.ToRunState();
        Assert.HasCount(1, runState.Runs);
        int[] expectedRunValues = [1, 2, 3, 4];
        CollectionAssert.AreEqual(expectedRunValues, runState.Runs[0].Values.ToArray());
        Assert.IsNull(runState.Runs[0].Predecessor);
        Assert.HasCount(1, runState.TombstoneSpans);
        Assert.AreEqual(2, runState.TombstoneSpans[0].FromCounter);
        Assert.AreEqual(3, runState.TombstoneSpans[0].ToCounter);
        Assert.AreEqual(removed, Rga<int>.FromRunState(runState));
    }


    //ToRunState fails closed on a state carrying dotted removes: the v1 run shape cannot represent them, and
    //the rga-rle.v2 run shape lands with the container slice.
    [TestMethod]
    public void ToRunStateFailsClosedOnDottedRemoves()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB, R1);

        Assert.ThrowsExactly<InvalidOperationException>(() => removed.ToRunState());
    }


    //ToState fails closed on an instance carrying translations, but still serializes a never-compacted one.
    [TestMethod]
    public void ToStateThrowsOnTranslationsButWorksOnANeverCompactedInstance()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        Assert.ThrowsExactly<InvalidOperationException>(() => compacted.ToState());

        //A never-compacted instance with no dotted removes still round-trips through the v1 state shape.
        Assert.AreEqual(withB, Rga<int>.FromState(withB.ToState()));
    }


    //TranslateAnchor: identity for a live dot, the map for a dropped dot, null for an unknown dot.
    [TestMethod]
    public void TranslateAnchorServesLiveDroppedAndUnknownDots()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);
        Dot unknown = new(R2, 99);

        Assert.AreEqual(idA, compacted.TranslateAnchor(idA));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));
        Assert.IsNull(compacted.TranslateAnchor(unknown));
    }


    //FromRunState validation: a dangling translation target, an invalid span, empty run values, duplicate
    //dots across runs, a W-shape translation (dropped dot is a live untombstoned vertex), and an
    //overlapping-span duplicate target each fail closed.
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

        //A translation whose dropped dot is a live vertex (present and NOT a tombstone target) is a W-shape
        //forgery — the tombstoned ghost-plus-witness shape remains legal, this one does not.
        VectorClockState twoContext = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaRunEntry<int> twoRun = new(DotStateOf(new Dot(R1, 1)), null, [1, 2]);
        RgaTranslationEntry wShape = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> wShapeState = new(twoContext, [twoRun], [], [wShape]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(wShapeState));

        //Two overlapping spans present the same tombstone target twice.
        RgaTombstoneSpan spanOne = new(Bytes(R1), 1, 2);
        RgaTombstoneSpan spanTwo = new(Bytes(R1), 2, 3);
        RgaRunState<int> overlappingSpans = new(context, [headRun], [spanOne, spanTwo], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(overlappingSpans));
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
