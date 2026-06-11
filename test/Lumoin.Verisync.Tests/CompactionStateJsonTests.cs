using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips both compaction-strategy JSON codecs — the offset-anchored sequence state and the RGA
/// run-length state — for uncompacted and compacted instances, and asserts the codecs fail closed with
/// <see cref="JsonException"/> on hand-authored payloads that mutate one field of the wire format at a time.
/// </summary>
[TestClass]
internal sealed class CompactionStateJsonTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);

    private static ImmutableArray<int> BaseValues { get; } = [10, 20, 30];


    //An uncompacted offset sequence with every anchor kind round-trips through the JSON codec and FromState.
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


    //A compacted offset sequence carrying both translation maps round-trips through the JSON codec with its
    //servability intact.
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
        Assert.IsNotNull(back.TranslateAnchor(OffsetAnchor.AtBase(1)));
    }


    //An uncompacted RGA round-trips through the run-state JSON codec and FromRunState.
    [TestMethod]
    public void UncompactedRgaRunStateRoundTripsThroughTheJsonCodec()
    {
        (Rga<int> array, Dot first) = Rga<int>.Empty.InsertAtHead(1, R1);
        (array, Dot second) = array.InsertAfter(first, 2, R1);
        (array, _) = array.InsertAfter(second, 3, R1);
        array = array.Remove(second);

        RgaRunState<int> reloaded = RoundTrip(
            array.ToRunState(),
            CrdtStateJson.CreateRgaRunStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateRgaRunStateDeserializer(ReadInt));
        Rga<int> back = Rga<int>.FromRunState(reloaded);

        Assert.AreEqual(array, back);
    }


    //A compacted RGA carrying a translation map round-trips through the run-state JSON codec.
    [TestMethod]
    public void CompactedRgaRunStateRoundTripsThroughTheJsonCodec()
    {
        (Rga<int> withA, Dot idA) = Rga<int>.Empty.InsertAtHead(1, R1);
        (Rga<int> withB, Dot idB) = withA.InsertAfter(idA, 2, R1);
        Rga<int> removed = withB.Remove(idB);

        VectorClock frontier = FrontierCovering(idA, idB);
        ImmutableArray<int> checkpoint = [1];
        Rga<int> compacted = removed.Compact(frontier, checkpoint);

        RgaRunState<int> reloaded = RoundTrip(
            compacted.ToRunState(),
            CrdtStateJson.CreateRgaRunStateSerializer<int>(WriteInt),
            CrdtStateJson.CreateRgaRunStateDeserializer(ReadInt));
        Rga<int> back = Rga<int>.FromRunState(reloaded);

        Assert.AreEqual(compacted, back);
        Assert.AreEqual(idA, back.TranslateAnchor(idB));
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

        Assert.ThrowsExactly<JsonException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsWrongLengthReplicaId()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"0102","counter":1},"anchor":{"baseOffset":0,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<JsonException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsZeroDotCounter()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"{{Hex(R1)}}","counter":0},"anchor":{"baseOffset":0,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<JsonException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsALiveAnchorWithABaseOffset()
    {
        //A non-null liveId paired with a baseOffset other than -1 is a shape no honest anchor takes.
        string json = OffsetStateJson($$$"""{"id":{"replica":"{{{Hex(R1)}}}","counter":1},"anchor":{"baseOffset":0,"liveId":{"replica":"{{{Hex(R2)}}}","counter":1}},"value":99}""");

        Assert.ThrowsExactly<JsonException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsABaseOffsetBelowMinusOne()
    {
        string json = OffsetStateJson($$"""{"id":{"replica":"{{Hex(R1)}}","counter":1},"anchor":{"baseOffset":-2,"liveId":null},"value":99}""");

        Assert.ThrowsExactly<JsonException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void OffsetDeserializerRejectsANegativeRemovedOffset()
    {
        string json = $$"""
        {
          "base":[10],
          "removedBaseOffsets":[-1],
          "context":{"entries":[]},
          "vertices":[],
          "tombstones":[],
          "compactedDotAnchors":[],
          "compactedBaseOffsets":[]
        }
        """;

        Assert.ThrowsExactly<JsonException>(() => DeserializeOffset(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsAnInvalidTombstoneSpan()
    {
        //A span whose "to" is below its "from" is invalid bounds.
        string json = $$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{Hex(R1)}}","counter":1},"predecessor":null,"values":[1]}],
          "tombstoneSpans":[{"replica":"{{Hex(R1)}}","from":3,"to":2}],
          "translations":[]
        }
        """;

        Assert.ThrowsExactly<JsonException>(() => DeserializeRunState(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsASpanFromBelowOne()
    {
        string json = $$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{Hex(R1)}}","counter":1},"predecessor":null,"values":[1]}],
          "tombstoneSpans":[{"replica":"{{Hex(R1)}}","from":0,"to":1}],
          "translations":[]
        }
        """;

        Assert.ThrowsExactly<JsonException>(() => DeserializeRunState(json));
    }


    [TestMethod]
    public void RunStateDeserializerRejectsEmptyRunValues()
    {
        string json = $$"""
        {
          "context":{"entries":[]},
          "runs":[{"first":{"replica":"{{Hex(R1)}}","counter":1},"predecessor":null,"values":[]}],
          "tombstoneSpans":[],
          "translations":[]
        }
        """;

        Assert.ThrowsExactly<JsonException>(() => DeserializeRunState(json));
    }


    //Wraps a single vertex object into an otherwise-minimal valid offset-state document over a one-element base.
    private static string OffsetStateJson(string vertex)
    {
        return $$"""
        {
          "base":[10],
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


    private static OffsetAnchoredSequence<int> Edited()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, _) = sequence.InsertAtHead(40, R1);
        (sequence, OffsetAnchor atBase) = sequence.InsertAfter(OffsetAnchor.AtBase(0), 50, R2);
        (sequence, OffsetAnchor chained) = sequence.InsertAfter(atBase, 60, R1);
        sequence = sequence.Remove(OffsetAnchor.AtBase(2));

        return sequence.Remove(chained);
    }


    //A compacted offset generation carrying both translation maps: a dropped stable tombstone, a converted
    //vertex that shifts the base, and an unstable insert that stays live.
    private static OffsetAnchoredSequence<int> CompactedWithBothMaps()
    {
        OffsetAnchoredSequence<int> sequence = OffsetAnchoredSequence<int>.WithBase(BaseValues);
        (sequence, OffsetAnchor converted) = sequence.InsertAfter(OffsetAnchor.AtBase(0), 50, R1);
        (sequence, OffsetAnchor dropped) = sequence.InsertAfter(OffsetAnchor.AtBase(1), 60, R1);
        sequence = sequence.Remove(dropped);
        (sequence, _) = sequence.InsertAfter(OffsetAnchor.AtBase(2), 70, R2);

        VectorClock frontier = FrontierCovering(converted.LiveId!, dropped.LiveId!);
        ImmutableArray<int> checkpoint = [10, 50, 20, 30];

        return sequence.Compact(frontier, checkpoint);
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
