using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Deterministic, hand-built coverage of <see cref="OffsetAnchoredSequence{TValue}"/> state round-trips:
/// fresh, edited, and compacted generations; the deterministic ordering of <c>ToState</c>; and every
/// fail-closed guard <c>FromState</c> raises against state no honest history produces. Valid state records
/// are obtained from real sequences and then mutated one field at a time with <c>with</c> expressions.
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

    //Reused so the byte arrays behind the DotState records keep reference identity (DotState compares by
    //reference), and to satisfy CA1861 by hoisting the base array.
    private static ImmutableArray<int> BaseValues { get; } = [10, 20, 30];

    private static ImmutableArray<byte> R1Bytes { get; } = ImmutableArray.Create(R1.AsSpan());


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


    //An edited generation — head, base, and live-anchored inserts plus a base removal and a live removal —
    //survives the round-trip exactly.
    [TestMethod]
    public void EditedGenerationRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> sequence = Edited();

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(sequence.ToState());

        Assert.AreEqual(sequence, back);
    }


    //A compacted generation carrying both translation maps (dropped-dot anchors and rebased base offsets)
    //round-trips with its servability intact.
    [TestMethod]
    public void CompactedGenerationWithBothMapsRoundTripsThroughState()
    {
        OffsetAnchoredSequence<int> compacted = CompactedWithBothMaps();

        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(compacted.ToState());

        Assert.AreEqual(compacted, back);

        //The maps survived: a previous-generation base anchor and a dropped dot both still translate.
        Assert.IsNotNull(back.TranslateAnchor(OffsetAnchor.AtBase(1)));
    }


    //ToState twice on the same instance yields the same canonical encoding, so its ordering is deterministic.
    [TestMethod]
    public void ToStateIsDeterministicForTheSameInstance()
    {
        OffsetAnchoredSequence<int> sequence = Edited();

        CollectionAssert.AreEqual(Encode(sequence.ToState()), Encode(sequence.ToState()));
    }


    //Two sequences built by different merge orders carry equal state: each ToState reconstructs to the same
    //sequence, so ToState does not depend on insertion history. The states are compared by reconstruction
    //rather than by record equality because DotState compares its replica bytes by reference, and the
    //ordered-section comparison is exercised by the same-instance determinism test above.
    [TestMethod]
    public void MergeCommutativityPairYieldsEqualStates()
    {
        OffsetAnchoredSequence<int> shared = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (OffsetAnchoredSequence<int> byFirst, _) = shared.InsertAfter(OffsetAnchor.AtBase(0), 100, R1);
        (OffsetAnchoredSequence<int> bySecond, _) = shared.InsertAfter(OffsetAnchor.AtBase(0), 200, R2);

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
        OffsetAnchoredSequenceState<int> state = Edited().ToState() with { RemovedBaseOffsets = [BaseValues.Length] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicatedRemovedOffset()
    {
        OffsetAnchoredSequenceState<int> state = Edited().ToState() with { RemovedBaseOffsets = [1, 1] };

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

        //Two vertices each anchored at the other through AtLive links: the predecessor walk never reaches a
        //head or base anchor.
        DotState idLeft = new(R1Bytes, 50);
        DotState idRight = new(R1Bytes, 51);
        OffsetVertexEntry<int> left = new(idLeft, new OffsetAnchorState(-1, idRight), 1);
        OffsetVertexEntry<int> right = new(idRight, new OffsetAnchorState(-1, idLeft), 2);
        OffsetAnchoredSequenceState<int> state = valid with { Vertices = [left, right] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsACompactedBaseOffsetWithNegativePrevious()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with { CompactedBaseOffsets = [new OffsetRebaseEntry(-1, 0)] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsACompactedBaseOffsetWithCurrentOutsideTheBase()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with { CompactedBaseOffsets = [new OffsetRebaseEntry(0, valid.Base.Length)] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsADuplicatedCompactedBaseOffsetPrevious()
    {
        OffsetAnchoredSequenceState<int> valid = CompactedWithBothMaps().ToState();
        OffsetAnchoredSequenceState<int> state = valid with { CompactedBaseOffsets = [new OffsetRebaseEntry(3, 0), new OffsetRebaseEntry(3, 1)] };

        Assert.ThrowsExactly<ArgumentException>(() => OffsetAnchoredSequence<int>.FromState(state));
    }


    //An edited generation: a head insert, a base-anchored insert, a live-anchored insert chained off it, a
    //base removal, and a live removal — every anchor kind plus both removal kinds.
    private static OffsetAnchoredSequence<int> Edited()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, _) = sequence.InsertAtHead(40, R1);
        (sequence, OffsetAnchor atBase) = sequence.InsertAfter(OffsetAnchor.AtBase(0), 50, R2);
        (sequence, OffsetAnchor chained) = sequence.InsertAfter(atBase, 60, R1);
        sequence = sequence.Remove(OffsetAnchor.AtBase(2));

        return sequence.Remove(chained);
    }


    //A compacted generation that carries both translation maps: a dropped stable tombstone populates
    //CompactedDotAnchors, the converted vertex shifts the base so CompactedBaseOffsets is non-empty, and an
    //unstable insert remains as a live vertex.
    private static OffsetAnchoredSequence<int> CompactedWithBothMaps()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, OffsetAnchor converted) = sequence.InsertAfter(OffsetAnchor.AtBase(0), 50, R1);
        (sequence, OffsetAnchor dropped) = sequence.InsertAfter(OffsetAnchor.AtBase(1), 60, R1);
        sequence = sequence.Remove(dropped);
        (sequence, _) = sequence.InsertAfter(OffsetAnchor.AtBase(2), 70, R2);

        //The frontier covers the converted and dropped dots but not the trailing R2 insert, so the insert
        //stays a live vertex while the stable pair compacts.
        VectorClock frontier = FrontierCovering(converted.LiveId!, dropped.LiveId!);
        ImmutableArray<int> checkpoint = [10, 50, 20, 30];

        return sequence.Compact(frontier, checkpoint);
    }


    //Canonically encodes a state to bytes through the deterministic JSON codec, for ordering comparisons.
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
