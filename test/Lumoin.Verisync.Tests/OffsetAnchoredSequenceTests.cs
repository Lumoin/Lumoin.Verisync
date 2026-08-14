using CsCheck;
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
/// and drop through the materialized base. The public addressing surface is <see cref="OffsetAddress"/>:
/// a base address carries the generation its offset belongs to, a live or head address carries the
/// canonical zero.
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
        Assert.AreSequenceEqual(expected, sequence.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterABaseOffsetLandsImmediatelyAfterIt()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        (OffsetAnchoredSequence<string> inserted, _) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(1), 0), "x", R1);

        string[] expected = ["b0", "b1", "x", "b2"];
        Assert.AreSequenceEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void InsertAtHeadLandsBeforeTheBase()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        (OffsetAnchoredSequence<string> inserted, _) = sequence.InsertAtHead("x", R1);

        string[] expected = ["x", "b0", "b1", "b2"];
        Assert.AreSequenceEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void InsertAfterALiveElementChains()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        (OffsetAnchoredSequence<string> chained, _) = sequence.InsertAfter(x, "y", R1);

        string[] expected = ["b0", "x", "y", "b1", "b2"];
        Assert.AreSequenceEqual(expected, chained.Values.ToArray());
    }


    [TestMethod]
    public void RemoveHidesABaseElementKeepsItsAnchorAndTicksTheContext()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        OffsetAnchoredSequence<string> removed = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(1), 0), R1);

        //A base removal is now a dotted event on the remover's axis.
        Assert.AreEqual(1, removed.CausalContext[R1]);

        (OffsetAnchoredSequence<string> inserted, _) = removed.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(1), 0), "x", R1);

        //b1 is hidden yet still anchors: x sits where b1 was, between b0 and b2.
        string[] expected = ["b0", "x", "b2"];
        Assert.AreSequenceEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void RemoveTombstonesALiveElementAndTicksTheContext()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        OffsetAnchoredSequence<string> removed = sequence.Remove(x, R1);

        //The remove minted a dot past the insert on the remover's axis.
        Assert.AreEqual(2, removed.CausalContext[R1]);

        string[] expected = ["b0", "b1", "b2"];
        Assert.AreSequenceEqual(expected, removed.Values.ToArray());
    }


    [TestMethod]
    public void ConcurrentInsertsAtTheSameBaseAnchorConvergeAcrossReplicas()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> byFirst, _) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        (OffsetAnchoredSequence<string> bySecond, _) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "y", R2);

        OffsetAnchoredSequence<string> merged = byFirst.Merge(bySecond);

        Assert.AreSequenceEqual(merged.Values.ToArray(), bySecond.Merge(byFirst).Values.ToArray());
        Assert.HasCount(5, merged.Values);
        Assert.AreEqual("b0", merged.Values[0]);
    }


    [TestMethod]
    public void InsertAfterMergedStateLandsImmediatelyAfterItsAnchor()
    {
        //R1 hangs a chain off base[0]; R2 merges and inserts at base[0]: the fresh Lamport identity
        //dominates the observed chain, so the insert lands immediately after b0.
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> withChain, OffsetAddress x) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        (withChain, _) = withChain.InsertAfter(x, "y", R1);

        (OffsetAnchoredSequence<string> merged, _) = shared.Merge(withChain).InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "z", R2);

        string[] expected = ["b0", "z", "x", "y", "b1", "b2"];
        Assert.AreSequenceEqual(expected, merged.Values.ToArray());
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

        Assert.ThrowsExactly<ArgumentException>(() => sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(3), 0), "x", R1));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtLive(foreign), 0), "x", R1));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Remove(new OffsetAddress(OffsetAnchor.Head, 0), R1));
        Assert.ThrowsExactly<ArgumentException>(() => sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(3), 0), R1));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Remove(null!, R1));
        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.Merge(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => OffsetAnchor.AtBase(-1));
        Assert.ThrowsExactly<ArgumentNullException>(() => OffsetAnchor.AtLive(null!));
    }


    /// <summary>
    /// An address is canonical at construction: the anchor is non-null, a base anchor carries a non-negative
    /// generation, and a live or head anchor carries exactly zero.
    /// </summary>
    /// <remarks>
    /// Record equality is then meaningful for every shape — two base addresses of one offset differ exactly by
    /// their generation, and two live addresses of one element are equal regardless of when they were read.
    /// </remarks>
    [TestMethod]
    public void OffsetAddressConstructionIsCanonicalAndFailClosed()
    {
        Dot dot = new(R1, 1);

        Assert.ThrowsExactly<ArgumentNullException>(() => new OffsetAddress(null!, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OffsetAddress(OffsetAnchor.AtBase(0), -1));
        Assert.ThrowsExactly<ArgumentException>(() => new OffsetAddress(OffsetAnchor.AtLive(dot), 1));
        Assert.ThrowsExactly<ArgumentException>(() => new OffsetAddress(OffsetAnchor.Head, 1));

        //The canonical shapes construct and surface both parts: a base address carries its generation, a
        //live or head address carries the canonical zero.
        OffsetAddress baseAddress = new(OffsetAnchor.AtBase(0), 3);
        OffsetAddress liveAddress = new(OffsetAnchor.AtLive(dot), 0);
        OffsetAddress headAddress = new(OffsetAnchor.Head, 0);
        Assert.AreEqual(OffsetAnchor.AtBase(0), baseAddress.Anchor);
        Assert.AreEqual(3, baseAddress.Generation);
        Assert.AreEqual(0, liveAddress.Generation);
        Assert.AreEqual(0, headAddress.Generation);

        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(2), 1), new OffsetAddress(OffsetAnchor.AtBase(2), 1));
        Assert.AreNotEqual(new OffsetAddress(OffsetAnchor.AtBase(2), 1), new OffsetAddress(OffsetAnchor.AtBase(2), 2));
        Assert.AreEqual(liveAddress, new OffsetAddress(OffsetAnchor.AtLive(dot), 0));
    }


    /// <summary>
    /// The canonical shape survives the copy path: a with-expression re-validates each changed member against
    /// the retained other, so it can never yield an address the constructor refuses, and a live address's
    /// equality stays generation-invariant.
    /// </summary>
    [TestMethod]
    public void AWithExpressionRevalidatesTheCanonicalShape()
    {
        Dot dot = new(R1, 1);
        OffsetAddress liveAddress = new(OffsetAnchor.AtLive(dot), 0);
        OffsetAddress baseAddress = new(OffsetAnchor.AtBase(0), 1);

        Assert.ThrowsExactly<ArgumentException>(() => _ = liveAddress with { Generation = 1 });
        Assert.ThrowsExactly<ArgumentException>(() => _ = new OffsetAddress(OffsetAnchor.Head, 0) with { Generation = 9 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = baseAddress with { Generation = -1 });
        Assert.ThrowsExactly<ArgumentException>(() => _ = baseAddress with { Anchor = OffsetAnchor.AtLive(dot) });
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = baseAddress with { Anchor = null! });

        Assert.AreEqual(liveAddress, liveAddress with { Generation = 0 });
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 2), baseAddress with { Generation = 2 });
    }


    [TestMethod]
    public void VisibleElementsPairEveryValueWithItsAnchor()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        IReadOnlyList<(OffsetAddress Anchor, string Value)> visible = sequence.VisibleElements;

        Assert.HasCount(4, visible);
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 0), visible[0].Anchor);
        Assert.AreEqual(x, visible[1].Anchor);
        Assert.AreEqual("x", visible[1].Value);
    }


    /// <summary>
    /// The projected addresses carry the sequence's current generation: a genesis generation stamps its base
    /// elements with zero, and a base-changing compaction advances the generation so every base element it
    /// projects carries the new one.
    /// </summary>
    /// <remarks>
    /// A live element carries the canonical zero throughout.
    /// </remarks>
    [TestMethod]
    public void VisibleElementAddressesCarryTheCurrentGeneration()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        IReadOnlyList<(OffsetAddress Anchor, string Value)> before = sequence.VisibleElements;
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 0), before[0].Anchor);
        Assert.AreEqual(x, before[1].Anchor);

        //The compaction converts x into the base, so every visible element is a base slot of generation 1.
        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, sequence.CertifiedProjection(frontier));

        IReadOnlyList<(OffsetAddress Anchor, string Value)> after = compacted.VisibleElements;
        Assert.HasCount(4, after);
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 1), after[0].Anchor);
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(1), 1), after[1].Anchor);
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(2), 1), after[2].Anchor);
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(3), 1), after[3].Anchor);
    }


    [TestMethod]
    public void CompactConvertsStableVisibleVerticesIntoBaseEntries()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //The visible values are unchanged and x now lives in the base at its linearization position.
        string[] expectedValues = ["b0", "x", "b1", "b2"];
        Assert.AreSequenceEqual(expectedValues, compacted.Values.ToArray());
        Assert.AreSequenceEqual(expectedValues, compacted.Base.ToArray());
    }


    [TestMethod]
    public void CompactKeepsAnUncertifiedRemovedBaseEntryHiddenInTheNewBase()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(1), 0), "x", R1);
        sequence = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(1), 0), R2);

        //The frontier certifies x's insert but not R2's base removal, so the removed slot stays in the
        //certified projection — the determinism inclusion — and in the new base, hidden and re-marked.
        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        Assert.HasCount(4, checkpoint);
        Assert.AreEqual("b1", checkpoint[1].Value);

        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        string[] expectedBase = ["b0", "b1", "x", "b2"];
        string[] expectedValues = ["b0", "x", "b2"];
        Assert.AreSequenceEqual(expectedBase, compacted.Base.ToArray());
        Assert.AreSequenceEqual(expectedValues, compacted.Values.ToArray());
    }


    [TestMethod]
    public void CompactWithACheckpointThatDoesNotMatchFailsClosed()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);

        //A checkpoint projected at a different frontier misses the stable x.
        ImmutableArray<SequenceCheckpointEntry<string>> staleCheckpoint = sequence.CertifiedProjection(VectorClock.Empty);
        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, staleCheckpoint));

        //The integrity check is dot-aware: the same values under a forged identity fail closed too.
        ImmutableArray<SequenceCheckpointEntry<string>> proper = sequence.CertifiedProjection(frontier);
        SequenceCheckpointEntry<string>[] forged = proper.ToArray();
        forged[1] = new SequenceCheckpointEntry<string>(DotStateOf(new Dot(R2, 9)), forged[1].Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, ImmutableArray.Create(forged)));
    }


    /// <summary>
    /// RE-POINTED under §17: a certified-removed parent kept alive because its child is retained is the ghost
    /// the taxonomy describes, but it can only exist above the waterline — its child is unstable — so
    /// compaction fails closed rather than materialize it.
    /// </summary>
    /// <remarks>
    /// The certified projection is unrestricted and still excludes the certified-removed parent.
    /// </remarks>
    [TestMethod]
    public void CompactFailsClosedOnACertifiedTombstoneThatStillRootsAnUnstableChild()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress parent) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "p", R1);
        (sequence, _) = sequence.InsertAfter(parent, "c", R1);
        sequence = sequence.Remove(parent, R2);

        //The parent's remove is CERTIFIED — R2's first event is the remove-dot — while the child stays
        //above the frontier as an unstable vertex, so the state is not insert-quiescent.
        VectorClock frontier = FrontierCovering(parent.Anchor.LiveId!, new Dot(R2, 1));
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        Assert.HasCount(3, checkpoint);

        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, checkpoint));
    }


    /// <summary>
    /// RE-POINTED under §17: an uncertified-removed stable vertex converting pending-removed WITH a retained
    /// child would re-anchor that child at the gap — but the child is unstable, so the state is not
    /// insert-quiescent and compaction fails closed.
    /// </summary>
    /// <remarks>
    /// The pending-removed conversion itself is exercised quiescently by the certification law suite. The
    /// certified projection is unrestricted and still carries the uncertified-removed parent with its real dot.
    /// </remarks>
    [TestMethod]
    public void CompactFailsClosedOnAnUncertifiedTombstoneWithAnUnstableChild()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress parent) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "p", R1);
        (sequence, _) = sequence.InsertAfter(parent, "c", R1);
        sequence = sequence.Remove(parent, R2);

        //The frontier covers p's insert but neither R2's remove nor the child.
        VectorClock frontier = FrontierCovering(parent.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);

        //The uncertified-removed p stays in the projection with its real dot; the checkpoint entry's
        //custom value equality compares the replica bytes by content.
        Assert.HasCount(4, checkpoint);
        Assert.AreEqual(new SequenceCheckpointEntry<string>(DotStateOf(parent.Anchor.LiveId!), "p"), checkpoint[1]);

        //The child is unstable, so the base-materializing compaction fails closed.
        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, checkpoint));
    }


    [TestMethod]
    public void CompactDropsACertifiedTombstoneWithNoRetainedDescendants()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress tombstoned) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "t", R1);
        sequence = sequence.Remove(tombstoned, R1);

        //The state's own context certifies both the insert and the remove.
        VectorClock frontier = sequence.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        //The dropped tombstone's anchor still translates to a servable position, but the vertex is gone.
        Assert.IsNotNull(compacted.TranslateAnchor(tombstoned));
        Assert.ThrowsExactly<ArgumentException>(() => compacted.InsertAfter(tombstoned, "z", R2));
    }


    /// <summary>
    /// A prior-generation base address translates through the map: b1 sits at base offset 1 in the
    /// pre-compaction generation and at offset 2 after x converts ahead of it, so the generation-0 address of
    /// offset 1 resolves to the generation-1 address of offset 2 and serves a following insert.
    /// </summary>
    [TestMethod]
    public void CompactTranslatesPreviousGenerationBaseOffsets()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, checkpoint);

        OffsetAddress? translated = compacted.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtBase(1), 0));
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(2), 1), translated);

        (OffsetAnchoredSequence<string> inserted, _) = compacted.InsertAfter(translated!, "after-b1", R2);
        string[] expected = ["b0", "x", "b1", "after-b1", "b2"];
        Assert.AreSequenceEqual(expected, inserted.Values.ToArray());
    }


    [TestMethod]
    public void TwoSuccessiveCompactionsStillTranslateAFirstGenerationDot()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        //First compaction folds x at an insert-quiescent frontier.
        VectorClock firstFrontier = FrontierCovering(x.Anchor.LiveId!);
        OffsetAnchoredSequence<string> first = sequence.Compact(firstFrontier, sequence.CertifiedProjection(firstFrontier));

        //A second generation: y is inserted AFTER the first compaction, then folded at a frontier that
        //covers it — each compaction stays insert-quiescent, which §17 requires. The insert names a
        //current-generation offset, so its address carries generation 1.
        (OffsetAnchoredSequence<string> withY, OffsetAddress y) = first.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(2), 1), "y", R2);
        VectorClock secondFrontier = FrontierCovering(x.Anchor.LiveId!, y.Anchor.LiveId!);
        OffsetAnchoredSequence<string> second = withY.Compact(secondFrontier, withY.CertifiedProjection(secondFrontier));

        //The dot folded away in the first generation is still translatable after the second, by map composition.
        Assert.IsNotNull(second.TranslateAnchor(x));
    }


    [TestMethod]
    public void RepeatedCompactionAtTheSameWaterlineIsANoOp()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = sequence.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> once = sequence.Compact(frontier, checkpoint);

        OffsetAnchoredSequence<string> twice = once.Compact(frontier, checkpoint);

        Assert.AreEqual(once, twice);
    }


    [TestMethod]
    public void IndependentCompactionsAtTheSameWaterlineMergeWhileMixedGenerationsFailClosed()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (shared, OffsetAddress x) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        //Both members stay insert-quiescent at the frontier and diverge only by removing different base
        //slots above it — a suffix insert would raise an unstable vertex and fail closed under §17.
        OffsetAnchoredSequence<string> a = shared.Remove(new OffsetAddress(OffsetAnchor.AtBase(1), 0), R1);
        OffsetAnchoredSequence<string> b = shared.Remove(new OffsetAddress(OffsetAnchor.AtBase(2), 0), R2);

        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> checkpoint = a.CertifiedProjection(frontier);
        OffsetAnchoredSequence<string> compactedA = a.Compact(frontier, checkpoint);
        OffsetAnchoredSequence<string> compactedB = b.Compact(frontier, checkpoint);

        OffsetAnchoredSequence<string> merged = compactedA.Merge(compactedB);

        Assert.AreSequenceEqual(merged.Values.ToArray(), compactedB.Merge(compactedA).Values.ToArray());
        Assert.AreEqual(merged, compactedB.Merge(compactedA));

        //An uncompacted operand carries the previous generation identity, which the fence rejects.
        Assert.ThrowsExactly<InvalidOperationException>(() => compactedA.Merge(a));
    }


    /// <summary>
    /// The map's §5a trace, green-but-unsound before offset.v2: a laggard that never saw the remove slips the
    /// base gate (a tombstone-only drop leaves the base unchanged) and the union used to resurrect the element
    /// cluster-wide.
    /// </summary>
    /// <remarks>
    /// The stale-replay detector now fails it closed in both merge orders.
    /// </remarks>
    [TestMethod]
    public void AStalePreRemoveLaggardFailsClosedAgainstACompactedRemove()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> withX, OffsetAddress x) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
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
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        Dot unknown = new(R2, 99);

        Assert.AreEqual(new OffsetAddress(OffsetAnchor.Head, 0), sequence.TranslateAnchor(new OffsetAddress(OffsetAnchor.Head, 0)));
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 0), sequence.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtBase(0), 0)));
        Assert.AreEqual(x, sequence.TranslateAnchor(x));
        Assert.IsNull(sequence.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtLive(unknown), 0)));
        Assert.IsNull(sequence.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtBase(99), 0)));
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


    /// <summary>
    /// T-O2-1, probe basics: the empty state and a pure-base state probe empty at any frontier — base slots
    /// mint no insert-dots — and a state with vertices probed at the empty frontier reports every vertex
    /// insert-dot in (Replica, Counter) ascending order.
    /// </summary>
    [TestMethod]
    public void TheProbeIsEmptyOnAnEmptyStateAndListsEveryVertexInOrder()
    {
        Assert.IsTrue(OffsetAnchoredSequence<string>.Empty.UnstableInserts(VectorClock.Empty).IsEmpty);
        OffsetAnchoredSequence<string> baseOnly = OffsetAnchoredSequence<string>.WithBase(Base);
        Assert.IsTrue(baseOnly.UnstableInserts(VectorClock.Empty).IsEmpty);
        Assert.IsTrue(baseOnly.UnstableInserts(VectorClock.Empty.Increment(R1)).IsEmpty);

        //Vertices minted on two axes: R2's head insert comes first in time, R1's dots come first in
        //the probe's replica order, counters ascending within one axis.
        (OffsetAnchoredSequence<string> withX, OffsetAddress x) = baseOnly.InsertAtHead("x", R2);
        (OffsetAnchoredSequence<string> withY, OffsetAddress y) = withX.InsertAfter(x, "y", R1);
        (OffsetAnchoredSequence<string> withZ, _) = withY.InsertAfter(y, "z", R1);

        Dot[] expected = [new Dot(R1, 2), new Dot(R1, 3), new Dot(R2, 1)];
        Assert.AreSequenceEqual(expected, withZ.UnstableInserts(VectorClock.Empty).ToArray());

        //The state's own context covers every insert-dot, so the probe reads empty there.
        Assert.IsTrue(withZ.UnstableInserts(withZ.CausalContext).IsEmpty);

        Assert.ThrowsExactly<ArgumentNullException>(() => withZ.UnstableInserts(null!));
    }


    /// <summary>
    /// T-O2-2: remove-dots never block insert-quiescence — the probe reads INSERT stability only.
    /// </summary>
    /// <remarks>
    /// A frontier covering both inserts but not the remove-dot probes empty on both members, the certified
    /// projection still carries the locally hidden element, and both members compact to byte-identical base
    /// value arrays: the remover converts it pending-removed, the laggard converts it visible.
    /// </remarks>
    [TestMethod]
    public void RemoveDotsAboveTheFrontierNeverBlockInsertQuiescence()
    {
        (OffsetAnchoredSequence<string> withA, OffsetAddress a) = OffsetAnchoredSequence<string>.Empty.InsertAtHead("a", R1);
        (OffsetAnchoredSequence<string> shared, OffsetAddress b) = withA.InsertAfter(a, "b", R1);
        OffsetAnchoredSequence<string> m2 = shared;
        OffsetAnchoredSequence<string> m1 = shared.Remove(b, R1);

        //The min-fold of the two members' digests is the laggard's pre-remove context: both insert
        //dots covered, the remove-dot (R1,3) not.
        VectorClock frontier = m2.CausalContext;
        Assert.IsTrue(m1.UnstableInserts(frontier).IsEmpty);
        Assert.IsTrue(m2.UnstableInserts(frontier).IsEmpty);

        //The determinism inclusion: the projection carries the locally hidden b, identically on both.
        ImmutableArray<SequenceCheckpointEntry<string>> m1Projection = m1.CertifiedProjection(frontier);
        ImmutableArray<SequenceCheckpointEntry<string>> m2Projection = m2.CertifiedProjection(frontier);
        Assert.AreSequenceEqual(m2Projection.ToArray(), m1Projection.ToArray());

        OffsetAnchoredSequence<string> m1Compacted = m1.Compact(frontier, m1Projection);
        OffsetAnchoredSequence<string> m2Compacted = m2.Compact(frontier, m2Projection);
        string[] expectedBase = ["a", "b"];
        string[] m1Visible = ["a"];
        Assert.AreSequenceEqual(expectedBase, m1Compacted.ToState().Base.ToArray());
        Assert.AreSequenceEqual(expectedBase, m2Compacted.ToState().Base.ToArray());
        Assert.AreSequenceEqual(m1Visible, m1Compacted.Values.ToArray());
        Assert.AreSequenceEqual(expectedBase, m2Compacted.Values.ToArray());

        //The remover's marking rides at offset 1 carrying exactly the remove-dot (R1,3); the laggard
        //never observed the remove and carries no marking.
        OffsetBaseRemovalEntry marking = m1Compacted.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(1, marking.Offset);
        Assert.HasCount(1, marking.RemoveDots);
        Assert.AreEqual(3, marking.RemoveDots[0].Counter);
        Assert.AreEqual(1, marking.RemoveDots[0].Replica[0]);
        Assert.HasCount(0, m2Compacted.ToState().RemovedBaseOffsets);
    }


    /// <summary>
    /// The probe/guard-agreement property: over generated op histories on the empty base, the probe at a
    /// snapshot-cut frontier is empty EXACTLY when the base-materializing compaction passes its quiescence
    /// guard, and the probe's content equals a naively recomputed uncovered set in (Replica, Counter) order.
    /// </summary>
    /// <remarks>
    /// Sampled over BOTH regions — histories with at least one post-cut insert (probe provably non-empty) and
    /// histories with none (probe provably empty) — so neither half of the iff can go vacuous.
    /// </remarks>
    [TestMethod]
    public void TheProbeIsEmptyExactlyWhenCompactionPasses()
    {
        GenProbeCase.Where(static input => NaiveUncoveredInsertDots(input.Full, input.Frontier).Length > 0).Sample(input =>
        {
            AssertProbeAgreesWithGuardAndOracle(input.Full, input.Frontier);
        });

        GenProbeCase.Where(static input => NaiveUncoveredInsertDots(input.Full, input.Frontier).Length == 0).Sample(input =>
        {
            AssertProbeAgreesWithGuardAndOracle(input.Full, input.Frontier);
        });
    }


    private static void AssertProbeAgreesWithGuardAndOracle(OffsetAnchoredSequence<int> full, VectorClock frontier)
    {
        ImmutableArray<Dot> probe = full.UnstableInserts(frontier);

        //The iff between the probe and the guard: the checkpoint is the state's own certified
        //projection at an honest historical frontier, so the quiescence guard is the only throw
        //reachable by construction.
        bool compactionPassed;
        try
        {
            full.Compact(frontier, full.CertifiedProjection(frontier));
            compactionPassed = true;
        }
        catch(InvalidOperationException)
        {
            compactionPassed = false;
        }

        Assert.AreEqual(probe.IsEmpty, compactionPassed);

        //The completeness oracle: the probe equals the naively recomputed uncovered set, in order.
        Assert.AreSequenceEqual(NaiveUncoveredInsertDots(full, frontier), probe.ToArray());
    }


    /// <summary>
    /// The replica axes the probe property's op histories mint on; one replica per operand index.
    /// </summary>
    private static ReplicaId[] HistoryReplicas { get; } = [Replica(10), Replica(11), Replica(12)];


    /// <summary>
    /// A replica-honest op history over the EMPTY base with a snapshot cut: the probed state is the full
    /// history and the frontier is the cut snapshot's own causal context.
    /// </summary>
    private static Gen<(OffsetAnchoredSequence<int> Full, VectorClock Frontier)> GenProbeCase { get; } =
        Gen.Select(
            Gen.Select(Gen.Int[0, 2], Gen.Int[0, 100], static (replica, seed) => (Replica: replica, Seed: seed)).Array[0, 8],
            Gen.Int[0, 8],
            static (ops, cut) =>
            {
                (OffsetAnchoredSequence<int> full, IReadOnlyList<OffsetAnchoredSequence<int>> snapshots) = BuildSnapshots(ops);

                return (full, SnapshotAt(snapshots, cut).CausalContext);
            });


    /// <summary>
    /// Live-axis op histories over the EMPTY base: head and live-anchored inserts plus dotted removes of
    /// still-visible elements.
    /// </summary>
    private static (OffsetAnchoredSequence<int> Full, IReadOnlyList<OffsetAnchoredSequence<int>> Snapshots) BuildSnapshots((int Replica, int Seed)[] ops)
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.Empty;
        var anchors = new List<OffsetAddress>();
        var snapshots = new List<OffsetAnchoredSequence<int>>(ops.Length);
        for(int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            (int replica, int seed) = ops[opIndex];
            int visibleCount = sequence.VisibleElements.Count;
            if(seed % 3 == 0 && visibleCount > 0)
            {
                OffsetAddress target = sequence.VisibleElements[seed % visibleCount].Anchor;
                sequence = sequence.Remove(target, HistoryReplicas[replica]);
            }
            else if(anchors.Count == 0)
            {
                (sequence, OffsetAddress head) = sequence.InsertAtHead((100 * replica) + opIndex, HistoryReplicas[replica]);
                anchors.Add(head);
            }
            else
            {
                (sequence, OffsetAddress inserted) = sequence.InsertAfter(anchors[seed % anchors.Count], (100 * replica) + opIndex, HistoryReplicas[replica]);
                anchors.Add(inserted);
            }

            snapshots.Add(sequence);
        }

        return (sequence, snapshots);
    }


    private static OffsetAnchoredSequence<int> SnapshotAt(IReadOnlyList<OffsetAnchoredSequence<int>> snapshots, int cut)
    {
        int bounded = Math.Min(cut, snapshots.Count);

        return bounded == 0 ? OffsetAnchoredSequence<int>.Empty : snapshots[bounded - 1];
    }


    /// <summary>
    /// The naive uncovered set: every vertex insert-dot the frontier does not cover, recomputed from the
    /// serialized state and sorted by (Replica, Counter) — the completeness oracle the probe must equal.
    /// </summary>
    private static Dot[] NaiveUncoveredInsertDots(OffsetAnchoredSequence<int> sequence, VectorClock frontier)
    {
        var uncovered = new List<Dot>();
        foreach(OffsetVertexEntry<int> vertex in sequence.ToState().Vertices)
        {
            var dot = new Dot(ReplicaId.FromSpan(vertex.Id.Replica.AsSpan()), vertex.Id.Counter);
            if(frontier[dot.Replica] < dot.Counter)
            {
                uncovered.Add(dot);
            }
        }

        uncovered.Sort(static (left, right) =>
        {
            int byReplica = left.Replica.CompareTo(right.Replica);

            return byReplica != 0 ? byReplica : left.Counter.CompareTo(right.Counter);
        });

        return uncovered.ToArray();
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
