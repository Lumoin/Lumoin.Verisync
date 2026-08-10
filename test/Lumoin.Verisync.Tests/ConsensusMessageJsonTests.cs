using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips and hostile-input checks for the consensus DTO JSON codecs and for the acceptor's durable
/// state, which is not a message but shares the codecs' validation split. The prepare kinds matter
/// here: the socket cluster tests only ever exercise accept and accept-reply on the wire.
/// </summary>
[TestClass]
internal sealed class ConsensusMessageJsonTests
{
    private static ReplicaId R1 { get; } = Replica(1);


    [TestMethod]
    public void PrepareRequestRoundTrips()
    {
        var request = new PrepareRequest<string>(FastBallot.Classic(3, R1));

        ConsensusRequest<string> back = RoundTripRequest(request);

        Assert.AreEqual(request, back);
    }


    [TestMethod]
    public void AcceptRequestRoundTrips()
    {
        var request = new AcceptRequest<string>(FastBallot.Fast(1), "value with spaces");

        ConsensusRequest<string> back = RoundTripRequest(request);

        Assert.AreEqual(request, back);
    }


    [TestMethod]
    public void AcceptRequestWithNextBallotRoundTrips()
    {
        var request = new AcceptRequest<string>(FastBallot.Fast(1), "value", FastBallot.Fast(2));

        ConsensusRequest<string> back = RoundTripRequest(request);

        Assert.AreEqual(request, back);
    }


    [TestMethod]
    public void AcceptRequestWithoutNextDeserializesAsNull()
    {
        //A wire payload from before the next field existed must still decode, with no piggyback.
        string json = """{"kind":"accept","ballot":{"round":1,"proposer":null},"value":"v"}""";

        ConsensusRequest<string> back = Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString));

        var accept = (AcceptRequest<string>)back;
        Assert.IsNull(accept.Next);
        Assert.AreEqual(FastBallot.Fast(1), accept.Ballot);
        Assert.AreEqual("v", accept.Value);
    }


    [TestMethod]
    public void PrepareReplyWithAcceptedValueRoundTrips()
    {
        var reply = new PrepareReply<string>(true, FastBallot.Fast(1), "recovered", FastBallot.Zero);

        ConsensusReply<string> back = RoundTripReply(reply);

        Assert.AreEqual(reply, back);
    }


    [TestMethod]
    public void RejectedPrepareReplyWithoutValueRoundTrips()
    {
        var reply = new PrepareReply<string>(false, FastBallot.Zero, null, FastBallot.Classic(2, R1));

        ConsensusReply<string> back = RoundTripReply(reply);

        Assert.AreEqual(reply, back);
    }


    [TestMethod]
    public void AcceptReplyRoundTrips()
    {
        var reply = new AcceptReply<string>(true, FastBallot.Classic(1, R1));

        ConsensusReply<string> back = RoundTripReply(reply);

        Assert.AreEqual(reply, back);
    }


    [TestMethod]
    public void UnknownRequestKindIsRejected()
    {
        string json = """{"kind":"evil","ballot":{"round":1,"proposer":null}}""";

        Assert.ThrowsExactly<MessageDeserializationException>(
            () => Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
    }


    [TestMethod]
    public void UnknownReplyKindIsRejected()
    {
        string json = """{"kind":"evil","ballot":{"round":1,"proposer":null}}""";

        Assert.ThrowsExactly<MessageDeserializationException>(
            () => Deserialize(json, ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
    }


    [TestMethod]
    public void WrongLengthProposerIdIsRejected()
    {
        //The proposer hex flows through ReplicaId.FromSpan, which enforces the fixed identity width.
        string json = """{"kind":"prepare","ballot":{"round":1,"proposer":"0102"}}""";

        Assert.ThrowsExactly<MessageDeserializationException>(
            () => Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
    }


    [TestMethod]
    public void TruncatedPayloadIsRejected()
    {
        string json = """{"kind":"prepare","ballot":{"rou""";

        //JsonDocument.Parse throws an internal JsonReaderException, which the codec wraps as the uniform
        //MessageDeserializationException.
        Assert.Throws<MessageDeserializationException>(
            () => Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
    }


    [TestMethod]
    public void MissingRequiredFieldsFailClosed()
    {
        //A required field absent from an otherwise well-formed object must fail closed as MessageDeserializationException, not
        //surface the framework's KeyNotFoundException from a raw property accessor. The optional piggybacked
        //next ballot is exempt and is covered by AcceptRequestWithoutNextDeserializesAsNull.

        //Request: the kind, the ballot, the accept value, and a ballot's two fields.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"ballot":{"round":1,"proposer":null}}""", ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare"}""", ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"accept","ballot":{"round":1,"proposer":null}}""", ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare","ballot":{"proposer":null}}""", ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare","ballot":{"round":1}}""", ConsensusMessageJson.CreateRequestDeserializer(ReadString)));

        //Reply: the kind, then each prepare-reply field and each accept-reply field.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"acceptedBallot":{"round":1,"proposer":null}}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare-reply","promised":true,"acceptedBallot":{"round":1,"proposer":null},"conflictingBallot":{"round":0,"proposer":null}}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare-reply","acceptedValue":null,"acceptedBallot":{"round":1,"proposer":null},"conflictingBallot":{"round":0,"proposer":null}}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare-reply","acceptedValue":null,"promised":true,"conflictingBallot":{"round":0,"proposer":null}}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"prepare-reply","acceptedValue":null,"promised":true,"acceptedBallot":{"round":1,"proposer":null}}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"accept-reply","ballot":{"round":1,"proposer":null}}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"kind":"accept-reply","accepted":true}""", ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
    }


    [TestMethod]
    public void AnAcceptorStateWithEveryFieldPresentRoundTrips()
    {
        //The promise and the accepted ballot are distinct on purpose, so a decoder that swapped the two
        //ballot fields would fail the comparison rather than round-trip by accident.
        FastAcceptorState<string> state = new(FastBallot.Classic(7, Replica(2)), FastBallot.Classic(2, R1), "v");

        Assert.AreEqual(state, RoundTripState(state));
    }


    [TestMethod]
    public void TheNeverAcceptedAcceptorStateRoundTrips()
    {
        //The initial acceptor's snapshot is the state every fresh host persists first, and the restore
        //accepts it back: the pair is an inverse at the bottom of the range.
        FastAcceptorState<string> state = FastAcceptor<string>.Initial.ToState();
        FastAcceptorState<string> back = RoundTripState(state);

        Assert.AreEqual(state, back);
        Assert.AreEqual(state, FastAcceptor<string>.FromState(back).ToState());
    }


    [TestMethod]
    public void AStructDefaultAtARealBallotIsWrittenAsAValueAndNotAsNull()
    {
        //A struct default accepted at a real ballot is a value, not an absence: the writer must route it
        //through writeValue rather than collapse it to JSON null, or the document changes shape for any
        //reader beyond this decoder. The invocation count pins the routing without coupling the kill to the
        //framework's number formatting; the payload text pins the emitted document itself.
        int writes = 0;
        void WriteCountedInt32(Utf8JsonWriter writer, int value)
        {
            writes++;
            writer.WriteNumberValue(value);
        }

        FastAcceptorState<int> state = new(FastBallot.Classic(2, R1), FastBallot.Classic(2, R1), 0);

        var buffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateAcceptorStateSerializer<int>(WriteCountedInt32)(state, buffer);
        string payload = Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.AreEqual(1, writes);
        Assert.Contains("\"acceptedValue\":0", payload);

        FastAcceptorState<int> back = ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadInt32)(new ReadOnlySequence<byte>(buffer.WrittenMemory));

        Assert.AreEqual(state, back);
    }


    [TestMethod]
    public void ANullReferenceValueAtARealAcceptedBallotRoundTripsAsNull()
    {
        //A null reference accepted at a real ballot is durable state, carried as JSON null and restored as
        //null — and the restore accepts it, because Accept validates nothing about its value.
        FastAcceptorState<string> state = new(FastBallot.Classic(2, R1), FastBallot.Classic(2, R1), null);

        var buffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateAcceptorStateSerializer<string>(WriteString)(state, buffer);
        string payload = Encoding.UTF8.GetString(buffer.WrittenSpan);

        Assert.Contains("\"acceptedValue\":null", payload);

        FastAcceptorState<string> back = ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString)(new ReadOnlySequence<byte>(buffer.WrittenMemory));

        Assert.AreEqual(state, back);
        Assert.AreEqual(state, FastAcceptor<string>.FromState(back).ToState());
    }


    [TestMethod]
    public void TheAcceptorStateEncodingIsPinned()
    {
        //The exact bytes are the durable-storage contract: a host's persisted snapshots must stay readable
        //across library versions, so the shape may not drift silently.
        FastAcceptorState<string> populated = new(FastBallot.Classic(7, Replica(2)), FastBallot.Classic(2, R1), "v");
        var populatedBuffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateAcceptorStateSerializer<string>(WriteString)(populated, populatedBuffer);

        Assert.AreEqual(
            """{"promised":{"round":7,"proposer":"0200000000000000000000000000000000000000000000000000000000000000"},"acceptedBallot":{"round":2,"proposer":"0100000000000000000000000000000000000000000000000000000000000000"},"acceptedValue":"v"}""",
            Encoding.UTF8.GetString(populatedBuffer.WrittenSpan));

        FastAcceptorState<string> initial = FastAcceptor<string>.Initial.ToState();
        var initialBuffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateAcceptorStateSerializer<string>(WriteString)(initial, initialBuffer);

        Assert.AreEqual(
            """{"promised":{"round":1,"proposer":null},"acceptedBallot":{"round":0,"proposer":null},"acceptedValue":null}""",
            Encoding.UTF8.GetString(initialBuffer.WrittenSpan));
    }


    [TestMethod]
    public void AMissingAcceptorStateFieldFailsClosed()
    {
        //Each of the three fields is required. A missing acceptedValue in particular is malformed rather
        //than an absent slot: only a present JSON null decodes as the never-accepted value.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"acceptedBallot":{"round":0,"proposer":null},"acceptedValue":null}""", ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"promised":{"round":1,"proposer":null},"acceptedValue":null}""", ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString)));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"promised":{"round":1,"proposer":null},"acceptedBallot":{"round":0,"proposer":null}}""", ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString)));
    }


    [TestMethod]
    public void TheDecoderAcceptsAPromiseBelowTheInitialFastBallotTheRestoreRefuses()
    {
        //The codec refuses only what the encoding can be wrong about; every restore rule lives in FromState
        //alone. A zero promise decodes here and is refused by its range rule there.
        FastAcceptorState<string> decoded = Deserialize(
            """{"promised":{"round":0,"proposer":null},"acceptedBallot":{"round":0,"proposer":null},"acceptedValue":null}""",
            ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString));

        Assert.AreEqual(FastBallot.Zero, decoded.Promised);
        Assert.ThrowsExactly<ArgumentException>(() => FastAcceptor<string>.FromState(decoded));
    }


    [TestMethod]
    public void TheDecoderAcceptsAnAcceptedBallotAboveThePromiseTheRestoreRefuses()
    {
        FastAcceptorState<string> decoded = Deserialize(
            """{"promised":{"round":1,"proposer":null},"acceptedBallot":{"round":2,"proposer":"0100000000000000000000000000000000000000000000000000000000000000"},"acceptedValue":"v"}""",
            ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString));

        Assert.AreEqual(FastBallot.Classic(2, R1), decoded.AcceptedBallot);
        Assert.ThrowsExactly<ArgumentException>(() => FastAcceptor<string>.FromState(decoded));
    }


    [TestMethod]
    public void TheDecoderAcceptsARoundZeroBallotOwningAProposerTheRestoreRefuses()
    {
        //The ballot reader builds through the raw constructor, so a round-zero ballot owning a proposer —
        //below the initial fast ballot yet not the zero ballot — decodes and reaches the restore's own range
        //rule; a decoder that started validating rounds would take that refusal over from its owner.
        FastAcceptorState<string> decoded = Deserialize(
            """{"promised":{"round":1,"proposer":null},"acceptedBallot":{"round":0,"proposer":"0100000000000000000000000000000000000000000000000000000000000000"},"acceptedValue":null}""",
            ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString));

        Assert.AreEqual(new FastBallot(0, R1), decoded.AcceptedBallot);
        Assert.ThrowsExactly<ArgumentException>(() => FastAcceptor<string>.FromState(decoded));
    }


    private static ConsensusRequest<string> RoundTripRequest(ConsensusRequest<string> request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateRequestSerializer<string>(WriteString)(request, buffer);

        return ConsensusMessageJson.CreateRequestDeserializer(ReadString)(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static ConsensusReply<string> RoundTripReply(ConsensusReply<string> reply)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateReplySerializer<string>(WriteString)(reply, buffer);

        return ConsensusMessageJson.CreateReplyDeserializer(ReadString)(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static FastAcceptorState<string> RoundTripState(FastAcceptorState<string> state)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ConsensusMessageJson.CreateAcceptorStateSerializer<string>(WriteString)(state, buffer);

        return ConsensusMessageJson.CreateAcceptorStateDeserializer(ReadString)(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static TMessage Deserialize<TMessage>(string json, DeserializeMessageDelegate<TMessage> deserializer)
    {
        return deserializer(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static int ReadInt32(JsonElement element) => element.GetInt32();


    private static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadString(JsonElement element) => element.GetString()!;


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
