using Lumoin.Verisync.Cbor;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The format-neutral value seam, which exists so that a caller writes one value codec rather than one per
/// format and never names a serialization type in its own code.
/// </summary>
/// <remarks>
/// The claim these rows make is that ONE encode and decode pair reaches both codecs. Nothing below constructs
/// a second codec for the second format, and the value round-trips through each; a seam that still needed a
/// per-format codec could not satisfy that with one pair.
/// </remarks>
[TestClass]
internal sealed class NeutralValueCodecTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>A value whose encoding the caller owns, standing in for a consumer's own record.</summary>
    /// <param name="Text">The value's content.</param>
    private sealed record Payload(string Text);


    [TestMethod]
    public void OneNeutralCodecCarriesAValueThroughBothFormats()
    {
        Payload sent = new("a value the caller owns the encoding of");

        //One pair, declared once, used by both formats below.
        EncodeValueDelegate<Payload> encode = static (value, output) => output.Write(Encoding.UTF8.GetBytes(value.Text));
        DecodeValueDelegate<Payload> decode = static payload => new Payload(Encoding.UTF8.GetString(payload));

        SerializeMessageDelegate<Payload> cborWrite = CborChannelSerialization.CreateSerializer(CborValueCodec.CreateWriter(encode));
        DeserializeMessageDelegate<Payload> cborRead = CborChannelSerialization.CreateDeserializer(CborValueCodec.CreateReader(decode));

        ArrayBufferWriter<byte> cborBytes = new();
        cborWrite(sent, cborBytes);
        Payload throughCbor = cborRead(new ReadOnlySequence<byte>(cborBytes.WrittenMemory));

        SerializeMessageDelegate<RecordRequest<Payload>> jsonWrite = QuePaxaMessageJson.CreateRequestSerializer(JsonValueCodec.CreateWriter(encode));
        DeserializeMessageDelegate<RecordRequest<Payload>> jsonRead = QuePaxaMessageJson.CreateRequestDeserializer(JsonValueCodec.CreateReader(decode));

        RecordRequest<Payload> request = new(RecorderStep.RoundOnePhaseZero, new PrioritizedProposal<Payload>(new ProposalKey(new ProposalPriority(7), ProposerLane.For(Replica(1))), sent));
        ArrayBufferWriter<byte> jsonBytes = new();
        jsonWrite(request, jsonBytes);
        RecordRequest<Payload> throughJson = jsonRead(new ReadOnlySequence<byte>(jsonBytes.WrittenMemory));

        Assert.AreEqual(sent, throughCbor, "The value did not survive the CBOR round trip through the neutral codec.");
        Assert.AreEqual(sent, throughJson.Proposal.Value, "The value did not survive the JSON round trip through the same neutral codec.");
    }


    /// <summary>
    /// The JSON binding refuses a slot that does not carry what it wrote, rather than decoding rubbish.
    /// </summary>
    [TestMethod]
    public void TheJsonBindingRefusesASlotThatIsNotBase64()
    {
        DecodeValueDelegate<Payload> decode = static payload => new Payload(Encoding.UTF8.GetString(payload));
        DeserializeMessageDelegate<RecordRequest<Payload>> read = QuePaxaMessageJson.CreateRequestDeserializer(JsonValueCodec.CreateReader(decode));

        string json = """{"step":4,"proposal":{"priority":7,"owner":{"replica":"0100000000000000000000000000000000000000000000000000000000000000","lane":0},"value":{"not":"base64"}}}""";

        _ = Assert.ThrowsExactly<MessageDeserializationException>(() => read(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json))));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
