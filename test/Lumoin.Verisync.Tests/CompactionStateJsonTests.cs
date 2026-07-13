using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips both compaction-strategy JSON codecs — the offset-anchored sequence state and the RGA
/// run-length state — for uncompacted, compacted, and pending-removed instances plus the reclaimed wire
/// shape a future consensus-carried follow-on emits, and asserts the codecs fail closed with
/// <see cref="MessageDeserializationException"/> on hand-authored payloads that mutate one field of the
/// wire format at a time. The codec validates shape only; model-level invariants (dot pools, coverage,
/// W-shapes) surface at <c>FromState</c> and are covered there. The offset wire shape carries a required
/// <c>baseGeneration</c> beside <c>baseFrontier</c>.
/// </summary>
[TestClass]
internal sealed class CompactionStateJsonTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);
    private static ReplicaId R3 { get; } = Replica(3);

    private static ImmutableArray<int> BaseValues { get; } = [10, 20, 30];


    //An uncompacted offset sequence with every anchor kind and dotted removes on both axes round-trips
    //through the JSON codec and FromState.
    [TestMethod]
    public void UncompactedOffsetSequenceRoundTripsThroughTheJsonCodec()
    {
        OffsetAnchoredSequence<int> sequence = Edited();

        OffsetAnchoredSequenceState<int> reloaded = RoundTrip(
            sequence.ToState(),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadInt));
        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(reloaded);

        Assert.AreEqual(sequence, back);
    }


    //A compacted offset sequence carrying both translation maps and the stamped generation identity
    //round-trips through the JSON codec with its servability intact, and the base generation survives the
    //round-trip.
    [TestMethod]
    public void CompactedOffsetSequenceRoundTripsThroughTheJsonCodec()
    {
        OffsetAnchoredSequence<int> compacted = CompactedWithBothMaps();

        OffsetAnchoredSequenceState<int> reloaded = RoundTrip(
            compacted.ToState(),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadInt));
        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(reloaded);

        Assert.AreEqual(compacted, back);
        Assert.AreEqual(1, reloaded.BaseGeneration);
        Assert.IsNotNull(back.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtBase(1), 0)));
    }


    //A pending-removed generation — a base-removal entry carrying remove-dots plus a non-empty
    //baseFrontier — round-trips and stays hidden.
    [TestMethod]
    public void APendingRemovedOffsetSequenceRoundTripsThroughTheJsonCodec()
    {
        OffsetAnchoredSequence<int> pending = PendingRemoved();

        OffsetAnchoredSequenceState<int> reloaded = RoundTrip(
            pending.ToState(),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadInt));
        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(reloaded);

        Assert.AreEqual(pending, back);
        CollectionAssert.AreEqual(BaseValues.ToArray(), back.Values.ToArray());
    }


    //A reclaimed generation — a dropped base offset served by the Head gap anchor through the
    //anchor-typed base-offset translation map — is a future consensus-carried follow-on's output, not
    //Compact's, which defers reclamation; the shape stays legal on the wire, so it is hand-built through
    //FromState and must round-trip with its servability intact. Its prior-generation offset-0 address maps
    //to the head.
    [TestMethod]
    public void AReclaimedOffsetSequenceRoundTripsThroughTheJsonCodec()
    {
        OffsetAnchoredSequence<int> reclaimed = OffsetAnchoredSequence<int>.FromState(ReclaimedShape());

        OffsetAnchoredSequenceState<int> reloaded = RoundTrip(
            reclaimed.ToState(),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadInt));
        OffsetAnchoredSequence<int> back = OffsetAnchoredSequence<int>.FromState(reloaded);

        Assert.AreEqual(reclaimed, back);
        Assert.HasCount(0, back.Values);
        Assert.AreEqual(new OffsetAddress(OffsetAnchor.Head, 0), back.TranslateAnchor(new OffsetAddress(OffsetAnchor.AtBase(0), 0)));
    }


    //A dotted-remove RGA round-trips through the run-state JSON codec and FromRunState, exercising a two-range
    //tombstone span (R2 removing a contiguous R1 range in one pass).
    [TestMethod]
    public void DottedRgaRunStateRoundTripsThroughTheJsonCodec()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        (Rga<int> withD, _) = withC.InsertAfter(idC, 4, R1);
        Rga<int> array = withD.Remove(idB, R2).Remove(idC, R2);

        RgaRunState<int> reloaded = RoundTrip(
            array.ToRunState(),
            CrdtStateJson.CreateRgaRunStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateRgaRunStateDeserializer(ReadInt));
        Rga<int> back = Rga<int>.FromRunState(reloaded);

        Assert.AreEqual(array, back);
    }


    //A two-dot concurrent-remove tombstone serializes as an irregular entry and round-trips through the JSON
    //codec.
    [TestMethod]
    public void IrregularRgaRunStateRoundTripsThroughTheJsonCodec()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        Rga<int> array = withA.Remove(idA, R2).Merge(withA.Remove(idA, R3));

        RgaRunState<int> reloaded = RoundTrip(
            array.ToRunState(),
            CrdtStateJson.CreateRgaRunStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateRgaRunStateDeserializer(ReadInt));
        Rga<int> back = Rga<int>.FromRunState(reloaded);

        Assert.AreEqual(array, back);
    }


    //A compacted RGA carrying a translation SPAN (two contiguous dropped dots served by one retained ancestor)
    //round-trips through the run-state JSON codec with its servability intact — the certified compaction is
    //now built through Compact rather than a hand-authored record.
    [TestMethod]
    public void CompactedRgaRunStateRoundTripsThroughTheJsonCodec()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        (Rga<int> withC, Dot idC) = withB.InsertAfter(idB, 3, R1);
        Rga<int> removed = withC.Remove(idB, R2).Remove(idC, R2);

        VectorClock frontier = removed.CausalContext;
        ImmutableArray<SequenceCheckpointEntry<int>> checkpoint = removed.CertifiedProjection(frontier);
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        RgaRunState<int> reloaded = RoundTrip(
            compacted.ToRunState(),
            CrdtStateJson.CreateRgaRunStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateRgaRunStateDeserializer(ReadInt));
        Rga<int> back = Rga<int>.FromRunState(reloaded);

        Assert.AreEqual(compacted, back);
        Assert.AreEqual(idA, back.TranslateAnchor(idB));
        Assert.AreEqual(idA, back.TranslateAnchor(idC));
    }


    [TestMethod]
    public void OffsetCodecFactoriesRejectNullValueDelegates()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateOffsetAnchoredSequenceStateSerializer<int>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer<int>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateRgaRunStateSerializer<int>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateRgaRunStateDeserializer<int>(null!));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsNonHexReplicaId()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"not-hex","counter":1},"anchor":{"baseOffset":0,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsWrongLengthReplicaId()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"0102","counter":1},"anchor":{"baseOffset":0,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsZeroDotCounter()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"{{Hex(R1)}}","counter":0},"anchor":{"baseOffset":0,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsALiveAnchorWithABaseOffset()
    {
        //A non-null liveId paired with a baseOffset other than -1 is a shape no honest anchor takes.
        string json = OffsetStateJson($$$"""{"id":{"replica":"{{{Hex(R1)}}}","counter":1},"anchor":{"baseOffset":0,"liveId":{"replica":"{{{Hex(R2)}}}","counter":1}},"value":99}""");

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsABaseOffsetBelowMinusOne()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"{{Hex(R1)}}","counter":1},"anchor":{"baseOffset":-2,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsANegativeRemovedOffset()
    {
        string json = """
        {
          "base":[10],
          "baseFrontier":{"entries":[]},
          "baseGeneration":0,
          "removedBaseOffsets":[{"offset":-1,"removeDots":[]}],
          "context":{"entries":[]},
          "vertices":[],
          "tombstones":[],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    //The generation ordinal is a required field beside the frontier: a payload omitting it fails closed on
    //the wire, distinct from an explicitly genesis zero.
    [TestMethod]
    public void OffsetDeserializerRejectsAMissingBaseGeneration()
    {
        string json = """
        {
          "base":[10],
          "baseFrontier":{"entries":[]},
          "removedBaseOffsets":[],
          "context":{"entries":[]},
          "vertices":[],
          "tombstones":[],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsAZeroTombstoneRemoveDotCounter()
    {
        //The triple-dollar form allows the nested JSON objects' consecutive closing braces as content.
        string json = $$$"""
        {
          "base":[10],
          "baseFrontier":{"entries":[]},
          "baseGeneration":0,
          "removedBaseOffsets":[],
          "context":{"entries":[]},
          "vertices":[],
          "tombstones":[{"target":{"replica":"{{{Hex(R1)}}}","counter":1},"removeDots":[{"replica":"{{{Hex(R2)}}}","counter":0}]}],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsAZeroBaseRemovalRemoveDotCounter()
    {
        string json = $$$"""
        {
          "base":[10],
          "baseFrontier":{"entries":[]},
          "baseGeneration":0,
          "removedBaseOffsets":[{"offset":0,"removeDots":[{"replica":"{{{Hex(R2)}}}","counter":0}]}],
          "context":{"entries":[]},
          "vertices":[],
          "tombstones":[],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsAMalformedCompactedBaseAnchorTarget()
    {
        //The anchor-typed base-offset translation target obeys the one-canonical-shape rule too. The
        //quadruple-dollar form allows the triply-nested JSON object's closing braces as content.
        string json = $$$$"""
        {
          "base":[10],
          "baseFrontier":{"entries":[]},
          "baseGeneration":0,
          "removedBaseOffsets":[],
          "context":{"entries":[]},
          "vertices":[],
          "tombstones":[],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[{"previous":0,"target":{"baseOffset":0,"liveId":{"replica":"{{{{Hex(R1)}}}}","counter":1}}}]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsAnInvalidTombstoneSpan()
    {
        //A two-range span whose "targetTo" is below its "targetFrom" is invalid bounds.
        string json = $$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{Hex(R1)}}","counter":1},"predecessor":null,"values":[1]}],
          "tombstoneSpans":[{"targetReplica":"{{Hex(R1)}}","targetFrom":3,"targetTo":2,"removeReplica":"{{Hex(R2)}}","removeFrom":1}],
          "irregularTombstones":[],
          "translations":[],
          "translationSpans":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsASpanFromBelowOne()
    {
        string json = $$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{Hex(R1)}}","counter":1},"predecessor":null,"values":[1]}],
          "tombstoneSpans":[{"targetReplica":"{{Hex(R1)}}","targetFrom":0,"targetTo":1,"removeReplica":"{{Hex(R2)}}","removeFrom":1}],
          "irregularTombstones":[],
          "translations":[],
          "translationSpans":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsEmptyRunValues()
    {
        string json = $$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{Hex(R1)}}","counter":1},"predecessor":null,"values":[]}],
          "tombstoneSpans":[],
          "irregularTombstones":[],
          "translations":[],
          "translationSpans":[]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsAnInvalidTranslationSpan()
    {
        //A translation span whose "to" is below its "from" is invalid bounds. The triple-dollar form
        //allows the nested JSON object's consecutive closing braces as content.
        string json = $$$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{{Hex(R1)}}}","counter":1},"predecessor":null,"values":[1]}],
          "tombstoneSpans":[],
          "irregularTombstones":[],
          "translations":[],
          "translationSpans":[{"replica":"{{{Hex(R1)}}}","from":3,"to":2,"target":{"replica":"{{{Hex(R1)}}}","counter":1}}]
        }
        """;

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsMissingV2Fields()
    {
        //A tombstone span missing its removeReplica.
        string missingRemoveReplica = $$"""
        {
          "context":{"entries":[]},
          "runs":[],
          "tombstoneSpans":[{"targetReplica":"{{Hex(R1)}}","targetFrom":1,"targetTo":1,"removeFrom":1}],
          "irregularTombstones":[],
          "translations":[],
          "translationSpans":[]
        }
        """;
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(missingRemoveReplica));

        //An irregular tombstone missing its removeDots. The triple-dollar form allows the nested JSON
        //object's consecutive closing braces as content, matching the offset arm's precedent above.
        string missingRemoveDots = $$$"""
        {
          "context":{"entries":[]},
          "runs":[],
          "tombstoneSpans":[],
          "irregularTombstones":[{"target":{"replica":"{{{Hex(R1)}}}","counter":1}}],
          "translations":[],
          "translationSpans":[]
        }
        """;
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(missingRemoveDots));

        //A translation span missing its target.
        string missingTranslationSpanTarget = $$"""
        {
          "context":{"entries":[]},
          "runs":[],
          "tombstoneSpans":[],
          "irregularTombstones":[],
          "translations":[],
          "translationSpans":[{"replica":"{{Hex(R1)}}","from":1,"to":2}]
        }
        """;
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeRunState(missingTranslationSpanTarget));
    }


    //Wraps a single vertex object into an otherwise-minimal valid offset-state document over a
    //one-element base.
    private static string OffsetStateJson(string vertex)
    {
        return $$"""
        {
          "base":[10],
          "baseFrontier":{"entries":[]},
          "baseGeneration":0,
          "removedBaseOffsets":[],
          "context":{"entries":[]},
          "vertices":[{{vertex}}],
          "tombstones":[],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[]
        }
        """;
    }


    private static OffsetAnchoredSequenceState<int> DeserializeOffset(string json)
    {
        return Deserialize(json, CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadInt));
    }


    private static RgaRunState<int> DeserializeRunState(string json)
    {
        return Deserialize(json, CrdtStateJson.CreateRgaRunStateDeserializer(ReadInt));
    }


    //An edited generation: every anchor kind plus a dotted base removal and a dotted live removal.
    private static OffsetAnchoredSequence<int> Edited()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, _) = sequence.InsertAtHead(40, R1);
        (sequence, OffsetAddress atBase) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R2);
        (sequence, OffsetAddress chained) = sequence.InsertAfter(atBase, 60, R1);
        sequence = sequence.Remove(new OffsetAddress(OffsetAnchor.AtBase(2), 0), R1);

        return sequence.Remove(chained, R2);
    }


    //A compacted offset generation carrying both translation maps and the stamped identity: a converted
    //vertex that shifts the base (populating the base-offset map) and a dropped certified tombstone
    //(populating the dot map). The frontier is insert-quiescent — it certifies both inserts and the
    //remove-dot — as §17 requires of any compaction. The compaction is base-changing, so the generation
    //is generation 1.
    private static OffsetAnchoredSequence<int> CompactedWithBothMaps()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, _) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R1);
        (sequence, OffsetAddress dropped) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(1), 0), 60, R1);
        sequence = sequence.Remove(dropped, R2);

        VectorClock frontier = sequence.CausalContext;

        return sequence.Compact(frontier, sequence.CertifiedProjection(frontier));
    }


    //A pending-removed generation: an uncertified-removed stable vertex converted into the base, hidden,
    //its remove-dot keyed to the new offset.
    private static OffsetAnchoredSequence<int> PendingRemoved()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, OffsetAddress x) = sequence.InsertAfter(new OffsetAddress(OffsetAnchor.AtBase(0), 0), 50, R1);
        sequence = sequence.Remove(x, R2);

        VectorClock frontier = FrontierCovering(x.Anchor.LiveId!);

        return sequence.Compact(frontier, sequence.CertifiedProjection(frontier));
    }


    //The reclaimed wire shape, hand-built: an empty base whose previous generation's only slot was
    //dropped, its offset served by the Head gap anchor, the generation identity stamped by the frontier
    //that certified the removal — a single base change, so generation 1.
    private static OffsetAnchoredSequenceState<int> ReclaimedShape()
    {
        VectorClockState identity = VectorClock.Empty.Increment(R1).ToState();

        return new OffsetAnchoredSequenceState<int>(
            [],
            identity,
            1,
            [],
            identity,
            [],
            [],
            [],
            [new OffsetBaseAnchorEntry(0, new OffsetAnchorState(-1, null))]);
    }


    private static TState RoundTrip<TState>(TState state, SerializeMessageDelegate<TState> serialize, DeserializeMessageDelegate<TState> deserialize)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serialize(state, buffer);

        return deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static TMessage Deserialize<TMessage>(string json, DeserializeMessageDelegate<TMessage> deserializer)
    {
        return deserializer(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static string Hex(ReplicaId replica) => Convert.ToHexStringLower(replica.AsSpan());


    private static void WriteInt(Utf8JsonWriter writer, int value) => writer.WriteNumberValue(value);


    private static int ReadInt(JsonElement element) => element.GetInt32();


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
