using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Deterministic, hand-built coverage of <see cref="Rga{TValue}"/> waterline compaction under dotted
/// removes: certified-remove retention, dropped-tombstone translation, and the rga-rle.v2 run state
/// round-trips (two-range tombstone spans, irregular-tombstone fallback, pinned translation-span coalescing)
/// together with the fail-closed guards. A drop now requires a certified remove-dot, so drop-site frontiers
/// are the removed state's own <see cref="Rga{TValue}.CausalContext"/> (which covers the remove-dots), and
/// checkpoints are the dotted certified projection at the frontier, derived through
/// <see cref="Rga{TValue}.CertifiedProjection"/> rather than hand-built value arrays. The certified
/// projection includes a locally tombstoned element whose remove is not yet certified.
/// </summary>
[TestClass]
internal sealed class RgaCompactionTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);
    private static ReplicaId R3 { get; } = Replica(3);


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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1];
        Assert.AreSequenceEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));

        (Rga<int> inserted, _) = compacted.InsertAfter(compacted.TranslateAnchor(idB)!, 9, R2);
        int[] expectedAfterInsert = [1, 9];
        Assert.AreSequenceEqual(expectedAfterInsert, inserted.Values.ToArray());
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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1, 3];
        Assert.AreSequenceEqual(expectedValues, compacted.Values.ToArray());
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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        //The tombstone survives: it still maps to itself and still anchors a direct insert.
        Assert.AreEqual(idHead, compacted.TranslateAnchor(idHead));

        (Rga<int> inserted, _) = compacted.InsertAfter(idHead, 9, R2);
        int[] expected = [9];
        Assert.AreSequenceEqual(expected, inserted.Values.ToArray());
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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        int[] expectedValues = [1];
        Assert.AreSequenceEqual(expectedValues, compacted.Values.ToArray());
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

        //The certified projection at this frontier is [a, b]; a single-entry checkpoint disagrees.
        ImmutableArray<SequenceCheckpointEntry<int>> wrongCheckpoint = [new SequenceCheckpointEntry<int>(DotStateOf(new Dot(R1, 1)), 1)];

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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
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
        ImmutableArray<SequenceCheckpointEntry<int>> firstCheckpoint = withC.CertifiedProjection(firstFrontier);
        Rga<int> first = withC.Compact(firstFrontier, firstCheckpoint);

        //Second compaction at a strictly higher frontier certifies idC's remove and folds it too.
        Rga<int> secondInput = first.Remove(idC, R1);
        VectorClock secondFrontier = secondInput.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> secondCheckpoint = secondInput.CertifiedProjection(secondFrontier);
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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = laggard.CertifiedProjection(frontier);
        Rga<int> compacted = laggard.Compact(frontier, checkpoint);

        //The laggard still carries the dropped vertex AND its dotted tombstone, so the merge re-enters the
        //ghost hidden (never live) and the stale-replay detector must not fire in either direction.
        Rga<int> merged = compacted.Merge(laggard);
        int[] expectedValues = [1];
        Assert.AreSequenceEqual(expectedValues, merged.Values.ToArray());
        Assert.AreSequenceEqual(laggard.Values.ToArray(), merged.Values.ToArray());
        Assert.AreSequenceEqual(expectedValues, laggard.Merge(compacted).Values.ToArray());

        //A repeat compaction at the same waterline drops it again, back to the compacted state.
        Rga<int> recompacted = merged.Compact(frontier, checkpoint);
        Assert.AreSequenceEqual(expectedValues, recompacted.Values.ToArray());
        Assert.AreEqual(compacted, recompacted);
    }


    //A typed chained run coalesces into a single RgaRunEntry, and a one-replica contiguous deletion pass
    //(R2 removing the middle two elements) coalesces into a single two-range RgaTombstoneSpan.
    [TestMethod]
    public void RunStateCoalescesRunsAndSpans()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        (Rga<int> withD, _) = withC.InsertAfter(idC, 4, R1);
        Rga<int> removed = withD.Remove(idB, R2).Remove(idC, R2);

        RgaRunState<int> runState = removed.ToRunState();
        Assert.HasCount(1, runState.Runs);
        int[] expectedRunValues = [1, 2, 3, 4];
        Assert.AreSequenceEqual(expectedRunValues, runState.Runs[0].Values.ToArray());
        Assert.IsNull(runState.Runs[0].Predecessor);
        Assert.HasCount(1, runState.TombstoneSpans);
        RgaTombstoneSpan span = runState.TombstoneSpans[0];
        Assert.AreEqual(2, span.TargetFrom);
        Assert.AreEqual(3, span.TargetTo);
        Assert.AreEqual(1, span.RemoveFrom);
        Assert.IsTrue(span.TargetReplica.AsSpan().SequenceEqual(R1.AsSpan()));
        Assert.IsTrue(span.RemoveReplica.AsSpan().SequenceEqual(R2.AsSpan()));
        Assert.HasCount(0, runState.IrregularTombstones);
        Assert.AreEqual(removed, Rga<int>.FromRunState(runState));
    }


    //(a) A dotted-remove state round-trips through the run shape, with a contiguous single-replica deletion
    //pass asserted as one two-range span. T6: R1 inserts 1..5 chained; R2 removes 2,3,4 minting
    //(R2,1),(R2,2),(R2,3), so ToRunState emits ONE span (TargetReplica R1, 2, 4, RemoveReplica R2, 1).
    [TestMethod]
    public void ADottedRemoveStateRoundTripsWithATwoRangeSpan()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        (Rga<int> withD, Dot idD) = withC.InsertAfter(idC, 4, R1);
        (Rga<int> withE, _) = withD.InsertAfter(idD, 5, R1);
        Rga<int> x = withE.Remove(idB, R2).Remove(idC, R2).Remove(idD, R2);

        RgaRunState<int> runState = x.ToRunState();
        Assert.HasCount(1, runState.TombstoneSpans);
        RgaTombstoneSpan span = runState.TombstoneSpans[0];
        Assert.AreEqual(2, span.TargetFrom);
        Assert.AreEqual(4, span.TargetTo);
        Assert.AreEqual(1, span.RemoveFrom);
        Assert.IsTrue(span.TargetReplica.AsSpan().SequenceEqual(R1.AsSpan()));
        Assert.IsTrue(span.RemoveReplica.AsSpan().SequenceEqual(R2.AsSpan()));
        Assert.HasCount(0, runState.IrregularTombstones);
        Assert.HasCount(0, runState.Translations);
        Assert.HasCount(0, runState.TranslationSpans);
        Assert.AreEqual(x, Rga<int>.FromRunState(runState));
    }


    //(b) A compacted state carrying a translation AND a retained dotted tombstone round-trips through the run
    //shape with its servability intact — the slice-1-deferred serialization half of the C-killer. R2 removes
    //the head a (retained head ghost) and the childless b (dropped, translated onto a).
    [TestMethod]
    public void ACompactedStateRoundTripsThroughTheRunShape()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idA, R2).Remove(idB, R2);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));

        RgaRunState<int> runState = compacted.ToRunState();
        Assert.HasCount(1, runState.Translations);
        Assert.HasCount(0, runState.TranslationSpans);

        Rga<int> back = Rga<int>.FromRunState(runState);
        Assert.AreEqual(compacted, back);
        Assert.AreEqual(idA, back.TranslateAnchor(idB));
    }


    //(c) A legacy tombstone (empty remove-dots) cannot become a span, so it serializes as an irregular entry
    //and round-trips, carrying the retain-forever v1 load.
    [TestMethod]
    public void ALegacyTombstoneRoundTripsThroughAnIrregularEntry()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaVertexEntry<int> vertexA = new(DotStateOf(new Dot(R1, 1)), null, 1);
        RgaVertexEntry<int> vertexB = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)), 2);
        RgaTombstoneEntry legacyB = new(DotStateOf(new Dot(R1, 2)), []);
        Rga<int> x = Rga<int>.FromState(new RgaState<int>(context, [vertexA, vertexB], [legacyB]));

        RgaRunState<int> runState = x.ToRunState();
        Assert.HasCount(0, runState.TombstoneSpans);
        Assert.HasCount(1, runState.IrregularTombstones);
        Assert.HasCount(0, runState.IrregularTombstones[0].RemoveDots);
        Assert.AreEqual(x, Rga<int>.FromRunState(runState));
    }


    //(d) Two concurrent removes of one target union into a two-dot tombstone that no span can express, so it
    //serializes irregularly and round-trips.
    [TestMethod]
    public void AConcurrentRemoveRoundTripsThroughAnIrregularEntry()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> byR2 = withA.Remove(idA, R2);
        Rga<int> byR3 = withA.Remove(idA, R3);
        Rga<int> x = byR2.Merge(byR3);

        RgaRunState<int> runState = x.ToRunState();
        Assert.HasCount(0, runState.TombstoneSpans);
        Assert.HasCount(1, runState.IrregularTombstones);
        Assert.HasCount(2, runState.IrregularTombstones[0].RemoveDots);
        Assert.AreEqual(x, Rga<int>.FromRunState(runState));
    }


    //(e) A laggard merge resurrects a dropped tombstone while its translation entry remains: the dropped dot
    //is a current (tombstoned) vertex, so its witness serializes as a SINGLETON translation entry, never
    //inside a span, and the ghost-plus-witness shape round-trips.
    [TestMethod]
    public void AResurrectedGhostWithWitnessRoundTripsWithASingletonTranslation()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> laggard = withB.Remove(idB, R1);

        VectorClock frontier = laggard.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = laggard.CertifiedProjection(frontier);
        Rga<int> compacted = laggard.Compact(frontier, checkpoint);
        Rga<int> resurrected = compacted.Merge(laggard);

        RgaRunState<int> runState = resurrected.ToRunState();
        Assert.HasCount(1, runState.Translations);
        Assert.HasCount(0, runState.TranslationSpans);
        Assert.AreEqual(resurrected, Rga<int>.FromRunState(runState));
    }


    //(f) FromRunState fails closed on the v2-shape violations: overlapping span targets, a span/irregular
    //duplicate target, a translation span landing on a vertex, translation-span bounds, and remove-dot
    //arithmetic overflow.
    [TestMethod]
    public void FromRunStateFailsClosedOnV2ShapeViolations()
    {
        VectorClockState oneAxis = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaRunEntry<int> chain = new(DotStateOf(new Dot(R1, 1)), null, [1, 2]);

        //Overlapping span targets: two two-range spans that both name target (R1,2).
        VectorClockState spanContext = new([new ReplicaCounterEntry(Bytes(R1), 3), new ReplicaCounterEntry(Bytes(R2), 10)]);
        RgaTombstoneSpan spanOne = new(Bytes(R1), 1, 2, Bytes(R2), 1);
        RgaTombstoneSpan spanTwo = new(Bytes(R1), 2, 3, Bytes(R2), 5);
        RgaRunState<int> overlappingSpans = new(spanContext, [], [spanOne, spanTwo], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(overlappingSpans));

        //A span target that also appears in an irregular tombstone.
        RgaTombstoneSpan span = new(Bytes(R1), 1, 1, Bytes(R2), 1);
        RgaConcurrentTombstone irregular = new(DotStateOf(new Dot(R1, 1)), [DotStateOf(new Dot(R2, 2))]);
        VectorClockState dupContext = new([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 2)]);
        RgaRunState<int> spanIrregularDuplicate = new(dupContext, [], [span], [irregular], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(spanIrregularDuplicate));

        //A translation span whose expanded dropped dots land on existing vertices.
        RgaTranslationSpan landsOnVertex = new(Bytes(R1), 1, 2, DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> translationSpanOnVertex = new(oneAxis, [chain], [], [], [], [landsOnVertex]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(translationSpanOnVertex));

        //A translation span with ToCounter below FromCounter is invalid bounds.
        RgaTranslationSpan invalidBounds = new(Bytes(R2), 3, 2, DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> translationSpanBounds = new(oneAxis, [chain], [], [], [], [invalidBounds]);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(translationSpanBounds));

        //A two-range span whose remove-dot arithmetic overflows int. The context COVERS the remove axis up
        //to int.MaxValue, so the coverage check cannot mask the overflow guard: without the guard the
        //wrapped negative counters would sail past coverage and be admitted.
        VectorClockState wideContext = new([new ReplicaCounterEntry(Bytes(R1), 3), new ReplicaCounterEntry(Bytes(R2), int.MaxValue)]);
        RgaTombstoneSpan overflowSpan = new(Bytes(R1), 1, 2, Bytes(R2), int.MaxValue);
        RgaRunState<int> removeDotOverflow = new(wideContext, [], [overflowSpan], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(removeDotOverflow));

        //The single-element companion at the same bound does not overflow and loads: the guard rejects
        //arithmetic, not magnitude.
        RgaTombstoneSpan atTheBound = new(Bytes(R1), 1, 1, Bytes(R2), int.MaxValue);
        RgaRunState<int> loadable = new(wideContext, [], [atTheBound], [], [], []);
        Assert.AreEqual(0, Rga<int>.FromRunState(loadable).Count);

        //A run whose expanded vertex counters would overflow is rejected the same way — the wrapped
        //negative counter would otherwise slip past both the positivity and coverage checks.
        VectorClockState runContext = new([new ReplicaCounterEntry(Bytes(R1), int.MaxValue)]);
        RgaRunEntry<int> overflowRun = new(DotStateOf(new Dot(R1, int.MaxValue)), null, [1, 2]);
        RgaRunState<int> runOverflow = new(runContext, [overflowRun], [], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(runOverflow));
    }


    //The gate-1 shared-counter-plane cost, pinned empirically: a type-delete-type workload fragments the
    //insert runs exactly as the plane-sharing arithmetic predicts. Each round types PerRound chained inserts
    //then removes the last; the remove tick opens a one-counter gap on the shared axis, so the next round's
    //inserts start past it and cannot extend the previous run — one run and one length-one span per round.
    [TestMethod]
    public void ATypeDeleteTypeWorkloadFragmentsInsertRunsAsThePlaneSharingPredicts()
    {
        const int PerRound = 3;
        const int Rounds = 4;
        (Rga<int> typed, Dot last) = Rga<int>.Empty.InsertAtHead(0, R1);
        int value = 1;
        for(int round = 0; round < Rounds; round++)
        {
            while(value < (round + 1) * PerRound)
            {
                (typed, last) = typed.InsertAfter(last, value, R1);
                value++;
            }

            typed = typed.Remove(last, R1);
        }

        RgaRunState<int> runState = typed.ToRunState();
        Assert.HasCount(Rounds, runState.Runs);
        Assert.HasCount(PerRound, runState.Runs[0].Values);
        Assert.HasCount(Rounds, runState.TombstoneSpans);
        Assert.HasCount(0, runState.IrregularTombstones);
        Assert.HasCount(0, runState.Translations);

        //The contrast: the same count of inserts with NO interleaved removes keeps the counter plane
        //contiguous, so every insert coalesces into a single run and no span is emitted.
        (Rga<int> contiguous, Dot tail) = Rga<int>.Empty.InsertAtHead(0, R1);
        for(int i = 1; i < PerRound * Rounds; i++)
        {
            (contiguous, tail) = contiguous.InsertAfter(tail, i, R1);
        }

        RgaRunState<int> contrastRunState = contiguous.ToRunState();
        Assert.HasCount(1, contrastRunState.Runs);
        Assert.HasCount(PerRound * Rounds, contrastRunState.Runs[0].Values);
        Assert.HasCount(0, contrastRunState.TombstoneSpans);
    }


    //ToState fails closed on an instance carrying translations, but still serializes a never-compacted one.
    [TestMethod]
    public void ToStateThrowsOnTranslationsButWorksOnANeverCompactedInstance()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
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
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);
        Dot unknown = new(R2, 99);

        Assert.AreEqual(idA, compacted.TranslateAnchor(idA));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));
        Assert.IsNull(compacted.TranslateAnchor(unknown));
    }


    //FromRunState validation of the shared model postures: a dangling translation target, an empty run, a
    //duplicate dot across runs, and a W-shape translation (a dropped dot that is a live untombstoned vertex)
    //each fail closed. The v2-shape span/translation-span violations live in the (f) case above.
    [TestMethod]
    public void FromRunStateValidatesItsInput()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaRunEntry<int> headRun = new(DotStateOf(new Dot(R1, 1)), null, [1]);

        //A translation whose target is not a vertex breaks servability.
        RgaTranslationEntry danglingTranslation = new(DotStateOf(new Dot(R1, 5)), DotStateOf(new Dot(R2, 7)));
        RgaRunState<int> danglingTarget = new(context, [headRun], [], [], [danglingTranslation], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(danglingTarget));

        //An empty run cannot expand into any vertex.
        RgaRunState<int> emptyRun = new(context, [new RgaRunEntry<int>(DotStateOf(new Dot(R1, 1)), null, [])], [], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(emptyRun));

        //Two runs minting the same dot collide.
        RgaRunEntry<int> duplicateRun = new(DotStateOf(new Dot(R1, 1)), null, [2]);
        RgaRunState<int> duplicateDots = new(context, [headRun, duplicateRun], [], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(duplicateDots));

        //A translation whose dropped dot is a live vertex (present and NOT a tombstone target) is a W-shape
        //forgery — the tombstoned ghost-plus-witness shape remains legal, this one does not.
        VectorClockState twoContext = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaRunEntry<int> twoRun = new(DotStateOf(new Dot(R1, 1)), null, [1, 2]);
        RgaTranslationEntry wShape = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> wShapeState = new(twoContext, [twoRun], [], [], [wShape], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(wShapeState));
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
