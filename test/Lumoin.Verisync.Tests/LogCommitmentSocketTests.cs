using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// End-to-end proof that the log-plane anti-equivocation evidence survives a real localhost socket and still
/// verifies. The <see cref="LogCommitmentJson"/> codec exists to carry segment seals over a channel during the
/// anti-equivocation exchange, but the seals — the nested, proof-bearing, digest-chained artifacts whose
/// deserializer is itself a verifier — had only ever round-tripped through an in-memory buffer in
/// <see cref="LogCommitmentJsonTests"/>. Here a log publishes a chain of <see cref="SegmentSeal{TProof}"/>
/// over a duplex loopback connection through the framed message channel; the monitor's deserializer re-derives
/// and checks each seal's digest as it arrives off the wire (a tampered seal would throw a
/// <see cref="MessageDeserializationException"/>), and the chain linkage — each seal's previous-seal digest
/// equal to its predecessor's digest — is asserted to survive real TCP framing.
/// </summary>
[TestClass]
internal sealed class LogCommitmentSocketTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task SegmentSealChainSurvivesAndVerifiesOverLocalhostSocket()
    {
        //A three-seal anti-equivocation chain: each seal commits the next segment and links to its predecessor
        //by the predecessor's digest. The deserializer re-derives each digest from the typed fields and rejects
        //a mismatch, so a seal that survives the wire unaltered both round-trips and verifies.
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x11 }, [], Sha256);
        SegmentSeal<string> second = SegmentSeal<string>.Create(3, 5, first.Digest, new byte[] { 0x22, 0x33 }, ["controller"], Sha256);
        SegmentSeal<string> third = SegmentSeal<string>.Create(6, 9, second.Digest, new byte[] { 0x44 }, ["auditor", "controller"], Sha256);
        SegmentSeal<string>[] chain = [first, second, third];

        SerializeMessageDelegate<SegmentSeal<string>> serialize = LogCommitmentJson.CreateSegmentSealSerializer<string>(WriteString);
        DeserializeMessageDelegate<SegmentSeal<string>> deserialize = LogCommitmentJson.CreateSegmentSealDeserializer(ReadString, Sha256);

        List<SegmentSeal<string>> received = await PublishOverSocketAsync(chain, serialize, deserialize).ConfigureAwait(false);

        //Every seal survived the wire intact, so each re-derived its transmitted digest and the deserializer
        //accepted it; a tampered seal would have surfaced a MessageDeserializationException off the reader.
        Assert.HasCount(3, received);
        Assert.AreEqual(first, received[0]);
        Assert.AreEqual(second, received[1]);
        Assert.AreEqual(third, received[2]);

        //The first seal opens the chain; the rest link to their predecessor by its digest, and that linkage
        //survived serialization across the socket.
        Assert.IsNull(received[0].PreviousSealDigest);
        Assert.IsTrue(received[1].PreviousSealDigest!.Value.Span.SequenceEqual(received[0].Digest.Span));
        Assert.IsTrue(received[2].PreviousSealDigest!.Value.Span.SequenceEqual(received[1].Digest.Span));

        //The attestation proofs rode along with the seal and arrived in order.
        Assert.AreSequenceEqual(third.Proofs.ToArray(), received[2].Proofs.ToArray());
    }


    /// <summary>
    /// Publishes a sequence of seals one way over a fresh duplex loopback connection and returns what the monitor
    /// reads back, following the ChannelSerializationTests socket plumbing: the log writes each framed seal then
    /// half-closes its send side so the monitor's reader observes end-of-stream and completes.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, and server are all disposed by their using declarations at the end of the scope.")]
    private async Task<List<SegmentSeal<string>>> PublishOverSocketAsync(
        IReadOnlyList<SegmentSeal<string>> seals,
        SerializeMessageDelegate<SegmentSeal<string>> serialize,
        DeserializeMessageDelegate<SegmentSeal<string>> deserialize)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using TcpClient client = new();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.CancellationToken).ConfigureAwait(false);
        using TcpClient server = await acceptTask.ConfigureAwait(false);

        MessageChannelWriter<SegmentSeal<string>> writer = new(PipeWriter.Create(client.GetStream(), new StreamPipeWriterOptions(leaveOpen: true)), serialize);
        MessageChannelReader<SegmentSeal<string>> reader = new(PipeReader.Create(server.GetStream()), deserialize);

        Task writeTask = Task.Run(async () =>
        {
            foreach(SegmentSeal<string> seal in seals)
            {
                await writer.WriteAsync(seal, TestContext.CancellationToken).ConfigureAwait(false);
            }

            //Half-close the send side so the monitor's reader observes end-of-stream and its loop ends.
            client.Client.Shutdown(SocketShutdown.Send);
        }, TestContext.CancellationToken);

        var received = new List<SegmentSeal<string>>();
        await foreach(SegmentSeal<string> seal in reader.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            received.Add(seal);
        }

        await writeTask.ConfigureAwait(false);

        return received;
    }


    private static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadString(JsonElement element) => element.GetString()!;


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);
}
