using Lumoin.Verisync.Cbor;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Formats.Cbor;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class ChannelSerializationTests
{
    public TestContext TestContext { get; set; } = null!;

    private static List<SampleMessage> Messages { get; } =
        [new SampleMessage(1, "one"), new SampleMessage(2, ""), new SampleMessage(3, "three with spaces")];


    [TestMethod]
    public async Task JsonRoundTripsOverInMemoryPipe()
    {
        SerializeMessageDelegate<SampleMessage> serialize = JsonChannelSerialization.CreateSerializer(SampleJsonContext.Default.SampleMessage);
        DeserializeMessageDelegate<SampleMessage> deserialize = JsonChannelSerialization.CreateDeserializer(SampleJsonContext.Default.SampleMessage);

        List<SampleMessage> received = await RoundTripOverPipe(serialize, deserialize).ConfigureAwait(false);

        Assert.AreSequenceEqual(Messages, received);
    }


    [TestMethod]
    public async Task CborRoundTripsOverInMemoryPipe()
    {
        SerializeMessageDelegate<SampleMessage> serialize = CborChannelSerialization.CreateSerializer<SampleMessage>(EncodeSample);
        DeserializeMessageDelegate<SampleMessage> deserialize = CborChannelSerialization.CreateDeserializer(DecodeSample);

        List<SampleMessage> received = await RoundTripOverPipe(serialize, deserialize).ConfigureAwait(false);

        Assert.AreSequenceEqual(Messages, received);
    }


    [TestMethod]
    public async Task JsonRoundTripsOverLocalhostSocket()
    {
        SerializeMessageDelegate<SampleMessage> serialize = JsonChannelSerialization.CreateSerializer(SampleJsonContext.Default.SampleMessage);
        DeserializeMessageDelegate<SampleMessage> deserialize = JsonChannelSerialization.CreateDeserializer(SampleJsonContext.Default.SampleMessage);

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using TcpClient client = new();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.CancellationToken).ConfigureAwait(false);
        using TcpClient server = await acceptTask.ConfigureAwait(false);

        MessageChannelWriter<SampleMessage> writer = new(PipeWriter.Create(client.GetStream(), new StreamPipeWriterOptions(leaveOpen: true)), serialize);
        MessageChannelReader<SampleMessage> reader = new(PipeReader.Create(server.GetStream()), deserialize);

        Task writeTask = Task.Run(async () =>
        {
            foreach(SampleMessage message in Messages)
            {
                await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
            }

            //Half-close the send side so the reader observes end-of-stream and completes.
            client.Client.Shutdown(SocketShutdown.Send);
        }, TestContext.CancellationToken);

        var received = new List<SampleMessage>();
        await foreach(SampleMessage message in reader.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            received.Add(message);
        }

        await writeTask.ConfigureAwait(false);

        Assert.AreSequenceEqual(Messages, received);
    }


    [TestMethod]
    public void JsonDeserializerRejectsLiteralNullPayload()
    {
        //A channel message is never null. The JSON literal "null" deserializes to a null reference, which
        //the deserializer must reject rather than smuggle through the null-forgiving operator.
        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeJson("null"));
    }


    [TestMethod]
    public void JsonDeserializerRejectsTrailingObject()
    {
        //A valid message followed by a second value is two tokens; allowing it would let distinct byte
        //sequences decode to the same message, breaking canonical-bytes assumptions. Reading the trailing
        //token surfaces a JsonReaderException, which the codec wraps as the uniform MessageDeserializationException.
        Assert.Throws<MessageDeserializationException>(() => DeserializeJson("""{"Sequence":1,"Payload":"one"}{"""));
    }


    [TestMethod]
    public void JsonDeserializerRejectsTrailingNumber()
    {
        Assert.Throws<MessageDeserializationException>(() => DeserializeJson("""{"Sequence":1,"Payload":"one"}1"""));
    }


    [TestMethod]
    public void JsonDeserializerRejectsTrailingValueAfterWhitespace()
    {
        Assert.Throws<MessageDeserializationException>(() => DeserializeJson("""{"Sequence":1,"Payload":"one"}   {}"""));
    }


    [TestMethod]
    public void JsonDeserializerAcceptsTrailingWhitespace()
    {
        //Insignificant whitespace after the value is legal JSON and Utf8JsonReader skips it, so the message
        //still deserializes.
        SampleMessage message = DeserializeJson("""{"Sequence":1,"Payload":"one"}   """ + "\r\n\t");

        Assert.AreEqual(new SampleMessage(1, "one"), message);
    }


    [TestMethod]
    public void CborDeserializerRejectsAWrongMajorTypeAsTheUniformException()
    {
        //A bare integer where the decoder expects an array fails closed as the encoding-agnostic
        //MessageDeserializationException — the same type the JSON path raises — even though the underlying
        //cause is a CBOR reader exception rather than a JsonException.
        byte[] integerPayload = [0x00];

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeCbor(integerPayload));
    }


    [TestMethod]
    public void CborDeserializerRejectsAMalformedPayloadAsTheUniformException()
    {
        //An array header that declares two items but supplies one and then ends is malformed CBOR; it surfaces
        //as the same MessageDeserializationException, proving the failure type is uniform across encodings.
        byte[] truncatedArray = [0x82, 0x01];

        Assert.ThrowsExactly<MessageDeserializationException>(() => DeserializeCbor(truncatedArray));
    }


    private static SampleMessage DeserializeJson(string json)
    {
        DeserializeMessageDelegate<SampleMessage> deserialize = JsonChannelSerialization.CreateDeserializer(SampleJsonContext.Default.SampleMessage);

        return deserialize(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static SampleMessage DeserializeCbor(byte[] payload)
    {
        DeserializeMessageDelegate<SampleMessage> deserialize = CborChannelSerialization.CreateDeserializer(DecodeSample);

        return deserialize(new ReadOnlySequence<byte>(payload));
    }


    private async Task<List<SampleMessage>> RoundTripOverPipe(
        SerializeMessageDelegate<SampleMessage> serialize,
        DeserializeMessageDelegate<SampleMessage> deserialize)
    {
        Pipe pipe = new();
        MessageChannelWriter<SampleMessage> writer = new(pipe.Writer, serialize);
        MessageChannelReader<SampleMessage> reader = new(pipe.Reader, deserialize);

        foreach(SampleMessage message in Messages)
        {
            await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync().ConfigureAwait(false);

        var received = new List<SampleMessage>();
        await foreach(SampleMessage message in reader.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            received.Add(message);
        }

        return received;
    }


    private static void EncodeSample(CborWriter writer, SampleMessage message)
    {
        writer.WriteStartArray(2);
        writer.WriteInt32(message.Sequence);
        writer.WriteTextString(message.Payload);
        writer.WriteEndArray();
    }


    private static SampleMessage DecodeSample(CborReader reader)
    {
        reader.ReadStartArray();
        int sequence = reader.ReadInt32();
        string payload = reader.ReadTextString();
        reader.ReadEndArray();

        return new SampleMessage(sequence, payload);
    }
}
