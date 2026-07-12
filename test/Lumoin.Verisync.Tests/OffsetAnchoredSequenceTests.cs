using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Focused coverage of <see cref="OffsetAnchoredSequence{TValue}"/> under offset.v2: both removal
/// kinds are dotted events that tick the context, compaction acts on the four-way retention taxonomy
/// (unstable, stable-visible, stable-tombstoned-uncertified, stable-tombstoned-certified), the
/// checkpoint is the dotted certified projection, and stale operands fail closed on the generation
/// fence or the stale-replay detector. Compaction requires an insert-quiescent frontier (§17): a state
/// carrying an unstable vertex — the only way to reach the ghost or the retained-child branches — fails
/// closed, so those branches are exercised here through the guard throw, and the quiescent conversions
/// and drop through the materialized base.
/// </summary>
[TestClass]
internal sealed class OffsetAnchoredSequenceTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);

    private static ImmutableArray<string> Base { get; } = ["b0", "b1", "b2"];


    [TestMethod]
    public void WithBaseShowsTheBaseInOrder()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        string[] expected = ["b0", "b1", "b2"];
        CollectionAssert.AreEqual(expected, sequence.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterABaseOffsetLandsImmediatelyAfterIt()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        (OffsetAnchoredSequence<string> inserted, _) = sequence.InsertAfter(OffsetAnchor.AtBase(1), "x", R1);

        string[] expected = ["b0", "b1", "x", "b2"];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void InsertAtHeadLandsBeforeTheBase()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        (OffsetAnchoredSequence<string> inserted, _) = sequence.InsertAtHead("x", R1);

        string[] expected = ["x", "b0", "b1", "b2"];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterALiveElementChains()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        (OffsetAnchoredSequence<string> chained, _) = sequence.InsertAfter(x, "y", R1);

        string[] expected = ["b0", "x", "y", "b1", "b2"];
        CollectionAssert.AreEqual(expected, chained.Values.ToArray());
    }


    [TestMethod]
    public void RemoveHidesABaseElementKeepsItsAnchorAndTicksTheContext()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        OffsetAnchoredSequence<string> removed = sequence.Remove(OffsetAnchor.AtBase(1), R1);

        //A base removal is now a dotted event on the remover's axis.
        Assert.AreEqual(1, removed.CausalContext[R1]);

        (OffsetAnchoredSequence<string> inserted, _) = removed.InsertAfter(OffsetAnchor.AtBase(1), "x", R1);

        //b1 is hidden yet still anchors: x sits where b1 was, between b0 and b2.
        string[] expected = ["b0", "x", "b2"];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void RemoveTombstonesALiveElementAndTicksTheContext()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        OffsetAnchoredSequence<string> removed = sequence.Remove(x, R1);

        //The remove minted a dot past the insert on the remover's axis.
        Assert.AreEqual(2, removed.CausalContext[R1]);

        string[] expected = ["b0", "b1", "b2"];
        CollectionAssert.AreEqual(expected, removed.Values.ToArray());
    }


    [TestMethod]
    public void ConcurrentInsertsAtTheSameBaseAnchorConvergeAcrossReplicas()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> byFirst, _) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        (OffsetAnchoredSequence<string> bySecond, _) = shared.InsertAfter(OffsetAnchor.AtBase(0), "y", R2);

        OffsetAnchoredSequence<string> merged = byFirst.Merge(bySecond);

        CollectionAssert.AreEqual(merged.Values.ToArray(), bySecond.Merge(byFirst).Values.ToArray());
        Assert.HasCount(5, merged.Values);
        Assert.AreEqual("b0", merged.Values[0]);
    }


    [TestMethod]
    public void InsertAfterMergedStateLandsImmediatelyAfterItsAnchor()
    {
        //R1 hangs a chain off base[0]; R2 merges and inserts at base[0]: the fresh Lamport identity
        //dominates the observed chain, so the insert lands immediately after b0.
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> withChain, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        (withChain, _) = withChain.InsertAfter(x, "y", R1);

        (OffsetAnchoredSequence<string> merged, _) = shared.Merge(withChain).InsertAfter(OffsetAnchor.AtBase(0), "z", R2);

        string[] expected = ["b0", "z", "x", "y", "b1", "b2"];
        CollectionAssert.AreEqual(expected, merged.Values.ToArray());
    }


    [TestMethod]
    public void MergingDifferentGenerationsFailsClosed()
    {
        //Two fresh generations share the genesis identity but carry divergent base values, which the
        //BaseEqual integrity assertion rejects as forged or corrupt.
        OffsetAnchoredSequence<string> first = OffsetAnchoredSequence<string>.WithBase(Base);
        OffsetAnchoredSequence<string> second = OffsetAnchoredSequence<string>.WithBase(["other"]);

        Assert.ThrowsExactly<InvalidOperationException>(() => first.Merge(second));
    }


    [TestMethod]
    public void AnchorsAndArgumentsAreValidated()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        Dot foreign = new(R2, 9);

        Assert.ThrowsExactly<ArgumentException>(() => sequence.InsertAfter(OffsetAnchor.AtBase(3), "x", R1));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.InsertAfter(OffsetAnchor.AtLive(foreign), "x", R1));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Remove(OffsetAnchor.Head, R1));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Remove(OffsetAnchor.AtBase(3), R1));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Remove(null!, R1));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Merge(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OffsetAnchor.AtBase(-1));
        Assert.ThrowsExactly<ArgumentNullException>(() => OffsetAnchor.AtLive(null!));
    }


    [TestMethod]
    public void VisibleElementsPairEveryValueWithItsAnchor()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        IReadOnlyList<(OffsetAnchor Anchor, string Value)> visible = sequence.VisibleElements;

        Assert.HasCount(4, visible);
        Assert.AreEqual(OffsetAnchor.AtBase(0), visible[0].Anchor);
        Assert.AreEqual(x, visible[1].Anchor);
        Assert.AreEqual("x", visible[1].Value);
    }


    [TestMethod]
    public void CompactConvertsStableVisibleVerticesIntoBaseEntries()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //The visible values are unchanged and x now lives in the base at its linearization position.
        string[] expectedValues = ["b0", "x", "b1", "b2"];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        CollectionAssert.AreEqual(expectedValues, compacted.Base.ToArray());
    }


    [TestMethod]
    public void CompactKeepsAnUncertifiedRemovedBaseEntryHiddenInTheNewBase()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(1), "x", R1);
        sequence = sequence.Remove(OffsetAnchor.AtBase(1), R2);

        //The frontier certifies x's insert but not R2's base removal, so the removed slot stays in the
        //certified projection — the determinism inclusion — and in the new base, hidden and re-marked.
        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        Assert.HasCount(4, checkpoint);
        Assert.AreEqual("b1", checkpoint[1].Value);

        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        string[] expectedBase = ["b0", "b1", "x", "b2"];
        string[] expectedValues = ["b0", "x", "b2"];
        CollectionAssert.AreEqual(expectedBase, compacted.Base.ToArray());
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
    }


    [TestMethod]
    public void CompactWithACheckpointThatDoesNotMatchFailsClosed()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        VectorClock frontier = FrontierCovering(x.LiveId!);

        //A checkpoint projected at a different frontier misses the stable x.
        ImmutableArray<SequenceCheckpointEntry<string>> staleCheckpoint = sequence.CertifiedProjection(VectorClock.Empty);
        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, staleCheckpoint));

        //The integrity check is dot-aware: the same values under a forged identity fail closed too.
        ImmutableArray<SequenceCheckpointEntry<string>> proper = sequence.CertifiedProjection(frontier);
        SequenceCheckpointEntry<string>[] forged = proper.ToArray();
        forged[1] = new SequenceCheckpointEntry<string>(DotStateOf(new Dot(R2, 9)), forged[1].Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, ImmutableArray.Create(forged)));
    }


    //RE-POINTED under §17: a certified-removed parent kept alive because its child is retained is the
    //ghost the taxonomy describes, but it can only exist above the waterline — its child is unstable — so
    //compaction fails closed rather than materialize it. The certified projection is unrestricted and
    //still excludes the certified-removed parent.
    [TestMethod]
    public void CompactFailsClosedOnACertifiedTombstoneThatStillRootsAnUnstableChild()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor parent) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "p", R1);
        (sequence, _) = sequence.InsertAfter(parent, "c", R1);
        sequence = sequence.Remove(parent, R2);

        //The parent's remove is CERTIFIED — R2's first event is the remove-dot — while the child stays
        //above the frontier as an unstable vertex, so the state is not insert-quiescent.
        VectorClock frontier = FrontierCovering(parent.LiveId!, new Dot(R2, 1));
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        Assert.HasCount(3, checkpoint);

        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, checkpoint));
    }


    //RE-POINTED under §17: an uncertified-removed stable vertex converting pending-removed WITH a retained
    //child would re-anchor that child at the gap — but the child is unstable, so the state is not
    //insert-quiescent and compaction fails closed. The pending-removed conversion itself is exercised
    //quiescently by the certification law suite. The certified projection is unrestricted and still
    //carries the uncertified-removed parent with its real dot.
    [TestMethod]
    public void CompactFailsClosedOnAnUncertifiedTombstoneWithAnUnstableChild()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor parent) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "p", R1);
        (sequence, _) = sequence.InsertAfter(parent, "c", R1);
        sequence = sequence.Remove(parent, R2);

        //The frontier covers p's insert but neither R2's remove nor the child.
        VectorClock frontier = FrontierCovering(parent.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);

        //The uncertified-removed p stays in the projection with its real dot; the checkpoint entry's
        //custom value equality compares the replica bytes by content.
        Assert.HasCount(4, checkpoint);
        Assert.AreEqual(new SequenceCheckpointEntry<string>(DotStateOf(parent.LiveId!), "p"), checkpoint[1]);

        //The child is unstable, so the base-materializing compaction fails closed.
        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, checkpoint));
    }


    [TestMethod]
    public void CompactDropsACertifiedTombstoneWithNoRetainedDescendants()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor tombstoned) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "t", R1);
        sequence = sequence.Remove(tombstoned, R1);

        //The state's own context certifies both the insert and the remove.
        VectorClock frontier = sequence.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //The dropped tombstone's anchor still translates to a servable position, but the vertex is gone.
        Assert.IsNotNull(compacted.TranslateAnchor(tombstoned));
        Assert.ThrowsExactly<ArgumentException>(() => compacted.InsertAfter(tombstoned, "z", R2));
    }


    [TestMethod]
    public void CompactTranslatesPreviousGenerationBaseOffsets()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //b1 sat at base offset 1 before compaction and at offset 2 after x converted ahead of it.
        OffsetAnchor? translated = compacted.TranslateAnchor(OffsetAnchor.AtBase(1));
        Assert.AreEqual(OffsetAnchor.AtBase(2), translated);

        (OffsetAnchoredSequence<string> inserted, _) = compacted.InsertAfter(translated!, "after-b1", R2);
        string[] expected = ["b0", "x", "b1", "after-b1", "b2"];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void TwoSuccessiveCompactionsStillTranslateAFirstGenerationDot()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        //First compaction folds x at an insert-quiescent frontier.
        VectorClock firstFrontier = FrontierCovering(x.LiveId!);
        OffsetAnchoredSequence<string> first = sequence.Compact(firstFrontier, sequence.CertifiedProjection(firstFrontier));

        //A second generation: y is inserted AFTER the first compaction, then folded at a frontier that
        //covers it — each compaction stays insert-quiescent, which §17 requires.
        (OffsetAnchoredSequence<string> withY, OffsetAnchor y) = first.InsertAfter(OffsetAnchor.AtBase(2), "y", R2);
        VectorClock secondFrontier = FrontierCovering(x.LiveId!, y.LiveId!);
        OffsetAnchoredSequence<string> second = withY.Compact(secondFrontier, withY.CertifiedProjection(secondFrontier));

        //The dot folded away in the first generation is still translatable after the second, by map composition.
        Assert.IsNotNull(second.TranslateAnchor(x));
    }


    [TestMethod]
    public void RepeatedCompactionAtTheSameWaterlineIsANoOp()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> once = sequence.Compact(frontier, checkpoint);

        OffsetAnchoredSequence<string> twice = once.Compact(frontier, checkpoint);

        Assert.AreEqual(once, twice);
    }


    [TestMethod]
    public void IndependentCompactionsAtTheSameWaterlineMergeWhileMixedGenerationsFailClosed()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        //Both members stay insert-quiescent at the frontier and diverge only by removing different base
        //slots above it — a suffix insert would raise an unstable vertex and fail closed under §17.
        OffsetAnchoredSequence<string> a = shared.Remove(OffsetAnchor.AtBase(1), R1);
        OffsetAnchoredSequence<string> b = shared.Remove(OffsetAnchor.AtBase(2), R2);

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = a.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compactedA = a.Compact(frontier, checkpoint);
        OffsetAnchoredSequence<string> compactedB = b.Compact(frontier, checkpoint);

        OffsetAnchoredSequence<string> merged = compactedA.Merge(compactedB);

        CollectionAssert.AreEqual(merged.Values.ToArray(), compactedB.Merge(compactedA).Values.ToArray());
        Assert.AreEqual(merged, compactedB.Merge(compactedA));

        //An uncompacted operand carries the previous generation identity, which the fence rejects.
        Assert.ThrowsExactly<InvalidOperationException>(() => compactedA.Merge(a));
    }


    //The map's §5a trace, green-but-unsound before offset.v2: a laggard that never saw the remove slips
    //the base gate (a tombstone-only drop leaves the base unchanged) and the union used to resurrect the
    //element cluster-wide. The stale-replay detector now fails it closed in both merge orders.
    [TestMethod]
    public void AStalePreRemoveLaggardFailsClosedAgainstACompactedRemove()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> withX, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        OffsetAnchoredSequence<string> laggard = withX;
        OffsetAnchoredSequence<string> removed = withX.Remove(x, R1);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = removed.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = removed.Compact(frontier, checkpoint);

        Assert.ThrowsExactly<InvalidOperationException>(() => compacted.Merge(laggard));
        Assert.ThrowsExactly<InvalidOperationException>(() => laggard.Merge(compacted));
    }


    [TestMethod]
    public void TranslateAnchorIsTheIdentityForServableAnchorsOnANeverCompactedSequence()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);
        Dot unknown = new(R2, 99);

        Assert.AreEqual(OffsetAnchor.Head, sequence.TranslateAnchor(OffsetAnchor.Head));
        Assert.AreEqual(OffsetAnchor.AtBase(0), sequence.TranslateAnchor(OffsetAnchor.AtBase(0)));
        Assert.AreEqual(x, sequence.TranslateAnchor(x));
        Assert.IsNull(sequence.TranslateAnchor(OffsetAnchor.AtLive(unknown)));
        Assert.IsNull(sequence.TranslateAnchor(OffsetAnchor.AtBase(99)));
    }


    [TestMethod]
    public void CompactAndTranslateAnchorValidateTheirArguments()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        ImmutableArray<SequenceCheckpointEntry<string>> emptyCheckpoint = [];

        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Compact(null!, emptyCheckpoint));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Compact(VectorClock.Empty, default));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.TranslateAnchor(null!));
    }


    private static DotState DotStateOf(Dot dot) => new(ImmutableArray.Create(dot.Replica.AsSpan()), dot.Counter);


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
