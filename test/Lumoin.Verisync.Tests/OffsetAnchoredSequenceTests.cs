using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

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
    public void RemoveHidesABaseElementButKeepsItsAnchor()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        OffsetAnchoredSequence<string> removed = sequence.Remove(OffsetAnchor.AtBase(1));
        (OffsetAnchoredSequence<string> inserted, _) = removed.InsertAfter(OffsetAnchor.AtBase(1), "x", R1);

        //b1 is hidden yet still anchors: x sits where b1 was, between b0 and b2.
        string[] expected = ["b0", "x", "b2"];
        CollectionAssert.AreEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void RemoveTombstonesALiveElement()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        OffsetAnchoredSequence<string> removed = sequence.Remove(x);

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
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Remove(OffsetAnchor.Head));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Remove(null!));
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
        ImmutableArray<string> checkpoint = ["b0", "x", "b1", "b2"];
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //The visible values are unchanged and x now lives in the base at its linearization position.
        string[] expectedValues = ["b0", "x", "b1", "b2"];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        CollectionAssert.AreEqual(expectedValues, compacted.Base.ToArray());
    }


    [TestMethod]
    public void CompactKeepsRemovedBaseEntriesWhileFoldingInTheCheckpoint()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(1), "x", R1);
        sequence = sequence.Remove(OffsetAnchor.AtBase(1));

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<string> checkpoint = ["b0", "x", "b2"];
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //The removed b1 is kept in the new base so re-anchored children stay in their sibling set; the
        //checkpoint is the new base minus that still-removed offset.
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
        ImmutableArray<string> wrongCheckpoint = ["b0", "b1", "b2"];

        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, wrongCheckpoint));
    }


    [TestMethod]
    public void CompactRetainsAStableTombstoneThatStillRootsAnUnstableChild()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor parent) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "p", R1);
        (sequence, OffsetAnchor child) = sequence.InsertAfter(parent, "c", R1);
        sequence = sequence.Remove(parent);

        //The parent is stable and tombstoned; the child is above the frontier and keeps it alive.
        VectorClock frontier = FrontierCovering(parent.LiveId!);
        ImmutableArray<string> checkpoint = ["b0", "b1", "b2"];
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        string[] expectedValues = ["b0", "c", "b1", "b2"];
        CollectionAssert.AreEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreEqual(child, compacted.VisibleElements[1].Anchor);
        Assert.AreEqual(parent, compacted.TranslateAnchor(parent));
    }


    [TestMethod]
    public void CompactDropsAStableTombstoneWithNoRetainedDescendants()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor tombstoned) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "t", R1);
        sequence = sequence.Remove(tombstoned);

        VectorClock frontier = FrontierCovering(tombstoned.LiveId!);
        ImmutableArray<string> checkpoint = ["b0", "b1", "b2"];
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
        ImmutableArray<string> checkpoint = ["b0", "x", "b1", "b2"];
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
        (sequence, OffsetAnchor y) = sequence.InsertAfter(OffsetAnchor.AtBase(2), "y", R2);

        //First compaction folds x but leaves y above the frontier.
        VectorClock firstFrontier = FrontierCovering(x.LiveId!);
        ImmutableArray<string> firstCheckpoint = ["b0", "x", "b1", "b2"];
        OffsetAnchoredSequence<string> first = sequence.Compact(firstFrontier, firstCheckpoint);

        //Second compaction at a strictly higher frontier folds y too.
        VectorClock secondFrontier = FrontierCovering(x.LiveId!, y.LiveId!);
        ImmutableArray<string> secondCheckpoint = ["b0", "x", "b1", "b2", "y"];
        OffsetAnchoredSequence<string> second = first.Compact(secondFrontier, secondCheckpoint);

        //The dot folded away in the first generation is still translatable after the second, by map composition.
        Assert.IsNotNull(second.TranslateAnchor(x));
    }


    [TestMethod]
    public void RepeatedCompactionAtTheSameWaterlineIsANoOp()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAnchor x) = sequence.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<string> checkpoint = ["b0", "x", "b1", "b2"];
        OffsetAnchoredSequence<string> once = sequence.Compact(frontier, checkpoint);

        OffsetAnchoredSequence<string> twice = once.Compact(frontier, checkpoint);

        Assert.AreEqual(once, twice);
    }


    [TestMethod]
    public void IndependentCompactionsAtTheSameWaterlineMergeWhileMixedGenerationsFailClosed()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (shared, OffsetAnchor x) = shared.InsertAfter(OffsetAnchor.AtBase(0), "x", R1);

        //Both members diverge with their own inserts above the frontier, then compact at the same waterline.
        (OffsetAnchoredSequence<string> a, _) = shared.InsertAfter(x, "a", R1);
        (OffsetAnchoredSequence<string> b, _) = shared.InsertAfter(x, "b", R2);

        VectorClock frontier = FrontierCovering(x.LiveId!);
        ImmutableArray<string> checkpoint = ["b0", "x", "b1", "b2"];
        OffsetAnchoredSequence<string> compactedA = a.Compact(frontier, checkpoint);
        OffsetAnchoredSequence<string> compactedB = b.Compact(frontier, checkpoint);

        OffsetAnchoredSequence<string> merged = compactedA.Merge(compactedB);

        CollectionAssert.AreEqual(merged.Values.ToArray(), compactedB.Merge(compactedA).Values.ToArray());
        Assert.AreEqual(merged, compactedB.Merge(compactedA));
        Assert.ThrowsExactly<InvalidOperationException>(() => compactedA.Merge(a));
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

        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Compact(null!, Base));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Compact(VectorClock.Empty, default));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.TranslateAnchor(null!));
    }


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
