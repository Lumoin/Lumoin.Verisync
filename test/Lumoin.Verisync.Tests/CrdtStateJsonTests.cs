using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
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
        array = array.Remove(second);

        RgaState<string> reloaded = RoundTrip(
            array.ToState(),
            CrdtStateJson.CreateRgaStateSerializer<string>(WriteString),
            CrdtStateJson.CreateRgaStateDeserializer(ReadString));
        Rga<string> back = Rga<string>.FromState(reloaded);

        Assert.AreEqual(array, back);
        string[] expected = ["a", "c"];
        CollectionAssert.AreEqual(expected, back.Values.ToArray());
    }


    [TestMethod]
    public void DeserializerRejectsNegativeReplicaCounter()
    {
        //A hostile peer must not be able to inject negative entries: max-merge would then silently
        //prefer stale values and counter monotonicity would be corrupted.
        string json = $$"""{"entries":[{"replica":"{{HexReplica(1)}}","count":-1}]}""";

        Assert.ThrowsExactly<JsonException>(() => Deserialize(json, CrdtStateJson.CreateVectorClockStateDeserializer()));
    }


    [TestMethod]
    public void DeserializerRejectsWrongLengthReplicaId()
    {
        string json = """{"entries":[{"replica":"0102","count":1}]}""";

        Assert.ThrowsExactly<JsonException>(() => Deserialize(json, CrdtStateJson.CreateVectorClockStateDeserializer()));
    }


    [TestMethod]
    public void DeserializerRejectsNonHexReplicaId()
    {
        string json = """{"entries":[{"replica":"not-hex","count":1}]}""";

        Assert.ThrowsExactly<JsonException>(() => Deserialize(json, CrdtStateJson.CreateVectorClockStateDeserializer()));
    }


    [TestMethod]
    public void DeserializerRejectsNonPositiveDotCounter()
    {
        //Dots are minted starting at one; a zero or negative counter is not a value any replica produces.
        string json = $$"""{"context":{"entries":[]},"entries":[{"replica":"{{HexReplica(1)}}","counter":0,"value":"x"}]}""";

        Assert.ThrowsExactly<JsonException>(() => Deserialize(json, CrdtStateJson.CreateDottedVersionVectorSetStateDeserializer(ReadString)));
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


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
