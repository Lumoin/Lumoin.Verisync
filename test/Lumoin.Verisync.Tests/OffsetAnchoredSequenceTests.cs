using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Linq;

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
        Assert.IsEmpty(m2Compacted.ToState().RemovedBaseOffsets);
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


    /// <summary>
    /// Merging two operands that concurrently tombstoned the same live target unions both remove-dots
    /// rather than keeping only one, per Merge's remarks on genuinely concurrent removes.
    /// </summary>
    [TestMethod]
    public void MergeUnionsTombstoneRemoveDotsForAConcurrentlyRemovedTarget()
    {
        OffsetAnchoredSequence<string> shared = OffsetAnchoredSequence<string>.WithBase(Base);
        (shared, OffsetAddress x) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);

        OffsetAnchoredSequence<string> removedByFirst = shared.Remove(x, R1);
        OffsetAnchoredSequence<string> removedBySecond = shared.Remove(x, R2);

        OffsetAnchoredSequence<string> merged = removedByFirst.Merge(removedBySecond);

        ImmutableArray<OffsetTombstoneEntry> tombstones = merged.ToState().Tombstones;
        Assert.HasCount(1, tombstones);
        Assert.HasCount(2, tombstones[0].RemoveDots);
    }


    /// <summary>
    /// The compacted-base-offsets map hash must be the exclusive-or of each previous offset's combine,
    /// for two distinct offsets translated to the head sentinel.
    /// </summary>
    [TestMethod]
    public void GetHashCodeFoldsTheCompactedBaseOffsetsMapByExclusiveOr()
    {
        VectorClockState emptyClock = new([]);
        OffsetAnchorState headState = new(-1, null);

        var state = new OffsetAnchoredSequenceState<string>(
            Base: ImmutableArray<string>.Empty,
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets: [],
            Context: emptyClock,
            Vertices: [],
            Tombstones: [],
            CompactedDotAnchors: [],
            CompactedBaseOffsets: [new OffsetBaseAnchorEntry(0, headState), new OffsetBaseAnchorEntry(1, headState)]);

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int expectedBaseOffsetsHash = HashCode.Combine(0, OffsetAnchor.Head) ^ HashCode.Combine(1, OffsetAnchor.Head);
        int expected = HashCode.Combine(HashCode.Combine(0, VectorClock.Empty, 0), VectorClock.Empty, 0, 0, 0, 0, expectedBaseOffsetsHash);
        Assert.AreEqual(expected, sequence.GetHashCode());
    }


    /// <summary>
    /// <see cref="OffsetAnchoredSequence{TValue}.CertifiedProjection"/> rejects a null frontier with
    /// <see cref="ArgumentNullException"/> before the core walk would dereference it.
    /// </summary>
    [TestMethod]
    public void CertifiedProjectionValidatesTheFrontierIsNotNull()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        sequence = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(0), 0), R1);

        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.CertifiedProjection(null!));
    }


    /// <summary>
    /// Merge sizes the merged base-offset translation dictionary from BOTH operands' counts and unions both
    /// operands' entries, even when "other" carries more entries than "this".
    /// </summary>
    [TestMethod]
    public void MergeUnionsBothOperandsBaseOffsetTranslationEntries()
    {
        VectorClockState frontierState = VectorClock.Empty.Increment(R1).ToState();
        OffsetAnchorState head = new(-1, null);
        OffsetAnchoredSequenceState<string> smaller = new(["v"], frontierState, 1, [], frontierState, [], [], [], [new OffsetBaseAnchorEntry(5, head)]);
        OffsetAnchoredSequenceState<string> larger = new(["v"], frontierState, 1, [], frontierState, [], [], [], [new OffsetBaseAnchorEntry(9, head), new OffsetBaseAnchorEntry(10, head), new OffsetBaseAnchorEntry(11, head)]);
        OffsetAnchoredSequence<string> a = OffsetAnchoredSequence<string>.FromState(smaller);
        OffsetAnchoredSequence<string> b = OffsetAnchoredSequence<string>.FromState(larger);

        OffsetAnchoredSequence<string> merged = a.Merge(b);

        ImmutableArray<OffsetBaseAnchorEntry> mergedOffsets = merged.ToState().CompactedBaseOffsets;
        Assert.HasCount(4, mergedOffsets);
        bool hasFromA = false;
        bool hasFromB = false;
        foreach (OffsetBaseAnchorEntry entry in mergedOffsets)
        {
            hasFromA |= entry.PreviousOffset == 5;
            hasFromB |= entry.PreviousOffset == 9;
        }

        Assert.IsTrue(hasFromA);
        Assert.IsTrue(hasFromB);
    }


    /// <summary>
    /// The BaseEqual integrity assertion must compare every element, not merely the lengths: two
    /// same-length bases differing at one position must still fail the generation fence.
    /// </summary>
    [TestMethod]
    public void MergingSameLengthDifferentContentBasesFailsClosed()
    {
        OffsetAnchoredSequence<string> first = OffsetAnchoredSequence<string>.WithBase(Base);
        OffsetAnchoredSequence<string> second = OffsetAnchoredSequence<string>.WithBase(["b0", "b1", "zz"]);

        InvalidOperationException refusal = Assert.ThrowsExactly<InvalidOperationException>(() => first.Merge(second));

        Assert.Contains("over different base generations", refusal.Message);
    }


    /// <summary>
    /// After a real compaction, TranslateAnchor still serves the head identically and still serves a live
    /// vertex minted after that compaction as its own address.
    /// </summary>
    [TestMethod]
    public void TranslateAnchorServesHeadAndCurrentLiveVerticesAfterCompaction()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, sequence.CertifiedProjection(frontier));

        (OffsetAnchoredSequence<string> withY, OffsetAddress y) = compacted.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 1), "y", R2);

        Assert.AreEqual(new OffsetAddress(OffsetAnchor.Head, 0), withY.TranslateAnchor(new OffsetAddress(OffsetAnchor.Head, 0)));
        Assert.AreEqual(y, withY.TranslateAnchor(y));
    }


    /// <summary>
    /// ToState serializes removed base offsets in ascending order even when there are enough sparse offsets
    /// to force the frozen dictionary off its small sorted representation onto the hashing path, whose key
    /// enumeration follows bucket order rather than value order.
    /// </summary>
    /// <remarks>
    /// A FrozenDictionary of int keys sorts its keys only in the small comparable representation used at ten
    /// keys or fewer; above that a hash table backs it and widely spaced keys enumerate scrambled, so the
    /// explicit sort in ToState is what restores the ascending contract the serialized form promises.
    /// </remarks>
    [TestMethod]
    public void ToStateOrdersManySparseRemovedOffsetsAscending()
    {
        //A base large enough to hold widely spaced offsets, so each removal is in range.
        ImmutableArray<string>.Builder baseBuilder = ImmutableArray.CreateBuilder<string>(256);
        for(int i = 0; i < 256; i++)
        {
            baseBuilder.Add("b" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        //More than ten sparse removed offsets, so the frozen dictionary uses its hashing (non-ascending)
        //enumeration path rather than the small sorted one.
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(baseBuilder.ToImmutable());
        int[] offsets = [1, 20, 47, 66, 93, 118, 145, 164, 189, 208, 227, 236, 241, 250];
        foreach(int offset in offsets)
        {
            sequence = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(offset), 0), R1);
        }

        int[] serialized = sequence.ToState().RemovedBaseOffsets.Select(static entry => entry.Offset).ToArray();

        //offsets is already ascending, so this asserts the serialized order is the sorted order the contract
        //promises rather than the frozen dictionary's bucket order.
        Assert.AreSequenceEqual(offsets, serialized);
    }


    /// <summary>
    /// ToState serializes compacted base-offset translations in ascending previous-offset order even when there
    /// are enough sparse offsets to force the frozen dictionary off its small sorted representation onto the
    /// hashing path, whose key enumeration follows bucket order rather than value order.
    /// </summary>
    /// <remarks>
    /// FromState and Merge admit a sparse compacted-base-offset map with any non-negative previous offsets, so
    /// the map is the same FrozenDictionary of int keys as the removed-offsets map: above ten keys a hash table
    /// backs it and widely spaced keys enumerate scrambled, and the explicit sort in ToState is what restores the
    /// previous-offset order the serialized form promises.
    /// </remarks>
    [TestMethod]
    public void ToStateOrdersManySparseCompactedBaseOffsetsAscending()
    {
        VectorClockState emptyClock = new([]);
        OffsetAnchorState headState = new(-1, null);

        //The same fourteen widely spaced keys the removed-offsets sibling uses, so the frozen dictionary takes
        //its hashing path and enumerates them off ascending order; they are supplied scrambled to underline that
        //neither insertion order nor bucket order is the ascending order the contract promises.
        int[] ascendingOffsets = [1, 20, 47, 66, 93, 118, 145, 164, 189, 208, 227, 236, 241, 250];
        int[] scrambledOffsets = [145, 1, 250, 47, 208, 20, 189, 93, 236, 66, 227, 118, 241, 164];

        ImmutableArray<OffsetBaseAnchorEntry>.Builder baseOffsetsBuilder = ImmutableArray.CreateBuilder<OffsetBaseAnchorEntry>(scrambledOffsets.Length);
        foreach(int offset in scrambledOffsets)
        {
            baseOffsetsBuilder.Add(new OffsetBaseAnchorEntry(offset, headState));
        }

        var state = new OffsetAnchoredSequenceState<string>(
            Base: ImmutableArray<string>.Empty,
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets: [],
            Context: emptyClock,
            Vertices: [],
            Tombstones: [],
            CompactedDotAnchors: [],
            CompactedBaseOffsets: baseOffsetsBuilder.ToImmutable());

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int[] serialized = sequence.ToState().CompactedBaseOffsets.Select(static entry => entry.PreviousOffset).ToArray();

        Assert.AreSequenceEqual(ascendingOffsets, serialized);
    }


    /// <summary>
    /// The removed-base-offsets map hash must be the exclusive-or of each offset's combine, for two
    /// distinct legacy (empty remove-dot set) offsets.
    /// </summary>
    [TestMethod]
    public void GetHashCodeFoldsTheRemovedBaseOffsetsMapByExclusiveOr()
    {
        VectorClockState emptyClock = new([]);
        var state = new OffsetAnchoredSequenceState<string>(
            Base: ["b0", "b1"],
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets: [new OffsetBaseRemovalEntry(0, []), new OffsetBaseRemovalEntry(1, [])],
            Context: emptyClock,
            Vertices: [],
            Tombstones: [],
            CompactedDotAnchors: [],
            CompactedBaseOffsets: []);

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int expectedRemovedHash = HashCode.Combine(0, 0) ^ HashCode.Combine(1, 0);
        int expected = HashCode.Combine(HashCode.Combine(2, VectorClock.Empty, 0), VectorClock.Empty, 0, 0, expectedRemovedHash, 0, 0);
        Assert.AreEqual(expected, sequence.GetHashCode());
    }


    /// <summary>
    /// Compact carries an orphan tombstone (a remove whose target is not a vertex) forward unchanged rather
    /// than dropping it.
    /// </summary>
    [TestMethod]
    public void CompactCarriesAnOrphanTombstoneForward()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        Dot orphanTarget = new(R2, 99);
        sequence = sequence.Remove(new OffsetAddress(OffsetAnchor.AtLive(orphanTarget), 0), R2);

        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, sequence.CertifiedProjection(frontier));

        bool found = false;
        foreach (OffsetTombstoneEntry entry in compacted.ToState().Tombstones)
        {
            if (entry.Target.Counter == orphanTarget.Counter && entry.Target.Replica.AsSpan().SequenceEqual(orphanTarget.Replica.AsSpan()))
            {
                found = true;
            }
        }

        Assert.IsTrue(found);
    }


    /// <summary>
    /// The per-target remove-dot fold inside the tombstone map hash must be the exclusive-or of each
    /// remove-dot's hash code, for a target certified by two concurrent removes.
    /// </summary>
    [TestMethod]
    public void GetHashCodeFoldsATombstonesRemoveDotsByExclusiveOr()
    {
        Dot removeDotA = new(R1, 1);
        Dot removeDotB = new(R2, 1);
        Dot target = new(Replica(3), 1);
        VectorClockState context = new([new ReplicaCounterEntry(ImmutableArray.Create(R1.AsSpan()), 1), new ReplicaCounterEntry(ImmutableArray.Create(R2.AsSpan()), 1)]);
        VectorClockState emptyClock = new([]);

        var state = new OffsetAnchoredSequenceState<string>(
            Base: ImmutableArray<string>.Empty,
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets: [],
            Context: context,
            Vertices: [],
            Tombstones:
            [
                new OffsetTombstoneEntry(
                    new DotState(ImmutableArray.Create(target.Replica.AsSpan()), target.Counter),
                    [new DotState(ImmutableArray.Create(R1.AsSpan()), 1), new DotState(ImmutableArray.Create(R2.AsSpan()), 1)])
            ],
            CompactedDotAnchors: [],
            CompactedBaseOffsets: []);

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int expectedRemoveDotsFold = removeDotA.GetHashCode() ^ removeDotB.GetHashCode();
        int expectedTombstonesHash = HashCode.Combine(target, expectedRemoveDotsFold);
        int expected = HashCode.Combine(HashCode.Combine(0, VectorClock.Empty, 0), VectorClock.FromState(context), 0, expectedTombstonesHash, 0, 0, 0);
        Assert.AreEqual(expected, sequence.GetHashCode());
    }


    /// <summary>
    /// Compact fails closed when the supplied checkpoint is longer than the certified projection, even when
    /// every projected entry it does share matches.
    /// </summary>
    [TestMethod]
    public void CompactRejectsACheckpointLongerThanTheCertifiedProjection()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);
        ImmutableArray<SequenceCheckpointEntry<string>> proper = sequence.CertifiedProjection(frontier);
        SequenceCheckpointEntry<string>[] longer = [.. proper, new SequenceCheckpointEntry<string>(DotStateOf(new Dot(R2, 99)), "extra")];

        Assert.ThrowsExactly<InvalidOperationException>(() => sequence.Compact(frontier, ImmutableArray.Create(longer)));
    }


    /// <summary>
    /// The head translates to itself even on a sequence whose translation maps are empty at a non-genesis
    /// generation, per TranslateAnchor's promise that the head is the same virtual position in every
    /// generation.
    /// </summary>
    [TestMethod]
    public void TranslateAnchorServesHeadOnAnUncompactedSequenceAtANonGenesisGeneration()
    {
        VectorClockState frontierState = VectorClock.Empty.Increment(R1).ToState();
        OffsetAnchoredSequenceState<string> state = new(ImmutableArray<string>.Empty, frontierState, 3, [], frontierState, [], [], [], []);
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        OffsetAddress? translated = sequence.TranslateAnchor(new OffsetAddress(OffsetAnchor.Head, 0));

        Assert.AreEqual(new OffsetAddress(OffsetAnchor.Head, 0), translated);
    }


    /// <summary>
    /// Composing a dropped dot's translation through a second compaction must still resolve the head
    /// sentinel, never fall through to an offset lookup that has no entry for it.
    /// </summary>
    [TestMethod]
    public void TranslateAnchorComposesAHeadTargetAcrossTwoCompactions()
    {
        OffsetAnchoredSequence<string> seq0 = OffsetAnchoredSequence<string>.Empty;
        (OffsetAnchoredSequence<string> seq1, OffsetAddress x) = seq0.InsertAtHead("x", R1);
        OffsetAnchoredSequence<string> removed = seq1.Remove(x, R1);

        VectorClock frontier1 = removed.CausalContext;
        OffsetAnchoredSequence<string> compacted1 = removed.Compact(frontier1, removed.CertifiedProjection(frontier1));

        (OffsetAnchoredSequence<string> seq2, _) = compacted1.InsertAtHead("y", R1);
        VectorClock frontier2 = seq2.CausalContext;
        OffsetAnchoredSequence<string> compacted2 = seq2.Compact(frontier2, seq2.CertifiedProjection(frontier2));

        //x's certified drop composed to the head sentinel in the first compaction (it was the only, and
        //therefore first, processed vertex over an empty base) must still compose to Head after the second.
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.Head, 0), compacted2.TranslateAnchor(x));
    }


    /// <summary>
    /// The compacted-dot-anchors map hash must be the exclusive-or of each dropped dot's combine, for
    /// two distinct dots translated to the head sentinel.
    /// </summary>
    [TestMethod]
    public void GetHashCodeFoldsTheCompactedDotAnchorsMapByExclusiveOr()
    {
        Dot dropped1 = new(R1, 1);
        Dot dropped2 = new(R2, 1);
        VectorClockState emptyClock = new([]);
        OffsetAnchorState headState = new(-1, null);

        var state = new OffsetAnchoredSequenceState<string>(
            Base: ImmutableArray<string>.Empty,
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets: [],
            Context: emptyClock,
            Vertices: [],
            Tombstones: [],
            CompactedDotAnchors:
            [
                new OffsetTranslationEntry(new DotState(ImmutableArray.Create(R1.AsSpan()), 1), headState),
                new OffsetTranslationEntry(new DotState(ImmutableArray.Create(R2.AsSpan()), 1), headState)
            ],
            CompactedBaseOffsets: []);

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int expectedDotAnchorsHash = HashCode.Combine(dropped1, OffsetAnchor.Head) ^ HashCode.Combine(dropped2, OffsetAnchor.Head);
        int expected = HashCode.Combine(HashCode.Combine(0, VectorClock.Empty, 0), VectorClock.Empty, 0, 0, 0, expectedDotAnchorsHash, 0);
        Assert.AreEqual(expected, sequence.GetHashCode());
    }


    /// <summary>
    /// The per-offset remove-dot fold inside the removed-base-offsets map hash must be the exclusive-or
    /// of each remove-dot's hash code, for an offset certified by two concurrent removes.
    /// </summary>
    [TestMethod]
    public void GetHashCodeFoldsARemovedBaseOffsetsRemoveDotsByExclusiveOr()
    {
        Dot removeDotA = new(R1, 1);
        Dot removeDotB = new(R2, 1);
        VectorClockState context = new([new ReplicaCounterEntry(ImmutableArray.Create(R1.AsSpan()), 1), new ReplicaCounterEntry(ImmutableArray.Create(R2.AsSpan()), 1)]);
        VectorClockState emptyClock = new([]);

        var state = new OffsetAnchoredSequenceState<string>(
            Base: ["b0", "b1"],
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets:
            [
                new OffsetBaseRemovalEntry(0, [new DotState(ImmutableArray.Create(R1.AsSpan()), 1), new DotState(ImmutableArray.Create(R2.AsSpan()), 1)])
            ],
            Context: context,
            Vertices: [],
            Tombstones: [],
            CompactedDotAnchors: [],
            CompactedBaseOffsets: []);

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int expectedRemoveDotsFold = removeDotA.GetHashCode() ^ removeDotB.GetHashCode();
        int expectedRemovedHash = HashCode.Combine(0, expectedRemoveDotsFold);
        int expected = HashCode.Combine(HashCode.Combine(2, VectorClock.Empty, 0), VectorClock.FromState(context), 0, 0, expectedRemovedHash, 0, 0);
        Assert.AreEqual(expected, sequence.GetHashCode());
    }


    /// <summary>
    /// Composing a dropped dot's translation through a second compaction must resolve a positive prior
    /// base offset via the offset-to-offset map, never treat it as the head sentinel.
    /// </summary>
    [TestMethod]
    public void TranslateAnchorComposesAPositiveBaseOffsetAcrossTwoCompactions()
    {
        OffsetAnchoredSequence<string> seq0 = OffsetAnchoredSequence<string>.WithBase(["base0"]);
        (OffsetAnchoredSequence<string> seq1, OffsetAddress x1) = seq0.InsertAtHead("x1val", R1);
        (OffsetAnchoredSequence<string> seq2, _) = seq1.InsertAtHead("x2val", R2);

        VectorClock frontier1 = seq2.CausalContext;
        OffsetAnchoredSequence<string> compacted1 = seq2.Compact(frontier1, seq2.CertifiedProjection(frontier1));

        (OffsetAnchoredSequence<string> seq3, _) = compacted1.InsertAtHead("x3val", R1);
        VectorClock frontier2 = seq3.CausalContext;
        OffsetAnchoredSequence<string> compacted2 = seq3.Compact(frontier2, seq3.CertifiedProjection(frontier2));

        //x1 converted to base offset 1 in the first compaction; that positive offset must compose through
        //the second compaction's offset map, landing at base offset 2 of the new generation.
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(2), 2), compacted2.TranslateAnchor(x1));
    }


    /// <summary>
    /// ToState orders the dot-translation map by the dropped dot's (replica, counter), so every R1 entry
    /// precedes every R2 entry regardless of mint or drop order.
    /// </summary>
    [TestMethod]
    public void ToStateOrdersCompactedDotAnchorsByReplicaThenCounter()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress p) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "p", R2);
        (sequence, OffsetAddress q) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(1), 0), "q", R1);
        (sequence, OffsetAddress r) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(2), 0), "r", R2);
        (sequence, OffsetAddress s) = sequence.InsertAfter(p, "s", R1);

        VectorClock frontier = FrontierCovering(p.Anchor.LiveId!, q.Anchor.LiveId!, r.Anchor.LiveId!, s.Anchor.LiveId!);
        OffsetAnchoredSequence<string> compacted = sequence.Compact(frontier, sequence.CertifiedProjection(frontier));

        ImmutableArray<OffsetTranslationEntry> dotAnchors = compacted.ToState().CompactedDotAnchors;
        Assert.HasCount(4, dotAnchors);

        int lastR1Index = -1;
        int firstR2Index = int.MaxValue;
        for (int i = 0; i < dotAnchors.Length; i++)
        {
            byte replicaByte = dotAnchors[i].Dropped.Replica[0];
            if (replicaByte == 1)
            {
                lastR1Index = i;
            }
            else if (replicaByte == 2 && i < firstR2Index)
            {
                firstR2Index = i;
            }
        }

        Assert.IsLessThan(firstR2Index, lastR1Index);
    }


    /// <summary>
    /// A CompactedDotAnchors target admitted by FromState that names a still-live tombstoned vertex composes
    /// through a following compaction to that vertex's collapsed base anchor, never the stale live anchor.
    /// </summary>
    /// <remarks>
    /// FromState admits a live translation target whose dot is a tombstoned vertex: ValidateTargetAnchor
    /// requires only that the dot be a vertex, and the W-shape guard bars only a live untombstoned dropped
    /// dot. V is stable and certified-removed at the frontier, so it drops and the composition seam takes the
    /// retained-false arm, resolving the dropped dot D to the anchor the walk recorded for V.
    /// </remarks>
    [TestMethod]
    public void ComposeThroughCompactionResolvesALiveTargetOfADroppedVertexToItsBaseAnchor()
    {
        //A tombstoned live vertex V anchored at base offset 0, its remove certified by the state's context.
        OffsetAnchoredSequence<string> seed = OffsetAnchoredSequence<string>.WithBase(Base);
        (seed, OffsetAddress v) = seed.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "v", R1);
        seed = seed.Remove(v, R1);

        //A dropped dot D that is not a vertex, translating to AtLive(V). FromState accepts this shape at the
        //deserialization boundary, so a following compaction must resolve it rather than carry it stale.
        Dot droppedD = new(R2, 1);
        OffsetAnchoredSequenceState<string> forged = seed.ToState() with
        {
            CompactedDotAnchors = [new OffsetTranslationEntry(DotStateOf(droppedD), new OffsetAnchorState(-1, DotStateOf(v.Anchor.LiveId!)))]
        };
        OffsetAnchoredSequence<string> loaded = OffsetAnchoredSequence<string>.FromState(forged);

        //The frontier covers V's insert and certifies its remove, so V drops to the gap anchor of base offset 0.
        VectorClock frontier = loaded.CausalContext;
        OffsetAnchoredSequence<string> compacted = loaded.Compact(frontier, loaded.CertifiedProjection(frontier));

        //D must translate to V's collapsed base anchor, never to the stale live anchor.
        OffsetAddress? translated = compacted.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtLive(droppedD), 0));
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.AtBase(0), 0), translated);
    }


    /// <summary>
    /// Pins that <see cref="OffsetAnchoredSequence{TValue}.InsertAfter"/> rejects a null address with
    /// <see cref="ArgumentNullException"/> before dereferencing it.
    /// </summary>
    [TestMethod]
    public void InsertAfterValidatesTheAddressIsNotNull()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);

        Assert.ThrowsExactly<ArgumentNullException>(() => sequence.InsertAfter(null!, "x", R1));
    }


    /// <summary>
    /// Merge fences on the base-frontier generation identity even when the base values and the
    /// base-generation ordinal agree, per the generation fence in <see cref="OffsetAnchoredSequence{TValue}.Merge"/>.
    /// </summary>
    [TestMethod]
    public void MergingSequencesWithDifferentBaseFrontiersFailsClosed()
    {
        VectorClock frontierA = VectorClock.Empty.Increment(R1);
        VectorClock frontierB = frontierA.Increment(R2);
        OffsetAnchoredSequenceState<string> stateA = new(["v"], frontierA.ToState(), 1, [], frontierA.ToState(), [], [], [], []);
        OffsetAnchoredSequenceState<string> stateB = new(["v"], frontierB.ToState(), 1, [], frontierB.ToState(), [], [], [], []);
        OffsetAnchoredSequence<string> a = OffsetAnchoredSequence<string>.FromState(stateA);
        OffsetAnchoredSequence<string> b = OffsetAnchoredSequence<string>.FromState(stateB);

        Assert.ThrowsExactly<InvalidOperationException>(() => a.Merge(b));
    }


    /// <summary>
    /// ToState orders vertices by (replica, counter), so every R1 vertex precedes every R2 vertex
    /// regardless of the order they were minted in.
    /// </summary>
    [TestMethod]
    public void ToStateOrdersVerticesByReplicaThenCounter()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (OffsetAnchoredSequence<string> withA, OffsetAddress a) = sequence.InsertAtHead("a", R2);
        (OffsetAnchoredSequence<string> withB, OffsetAddress b) = withA.InsertAfter(a, "b", R1);
        (OffsetAnchoredSequence<string> withC, OffsetAddress c) = withB.InsertAfter(b, "c", R2);
        (OffsetAnchoredSequence<string> withD, _) = withC.InsertAfter(c, "d", R1);

        ImmutableArray<OffsetVertexEntry<string>> vertices = withD.ToState().Vertices;
        Assert.HasCount(4, vertices);

        int lastR1Index = -1;
        int firstR2Index = int.MaxValue;
        for (int i = 0; i < vertices.Length; i++)
        {
            byte replicaByte = vertices[i].Id.Replica[0];
            if (replicaByte == 1)
            {
                lastR1Index = i;
            }
            else if (replicaByte == 2 && i < firstR2Index)
            {
                firstR2Index = i;
            }
        }

        Assert.IsLessThan(firstR2Index, lastR1Index);
    }


    /// <summary>
    /// Re-removing an already-removed base offset mints no new remove-dot and returns this sequence
    /// unchanged.
    /// </summary>
    [TestMethod]
    public void RemovingAnAlreadyRemovedBaseOffsetIsIdempotent()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        OffsetAnchoredSequence<string> onceRemoved = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(1), 0), R1);

        OffsetAnchoredSequence<string> againRemoved = onceRemoved.Remove(new OffsetAddress(OffsetAnchor.AtBase(1), 0), R2);

        Assert.IsTrue(ReferenceEquals(onceRemoved, againRemoved));
    }


    /// <summary>
    /// Re-removing an already-tombstoned live element mints no new remove-dot and returns this sequence
    /// unchanged, per <see cref="OffsetAnchoredSequence{TValue}.Remove"/>'s idempotency contract.
    /// </summary>
    [TestMethod]
    public void RemovingAnAlreadyTombstonedLiveElementIsIdempotent()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), "x", R1);
        OffsetAnchoredSequence<string> onceRemoved = sequence.Remove(x, R1);

        OffsetAnchoredSequence<string> againRemoved = onceRemoved.Remove(x, R2);

        Assert.IsTrue(ReferenceEquals(onceRemoved, againRemoved));
    }


    /// <summary>
    /// GetHashCode's per-entry vertex fold must be sensitive to which vertex changed: two sequences built
    /// from the identical replica/anchor history but differing in one inserted value must hash differently.
    /// </summary>
    [TestMethod]
    public void GetHashCodeDistinguishesDifferingVertexContent()
    {
        (OffsetAnchoredSequence<string> a, _) = OffsetAnchoredSequence<string>.WithBase(Base).InsertAtHead("p", R1);
        (a, _) = a.InsertAtHead("q", R2);

        (OffsetAnchoredSequence<string> b, _) = OffsetAnchoredSequence<string>.WithBase(Base).InsertAtHead("p", R1);
        (b, _) = b.InsertAtHead("r", R2);

        Assert.AreNotEqual(a.GetHashCode(), b.GetHashCode());
    }


    /// <summary>
    /// A current-generation base offset exactly at Base.Length is out of range and TranslateAnchor must
    /// refuse it rather than treat it as servable.
    /// </summary>
    [TestMethod]
    public void ResolveBaseAddressRejectsAnOffsetAtTheEndOfTheCurrentBase()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        OffsetAddress outOfRange = new(OffsetAnchor.AtBase(Base.Length), 0);

        Assert.IsNull(sequence.TranslateAnchor(outOfRange));
    }


    /// <summary>
    /// ToState orders tombstones by the target's (replica, counter), so every R1-targeted tombstone
    /// precedes every R2-targeted tombstone regardless of removal order.
    /// </summary>
    [TestMethod]
    public void ToStateOrdersTombstonesByReplicaThenCounter()
    {
        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.WithBase(Base);
        (sequence, OffsetAddress a) = sequence.InsertAtHead("a", R2);
        (sequence, OffsetAddress b) = sequence.InsertAfter(a, "b", R1);
        (sequence, OffsetAddress c) = sequence.InsertAfter(b, "c", R2);
        (sequence, OffsetAddress d) = sequence.InsertAfter(c, "d", R1);
        sequence = sequence.Remove(a, R1);
        sequence = sequence.Remove(b, R2);
        sequence = sequence.Remove(c, R1);
        sequence = sequence.Remove(d, R2);

        ImmutableArray<OffsetTombstoneEntry> tombstones = sequence.ToState().Tombstones;
        Assert.HasCount(4, tombstones);

        int lastR1Index = -1;
        int firstR2Index = int.MaxValue;
        for (int i = 0; i < tombstones.Length; i++)
        {
            byte replicaByte = tombstones[i].Target.Replica[0];
            if (replicaByte == 1)
            {
                lastR1Index = i;
            }
            else if (replicaByte == 2 && i < firstR2Index)
            {
                firstR2Index = i;
            }
        }

        Assert.IsLessThan(firstR2Index, lastR1Index);
    }


    /// <summary>
    /// The tombstone-map hash must be the exclusive-or of each target's combine, for two distinct
    /// legacy (empty remove-dot set) targets.
    /// </summary>
    [TestMethod]
    public void GetHashCodeFoldsTheTombstoneMapByExclusiveOr()
    {
        VectorClockState emptyClock = new([]);
        Dot target1 = new(R1, 1);
        Dot target2 = new(Replica(3), 1);

        var state = new OffsetAnchoredSequenceState<string>(
            Base: ImmutableArray<string>.Empty,
            BaseFrontier: emptyClock,
            BaseGeneration: 0,
            RemovedBaseOffsets: [],
            Context: emptyClock,
            Vertices: [],
            Tombstones:
            [
                new OffsetTombstoneEntry(new DotState(ImmutableArray.Create(target1.Replica.AsSpan()), target1.Counter), []),
                new OffsetTombstoneEntry(new DotState(ImmutableArray.Create(target2.Replica.AsSpan()), target2.Counter), [])
            ],
            CompactedDotAnchors: [],
            CompactedBaseOffsets: []);

        OffsetAnchoredSequence<string> sequence = OffsetAnchoredSequence<string>.FromState(state);

        int expectedTombstonesHash = HashCode.Combine(target1, 0) ^ HashCode.Combine(target2, 0);
        int expected = HashCode.Combine(HashCode.Combine(0, VectorClock.Empty, 0), VectorClock.Empty, 0, expectedTombstonesHash, 0, 0, 0);
        Assert.AreEqual(expected, sequence.GetHashCode());
    }
}
