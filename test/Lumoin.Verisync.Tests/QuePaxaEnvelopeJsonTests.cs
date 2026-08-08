using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round trips, hostile input and byte pins for the versioned envelope and for the record a versioned
/// register decides.
/// </summary>
/// <remarks>
/// <para>
/// EVERY HOSTILE VECTOR IS A COMPLETE, OTHERWISE-VALID PAYLOAD differing from a good one in exactly the field
/// under test, and each asserts on the inner exception so that it fails for the reason it was written for
/// rather than on a missing field it never reached.
/// </para>
/// <para>
/// THE VERSION RANGE IS PINNED BY A LITERAL AND NOT BY AN EXPRESSION OVER THE TYPE'S OWN BOUND. A vector
/// computed as one above <see cref="RegisterVersion.MaxValue"/> moves with the mutation it exists to catch:
/// raise the bound to the whole unsigned range and the computed vector wraps to zero, which the envelope
/// refuses anyway, so the payload still fails and the mutant lives.
/// </para>
/// <para>
/// THE PAIR AT THE TOP OF THE RANGE CANNOT DISTINGUISH A DOUBLE-PARSING READER, and that is the point of the
/// range rather than a gap in the test. Both members are exactly representable as doubles, which is exactly
/// what the bound buys. What separates an exact reader from a lax one here is a NON-INTEGRAL TOKEN, which the
/// unsigned accessor refuses and a floating-point path accepts.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaEnvelopeJsonTests
{
    private const string EnvelopedRequestTemplate = """{"version":$VERSION,"request":{"step":4,"proposal":{"priority":1,"owner":{"replica":"$REPLICA","lane":0},"value":$VALUE}}}""";
    private const string RecordTemplate = """{"version":$VERSION,"writer":"$WRITER","value":"v"}""";

    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;

    private static string ReplicaHex { get; } = Convert.ToHexStringLower(Replica(1).AsSpan());
    private static string WriterHex { get; } = Convert.ToHexStringLower(Replica(2).AsSpan());


    [TestMethod]
    public void AnEnvelopedRequestRoundTrips()
    {
        VersionedRecordRequest<string> request = new(new RegisterVersion(7UL), new RecordRequest<string>(Four, Proposal(ProposalPriority.Lowest, LaneA, "a value")));

        Assert.AreEqual(request, RoundTripRequest(request));
    }


    [TestMethod]
    public void AnEnvelopedReplyRoundTrips()
    {
        VersionedRecordReply<string> reply = new(
            new RegisterVersion(7UL),
            new RecordReply<string>(Four, Proposal(ProposalPriority.Reserved, LaneA, "first"), Proposal(new ProposalPriority(3), LaneA, "prior")));

        Assert.AreEqual(reply, RoundTripReply(reply));
    }


    //A narrowed accessor overflows on the top of the range, and no other vector uses a version that large.
    [TestMethod]
    public void TheTopOfTheVersionRangeAndTheValueBelowItBothRoundTrip()
    {
        RegisterVersion top = RegisterVersion.MaxValue;
        RegisterVersion below = new(top.Value - 1);

        Assert.AreEqual(top, RoundTripRequest(Request(top)).Version);
        Assert.AreEqual(below, RoundTripRequest(Request(below)).Version);
        Assert.AreNotEqual(RoundTripRequest(Request(top)).Version, RoundTripRequest(Request(below)).Version);
    }


    [TestMethod]
    public void AVersionCarryingAFractionOrAnExponentIsRefused()
    {
        Assert.Throws<MessageDeserializationException>(() => DeserializeRequest(Fill(EnvelopedRequestTemplate, "7.0", ReplicaHex, "\"v\"")));
        Assert.Throws<MessageDeserializationException>(() => DeserializeRequest(Fill(EnvelopedRequestTemplate, "7e0", ReplicaHex, "\"v\"")));
        Assert.Throws<MessageDeserializationException>(() => DeserializeRequest(Fill(EnvelopedRequestTemplate, "-1", ReplicaHex, "\"v\"")));
    }


    [TestMethod]
    public void AVersionAboveTheRangeIsRefusedByNameRatherThanByTheNextAccessor()
    {
        MessageDeserializationException failure = Assert.Throws<MessageDeserializationException>(
            () => DeserializeRequest(Fill(EnvelopedRequestTemplate, "9007199254740992", ReplicaHex, "\"v\"")));

        Assert.IsInstanceOfType<JsonException>(failure.InnerException);
        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(failure.InnerException!.InnerException);
    }


    [TestMethod]
    public void TheUnwrittenVersionIsRefusedOnAnEnvelope()
    {
        MessageDeserializationException failure = Assert.Throws<MessageDeserializationException>(
            () => DeserializeRequest(Fill(EnvelopedRequestTemplate, "0", ReplicaHex, "\"v\"")));

        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(failure.InnerException!.InnerException);
    }


    [TestMethod]
    public void AMissingEnvelopeFieldIsRefusedByName()
    {
        MessageDeserializationException withoutVersion = Assert.Throws<MessageDeserializationException>(
            () => DeserializeRequest("""{"request":{"step":4,"proposal":{"priority":1,"owner":{"replica":"0000","lane":0},"value":"v"}}}"""));

        Assert.Contains("version", withoutVersion.InnerException!.Message);

        MessageDeserializationException withoutRequest = Assert.Throws<MessageDeserializationException>(
            () => DeserializeRequest("""{"version":1}"""));

        Assert.Contains("request", withoutRequest.InnerException!.Message);
    }


    //Nothing else in the suite would notice the enveloped and standalone encodings drifting apart.
    [TestMethod]
    public void TheEnvelopeCarriesTheStandaloneEncodingUnchanged()
    {
        RecordRequest<string> inner = new(Four, Proposal(ProposalPriority.Lowest, LaneA, "v"));
        VersionedRecordRequest<string> enveloped = new(new RegisterVersion(5UL), inner);

        string standalone = Serialize(QuePaxaMessageJson.CreateRequestSerializer<string>(WriteValue), inner);
        string wrapped = Serialize(QuePaxaMessageJson.CreateVersionedRequestSerializer<string>(WriteValue), enveloped);

        Assert.AreEqual($$"""{"version":5,"request":{{standalone}}}""", wrapped);
    }


    //The two versions differ so that readers crossed between the depths cannot pass.
    [TestMethod]
    public void TheComposedEncodingIsPinned()
    {
        VersionedValue<string> record = new(new RegisterVersion(9UL), Replica(2), "v");
        VersionedRecordRequest<VersionedValue<string>> request = new(
            new RegisterVersion(5UL),
            new RecordRequest<VersionedValue<string>>(Four, new PrioritizedProposal<VersionedValue<string>>(new ProposalKey(ProposalPriority.Lowest, LaneA), record)));

        string expected = Fill(
            EnvelopedRequestTemplate,
            "5",
            ReplicaHex,
            Fill(RecordTemplate, "9", WriterHex));

        Assert.AreEqual(
            expected,
            Serialize(QuePaxaMessageJson.CreateVersionedRequestSerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>(WriteValue)), request));
    }


    //A register's leader claims the reserved priority, and comparing the whole proposal catches a dropped writer.
    [TestMethod]
    public void TheDecidedRecordRoundTripsInsideAProposal()
    {
        VersionedValue<string> record = new(new RegisterVersion(3UL), Replica(2), "v");
        VersionedRecordRequest<VersionedValue<string>> request = new(
            new RegisterVersion(3UL),
            new RecordRequest<VersionedValue<string>>(Four, new PrioritizedProposal<VersionedValue<string>>(new ProposalKey(ProposalPriority.Reserved, LaneA), record)));

        SerializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> serialize =
            QuePaxaMessageJson.CreateVersionedRequestSerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>(WriteValue));
        DeserializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> deserialize =
            QuePaxaMessageJson.CreateVersionedRequestDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(ReadValue));

        VersionedRecordRequest<VersionedValue<string>> decoded = Roundtrip(request, serialize, deserialize);

        Assert.AreEqual(request, decoded);
        Assert.AreEqual(Replica(2), decoded.Request.Proposal.Value.Writer);
        Assert.AreEqual(ProposalPriority.Reserved, decoded.Request.Proposal.Key.Priority);
    }


    [TestMethod]
    public void AMissingRecordFieldIsRefusedByName()
    {
        DeserializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> deserialize =
            QuePaxaMessageJson.CreateVersionedRequestDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(ReadValue));

        MessageDeserializationException failure = Assert.Throws<MessageDeserializationException>(
            () => deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(
                Fill(EnvelopedRequestTemplate, "3", ReplicaHex, """{"version":3,"value":"v"}""")))));

        Assert.Contains("writer", failure.InnerException!.Message);
    }


    private static VersionedRecordRequest<string> Request(RegisterVersion version)
    {
        return new VersionedRecordRequest<string>(version, new RecordRequest<string>(Four, Proposal(ProposalPriority.Lowest, LaneA, "v")));
    }


    private static PrioritizedProposal<string> Proposal(ProposalPriority priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(priority, owner), value);
    }


    private static VersionedRecordRequest<string> RoundTripRequest(VersionedRecordRequest<string> request)
    {
        return Roundtrip(
            request,
            QuePaxaMessageJson.CreateVersionedRequestSerializer<string>(WriteValue),
            QuePaxaMessageJson.CreateVersionedRequestDeserializer<string>(ReadValue));
    }


    private static VersionedRecordReply<string> RoundTripReply(VersionedRecordReply<string> reply)
    {
        return Roundtrip(
            reply,
            QuePaxaMessageJson.CreateVersionedReplySerializer<string>(WriteValue),
            QuePaxaMessageJson.CreateVersionedReplyDeserializer<string>(ReadValue));
    }


    private static VersionedRecordRequest<string> DeserializeRequest(string payload)
    {
        DeserializeMessageDelegate<VersionedRecordRequest<string>> deserialize = QuePaxaMessageJson.CreateVersionedRequestDeserializer<string>(ReadValue);

        return deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(payload)));
    }


    private static TMessage Roundtrip<TMessage>(TMessage message, SerializeMessageDelegate<TMessage> serialize, DeserializeMessageDelegate<TMessage> deserialize)
    {
        ArrayBufferWriter<byte> buffer = new();
        serialize(message, buffer);

        return deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static string Serialize<TMessage>(SerializeMessageDelegate<TMessage> serialize, TMessage message)
    {
        ArrayBufferWriter<byte> buffer = new();
        serialize(message, buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    private static void WriteValue(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadValue(JsonElement element) => element.GetString()!;


    private static string Fill(string template, string version, string replica, string value)
    {
        return template
            .Replace("$VERSION", version, StringComparison.Ordinal)
            .Replace("$REPLICA", replica, StringComparison.Ordinal)
            .Replace("$VALUE", value, StringComparison.Ordinal);
    }


    private static string Fill(string template, string version, string writer)
    {
        return template
            .Replace("$VERSION", version, StringComparison.Ordinal)
            .Replace("$WRITER", writer, StringComparison.Ordinal);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
