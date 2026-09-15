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


    /// <summary>
    /// A stable, childless, non-head-anchored tombstone whose remove is certified drops; values stay; its dot
    /// serves the nearest retained ancestor and an insert after the translated dot lands right after it.
    /// </summary>
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


    /// <summary>
    /// A stable tombstone with an unstable child is kept as a ghost regardless of certification; the child's
    /// recorded predecessor is unchanged.
    /// </summary>
    /// <remarks>
    /// The certified projection includes the ghost, whose remove is not yet certified at the partial frontier.
    /// </remarks>
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


    /// <summary>
    /// A head-anchored stable childless tombstone is retained even when its remove is certified — Dot cannot
    /// express the head, so there is no translation target — and it still anchors inserts directly.
    /// </summary>
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


    /// <summary>
    /// A chain of two stable tombstones (t2 inserted after t1), both removes certified, drops together; t2
    /// resolves through to t1's retained predecessor in the same pass.
    /// </summary>
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


    /// <summary>
    /// A (frontier, checkpoint) pair that disagrees with the certified projection fails closed.
    /// </summary>
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


    /// <summary>
    /// Re-compacting at the same (frontier, checkpoint) yields a sequence equal to the first compaction.
    /// </summary>
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


    /// <summary>
    /// Two successive compactions at increasing frontiers: a dot dropped in the first still translates after
    /// the second, by map composition.
    /// </summary>
    /// <remarks>
    /// The first remove is minted before the surviving sibling is inserted, so a frontier can certify that
    /// remove while the sibling stays above the line.
    /// </remarks>
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


    /// <summary>
    /// Merging a compacted state with an uncompacted laggard that holds the dotted tombstone resurrects the
    /// ghost with its tombstone (the detector stays quiet), values converge, and a repeat compaction drops it.
    /// </summary>
    [TestMethod]
    public void MergingACompactedStateWithAnUncompactedLaggardResurrectsThenDropsAgain()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> laggard = withB.Remove(idB, R1);

        VectorClock frontier = laggard.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = laggard.CertifiedProjection(frontier);
        Rga<int> compacted = laggard.Compact(frontier, checkpoint);

        //The laggard still carries the dropped vertex and its dotted tombstone, so the merge re-enters the
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


    /// <summary>
    /// A typed chained run coalesces into a single RgaRunEntry, and a one-replica contiguous deletion pass (R2
    /// removing the middle two elements) coalesces into a single two-range RgaTombstoneSpan.
    /// </summary>
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
        Assert.IsEmpty(runState.IrregularTombstones);
        Assert.AreEqual(removed, Rga<int>.FromRunState(runState));
    }


    /// <summary>
    /// (a) A dotted-remove state round-trips through the run shape, with a contiguous single-replica deletion
    /// pass asserted as one two-range span.
    /// </summary>
    /// <remarks>
    /// T6: R1 inserts 1..5 chained; R2 removes 2,3,4 minting (R2,1),(R2,2),(R2,3), so ToRunState emits one span
    /// (TargetReplica R1, 2, 4, RemoveReplica R2, 1).
    /// </remarks>
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
        Assert.IsEmpty(runState.IrregularTombstones);
        Assert.IsEmpty(runState.Translations);
        Assert.IsEmpty(runState.TranslationSpans);
        Assert.AreEqual(x, Rga<int>.FromRunState(runState));
    }


    /// <summary>
    /// (b) A compacted state carrying a translation and a retained dotted tombstone round-trips through the run
    /// shape with its servability intact — the slice-1-deferred serialization half of the C-killer.
    /// </summary>
    /// <remarks>
    /// R2 removes the head a (retained head ghost) and the childless b (dropped, translated onto a).
    /// </remarks>
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

        //Both elements are removed, so nothing is visible; the retained head ghost's tombstone must survive the
        //compaction, or the removed head would resurface. Its tombstone serializes as the sole span.
        Assert.IsEmpty(compacted.Values);
        Assert.AreEqual(0, compacted.Count);

        RgaRunState<int> runState = compacted.ToRunState();
        Assert.HasCount(1, runState.Translations);
        Assert.IsEmpty(runState.TranslationSpans);
        Assert.HasCount(1, runState.TombstoneSpans);
        Assert.AreEqual(idA.Counter, runState.TombstoneSpans[0].TargetFrom);

        Rga<int> back = Rga<int>.FromRunState(runState);
        Assert.AreEqual(compacted, back);
        Assert.AreEqual(idA, back.TranslateAnchor(idB));
    }


    /// <summary>
    /// (c) A legacy tombstone (empty remove-dots) cannot become a span, so it serializes as an irregular entry
    /// and round-trips, carrying the retain-forever v1 load.
    /// </summary>
    [TestMethod]
    public void ALegacyTombstoneRoundTripsThroughAnIrregularEntry()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaVertexEntry<int> vertexA = new(DotStateOf(new Dot(R1, 1)), null, 1);
        RgaVertexEntry<int> vertexB = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)), 2);
        RgaTombstoneEntry legacyB = new(DotStateOf(new Dot(R1, 2)), []);
        Rga<int> x = Rga<int>.FromState(new RgaState<int>(context, [vertexA, vertexB], [legacyB]));

        RgaRunState<int> runState = x.ToRunState();
        Assert.IsEmpty(runState.TombstoneSpans);
        Assert.HasCount(1, runState.IrregularTombstones);
        Assert.IsEmpty(runState.IrregularTombstones[0].RemoveDots);
        Assert.AreEqual(x, Rga<int>.FromRunState(runState));
    }


    /// <summary>
    /// (d) Two concurrent removes of one target union into a two-dot tombstone that no span can express, so it
    /// serializes irregularly and round-trips.
    /// </summary>
    [TestMethod]
    public void AConcurrentRemoveRoundTripsThroughAnIrregularEntry()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> byR2 = withA.Remove(idA, R2);
        Rga<int> byR3 = withA.Remove(idA, R3);
        Rga<int> x = byR2.Merge(byR3);

        RgaRunState<int> runState = x.ToRunState();
        Assert.IsEmpty(runState.TombstoneSpans);
        Assert.HasCount(1, runState.IrregularTombstones);
        Assert.HasCount(2, runState.IrregularTombstones[0].RemoveDots);
        Assert.AreEqual(x, Rga<int>.FromRunState(runState));
    }


    /// <summary>
    /// (e) A laggard merge resurrects a dropped tombstone while its translation entry remains: the dropped dot
    /// is a current (tombstoned) vertex, so its witness serializes as a singleton translation entry, never
    /// inside a span, and the ghost-plus-witness shape round-trips.
    /// </summary>
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
        Assert.IsEmpty(runState.TranslationSpans);
        Assert.AreEqual(resurrected, Rga<int>.FromRunState(runState));
    }


    /// <summary>
    /// (f) FromRunState fails closed on the v2-shape violations: overlapping span targets, a span/irregular
    /// duplicate target, a translation span landing on a vertex, translation-span bounds, and remove-dot
    /// arithmetic overflow.
    /// </summary>
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

        //A two-range span whose remove-dot arithmetic overflows int. The context covers the remove axis up
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


    /// <summary>
    /// The gate-1 shared-counter-plane cost, pinned empirically: a type-delete-type workload fragments the
    /// insert runs exactly as the plane-sharing arithmetic predicts.
    /// </summary>
    /// <remarks>
    /// Each round types PerRound chained inserts then removes the last; the remove tick opens a one-counter gap
    /// on the shared axis, so the next round's inserts start past it and cannot extend the previous run — one
    /// run and one length-one span per round.
    /// </remarks>
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
        Assert.IsEmpty(runState.IrregularTombstones);
        Assert.IsEmpty(runState.Translations);

        //The contrast: the same count of inserts with no interleaved removes keeps the counter plane
        //contiguous, so every insert coalesces into a single run and no span is emitted.
        (Rga<int> contiguous, Dot tail) = Rga<int>.Empty.InsertAtHead(0, R1);
        for(int i = 1; i < PerRound * Rounds; i++)
        {
            (contiguous, tail) = contiguous.InsertAfter(tail, i, R1);
        }

        RgaRunState<int> contrastRunState = contiguous.ToRunState();
        Assert.HasCount(1, contrastRunState.Runs);
        Assert.HasCount(PerRound * Rounds, contrastRunState.Runs[0].Values);
        Assert.IsEmpty(contrastRunState.TombstoneSpans);
    }


    /// <summary>
    /// ToState fails closed on an instance carrying translations, but still serializes a never-compacted one.
    /// </summary>
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


    /// <summary>
    /// TranslateAnchor: identity for a live dot, the map for a dropped dot, null for an unknown dot.
    /// </summary>
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


    /// <summary>
    /// FromRunState validation of the shared model postures: a dangling translation target, an empty run, a
    /// duplicate dot across runs, and a W-shape translation (a dropped dot that is a live untombstoned vertex)
    /// each fail closed.
    /// </summary>
    /// <remarks>
    /// The v2-shape span/translation-span violations live in the (f) case above.
    /// </remarks>
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

        //A translation whose dropped dot is a live vertex (present and not a tombstone target) is a W-shape
        //forgery — the tombstoned ghost-plus-witness shape remains legal, this one does not.
        VectorClockState twoContext = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaRunEntry<int> twoRun = new(DotStateOf(new Dot(R1, 1)), null, [1, 2]);
        RgaTranslationEntry wShape = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> wShapeState = new(twoContext, [twoRun], [], [], [wShape], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(wShapeState));
    }


    /// <summary>
    /// Pins that TranslateAnchor's own null guard fires (ParamName "anchor"), not the downstream vertex
    /// lookup's guard, which would report ParamName "key" instead.
    /// </summary>
    [TestMethod]
    public void TranslateAnchorRejectsNullAnchor()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => Rga<int>.Empty.TranslateAnchor(null!));

        Assert.AreEqual("anchor", thrown.ParamName);
    }


    /// <summary>
    /// Two same-replica dropped dots sharing one retained target, with contiguous counters, must coalesce
    /// into a single RgaTranslationSpan rather than two bare RgaTranslationEntry records.
    /// </summary>
    [TestMethod]
    public void ContiguousSameTargetDropsCoalesceIntoOneTranslationSpan()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        Rga<int> removed = withC.Remove(idB, R2).Remove(idC, R2);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        RgaRunState<int> runState = compacted.ToRunState();

        Assert.IsEmpty(runState.Translations);
        Assert.HasCount(1, runState.TranslationSpans);
        RgaTranslationSpan span = runState.TranslationSpans[0];
        Assert.AreEqual(idB.Counter, span.FromCounter);
        Assert.AreEqual(idC.Counter, span.ToCounter);
        AssertDotStateEquals(DotStateOf(idA), span.Target);
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idC));
    }


    /// <summary>
    /// A tombstone target whose remove-dot shares the run's remove-replica but breaks counter continuity
    /// must not be folded into the same span, even though the replica half of the check would pass.
    /// </summary>
    [TestMethod]
    public void TombstoneSpansBreakWhenOnlyTheRemoveCounterContinuityFails()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 6),
            new ReplicaCounterEntry(Bytes(R2), 999)]);
        RgaRunEntry<int> vertex1 = new(DotStateOf(new Dot(R1, 5)), null, [1]);
        RgaRunEntry<int> vertex2 = new(DotStateOf(new Dot(R1, 6)), null, [2]);
        RgaTombstoneSpan span1 = new(Bytes(R1), 5, 5, Bytes(R2), 10);
        RgaTombstoneSpan span2 = new(Bytes(R1), 6, 6, Bytes(R2), 999);

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [vertex1, vertex2], [span1, span2], [], [], []));

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(2, runState.TombstoneSpans);
        RgaTombstoneSpan first = runState.TombstoneSpans[0];
        Assert.AreEqual(5, first.TargetTo);
        Assert.AreEqual(10, first.RemoveFrom);
    }


    /// <summary>Every guard on an irregular tombstone's fields fails closed: a default remove-dot array, a non-positive target or remove-dot counter, a within-entry duplicate, a remove-dot colliding with a vertex, missing context coverage, and a cross-entry duplicate.</summary>
    [TestMethod]
    public void FromRunStateRejectsIrregularTombstoneViolations()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 1)]);

        RgaConcurrentTombstone defaultRemoveDots = new(DotStateOf(new Dot(R1, 1)), default);
        RgaRunState<int> defaultRemoveDotsState = new(context, [], [], [defaultRemoveDots], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(defaultRemoveDotsState));

        RgaConcurrentTombstone zeroTarget = new(DotStateOf(new Dot(R1, 0)), [DotStateOf(new Dot(R2, 1))]);
        RgaRunState<int> zeroTargetState = new(context, [], [], [zeroTarget], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(zeroTargetState));

        RgaConcurrentTombstone zeroRemoveDot = new(DotStateOf(new Dot(R1, 1)), [DotStateOf(new Dot(R2, 0))]);
        RgaRunState<int> zeroRemoveDotState = new(context, [], [], [zeroRemoveDot], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(zeroRemoveDotState));

        RgaConcurrentTombstone withinDuplicate = new(DotStateOf(new Dot(R1, 1)), [DotStateOf(new Dot(R2, 1)), DotStateOf(new Dot(R2, 1))]);
        RgaRunState<int> withinDuplicateState = new(context, [], [], [withinDuplicate], [], []);
        //The within-entry guard is pinned by its own message: without it the duplicate is accepted or trips the
        //cross-entry guard, whose message differs.
        ArgumentException withinDuplicateFault = Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(withinDuplicateState));
        Assert.Contains("appears more than once in a tombstone", withinDuplicateFault.Message);

        RgaRunEntry<int> vertexRun = new(DotStateOf(new Dot(R2, 1)), null, [1]);
        RgaConcurrentTombstone collidingRemoveDot = new(DotStateOf(new Dot(R1, 1)), [DotStateOf(new Dot(R2, 1))]);
        RgaRunState<int> collidingState = new(context, [vertexRun], [], [collidingRemoveDot], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(collidingState));

        VectorClockState uncoveredContext = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaConcurrentTombstone uncoveredRemoveDot = new(DotStateOf(new Dot(R1, 1)), [DotStateOf(new Dot(R2, 1))]);
        RgaRunState<int> uncoveredState = new(uncoveredContext, [], [], [uncoveredRemoveDot], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(uncoveredState));

        RgaConcurrentTombstone crossFirst = new(DotStateOf(new Dot(R1, 1)), [DotStateOf(new Dot(R2, 1))]);
        RgaConcurrentTombstone crossSecond = new(DotStateOf(new Dot(R1, 2)), [DotStateOf(new Dot(R2, 1))]);
        VectorClockState crossContext = new([new ReplicaCounterEntry(Bytes(R1), 2), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaRunState<int> crossState = new(crossContext, [], [], [crossFirst, crossSecond], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(crossState));
    }


    /// <summary>
    /// Two dropped dots on different replicas that happen to share the same retained target must not
    /// coalesce into one translation span (a span can only describe one replica's counter range).
    /// </summary>
    [TestMethod]
    public void TranslationsFromDifferentReplicasDoNotCoalesceIntoOneSpanEvenWithTheSameTarget()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R2);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idA, 3, R3);
        Rga<int> removed = withC.Remove(idB, R2).Remove(idC, R2);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        RgaRunState<int> runState = compacted.ToRunState();

        Assert.HasCount(2, runState.Translations);
        Assert.IsEmpty(runState.TranslationSpans);
        Assert.AreEqual(idA, compacted.TranslateAnchor(idB));
        Assert.AreEqual(idA, compacted.TranslateAnchor(idC));
    }


    /// <summary>
    /// Two tombstoned targets on different replicas, whose remove-dots happen to satisfy the run's remove
    /// continuity, must still not coalesce into one span: the target side's own continuity guard controls.
    /// </summary>
    [TestMethod]
    public void TombstoneSpansDoNotCoalesceAcrossDifferentTargetReplicas()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 5),
            new ReplicaCounterEntry(Bytes(R3), 6),
            new ReplicaCounterEntry(Bytes(R2), 11)]);
        RgaRunEntry<int> vertex1 = new(DotStateOf(new Dot(R1, 5)), null, [1]);
        RgaRunEntry<int> vertex2 = new(DotStateOf(new Dot(R3, 6)), null, [2]);
        RgaTombstoneSpan span1 = new(Bytes(R1), 5, 5, Bytes(R2), 10);
        RgaTombstoneSpan span2 = new(Bytes(R3), 6, 6, Bytes(R2), 11);

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [vertex1, vertex2], [span1, span2], [], [], []));

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(2, runState.TombstoneSpans);
        Assert.IsEmpty(runState.IrregularTombstones);
    }


    /// <summary>A translation span's expanded dropped dots must be covered by the declared context.</summary>
    [TestMethod]
    public void FromRunStateRejectsATranslationSpanDroppedDotNotCoveredByTheContext()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaRunEntry<int> vertexRun = new(DotStateOf(new Dot(R1, 1)), null, [1]);
        RgaTranslationSpan uncoveredSpan = new(Bytes(R2), 1, 1, DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> state = new(context, [vertexRun], [], [], [], [uncoveredSpan]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(state));
    }


    /// <summary>Each of FromRunState's five required arrays fails closed independently when left default, even when every sibling array is a valid (non-default) value.</summary>
    [TestMethod]
    public void FromRunStateRejectsEachRequiredArrayDefaultIndependently()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaRunEntry<int> headRun = new(DotStateOf(new Dot(R1, 1)), null, [1]);

        RgaRunState<int> runsDefault = new(context, default, [], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(runsDefault));

        RgaRunState<int> spansDefault = new(context, [headRun], default, [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(spansDefault));

        RgaRunState<int> irregularsDefault = new(context, [headRun], [], default, [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(irregularsDefault));

        RgaRunState<int> translationsDefault = new(context, [headRun], [], [], default, []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(translationsDefault));

        RgaRunState<int> translationSpansDefault = new(context, [headRun], [], [], [], default);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(translationSpansDefault));
    }


    /// <summary>ToState orders tombstone targets by replica first: a lower counter on a lexicographically higher replica must not sort ahead of a higher counter on a lower replica.</summary>
    [TestMethod]
    public void ToStateOrdersTombstoneTargetsByReplicaBeforeCounter()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withHigh, Dot idHigh) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> r2Side, Dot idLow) = Rga<int>.Empty.InsertAtHead(9, R2);
        Rga<int> merged = withHigh.Merge(r2Side);
        Rga<int> removed = merged.Remove(idHigh, R1).Remove(idLow, R1);

        RgaState<int> state = removed.ToState();

        Assert.HasCount(2, state.Tombstones);
        AssertDotStateEquals(DotStateOf(idHigh), state.Tombstones[0].Target);
        AssertDotStateEquals(DotStateOf(idLow), state.Tombstones[1].Target);
    }


    /// <summary>FromRunState rejects a null state with ArgumentNullException.</summary>
    [TestMethod]
    public void FromRunStateRejectsNullState()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Rga<int>.FromRunState(null!));
    }


    /// <summary>
    /// Translation entries are ordered by (dropped replica, dropped counter), independent of the order the
    /// underlying dictionary happens to enumerate.
    /// </summary>
    [TestMethod]
    public void TranslationEntriesAreOrderedByDroppedReplicaThenCounterRegardlessOfInputOrder()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 2),
            new ReplicaCounterEntry(Bytes(R3), 2)]);
        RgaRunEntry<int> head1 = new(DotStateOf(new Dot(R1, 1)), null, [1]);
        RgaRunEntry<int> head3 = new(DotStateOf(new Dot(R3, 1)), null, [2]);
        RgaTranslationEntry dropOnR3 = new(DotStateOf(new Dot(R3, 2)), DotStateOf(new Dot(R3, 1)));
        RgaTranslationEntry dropOnR1 = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)));

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [head1, head3], [], [], [dropOnR3, dropOnR1], []));

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(2, runState.Translations);
        AssertDotStateEquals(DotStateOf(new Dot(R1, 2)), runState.Translations[0].Dropped);
        AssertDotStateEquals(DotStateOf(new Dot(R3, 2)), runState.Translations[1].Dropped);
    }


    /// <summary>CertifiedProjection rejects a null frontier before touching the order.</summary>
    [TestMethod]
    public void CertifiedProjectionRejectsNullFrontier()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Rga<int>.Empty.CertifiedProjection(null!));
    }


    /// <summary>A run whose expanded counters land exactly at int.MaxValue (no arithmetic overflow) still loads.</summary>
    [TestMethod]
    public void FromRunStateAcceptsARunAtTheOverflowBoundary()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), int.MaxValue)]);
        RgaRunEntry<int> boundaryRun = new(DotStateOf(new Dot(R1, int.MaxValue)), null, [1]);
        RgaRunState<int> state = new(context, [boundaryRun], [], [], [], []);

        Assert.AreEqual(1, Rga<int>.FromRunState(state).Count);
    }


    /// <summary>A tombstone span's expanded remove-dots are validated the same way FromState validates them: collision with a vertex, context coverage, and cross-tombstone uniqueness.</summary>
    [TestMethod]
    public void FromRunStateRejectsATombstoneSpansRemoveDotViolations()
    {
        VectorClockState collideContext = new([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaRunEntry<int> vertexRun = new(DotStateOf(new Dot(R2, 1)), null, [1]);
        RgaTombstoneSpan collidingSpan = new(Bytes(R1), 1, 1, Bytes(R2), 1);
        RgaRunState<int> collideState = new(collideContext, [vertexRun], [collidingSpan], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(collideState));

        VectorClockState uncoveredContext = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaTombstoneSpan uncoveredSpan = new(Bytes(R1), 1, 1, Bytes(R2), 1);
        RgaRunState<int> uncoveredState = new(uncoveredContext, [], [uncoveredSpan], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(uncoveredState));

        VectorClockState duplicateContext = new([new ReplicaCounterEntry(Bytes(R1), 2), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaTombstoneSpan spanOne = new(Bytes(R1), 1, 1, Bytes(R2), 1);
        RgaTombstoneSpan spanTwo = new(Bytes(R1), 2, 2, Bytes(R2), 1);
        RgaRunState<int> duplicateState = new(duplicateContext, [], [spanOne, spanTwo], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(duplicateState));
    }


    /// <summary>Composing a prior translation must never overwrite a fresh entry this round's own dropped-vertex pass already computed, even when the prior entry's own key gets re-dropped after a ghost resurrection.</summary>
    [TestMethod]
    public void ComposingPriorTranslationsNeverOverwritesAFreshEntryFromAResurrectedIntermediateAncestor()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withX, Dot idX) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withB, Dot idB) = withX.InsertAfter(idX, 3, R1);
        Rga<int> removedX = withB.Remove(idX, R2);
        Rga<int> removedXB = removedX.Remove(idB, R1);

        VectorClock frontier1 = removedXB.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint1 = removedXB.CertifiedProjection(frontier1);
        Rga<int> first = removedXB.Compact(frontier1, checkpoint1);

        Rga<int> merged = first.Merge(removedXB);

        VectorClock frontier2 = FrontierCovering(idA, idX, new Dot(R1, 4));
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint2 = merged.CertifiedProjection(frontier2);
        Rga<int> second = merged.Compact(frontier2, checkpoint2);

        Assert.AreEqual(idX, second.TranslateAnchor(idB));
    }


    /// <summary>ToState orders same-replica tombstone targets by ascending counter, the tie-break CompareDotsByReplica falls back to when the replica comparison is 0.</summary>
    [TestMethod]
    public void ToStateOrdersSameReplicaTombstoneTargetsByAscendingCounter()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        Rga<int> removed = withC.Remove(idC, R1).Remove(idB, R1);

        RgaState<int> state = removed.ToState();

        Assert.HasCount(2, state.Tombstones);
        AssertDotStateEquals(DotStateOf(idB), state.Tombstones[0].Target);
        AssertDotStateEquals(DotStateOf(idC), state.Tombstones[1].Target);
    }


    /// <summary>A run's declared non-null predecessor is wired onto its first minted vertex, not discarded in favor of a head insert.</summary>
    [TestMethod]
    public void FromRunStateWiresARunsDeclaredPredecessorOntoItsFirstVertex()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaRunEntry<int> head = new(DotStateOf(new Dot(R1, 1)), null, [10]);
        RgaRunEntry<int> child = new(DotStateOf(new Dot(R2, 1)), DotStateOf(new Dot(R1, 1)), [20]);
        RgaRunState<int> state = new(context, [head, child], [], [], [], []);

        int[] expected = [10, 20];
        Assert.AreSequenceEqual(expected, Rga<int>.FromRunState(state).Values.ToArray());
    }


    /// <summary>
    /// A run that starts after a shared-counter-plane gap (its Vertex.Predecessor is set, but the counter
    /// isn't contiguous with a same-replica prior vertex) must serialize that real, non-null predecessor.
    /// </summary>
    [TestMethod]
    public void ARunStartedAfterAPlaneGapRecordsItsActualPredecessor()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removedB = withB.Remove(idB, R1);
        (Rga<int> withC, _) = removedB.InsertAfter(idB, 3, R1);

        RgaRunState<int> runState = withC.ToRunState();

        Assert.HasCount(2, runState.Runs);
        Assert.IsNull(runState.Runs[0].Predecessor);
        Assert.IsNotNull(runState.Runs[1].Predecessor);
        AssertDotStateEquals(DotStateOf(idB), runState.Runs[1].Predecessor);
    }


    /// <summary>A checkpoint of the correct length but wrong content at one index must still fail closed -- only the element-wise comparison, not the length gate, can catch this.</summary>
    [TestMethod]
    public void CompactRejectsAnEqualLengthCheckpointWithWrongContent()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        VectorClock frontier = FrontierCovering(idA, idB);

        ImmutableArray<SequenceCheckpointEntry<int>> wrongContent = [new SequenceCheckpointEntry<int>(DotStateOf(idA), 99), new SequenceCheckpointEntry<int>(DotStateOf(idB), 2)];

        Assert.ThrowsExactly<InvalidOperationException>(() => withB.Compact(frontier, wrongContent));
    }


    /// <summary>Two dropped dots that are counter-consecutive on one replica but resolve to different retained ancestors must serialize as two singleton translations, never one coalesced span.</summary>
    [TestMethod]
    public void ToRunStateNeverCoalescesConsecutiveDroppedDotsWithDifferentTargetsIntoOneSpan()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withQ, Dot idQ) = withA.InsertAfter(idA, 2, R2);
        (Rga<int> withX, Dot idX) = withQ.InsertAfter(idA, 3, R1);
        (Rga<int> withY, Dot idY) = withX.InsertAfter(idQ, 4, R1);
        Rga<int> removed = withY.Remove(idX, R1).Remove(idY, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        RgaRunState<int> runState = compacted.ToRunState();
        Assert.IsEmpty(runState.TranslationSpans);
        Assert.HasCount(2, runState.Translations);

        Rga<int> roundTripped = Rga<int>.FromRunState(runState);
        Assert.AreEqual(idA, roundTripped.TranslateAnchor(idX));
        Assert.AreEqual(idQ, roundTripped.TranslateAnchor(idY));
    }


    /// <summary>
    /// Two arrays with the same vertices but a tombstone on a different target (same tombstone count) must
    /// not be Equal.
    /// </summary>
    [TestMethod]
    public void DifferentTombstonedTargetsMakeTwoOtherwiseIdenticalArraysUnequal()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 2)]);
        RgaVertexEntry<int> vertexA = new(DotStateOf(new Dot(R1, 1)), null, 1);
        RgaVertexEntry<int> vertexB = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)), 2);
        RgaTombstoneEntry tombstoneA = new(DotStateOf(new Dot(R1, 1)), []);
        RgaTombstoneEntry tombstoneB = new(DotStateOf(new Dot(R1, 2)), []);

        Rga<int> tombstonedA = Rga<int>.FromState(new RgaState<int>(context, [vertexA, vertexB], [tombstoneA]));
        Rga<int> tombstonedB = Rga<int>.FromState(new RgaState<int>(context, [vertexA, vertexB], [tombstoneB]));

        Assert.AreNotEqual(tombstonedA, tombstonedB);
    }


    /// <summary>A run's vertex dot must be covered by the declared context, mirroring FromState's coverage guard.</summary>
    [TestMethod]
    public void FromRunStateRejectsAVertexDotNotCoveredByTheContext()
    {
        VectorClockState context = new([]);
        RgaRunEntry<int> run = new(DotStateOf(new Dot(R1, 1)), null, [10]);
        RgaRunState<int> state = new(context, [run], [], [], [], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(state));
    }


    /// <summary>A tombstone span's three bound clauses (TargetFrom>=1, TargetTo>=TargetFrom, RemoveFrom>=1) each independently reject.</summary>
    [TestMethod]
    public void FromRunStateRejectsATombstoneSpanViolatingAnyOneBoundClause()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 5), new ReplicaCounterEntry(Bytes(R2), 5)]);

        RgaTombstoneSpan targetFromTooLow = new(Bytes(R1), 0, 0, Bytes(R2), 1);
        RgaRunState<int> targetFromState = new(context, [], [targetFromTooLow], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(targetFromState));

        RgaTombstoneSpan targetToBelowFrom = new(Bytes(R1), 2, 1, Bytes(R2), 1);
        RgaRunState<int> targetToState = new(context, [], [targetToBelowFrom], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(targetToState));

        RgaTombstoneSpan removeFromTooLow = new(Bytes(R1), 1, 1, Bytes(R2), 0);
        RgaRunState<int> removeFromState = new(context, [], [removeFromTooLow], [], [], []);
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(removeFromState));
    }


    /// <summary>Compact rejects a null frontier before any retention or checkpoint work.</summary>
    [TestMethod]
    public void CompactRejectsNullFrontier()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Rga<int>.Empty.Compact(null!, ImmutableArray<SequenceCheckpointEntry<int>>.Empty));
    }


    /// <summary>A default Translations array must fail closed even when every other array (including TranslationSpans) is a valid, non-default array.</summary>
    [TestMethod]
    public void FromRunStateRejectsADefaultTranslationsArrayEvenWhenTranslationSpansIsPresent()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaRunEntry<int> head = new(DotStateOf(new Dot(R1, 1)), null, [1]);
        RgaRunState<int> onlyTranslationsDefault = new(context, [head], [], [], default, []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(onlyTranslationsDefault));
    }


    /// <summary>FromRunState still runs the predecessor-cycle guard: two runs whose predecessors point at each other are rejected, not silently accepted as an inconsistent vertex graph.</summary>
    [TestMethod]
    public void FromRunStateRejectsACyclicPredecessorGraph()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaRunEntry<int> first = new(DotStateOf(new Dot(R1, 1)), DotStateOf(new Dot(R2, 1)), [10]);
        RgaRunEntry<int> second = new(DotStateOf(new Dot(R2, 1)), DotStateOf(new Dot(R1, 1)), [20]);
        RgaRunState<int> state = new(context, [first, second], [], [], [], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(state));
    }


    /// <summary>
    /// Two arrays with the same vertices and the same CompactedPredecessors key but a different target dot
    /// must not be Equal, even though their CompactedPredecessors counts match.
    /// </summary>
    [TestMethod]
    public void UnequalCompactedPredecessorTargetsMakeTwoOtherwiseIdenticalArraysUnequal()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 3)]);
        RgaRunEntry<int> runA = new(DotStateOf(new Dot(R1, 1)), null, [1]);
        RgaRunEntry<int> runC = new(DotStateOf(new Dot(R1, 2)), null, [2]);
        RgaTranslationEntry droppedToA = new(DotStateOf(new Dot(R1, 3)), DotStateOf(new Dot(R1, 1)));
        RgaTranslationEntry droppedToC = new(DotStateOf(new Dot(R1, 3)), DotStateOf(new Dot(R1, 2)));

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [runA, runC], [], [], [droppedToA], []));
        Rga<int> y = Rga<int>.FromRunState(new RgaRunState<int>(context, [runA, runC], [], [], [droppedToC], []));

        Assert.AreNotEqual(x, y);
        Assert.AreNotEqual(x.GetHashCode(), y.GetHashCode());
    }


    /// <summary>
    /// A same-replica, counter-contiguous tombstone target whose remove-dot breaks both replica and counter
    /// continuity from the run's remove-dot must not be folded into the same span.
    /// </summary>
    [TestMethod]
    public void TombstoneSpansBreakWhenTheNextRemoveDotBreaksBothReplicaAndCounterContinuity()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 6),
            new ReplicaCounterEntry(Bytes(R2), 10),
            new ReplicaCounterEntry(Bytes(R3), 50)]);
        RgaRunEntry<int> vertex1 = new(DotStateOf(new Dot(R1, 5)), null, [1]);
        RgaRunEntry<int> vertex2 = new(DotStateOf(new Dot(R1, 6)), null, [2]);
        RgaTombstoneSpan span1 = new(Bytes(R1), 5, 5, Bytes(R2), 10);
        RgaTombstoneSpan span2 = new(Bytes(R1), 6, 6, Bytes(R3), 50);

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [vertex1, vertex2], [span1, span2], [], [], []));

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(2, runState.TombstoneSpans);
        RgaTombstoneSpan first = runState.TombstoneSpans[0];
        Assert.AreEqual(5, first.TargetTo);
        Assert.IsTrue(first.RemoveReplica.AsSpan().SequenceEqual(R2.AsSpan()));
    }


    /// <summary>
    /// GetHashCode over many vertices must reflect a change to even one vertex's value, exercised with
    /// enough vertices that a wrong fold operator's degeneracy would be observable.
    /// </summary>
    [TestMethod]
    public void VerticesHashCodeReflectsAChangedValueAmongManyVertices()
    {
        const int Count = 20;
        (Rga<int> baseline, Dot last) = Rga<int>.Empty.InsertAtHead(0, R1);
        for (int i = 1; i < Count; i++)
        {
            (baseline, last) = baseline.InsertAfter(last, i, R1);
        }

        (Rga<int> altered, Dot alteredLast) = Rga<int>.Empty.InsertAtHead(0, R1);
        for (int i = 1; i < Count; i++)
        {
            int value = i == Count - 1 ? -1 : i;
            (altered, alteredLast) = altered.InsertAfter(alteredLast, value, R1);
        }

        Assert.AreNotEqual(baseline, altered);
        Assert.AreNotEqual(baseline.GetHashCode(), altered.GetHashCode());
    }


    /// <summary>
    /// A dropped dot that is also a live (tombstoned) vertex — the ghost-plus-witness shape — must always
    /// serialize as its own singleton translation entry, never absorbed into a span with contiguous,
    /// purely-dropped siblings that share its target.
    /// </summary>
    [TestMethod]
    public void AWitnessDropCannotBeCoalescedIntoASpanWithItsPurelyDroppedSiblings()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 4),
            new ReplicaCounterEntry(Bytes(R3), 1)]);
        RgaRunEntry<int> vertexA = new(DotStateOf(new Dot(R1, 1)), null, [1]);
        RgaRunEntry<int> vertexB = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)), [2]);
        RgaTombstoneSpan tombstoneB = new(Bytes(R1), 2, 2, Bytes(R3), 1);
        RgaTranslationEntry witnessBToA = new(DotStateOf(new Dot(R1, 2)), DotStateOf(new Dot(R1, 1)));
        RgaTranslationEntry cToA = new(DotStateOf(new Dot(R1, 3)), DotStateOf(new Dot(R1, 1)));
        RgaTranslationEntry dToA = new(DotStateOf(new Dot(R1, 4)), DotStateOf(new Dot(R1, 1)));

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(
            context, [vertexA, vertexB], [tombstoneB], [], [witnessBToA, cToA, dToA], []));

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(1, runState.Translations);
        AssertDotStateEquals(DotStateOf(new Dot(R1, 2)), runState.Translations[0].Dropped);
        Assert.HasCount(1, runState.TranslationSpans);
        RgaTranslationSpan span = runState.TranslationSpans[0];
        Assert.AreEqual(3, span.FromCounter);
        Assert.AreEqual(4, span.ToCounter);
    }


    /// <summary>
    /// Tombstone spans are ordered by (target replica, target counter), independent of the order the
    /// underlying dictionary happens to enumerate. The two spans carry distinct remove-dots so both survive
    /// FromRunState's one-remove-dot-per-tombstone validation.
    /// </summary>
    [TestMethod]
    public void TombstoneSpansAreOrderedByTargetReplicaThenCounterRegardlessOfInputOrder()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 1),
            new ReplicaCounterEntry(Bytes(R3), 1),
            new ReplicaCounterEntry(Bytes(R2), 2)]);
        RgaRunEntry<int> vertex1 = new(DotStateOf(new Dot(R1, 1)), null, [1]);
        RgaRunEntry<int> vertex3 = new(DotStateOf(new Dot(R3, 1)), null, [2]);
        RgaTombstoneSpan spanR3 = new(Bytes(R3), 1, 1, Bytes(R2), 1);
        RgaTombstoneSpan spanR1 = new(Bytes(R1), 1, 1, Bytes(R2), 2);

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [vertex1, vertex3], [spanR3, spanR1], [], [], []));

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(2, runState.TombstoneSpans);
        Assert.IsTrue(runState.TombstoneSpans[0].TargetReplica.AsSpan().SequenceEqual(R1.AsSpan()));
        Assert.IsTrue(runState.TombstoneSpans[1].TargetReplica.AsSpan().SequenceEqual(R3.AsSpan()));
    }


    /// <summary>Two translation spans that both claim the same dropped dot for different (both valid) targets must fail closed.</summary>
    [TestMethod]
    public void FromRunStateRejectsATranslationSpanDroppedDotClaimedByTwoSpans()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 2), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaRunEntry<int> chain = new(DotStateOf(new Dot(R1, 1)), null, [1, 2]);
        RgaTranslationSpan spanToFirst = new(Bytes(R2), 1, 1, DotStateOf(new Dot(R1, 1)));
        RgaTranslationSpan spanToSecond = new(Bytes(R2), 1, 1, DotStateOf(new Dot(R1, 2)));
        RgaRunState<int> state = new(context, [chain], [], [], [], [spanToFirst, spanToSecond]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(state));
    }


    /// <summary>
    /// Two same-replica, counter-contiguous dropped dots that resolve to different retained targets must not
    /// coalesce into one translation span, which can only carry a single target.
    /// </summary>
    [TestMethod]
    public void TranslationsWithDifferentTargetsDoNotCoalesceEvenWhenCountersAreContiguous()
    {
        (Rga<int> withA1, Dot idA1) = Rga<int>.Empty.InsertAtHead(10, R2);
        (Rga<int> withA2, Dot idA2) = withA1.InsertAtHead(20, R3);
        (Rga<int> withB, Dot idB) = withA2.InsertAfter(idA1, 1, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idA2, 2, R1);
        Rga<int> removed = withC.Remove(idB, R2).Remove(idC, R2);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        RgaRunState<int> runState = compacted.ToRunState();

        Assert.HasCount(2, runState.Translations);
        Assert.IsEmpty(runState.TranslationSpans);
        Assert.AreEqual(idA1, compacted.TranslateAnchor(idB));
        Assert.AreEqual(idA2, compacted.TranslateAnchor(idC));
    }


    /// <summary>
    /// An irregular (concurrent) tombstone's remove-dots serialize ordered by (replica, counter),
    /// independent of the order the underlying FrozenSet happens to enumerate.
    /// </summary>
    [TestMethod]
    public void IrregularTombstoneRemoveDotsAreOrderedByReplicaThenCounterRegardlessOfInputOrder()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> byR3 = withA.Remove(idA, R3);
        Rga<int> byR1 = withA.Remove(idA, R1);
        Rga<int> x = byR3.Merge(byR1);

        RgaRunState<int> runState = x.ToRunState();

        Assert.HasCount(1, runState.IrregularTombstones);
        ImmutableArray<DotState> removeDots = runState.IrregularTombstones[0].RemoveDots;
        Assert.HasCount(2, removeDots);
        AssertDotStateEquals(DotStateOf(new Dot(R1, 2)), removeDots[0]);
        AssertDotStateEquals(DotStateOf(new Dot(R3, 1)), removeDots[1]);
    }


    /// <summary>A run's first counter of exactly zero is rejected as non-positive.</summary>
    [TestMethod]
    public void FromRunStateRejectsARunWithAZeroFirstCounter()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1)]);
        RgaRunEntry<int> zeroCounterRun = new(DotStateOf(new Dot(R1, 0)), null, [1]);
        RgaRunState<int> state = new(context, [zeroCounterRun], [], [], [], []);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(state));
    }


    /// <summary>ToState orders the remove-dots within one tombstone entry by replica before counter, matching CompareDotsByReplica.</summary>
    [TestMethod]
    public void ToStateOrdersRemoveDotsWithinATombstoneByReplicaBeforeCounter()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> removedByR2 = withA.Remove(idA, R2);
        Rga<int> removedByR1 = withA.Remove(idA, R1);
        Rga<int> merged = removedByR2.Merge(removedByR1);

        RgaState<int> state = merged.ToState();

        Assert.HasCount(1, state.Tombstones);
        ImmutableArray<DotState> removeDots = state.Tombstones[0].RemoveDots;
        Assert.HasCount(2, removeDots);
        Assert.AreEqual(R1, ReplicaId.FromSpan(removeDots[0].Replica.AsSpan()));
        Assert.AreEqual(R2, ReplicaId.FromSpan(removeDots[1].Replica.AsSpan()));
    }


    /// <summary>Compact rejects a default checkpoint array as an absent field, distinct from an explicitly empty one.</summary>
    [TestMethod]
    public void CompactRejectsDefaultCheckpoint()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.Empty.Compact(VectorClock.Empty, default));
    }


    /// <summary>
    /// A tombstone target with a concurrent (multi-dot) remove-dot set must never be silently folded into a
    /// span using only one of its remove-dots; it must round-trip with all of its remove-dots intact.
    /// </summary>
    [TestMethod]
    public void TombstoneSpansDoNotSilentlyDropAConcurrentSecondRemoveDot()
    {
        VectorClockState context = new([
            new ReplicaCounterEntry(Bytes(R1), 6),
            new ReplicaCounterEntry(Bytes(R2), 11),
            new ReplicaCounterEntry(Bytes(R3), 99)]);
        RgaRunEntry<int> vertex1 = new(DotStateOf(new Dot(R1, 5)), null, [1]);
        RgaRunEntry<int> vertex2 = new(DotStateOf(new Dot(R1, 6)), null, [2]);
        RgaTombstoneSpan span1 = new(Bytes(R1), 5, 5, Bytes(R2), 10);
        RgaConcurrentTombstone concurrent2 = new(
            DotStateOf(new Dot(R1, 6)),
            [DotStateOf(new Dot(R2, 11)), DotStateOf(new Dot(R3, 99))]);

        Rga<int> x = Rga<int>.FromRunState(new RgaRunState<int>(context, [vertex1, vertex2], [span1], [concurrent2], [], []));

        RgaRunState<int> runState = x.ToRunState();
        Rga<int> roundTripped = Rga<int>.FromRunState(runState);

        Assert.AreEqual(x, roundTripped);
    }


    /// <summary>
    /// GetHashCode over many different tombstoned targets must reflect which elements are removed, not just
    /// how many — exercised with enough targets that a wrong fold operator's degeneracy would be observable.
    /// </summary>
    [TestMethod]
    public void TombstonesHashCodeReflectsWhichElementsAreRemoved()
    {
        const int Count = 20;
        (Rga<int> chain, Dot last) = Rga<int>.Empty.InsertAtHead(0, R1);
        var dots = new List<Dot> { last };
        for (int i = 1; i < Count; i++)
        {
            (chain, last) = chain.InsertAfter(last, i, R1);
            dots.Add(last);
        }

        Rga<int> removedEven = chain;
        Rga<int> removedOdd = chain;
        for (int i = 0; i < Count; i++)
        {
            if (i % 2 == 0)
            {
                removedEven = removedEven.Remove(dots[i], R2);
            }
            else
            {
                removedOdd = removedOdd.Remove(dots[i], R2);
            }
        }

        Assert.AreNotEqual(removedEven, removedOdd);
        Assert.AreNotEqual(removedEven.GetHashCode(), removedOdd.GetHashCode());
    }


    /// <summary>
    /// Runs are ordered by (start replica, start counter), independent of the order vertices from
    /// different replicas were inserted or the dictionary happens to enumerate them.
    /// </summary>
    [TestMethod]
    public void RunsAreOrderedByReplicaThenCounterRegardlessOfInsertionOrder()
    {
        (Rga<int> withR2, Dot idR2) = Rga<int>.Empty.InsertAtHead(2, R2);
        (Rga<int> withR3, Dot idR3) = withR2.InsertAtHead(3, R3);
        (Rga<int> withR1, Dot idR1) = withR3.InsertAtHead(1, R1);

        RgaRunState<int> runState = withR1.ToRunState();

        Assert.HasCount(3, runState.Runs);
        AssertDotStateEquals(DotStateOf(idR1), runState.Runs[0].First);
        AssertDotStateEquals(DotStateOf(idR2), runState.Runs[1].First);
        AssertDotStateEquals(DotStateOf(idR3), runState.Runs[2].First);
    }


    /// <summary>A translation span whose target names a dot that never arrived as a vertex must fail closed.</summary>
    [TestMethod]
    public void FromRunStateRejectsATranslationSpanTargetThatIsNotAVertex()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R2), 5)]);
        RgaTranslationSpan danglingSpanTarget = new(Bytes(R2), 1, 1, DotStateOf(new Dot(R2, 5)));
        RgaRunState<int> state = new(context, [], [], [], [], [danglingSpanTarget]);

        Assert.ThrowsExactly<ArgumentException>(() => Rga<int>.FromRunState(state));
    }


    /// <summary>A translation span at its bound boundaries (FromCounter == 1, ToCounter == FromCounter) still loads.</summary>
    [TestMethod]
    public void FromRunStateAcceptsATranslationSpanAtItsBoundaries()
    {
        VectorClockState context = new([new ReplicaCounterEntry(Bytes(R1), 1), new ReplicaCounterEntry(Bytes(R2), 1)]);
        RgaRunEntry<int> vertexRun = new(DotStateOf(new Dot(R1, 1)), null, [1]);

        RgaTranslationSpan singleElementAtBoundary = new(Bytes(R2), 1, 1, DotStateOf(new Dot(R1, 1)));
        RgaRunState<int> state = new(context, [vertexRun], [], [], [], [singleElementAtBoundary]);

        Assert.AreEqual(1, Rga<int>.FromRunState(state).Count);
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


    /// <summary>
    /// Compares two dot states by content. A <see cref="DotState"/> is a record over an
    /// <see cref="ImmutableArray{T}"/>, whose default equality compares the underlying array by reference, so a
    /// reconstructed state never equals an expected one under <c>AreEqual</c> even with identical bytes.
    /// </summary>
    private static void AssertDotStateEquals(DotState expected, DotState? actual)
    {
        Assert.IsNotNull(actual);
        Assert.IsTrue(expected.Replica.AsSpan().SequenceEqual(actual.Replica.AsSpan()), "DotState replica bytes differ.");
        Assert.AreEqual(expected.Counter, actual.Counter);
    }


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
