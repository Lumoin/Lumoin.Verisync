using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips and hostile-input checks for the consensus DTO JSON codecs. The prepare kinds matter
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

        Assert.ThrowsExactly<NotSupportedException>(
            () => Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
    }


    [TestMethod]
    public void UnknownReplyKindIsRejected()
    {
        string json = """{"kind":"evil","ballot":{"round":1,"proposer":null}}""";

        Assert.ThrowsExactly<NotSupportedException>(
            () => Deserialize(json, ConsensusMessageJson.CreateReplyDeserializer(ReadString)));
    }


    [TestMethod]
    public void WrongLengthProposerIdIsRejected()
    {
        //The proposer hex flows through ReplicaId.FromSpan, which enforces the fixed identity width.
        string json = """{"kind":"prepare","ballot":{"round":1,"proposer":"0102"}}""";

        Assert.ThrowsExactly<ArgumentException>(
            () => Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
    }


    [TestMethod]
    public void TruncatedPayloadIsRejected()
    {
        string json = """{"kind":"prepare","ballot":{"rou""";

        //JsonDocument.Parse throws the internal JsonReaderException subclass, so match by base type.
        Assert.Throws<JsonException>(
            () => Deserialize(json, ConsensusMessageJson.CreateRequestDeserializer(ReadString)));
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


    private static TMessage Deserialize<TMessage>(string json, DeserializeMessageDelegate<TMessage> deserializer)
    {
        return deserializer(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
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
