using System.Collections.Generic;
using System.Formats.Cbor;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lumoin.Verisync.Cbor;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;

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

        CollectionAssert.AreEqual(Messages, received);
    }


    [TestMethod]
    public async Task CborRoundTripsOverInMemoryPipe()
    {
        SerializeMessageDelegate<SampleMessage> serialize = CborChannelSerialization.CreateSerializer<SampleMessage>(EncodeSample);
        DeserializeMessageDelegate<SampleMessage> deserialize = CborChannelSerialization.CreateDeserializer(DecodeSample);

        List<SampleMessage> received = await RoundTripOverPipe(serialize, deserialize).ConfigureAwait(false);

        CollectionAssert.AreEqual(Messages, received);
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

        CollectionAssert.AreEqual(Messages, received);
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
