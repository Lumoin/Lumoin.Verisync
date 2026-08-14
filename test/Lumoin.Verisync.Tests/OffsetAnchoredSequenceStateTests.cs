using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Deterministic, hand-built coverage of <see cref="OffsetAnchoredSequence{TValue}"/> state round-trips:
/// fresh, edited, compacted, pending-removed, legacy, and ghost-witness generations; the deterministic
/// ordering of <c>ToState</c>; and every fail-closed guard <c>FromState</c> raises against state no honest
/// history produces — one DISTINCT discriminating case per validation clause, each crafted so no earlier
/// guard masks the one under test. Valid state records are obtained from real sequences and then mutated
/// one field at a time with <c>with</c> expressions. The base generation ordinal is genesis exactly when
/// the base frontier is empty, so a compacted fixture carries generation 1 and a genesis fixture carries 0.
/// </summary>
/// <remarks>
/// <c>ToState</c> determinism is asserted over the canonical serialized bytes rather than raw record
/// equality: the state record's <see cref="ImmutableArray{T}"/> members compare by backing-array reference,
/// not content, so two records built from separate <c>ToState</c> calls are never reference-equal even when
/// their ordering is identical — the deterministic byte encoding is the property under test.
/// </remarks>
[TestClass]
internal sealed class OffsetAnchoredSequenceStateTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);
    private static ReplicaId R3 { get; } = Replica(3);

    /// <summary>
    /// Reused so the byte arrays behind the DotState records keep reference identity (DotState compares by
    /// reference), and to satisfy CA1861 by hoisting the base array.
    /// </summary>
    private static ImmutableArray<int> BaseValues { get; } = [10, 20, 30];

    private static ImmutableArray<byte> R1Bytes { get; } = ImmutableArray.Create(R1.AsSpan());

    private static ImmutableArray<byte> R2Bytes { get; } = ImmutableArray.Create(R2.AsSpan());

    private static ImmutableArray<byte> R3Bytes { get; } = ImmutableArray.Create(R3.AsSpan());


    [TestMethod]
    public void FreshGenerationRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(sequence.ToState());

        Assert.AreEqual(sequence, back);
    }


    [TestMethod]
    public void EmptyGenerationRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(OffsetAnchoredSequence<int>.Empty.ToState());

        Assert.AreEqual(OffsetAnchoredSequence<int>.Empty, back);
    }


    /// <summary>
    /// An edited generation — head, base, and live-anchored inserts plus a dotted base removal and a dotted
    /// live removal — survives the round-trip exactly.
    /// </summary>
    [TestMethod]
    public void EditedGenerationRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> sequence = Edited();

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(sequence.ToState());

        Assert.AreEqual(sequence, back);
    }


    /// <summary>
    /// A compacted generation carrying both translation maps (dropped-dot anchors and anchor-typed rebased base
    /// offsets) plus the stamped generation identity round-trips with its servability intact.
    /// </summary>
    [TestMethod]
    public void CompactedGenerationWithBothMapsRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> compacted = CompactedWithBothMaps();

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(compacted.ToState());

        Assert.AreEqual(compacted, back);

        //The maps survived: a previous-generation base address still translates through the map arm.
        Assert.IsNotNull(back.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtBase(1), 0)));
    }


    /// <summary>
    /// A pending-removed conversion — an uncertified-removed vertex materialized into the base with its
    /// remove-dot keyed to the new offset — round-trips, stays hidden, and keeps its marking.
    /// </summary>
    [TestMethod]
    public void APendingRemovedGenerationRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> pending = PendingRemoved();

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(pending.ToState());

        Assert.AreEqual(pending, back);
        Assert.AreSequenceEqual(BaseValues.ToArray(), back.Values.ToArray());

        OffsetBaseRemovalEntry marking = back.ToState().RemovedBaseOffsets[0];
        Assert.AreEqual(1, marking.Offset);
        Assert.HasCount(1, marking.RemoveDots);
    }


    /// <summary>
    /// Gate 9: legacy (v1-loaded, empty remove-dot set) removals on BOTH axes round-trip, stay hidden, and are
    /// retained forever — a compaction converts the legacy tombstone pending-removed with its EMPTY set and
    /// keeps the legacy base slot, because an empty set can never be certified.
    /// </summary>
    [TestMethod]
    public void ALegacyStateRoundTripsAndIsRetainedForever()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R1);
        OffsetAnchoredSequenceState<int> legacy = sequence.ToState() with
        {
            Tombstones = [new OffsetTombstoneEntry(new DotState(R1Bytes, 1), [])],
            RemovedBaseOffsets = [new OffsetBaseRemovalEntry(2, [])]
        };

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(legacy);
        int[] hidden = [10, 20];
        Assert.AreSequenceEqual(hidden, back.Values.ToArray());
        Assert.AreEqual(back, OffsetAnchoredSequence<int>.FromState(back.ToState()));
        Assert.AreEqual(x, back.TranslateAnchor(x));

        //The state's own context certifies every dot it covers, yet neither legacy removal is
        //reclaimable: the tombstone converts pending-removed carrying its empty set, the slot stays.
        VectorClock frontier = back.CausalContext;
        OffsetAnchoredSequence<int> compacted = back.Compact(frontier, back.CertifiedProjection(frontier));
        Assert.AreSequenceEqual(hidden, compacted.Values.ToArray());
        ImmutableArray<OffsetBaseRemovalEntry> markings = compacted.ToState().RemovedBaseOffsets;
        Assert.HasCount(2, markings);
        Assert.AreEqual(1, markings[0].Offset);
        Assert.HasCount(0, markings[0].RemoveDots);
        Assert.AreEqual(3, markings[1].Offset);
        Assert.HasCount(0, markings[1].RemoveDots);
    }


    /// <summary>
    /// The legal half of the W-shape rule: a ghost re-entered by merge sits live WITH its tombstone while the
    /// witness entry remains — that state round-trips; only the untombstoned form is rejected.
    /// </summary>
    [TestMethod]
    public void AGhostWitnessShapeRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> merged = GhostWitnessMerge();

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(merged.ToState());

        Assert.AreEqual(merged, back);
    }


    /// <summary>
    /// ToState twice on the same instance yields the same canonical encoding, so its ordering is deterministic.
    /// </summary>
    [TestMethod]
    public void ToStateIsDeterministicForTheSameInstance()
    {
        OffsetAnchoredSequence<int> sequence = Edited();

        Assert.AreSequenceEqual(Encode(sequence.ToState()), Encode(sequence.ToState()));
    }


    /// <summary>
    /// Two sequences built by different merge orders carry equal state: each ToState reconstructs to the same
    /// sequence, so ToState does not depend on insertion history.
    /// </summary>
    /// <remarks>
    /// The states are compared by reconstruction rather than by record equality because DotState compares its
    /// replica bytes by reference, and the ordered-section comparison is exercised by the same-instance
    /// determinism test above.
    /// </remarks>
    [TestMethod]
    public void MergeCommutativityPairYieldsEqualStates()
    {
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (OffsetAnchoredSequence<int> byFirst, _) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 100, R1);
        (OffsetAnchoredSequence<int> bySecond, _) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 200, R2);

        OffsetAnchoredSequence<int> oneWay = byFirst.Merge(bySecond);
        OffsetAnchoredSequence<int> otherWay = bySecond.Merge(byFirst);

        Assert.AreEqual(oneWay, otherWay);
        Assert.AreEqual(
            OffsetAnchoredSequence<int>.FromState(oneWay.ToState()),
            OffsetAnchoredSequence<int>.FromState(otherWay.ToState()));
    }


    [TestMethod]
    public void FromStateRejectsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => OffsetAnchoredSequence<int>.FromState(null!));
    }


    [TestMethod]
    public void FromStateRejectsARemovedOffsetOutsideTheBase()
    {
        //The remove-dot is covered, positive, and collision-free, so only the range guard can fire.
        OffsetAnchoredSequenceState<int> state = Edited().ToState() with
        {
            RemovedBaseOffsets = [new OffsetBaseRemovalEntry(BaseValues.Length, [new DotState(R2Bytes, 1)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicatedRemovedOffset()
    {
        //Two distinct, covered, collision-free remove-dots on one offset key only the duplicate guard.
        OffsetAnchoredSequenceState<int> state = Edited().ToState() with
        {
            RemovedBaseOffsets =
            [
                new OffsetBaseRemovalEntry(1, [new DotState(R2Bytes, 1)]),
                new OffsetBaseRemovalEntry(1, [new DotState(R1Bytes, 2)])
            ]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicateVertexId()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetVertexEntry<int> first = valid.Vertices[0];
        OffsetAnchoredSequenceState<int> state = valid with { Vertices = [first, first] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsAVertexAnchorThatViolatesTheCanonicalShape()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetVertexEntry<int> entry = valid.Vertices[0];

        //A non-null LiveId with BaseOffset != -1 is a shape no honest anchor takes.
        OffsetVertexEntry<int> malformed = entry with { Anchor = new OffsetAnchorState(0, new DotState(R1Bytes, 1)) };
        OffsetAnchoredSequenceState<int> state = valid with { Vertices = [malformed] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsABaseAnchorAtOrBeyondTheBase()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetVertexEntry<int> entry = valid.Vertices[0];

        OffsetVertexEntry<int> outOfRange = entry with { Anchor = new OffsetAnchorState(BaseValues.Length, null) };
        OffsetAnchoredSequenceState<int> state = valid with { Vertices = [outOfRange] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsALiveAnchorNamingANonVertexDot()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetVertexEntry<int> entry = valid.Vertices[0];

        OffsetVertexEntry<int> dangling = entry with { Anchor = new OffsetAnchorState(-1, new DotState(R1Bytes, 999)) };
        OffsetAnchoredSequenceState<int> state = valid with { Vertices = [dangling] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsATranslationTargetThatViolatesTheCanonicalShape()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetTranslationEntry entry = valid.CompactedDotAnchors[0];

        OffsetTranslationEntry malformed = entry with { Target = new OffsetAnchorState(0, new DotState(R1Bytes, 1)) };
        OffsetAnchoredSequenceState<int> state = valid with { CompactedDotAnchors = [malformed] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsACycleInTheLiveAnchorGraph()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();

        //Two vertices each anchored at the other through AtLive links: the anchor walk never reaches a
        //head or base anchor. Both dots are context-covered so the coverage guard cannot mask the cycle.
        DotState idLeft = new(R1Bytes, 2);
        DotState idRight = new(R2Bytes, 1);
        OffsetVertexEntry<int> left = new(idLeft, new OffsetAnchorState(-1, idRight), 1);
        OffsetVertexEntry<int> right = new(idRight, new OffsetAnchorState(-1, idLeft), 2);
        OffsetAnchoredSequenceState<int> state = valid with { Vertices = [left, right] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsACompactedBaseOffsetWithNegativePrevious()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            CompactedBaseOffsets = [new OffsetBaseAnchorEntry(-1, new OffsetAnchorState(0, null))]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsACompactedBaseAnchorTargetOutsideTheBase()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            CompactedBaseOffsets = [new OffsetBaseAnchorEntry(0, new OffsetAnchorState(valid.Base.Length, null))]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsACompactedBaseAnchorTargetWithACanonicalShapeViolation()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            CompactedBaseOffsets = [new OffsetBaseAnchorEntry(0, new OffsetAnchorState(0, new DotState(R1Bytes, 1)))]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicatedCompactedBaseOffsetPrevious()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            CompactedBaseOffsets =
            [
                new OffsetBaseAnchorEntry(3, new OffsetAnchorState(0, null)),
                new OffsetBaseAnchorEntry(3, new OffsetAnchorState(1, null))
            ]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// A base-offset translation whose target is a LIVE anchor is a forged state: honest base-offset
    /// translations point only at base positions or the head, and a live target would dangle uncomposed through
    /// a drop-only compaction (which keeps this map verbatim while the vertex may drop).
    /// </summary>
    /// <remarks>
    /// (R2,3) is a retained live vertex of the compacted fixture, so the live-target guard fires before the
    /// target-anchor validation would otherwise accept it; the base- and head-targeting twins both load fine.
    /// </remarks>
    [TestMethod]
    public void ABaseOffsetTranslationTargetingALiveAnchorFailsClosed()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();

        OffsetAnchoredSequenceState<int> liveTarget = valid with
        {
            CompactedBaseOffsets = [new OffsetBaseAnchorEntry(0, new OffsetAnchorState(-1, new DotState(R2Bytes, 3)))]
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(liveTarget));

        OffsetAnchoredSequenceState<int> baseTarget = valid with
        {
            CompactedBaseOffsets = [new OffsetBaseAnchorEntry(0, new OffsetAnchorState(0, null))]
        };
        Assert.IsNotNull(OffsetAnchoredSequence<int>.FromState(baseTarget));

        OffsetAnchoredSequenceState<int> headTarget = valid with
        {
            CompactedBaseOffsets = [new OffsetBaseAnchorEntry(0, new OffsetAnchorState(-1, null))]
        };
        Assert.IsNotNull(OffsetAnchoredSequence<int>.FromState(headTarget));
    }


    /// <summary>
    /// The CompactedDotAnchors duplicate posture is unified to TryAdd-throw; the duplicated dropped dot is
    /// neither live nor tombstoned, so the W-shape guard cannot mask the duplicate.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsADuplicatedCompactedDotAnchor()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetTranslationEntry entry = valid.CompactedDotAnchors[0];
        OffsetAnchoredSequenceState<int> state = valid with { CompactedDotAnchors = [entry, entry] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// W-shape rejection: a translation entry whose dropped dot is simultaneously a LIVE untombstoned vertex is
    /// a forged state.
    /// </summary>
    /// <remarks>
    /// The ghost-plus-witness shape stays legal (see the round-trip above).
    /// </remarks>
    [TestMethod]
    public void FromStateRejectsAWShapeTranslation()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();

        //(R2,3) is the retained live, untombstoned vertex of the compacted fixture.
        OffsetAnchoredSequenceState<int> state = valid with
        {
            CompactedDotAnchors =
            [
                .. valid.CompactedDotAnchors,
                new OffsetTranslationEntry(new DotState(R2Bytes, 3), new OffsetAnchorState(0, null))
            ]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// Invariant CC over vertex dots — new for offset in v2.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAVertexDotNotCoveredByTheContext()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            Vertices = [.. valid.Vertices, new OffsetVertexEntry<int>(new DotState(R1Bytes, 99), new OffsetAnchorState(-1, null), 77)]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// Invariant CC over live remove-dots: the target is a real vertex, the dot is positive and collision-free,
    /// so only the coverage guard can fire.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsALiveRemoveDotNotCoveredByTheContext()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R2Bytes, 2), [new DotState(R2Bytes, 99)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// Invariant CC over base remove-dots — base removals are never exempt.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsABaseRemoveDotNotCoveredByTheContext()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            RemovedBaseOffsets = [.. valid.RemovedBaseOffsets, new OffsetBaseRemovalEntry(1, [new DotState(R1Bytes, 99)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// The coverage exemption is exactly the orphan live TARGET — its insert may not have arrived — and never
    /// the orphan's remove-dots.
    /// </summary>
    [TestMethod]
    public void AnOrphanTombstoneTargetIsExemptFromCoverageButItsRemoveDotsAreNot()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();

        OffsetAnchoredSequenceState<int> accepted = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R3Bytes, 50), [new DotState(R2Bytes, 1)])]
        };
        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(accepted);
        Assert.AreSequenceEqual(Edited().Values.ToArray(), back.Values.ToArray());

        OffsetAnchoredSequenceState<int> rejected = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R3Bytes, 50), [new DotState(R3Bytes, 1)])]
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(rejected));
    }


    /// <summary>
    /// Counter positivity on both remove axes and on an orphan target: a zero counter passes the coverage
    /// comparison, so positivity is the only guard that can fire.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsANonPositiveDotCounterOnEitherRemoveAxis()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();

        OffsetAnchoredSequenceState<int> liveAxis = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R2Bytes, 2), [new DotState(R2Bytes, 0)])]
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(liveAxis));

        OffsetAnchoredSequenceState<int> baseAxis = valid with
        {
            RemovedBaseOffsets = [.. valid.RemovedBaseOffsets, new OffsetBaseRemovalEntry(1, [new DotState(R1Bytes, 0)])]
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(baseAxis));

        OffsetAnchoredSequenceState<int> orphanTarget = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R3Bytes, 0), [new DotState(R2Bytes, 1)])]
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(orphanTarget));
    }


    /// <summary>
    /// THE cross-axis clause (§14.5): one dot pool spans both remove axes, so a remove-dot appearing as a live
    /// remove AND a base remove is rejected even though each entry is valid on its own.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsARemoveDotSharedAcrossTheAxes()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        DotState shared = new(R2Bytes, 1);
        OffsetAnchoredSequenceState<int> state = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R2Bytes, 2), [shared])],
            RemovedBaseOffsets = [.. valid.RemovedBaseOffsets, new OffsetBaseRemovalEntry(1, [shared])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicateRemoveDotWithinATombstone()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R2Bytes, 2), [new DotState(R2Bytes, 1), new DotState(R2Bytes, 1)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsARemoveDotSharedByTwoTombstones()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            Tombstones =
            [
                new OffsetTombstoneEntry(new DotState(R1Bytes, 1), [new DotState(R2Bytes, 1)]),
                new OffsetTombstoneEntry(new DotState(R2Bytes, 2), [new DotState(R2Bytes, 1)])
            ]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicateRemoveDotWithinABaseRemoval()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            RemovedBaseOffsets = [new OffsetBaseRemovalEntry(1, [new DotState(R2Bytes, 1), new DotState(R2Bytes, 1)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// Remove-dot and vertex-id disjointness, live axis: (R2,2) is a vertex id of the fixture.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsARemoveDotEqualToAVertexId()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            Tombstones = [.. valid.Tombstones, new OffsetTombstoneEntry(new DotState(R1Bytes, 1), [new DotState(R2Bytes, 2)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// Remove-dot and vertex-id disjointness crosses the axes too: a base remove-dot aliasing a vertex id would
    /// let an honest live certification reclaim an unremoved base slot.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsABaseRemoveDotEqualToAVertexId()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();
        OffsetAnchoredSequenceState<int> state = valid with
        {
            RemovedBaseOffsets = [.. valid.RemovedBaseOffsets, new OffsetBaseRemovalEntry(1, [new DotState(R1Bytes, 1)])]
        };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    /// <summary>
    /// An absent array is not the same statement as an explicitly empty one: every default ImmutableArray,
    /// including a per-entry RemoveDots, fails closed.
    /// </summary>
    [TestMethod]
    public void FromStateFailsClosedOnDefaultArrays()
    {
        OffsetAnchoredSequenceState<int> valid = Edited().ToState();

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { Base = default }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { Vertices = default }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { Tombstones = default }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { RemovedBaseOffsets = default }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { CompactedDotAnchors = default }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { CompactedBaseOffsets = default }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { Tombstones = [valid.Tombstones[0] with { RemoveDots = default }] }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(valid with { RemovedBaseOffsets = [valid.RemovedBaseOffsets[0] with { RemoveDots = default }] }));
    }


    /// <summary>
    /// §12.2: the generation-fence field cannot arrive inconsistent with the context that certifies the
    /// generation — the context must dominate the base frontier element-wise.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsABaseFrontierTheContextDoesNotDominate()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();

        //An axis the context has never seen.
        OffsetAnchoredSequenceState<int> foreignAxis = valid with
        {
            BaseFrontier = new VectorClockState([new ReplicaCounterEntry(R3Bytes, 1)])
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(foreignAxis));

        //A known axis raised above the context.
        OffsetAnchoredSequenceState<int> raisedAxis = valid with
        {
            BaseFrontier = new VectorClockState([new ReplicaCounterEntry(R1Bytes, 99)])
        };
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(raisedAxis));
    }


    /// <summary>
    /// §5: the base generation ordinal is genesis EXACTLY when the base frontier is empty, and is never
    /// negative.
    /// </summary>
    /// <remarks>
    /// A genesis frontier paired with a non-zero generation, a non-genesis frontier paired with the genesis
    /// generation, and a negative generation are each forged and fail closed.
    /// </remarks>
    [TestMethod]
    public void FromStateRejectsABaseGenerationInconsistentWithItsFrontier()
    {
        //Edited never compacts, so its base frontier is empty and its generation is genesis.
        OffsetAnchoredSequenceState<int> genesisFrontier = Edited().ToState();
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(genesisFrontier with { BaseGeneration = 1 }));

        //The compacted fixture carries a non-genesis frontier and generation 1.
        OffsetAnchoredSequenceState<int> nonGenesisFrontier = CompactedWithBothMaps().ToState();
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(nonGenesisFrontier with { BaseGeneration = 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(nonGenesisFrontier with { BaseGeneration = -1 }));
    }


    /// <summary>
    /// An edited generation: a head insert, a base-anchored insert, a live-anchored insert chained off it, a
    /// dotted base removal by R1, and a dotted live removal by R2 — every anchor kind plus both removal kinds.
    /// </summary>
    /// <remarks>
    /// Context {R1:4, R2:3}; vertices (R1,1), (R2,2), (R1,3); remove-dots (R1,4) and (R2,3), leaving (R1,2) and
    /// (R2,1) as covered, collision-free dots the fail-closed crafts can use.
    /// </remarks>
    private static OffsetAnchoredSequence<int> Edited()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, _) = sequence.InsertAtHead(40, R1);
        (sequence, OffsetAddress atBase) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R2);
        (sequence, OffsetAddress chained) = sequence.InsertAfter(atBase, 60, R1);
        sequence = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(2), 0), R1);

        return sequence.Remove(chained, R2);
    }


    /// <summary>
    /// A compacted generation that carries both translation maps and the stamped identity, then a post-seal
    /// live edit: the converted vertex 50 shifts the base so CompactedBaseOffsets is non-empty, the certified
    /// tombstone 60 drops so CompactedDotAnchors is non-empty, the frontier is insert-quiescent as §17
    /// requires, and a fresh insert 70=(R2,3) lands live in the new generation AFTER the compaction — the
    /// retained live vertex the W-shape fixture references.
    /// </summary>
    /// <remarks>
    /// The compaction is base-changing, so the sealed generation is generation 1 and the post-seal base address
    /// of offset 3 carries that generation.
    /// </remarks>
    private static OffsetAnchoredSequence<int> CompactedWithBothMaps()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, _) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R1);
        (sequence, OffsetAddress dropped) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(1), 0), 60, R1);
        sequence = sequence.Remove(dropped, R2);

        //The frontier certifies both inserts and the remove-dot, so the state is insert-quiescent.
        VectorClock frontier = sequence.CausalContext;
        OffsetAnchoredSequence<int> compacted = sequence.Compact(frontier, sequence.CertifiedProjection(frontier));

        //A fresh insert after the seal lands live in the new generation: 70=(R2,3).
        (compacted, _) = compacted.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(3), 1), 70, R2);

        return compacted;
    }


    /// <summary>
    /// A pending-removed generation: an uncertified-removed stable vertex converted into the base, hidden, its
    /// remove-dot keyed to the new offset, and the generation identity stamped.
    /// </summary>
    private static OffsetAnchoredSequence<int> PendingRemoved()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R1);
        sequence = sequence.Remove(x, R2);

        //The frontier covers the insert but not R2's remove.
        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);

        return sequence.Compact(frontier, sequence.CertifiedProjection(frontier));
    }


    /// <summary>
    /// A ghost re-entered by merge after its certified drop: the vertex is live WITH its tombstone while the
    /// compacted operand's witness entry remains — the legal half of the W-shape rule.
    /// </summary>
    private static OffsetAnchoredSequence<int> GhostWitnessMerge()
    {
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (OffsetAnchoredSequence<int> withX, OffsetAddress x) = shared.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R1);
        OffsetAnchoredSequence<int> ghostHolder = withX.Remove(x, R1);

        VectorClock frontier = ghostHolder.CausalContext;
        OffsetAnchoredSequence<int> compacted = ghostHolder.Compact(frontier, ghostHolder.CertifiedProjection(frontier));

        return compacted.Merge(ghostHolder);
    }


    /// <summary>
    /// Canonically encodes a state to bytes through the deterministic JSON codec, for ordering comparisons.
    /// </summary>
    private static byte[] Encode(OffsetAnchoredSequenceState<int> state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        CrdtStateJson.CreateOffsetAnchoredSequenceStateSerializer<int>(WriteInt)(state, buffer);

        return buffer.WrittenSpan.ToArray();
    }


    private static void WriteInt(Utf8JsonWriter writer, int value) => writer.WriteNumberValue(value);


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
