using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class CrdtStateJsonTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void GCounterStateRoundTripsThroughJson()
    {
        GCounter counter = GCounter.Empty.Increment(R1, 3).Increment(R2, 2);

        GCounterState reloaded = RoundTrip(counter.ToState(), CrdtStateJson.CreateGCounterStateSerializer(), CrdtStateJson.CreateGCounterStateDeserializer());
        GCounter back = GCounter.FromState(reloaded);

        Assert.AreEqual(counter, back);
        Assert.AreEqual(5, back.Value);
    }


    [TestMethod]
    public void VectorClockStateRoundTripsThroughJson()
    {
        VectorClock clock = VectorClock.Empty.Increment(R1).Increment(R1).Increment(R2);

        VectorClockState reloaded = RoundTrip(clock.ToState(), CrdtStateJson.CreateVectorClockStateSerializer(), CrdtStateJson.CreateVectorClockStateDeserializer());
        VectorClock back = VectorClock.FromState(reloaded);

        Assert.AreEqual(clock, back);
        Assert.AreEqual(2, back[R1]);
        Assert.AreEqual(1, back[R2]);
    }


    [TestMethod]
    public void PNCounterStateRoundTripsThroughJson()
    {
        PNCounter counter = PNCounter.Empty.Increment(R1, 3).Decrement(R2, 5);

        PNCounterState reloaded = RoundTrip(counter.ToState(), CrdtStateJson.CreatePNCounterStateSerializer(), CrdtStateJson.CreatePNCounterStateDeserializer());
        PNCounter back = PNCounter.FromState(reloaded);

        Assert.AreEqual(counter, back);
        Assert.AreEqual(-2, back.Value);
    }


    [TestMethod]
    public void LwwRegisterStateRoundTripsThroughJson()
    {
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("alpha", new Timestamp(100), R1);

        LwwRegisterState<string> reloaded = RoundTrip(
            register.ToState(),
            CrdtStateJson.CreateLwwRegisterStateSerializer<string>(WriteString),
            CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString));
        LwwRegister<string> back = LwwRegister<string>.FromState(reloaded);

        Assert.AreEqual(register, back);
        Assert.AreEqual("alpha", back.Value);
    }


    [TestMethod]
    public void EmptyLwwRegisterStateRoundTripsThroughJson()
    {
        LwwRegisterState<string> reloaded = RoundTrip(
            LwwRegister<string>.Empty.ToState(),
            CrdtStateJson.CreateLwwRegisterStateSerializer<string>(WriteString),
            CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString));
        LwwRegister<string> back = LwwRegister<string>.FromState(reloaded);

        Assert.IsFalse(back.HasValue);
        Assert.AreEqual(LwwRegister<string>.Empty, back);
    }


    [TestMethod]
    public void DottedVersionVectorSetStateRoundTripsThroughJson()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "x").Add(R2, "y");

        DottedVersionVectorSetState<string> reloaded = RoundTrip(
            set.ToState(),
            CrdtStateJson.CreateDottedVersionVectorSetStateSerializer<string>(WriteString),
            CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString));
        DottedVersionVectorSet<string> back = DottedVersionVectorSet<string>.FromState(reloaded);

        Assert.AreEqual(set, back);
        Assert.HasCount(2, back.Values);
    }


    [TestMethod]
    public void OrSetStateRoundTripsThroughJson()
    {
        //The causal context must survive: a reloaded observed remove still loses to a concurrent add.
        OrSet<string> removed = OrSet<string>.Empty.Add("x", R1).Remove("x");

        OrSetState<string> reloaded = RoundTrip(
            removed.ToState(),
            CrdtStateJson.CreateOrSetStateSerializer<string>(WriteString),
            CrdtStateJson.CreateOrSetStateDeserializer(ReadString));
        OrSet<string> back = OrSet<string>.FromState(reloaded);

        Assert.IsFalse(back.Contains("x"));
        Assert.IsTrue(back.Merge(OrSet<string>.Empty.Add("x", R2)).Contains("x"));
    }


    [TestMethod]
    public void MvRegisterStateRoundTripsThroughJson()
    {
        MvRegister<string> concurrent = MvRegister<string>.Empty.Write("x", R1).Merge(MvRegister<string>.Empty.Write("y", R2));

        MvRegisterState<string> reloaded = RoundTrip(
            concurrent.ToState(),
            CrdtStateJson.CreateMvRegisterStateSerializer<string>(WriteString),
            CrdtStateJson.CreateMvRegisterStateDeserializer(ReadString));
        MvRegister<string> back = MvRegister<string>.FromState(reloaded);

        Assert.AreEqual(concurrent, back);
        Assert.HasCount(2, back.Values);
    }


    [TestMethod]
    public void RgaStateRoundTripsThroughJson()
    {
        (Rga<string> array, Dot first) = Rga<string>.Empty.InsertAtHead("a", R1);
        (array, Dot second) = array.InsertAfter(first, "b", R1);
        (array, _) = array.InsertAfter(second, "c", R1);
        array = array.Remove(second, R1);

        //Combine the dotted tombstone the remove minted with a legacy (empty remove-dots) orphan tombstone,
        //so the round-trip exercises both tombstone-entry shapes.
        RgaState<string> dotted = array.ToState();
        RgaTombstoneEntry legacy = new(new DotState(Bytes(R2), 5), []);
        RgaState<string> withBoth = new(dotted.Context, dotted.Vertices, [.. dotted.Tombstones, legacy]);
        Rga<string> source = Rga<string>.FromState(withBoth);

        RgaState<string> reloaded = RoundTrip(
            source.ToState(),
            CrdtStateJson.CreateRgaStateSerializer<string>(WriteString),
            CrdtStateJson.CreateRgaStateDeserializer(ReadString));
        Rga<string> back = Rga<string>.FromState(reloaded);

        Assert.AreEqual(source, back);
        string[] expected = ["a", "c"];
        CollectionAssert.AreEqual(expected, back.Values.ToArray());
    }


    [TestMethod]
    public void DeserializerRejectsNegativeReplicaCounter()
    {
        //A hostile peer must not be able to inject negative entries: max-merge would then silently
        //prefer stale values and counter monotonicity would be corrupted.
        string json = $$"""{"entries":[{"replica":"{{HexReplica(1)}}","count":-1}]}""";

        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize(json, CrdtStateJson.CreateVectorClockStateDeserializer()));
    }


    [TestMethod]
    public void DeserializerRejectsWrongLengthReplicaId()
    {
        string json = """{"entries":[{"replica":"0102","count":1}]}""";

        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize(json, CrdtStateJson.CreateVectorClockStateDeserializer()));
    }


    [TestMethod]
    public void DeserializerRejectsNonHexReplicaId()
    {
        string json = """{"entries":[{"replica":"not-hex","count":1}]}""";

        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize(json, CrdtStateJson.CreateVectorClockStateDeserializer()));
    }


    [TestMethod]
    public void DeserializerRejectsNonPositiveDotCounter()
    {
        //Dots are minted starting at one; a zero or negative counter is not a value any replica produces.
        string json = $$"""{"context":{"entries":[]},"entries":[{"replica":"{{HexReplica(1)}}","counter":0,"value":"x"}]}""";

        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize(json, CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));
    }


    [TestMethod]
    public void StateCodecFactoriesRejectNullValueDelegates()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateLwwRegisterStateSerializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateLwwRegisterStateDeserializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateDottedVersionVectorSetStateSerializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateOrSetStateSerializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateOrSetStateDeserializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateMvRegisterStateSerializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateMvRegisterStateDeserializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateRgaStateSerializer<string>(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => CrdtStateJson.CreateRgaStateDeserializer<string>(null!));
    }


    [TestMethod]
    public void MissingFieldsInCounterAndRegisterCodecsFailClosed()
    {
        //A required field absent from an otherwise well-formed object must fail closed as MessageDeserializationException, not
        //surface the framework's KeyNotFoundException from a raw property accessor.
        string replica = HexReplica(1);

        //ReadReplicaCounterEntries, the shared reader behind the vector clock and the G-counter: the entries
        //array, then a field of an entry (the count is read before the replica).
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{}""", CrdtStateJson.CreateVectorClockStateDeserializer()));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"entries":[{"replica":"{{{replica}}}"}]}""", CrdtStateJson.CreateVectorClockStateDeserializer()));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"entries":[{"count":1}]}""", CrdtStateJson.CreateVectorClockStateDeserializer()));

        //The PN-counter's two nested counters.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"decrements":{"entries":[]}}""", CrdtStateJson.CreatePNCounterStateDeserializer()));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"increments":{"entries":[]}}""", CrdtStateJson.CreatePNCounterStateDeserializer()));

        //Each of the LWW register's four fields.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"hasValue":false,"utcTicks":0,"writer":null}""", CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"hasValue":false,"value":null,"utcTicks":0}""", CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"value":null,"utcTicks":0,"writer":null}""", CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"hasValue":false,"value":null,"writer":null}""", CrdtStateJson.CreateLwwRegisterStateDeserializer(ReadString)));
    }


    [TestMethod]
    public void MissingFieldsInDottedSetCodecsFailClosed()
    {
        string replica = HexReplica(1);

        //ReadDottedVersionVectorSetState and the dotted entry it reads, exercising ReadDotState and
        //ReadDotCounter: the entries array, the context, then each field of an entry.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]}}""", CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"entries":[]}""", CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$"""{"context":{"entries":[]},"entries":[{"counter":1,"value":"x"}]}""", CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"entries":[{"replica":"{{{replica}}}","value":"x"}]}""", CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"entries":[{"replica":"{{{replica}}}","counter":1}]}""", CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));

        //The OR-set and MV-register wrappers each require their single nested object.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{}""", CrdtStateJson.CreateOrSetStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{}""", CrdtStateJson.CreateMvRegisterStateDeserializer(ReadString)));
    }


    [TestMethod]
    public void MissingFieldsInSequenceCodecsFailClosed()
    {
        string replica = HexReplica(1);

        //The RGA's three sections, then each field of a vertex (the predecessor is read first).
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"tombstones":[]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"vertices":[]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"vertices":[],"tombstones":[]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"vertices":[{"id":{"replica":"{{{replica}}}","counter":1},"value":"x"}],"tombstones":[]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"vertices":[{"predecessor":null,"value":"x"}],"tombstones":[]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"vertices":[{"predecessor":null,"id":{"replica":"{{{replica}}}","counter":1}}],"tombstones":[]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));

        //An RGA tombstone entry's two fields, then its remove-dot's counter bound.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"vertices":[],"tombstones":[{"removeDots":[]}]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"vertices":[],"tombstones":[{"target":{"replica":"{{{replica}}}","counter":1}}]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"vertices":[],"tombstones":[{"target":{"replica":"{{{replica}}}","counter":1},"removeDots":[{"replica":"{{{replica}}}","counter":0}]}]}""", CrdtStateJson.CreateRgaStateDeserializer(ReadString)));

        //The run-length RGA's six v2 sections, then a field within a run, a two-range span, an irregular
        //tombstone, a translation, and a translation span.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"runs":[],"tombstoneSpans":[],"irregularTombstones":[],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"tombstoneSpans":[],"irregularTombstones":[],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"runs":[],"irregularTombstones":[],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[],"irregularTombstones":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[],"irregularTombstones":[],"translations":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"context":{"entries":[]},"runs":[{"predecessor":null}],"tombstoneSpans":[],"irregularTombstones":[],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[{"targetReplica":"{{{replica}}}","targetFrom":1,"targetTo":1,"removeFrom":1}],"irregularTombstones":[],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[],"irregularTombstones":[{"target":{"replica":"{{{replica}}}","counter":1}}],"translations":[],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[],"irregularTombstones":[],"translations":[{"target":{"replica":"{{{replica}}}","counter":1}}],"translationSpans":[]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"context":{"entries":[]},"runs":[],"tombstoneSpans":[],"irregularTombstones":[],"translations":[],"translationSpans":[{"replica":"{{{replica}}}","from":1,"to":2}]}""", CrdtStateJson.CreateRgaRunStateDeserializer(ReadString)));

        //The offset-anchored sequence's eight sections, then a vertex's anchor and the anchor's own fields.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"vertices":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"base":[],"removedBaseOffsets":[],"vertices":[{"id":{"replica":"{{{replica}}}","counter":1},"value":"x"}],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"base":[],"removedBaseOffsets":[],"vertices":[{"id":{"replica":"{{{replica}}}","counter":1},"anchor":{"liveId":null},"value":"x"}],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"base":[],"removedBaseOffsets":[],"vertices":[{"id":{"replica":"{{{replica}}}","counter":1},"anchor":{"baseOffset":-1},"value":"x"}],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));

        //An offset tombstone entry's two fields, an offset base-removal entry's two fields, and the
        //anchor-typed compacted-base-offset entry's two fields.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[],"vertices":[],"tombstones":[{"removeDots":[]}],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"base":[],"removedBaseOffsets":[],"vertices":[],"tombstones":[{"target":{"replica":"{{{replica}}}","counter":1}}],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":[],"removedBaseOffsets":[{"removeDots":[]}],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":["x"],"removedBaseOffsets":[{"offset":0}],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":["x"],"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[{"target":{"baseOffset":0,"liveId":null}}],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"base":["x"],"removedBaseOffsets":[],"vertices":[],"tombstones":[],"compactedDotAnchors":[],"compactedBaseOffsets":[{"previous":0}],"context":{"entries":[]},"baseFrontier":{"entries":[]}}""", CrdtStateJson.CreateOffsetAnchoredSequenceStateDeserializer(ReadString)));
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


    private static string HexReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return Convert.ToHexStringLower(buffer);
    }


    private static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadString(JsonElement element) => element.GetString()!;


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
