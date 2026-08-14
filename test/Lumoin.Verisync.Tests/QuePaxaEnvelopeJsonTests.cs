using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Globalization;
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
    private const string RecordTemplate = """{"version":$VERSION,"writer":"$WRITER","configuration":$CONFIGURATION,"value":"v"}""";
    private const string EnvelopedReplyTemplate = """{"version":7,$RECORDER"reply":{"step":4,"first":{"priority":1,"owner":{"replica":"$REPLICA","lane":0},"value":"v"},"priorAggregate":null}}""";
    private const string RecordLabel = "A versioned value";
    private const string ConfigurationLabel = "A configuration";
    private const string VersionedReplyLabel = "A versioned record reply";

    public TestContext TestContext { get; set; } = null!;

    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;

    private static string ReplicaHex { get; } = Convert.ToHexStringLower(Replica(1).AsSpan());
    private static string WriterHex { get; } = Convert.ToHexStringLower(Replica(2).AsSpan());

    /// <summary>The membership the records in this suite carry.</summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis([Replica(1), Replica(2), Replica(3)]);

    /// <summary>The membership's payload, written the way the record codec writes it.</summary>
    private static string ConfigurationJson { get; } = $$"""{"cluster":"{{Convert.ToHexStringLower(Configuration.Cluster.AsSpan())}}","members":["{{ReplicaHex}}","{{WriterHex}}","{{Convert.ToHexStringLower(Replica(3).AsSpan())}}"]}""";


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
            Replica(3),
            new RecordReply<string>(Four, Proposal(ProposalPriority.Reserved, LaneA, "first"), Proposal(new ProposalPriority(3), LaneA, "prior")));

        VersionedRecordReply<string> decoded = RoundTripReply(reply);

        Assert.AreEqual(reply, decoded);

        //The identity is what a writer counts a quorum over distinct members with, so it is asserted on its
        //own rather than left to the record's equality.
        Assert.AreEqual(Replica(3), decoded.Recorder);
    }


    /// <summary>
    /// The identity a writer counts its quorum over distinct members with cannot be optional: an omitted slot
    /// would decode into the all-zero replica, which is an identity a deployment may legitimately hold, so the
    /// check would pass for whichever member happened to carry it and silently fail for the rest.
    /// </summary>
    [TestMethod]
    public void AReplyMissingItsRecorderIsRefusedByName()
    {
        //A complete, otherwise-valid reply differing from a good one in exactly the omitted slot.
        MessageDeserializationException failure = Assert.Throws<MessageDeserializationException>(
            () => DeserializeReply(FillReply(string.Empty)));

        Assert.IsInstanceOfType<JsonException>(failure.InnerException);
        Assert.Contains("recorder", failure.InnerException!.Message);
        Assert.Contains(VersionedReplyLabel, failure.InnerException.Message);

        //The same payload with the slot present decodes, so the vector fails on the omission and on nothing
        //else it happens to be short of.
        Assert.AreEqual(
            Replica(3),
            DeserializeReply(FillReply($"\"recorder\":\"{Convert.ToHexStringLower(Replica(3).AsSpan())}\",")).Recorder);
    }


    /// <summary>
    /// A narrowed accessor overflows on the top of the range, and no other vector uses a version that large.
    /// </summary>
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


    /// <summary>
    /// Nothing else in the suite would notice the enveloped and standalone encodings drifting apart.
    /// </summary>
    [TestMethod]
    public void TheEnvelopeCarriesTheStandaloneEncodingUnchanged()
    {
        RecordRequest<string> inner = new(Four, Proposal(ProposalPriority.Lowest, LaneA, "v"));
        VersionedRecordRequest<string> enveloped = new(new RegisterVersion(5UL), inner);

        string standalone = Serialize(QuePaxaMessageJson.CreateRequestSerializer<string>(WriteValue), inner);
        string wrapped = Serialize(QuePaxaMessageJson.CreateVersionedRequestSerializer<string>(WriteValue), enveloped);

        Assert.AreEqual($$"""{"version":5,"request":{{standalone}}}""", wrapped);
    }


    /// <summary>
    /// The two versions differ so that readers crossed between the depths cannot pass.
    /// </summary>
    [TestMethod]
    public void TheComposedEncodingIsPinned()
    {
        VersionedValue<string> record = new(new RegisterVersion(9UL), Replica(2), Configuration, "v");
        VersionedRecordRequest<VersionedValue<string>> request = new(
            new RegisterVersion(5UL),
            new RecordRequest<VersionedValue<string>>(Four, new PrioritizedProposal<VersionedValue<string>>(new ProposalKey(ProposalPriority.Lowest, LaneA), record)));

        string expected = Fill(
            EnvelopedRequestTemplate,
            "5",
            ReplicaHex,
            FillRecord(RecordTemplate, "9", WriterHex, ConfigurationJson));

        Assert.AreEqual(
            expected,
            Serialize(QuePaxaMessageJson.CreateVersionedRequestSerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>(WriteValue)), request));
    }


    /// <summary>
    /// A register's leader claims the reserved priority, and comparing the whole proposal catches a dropped writer.
    /// </summary>
    [TestMethod]
    public void TheDecidedRecordRoundTripsInsideAProposal()
    {
        VersionedValue<string> record = new(new RegisterVersion(3UL), Replica(2), Configuration, "v");
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

        //The configuration crossed the codec as its own object over its own buffers, so equality here is
        //element-wise rather than an identity the encoder and decoder happen to share.
        Assert.AreNotSame(Configuration, decoded.Request.Proposal.Value.NextConfiguration);
        Assert.AreEqual(Configuration, decoded.Request.Proposal.Value.NextConfiguration);
        Assert.AreSequenceEqual(Configuration.Members, decoded.Request.Proposal.Value.NextConfiguration.Members);
        Assert.AreEqual(Configuration.Cluster, decoded.Request.Proposal.Value.NextConfiguration.Cluster);
    }


    [TestMethod]
    public void AMissingRecordFieldIsRefusedByName()
    {
        //Each vector is a complete, otherwise-valid record differing from a good one in exactly the omitted
        //slot, so each draws one rejection and no other guard can answer for it. The configuration's own two
        //slots carry their own label, so a reader learns which object was short rather than only which field.
        string members = $$"""["{{ReplicaHex}}","{{WriterHex}}"]""";
        string cluster = Convert.ToHexStringLower(Configuration.Cluster.AsSpan());

        (string Record, string Field, string Label)[] vectors =
        [
            ($$"""{"writer":"{{WriterHex}}","configuration":{{ConfigurationJson}},"value":"v"}""", "version", RecordLabel),
            ($$"""{"version":3,"configuration":{{ConfigurationJson}},"value":"v"}""", "writer", RecordLabel),
            ($$"""{"version":3,"writer":"{{WriterHex}}","value":"v"}""", "configuration", RecordLabel),
            ($$"""{"version":3,"writer":"{{WriterHex}}","configuration":{{ConfigurationJson}}}""", "value", RecordLabel),
            ($$"""{"version":3,"writer":"{{WriterHex}}","configuration":{"members":{{members}}},"value":"v"}""", "cluster", ConfigurationLabel),
            ($$"""{"version":3,"writer":"{{WriterHex}}","configuration":{"cluster":"{{cluster}}"},"value":"v"}""", "members", ConfigurationLabel)
        ];

        DeserializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> deserialize =
            QuePaxaMessageJson.CreateVersionedRequestDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(ReadValue));

        foreach((string record, string field, string label) in vectors)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"record omitting {field}, refused as {label}"));

            MessageDeserializationException failure = Assert.Throws<MessageDeserializationException>(
                () => deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(
                    Fill(EnvelopedRequestTemplate, "3", ReplicaHex, record)))));

            Assert.IsInstanceOfType<JsonException>(failure.InnerException);
            Assert.Contains(field, failure.InnerException!.Message);
            Assert.Contains(label, failure.InnerException.Message);
        }
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


    /// <summary>A complete enveloped reply whose recorder slot is whatever <paramref name="recorder"/> is.</summary>
    /// <param name="recorder">The recorder slot including its trailing comma, or an empty string to omit it.</param>
    /// <returns>The payload.</returns>
    private static string FillReply(string recorder)
    {
        return EnvelopedReplyTemplate
            .Replace("$RECORDER", recorder, StringComparison.Ordinal)
            .Replace("$REPLICA", ReplicaHex, StringComparison.Ordinal);
    }


    private static VersionedRecordReply<string> DeserializeReply(string payload)
    {
        DeserializeMessageDelegate<VersionedRecordReply<string>> deserialize = QuePaxaMessageJson.CreateVersionedReplyDeserializer<string>(ReadValue);

        return deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(payload)));
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


    private static string FillRecord(string template, string version, string writer, string configuration)
    {
        return template
            .Replace("$VERSION", version, StringComparison.Ordinal)
            .Replace("$WRITER", writer, StringComparison.Ordinal)
            .Replace("$CONFIGURATION", configuration, StringComparison.Ordinal);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
