using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class SocketClusterTests
{
    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, clients, servers, and enumerators are tracked in lists and disposed in the finally block.")]
    public async Task FastWriteCommitsOverLocalhostSocketClusterWithJson()
    {
        const int count = 3;

        SerializeMessageDelegate<ConsensusRequest<string>> requestSerialize = ConsensusMessageJson.CreateRequestSerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<ConsensusRequest<string>> requestDeserialize = ConsensusMessageJson.CreateRequestDeserializer(element => element.GetString()!);
        SerializeMessageDelegate<ConsensusReply<string>> replySerialize = ConsensusMessageJson.CreateReplySerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<ConsensusReply<string>> replyDeserialize = ConsensusMessageJson.CreateReplyDeserializer(element => element.GetString()!);

        var listeners = new List<TcpListener>();
        var clients = new List<TcpClient>();
        var servers = new List<TcpClient>();
        var replyEnumerators = new List<IAsyncEnumerator<ConsensusReply<string>>>();
        var nodeTasks = new List<Task>();

        try
        {
            var ports = new int[count];
            for(int i = 0; i < count; i++)
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                ports[i] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            Task<TcpClient>[] acceptTasks = listeners.Select(listener => listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask()).ToArray();
            for(int i = 0; i < count; i++)
            {
                TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, ports[i], TestContext.CancellationToken).ConfigureAwait(false);
                clients.Add(client);
            }

            servers.AddRange(await Task.WhenAll(acceptTasks).ConfigureAwait(false));

            for(int i = 0; i < count; i++)
            {
                ConsensusNode<string> node = new();
                NetworkStream serverStream = servers[i].GetStream();
                MessageChannelReader<ConsensusRequest<string>> requests = new(PipeReader.Create(serverStream), requestDeserialize);
                MessageChannelWriter<ConsensusReply<string>> replies = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), replySerialize);
                nodeTasks.Add(node.RunAsync(requests.ReadAllAsync(TestContext.CancellationToken), (reply, token) => replies.WriteAsync(reply, token), cancellationToken: TestContext.CancellationToken));
            }

            var endpoints = new ConsensusEndpointDelegate<string>[count];
            for(int i = 0; i < count; i++)
            {
                NetworkStream clientStream = clients[i].GetStream();
                MessageChannelWriter<ConsensusRequest<string>> requestWriter = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), requestSerialize);
                MessageChannelReader<ConsensusReply<string>> replyReader = new(PipeReader.Create(clientStream), replyDeserialize);
                IAsyncEnumerator<ConsensusReply<string>> replies = replyReader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
                replyEnumerators.Add(replies);

                endpoints[i] = async (request, token) =>
                {
                    await requestWriter.WriteAsync(request, token).ConfigureAwait(false);
                    await replies.MoveNextAsync().ConfigureAwait(false);

                    return replies.Current;
                };
            }

            FastProposer<string> proposer = new(endpoints);

            (int acceptedCount, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(3, acceptedCount);
            Assert.IsTrue(committed);

            foreach(TcpClient client in clients)
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }

            await Task.WhenAll(nodeTasks).ConfigureAwait(false);
        }
        finally
        {
            foreach(IAsyncEnumerator<ConsensusReply<string>> enumerator in replyEnumerators)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            foreach(TcpClient client in clients)
            {
                client.Dispose();
            }

            foreach(TcpClient server in servers)
            {
                server.Dispose();
            }

            foreach(TcpListener listener in listeners)
            {
                listener.Dispose();
            }
        }
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, clients, servers, and enumerators are tracked in lists and disposed in the finally block.")]
    public async Task FastWriteCommitsWithAPersistHookWiredOnEverySocketNode()
    {
        const int count = 3;

        SerializeMessageDelegate<ConsensusRequest<string>> requestSerialize = ConsensusMessageJson.CreateRequestSerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<ConsensusRequest<string>> requestDeserialize = ConsensusMessageJson.CreateRequestDeserializer(element => element.GetString()!);
        SerializeMessageDelegate<ConsensusReply<string>> replySerialize = ConsensusMessageJson.CreateReplySerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<ConsensusReply<string>> replyDeserialize = ConsensusMessageJson.CreateReplyDeserializer(element => element.GetString()!);

        //One concurrent sink per node so the per-node persist counts can be asserted independently; the
        //ConsensusNode loop is sequential per node but the nodes run on separate tasks.
        var persisted = new ConcurrentQueue<FastAcceptor<string>>[count];
        for(int i = 0; i < count; i++)
        {
            persisted[i] = new ConcurrentQueue<FastAcceptor<string>>();
        }

        var listeners = new List<TcpListener>();
        var clients = new List<TcpClient>();
        var servers = new List<TcpClient>();
        var replyEnumerators = new List<IAsyncEnumerator<ConsensusReply<string>>>();
        var nodeTasks = new List<Task>();

        try
        {
            var ports = new int[count];
            for(int i = 0; i < count; i++)
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                ports[i] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            Task<TcpClient>[] acceptTasks = listeners.Select(listener => listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask()).ToArray();
            for(int i = 0; i < count; i++)
            {
                TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, ports[i], TestContext.CancellationToken).ConfigureAwait(false);
                clients.Add(client);
            }

            servers.AddRange(await Task.WhenAll(acceptTasks).ConfigureAwait(false));

            for(int i = 0; i < count; i++)
            {
                ConsensusNode<string> node = new();
                NetworkStream serverStream = servers[i].GetStream();
                MessageChannelReader<ConsensusRequest<string>> requests = new(PipeReader.Create(serverStream), requestDeserialize);
                MessageChannelWriter<ConsensusReply<string>> replies = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), replySerialize);

                //The hook records each acceptor state before its reply is sent: a stand-in for the fsync a
                //durable host would do. RunAsync only invokes it when the acceptor is not already durable.
                ConcurrentQueue<FastAcceptor<string>> sink = persisted[i];
                PersistAcceptorDelegate<string> persist = (acceptor, _) =>
                {
                    sink.Enqueue(acceptor);

                    return ValueTask.CompletedTask;
                };

                nodeTasks.Add(node.RunAsync(requests.ReadAllAsync(TestContext.CancellationToken), (reply, token) => replies.WriteAsync(reply, token), persist, TestContext.CancellationToken));
            }

            var endpoints = new ConsensusEndpointDelegate<string>[count];
            for(int i = 0; i < count; i++)
            {
                NetworkStream clientStream = clients[i].GetStream();
                MessageChannelWriter<ConsensusRequest<string>> requestWriter = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), requestSerialize);
                MessageChannelReader<ConsensusReply<string>> replyReader = new(PipeReader.Create(clientStream), replyDeserialize);
                IAsyncEnumerator<ConsensusReply<string>> replies = replyReader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
                replyEnumerators.Add(replies);

                endpoints[i] = async (request, token) =>
                {
                    await requestWriter.WriteAsync(request, token).ConfigureAwait(false);
                    await replies.MoveNextAsync().ConfigureAwait(false);

                    return replies.Current;
                };
            }

            FastProposer<string> proposer = new(endpoints);

            (int acceptedCount, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(3, acceptedCount);
            Assert.IsTrue(committed);

            foreach(TcpClient client in clients)
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }

            await Task.WhenAll(nodeTasks).ConfigureAwait(false);
        }
        finally
        {
            foreach(IAsyncEnumerator<ConsensusReply<string>> enumerator in replyEnumerators)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            foreach(TcpClient client in clients)
            {
                client.Dispose();
            }

            foreach(TcpClient server in servers)
            {
                server.Dispose();
            }

            foreach(TcpListener listener in listeners)
            {
                listener.Dispose();
            }
        }

        //A fast write that committed on every node must have changed every node's acceptor at least once, so
        //each node's durability hook fired before it ever sent a reply: an unpersisted accept never escaped.
        for(int i = 0; i < count; i++)
        {
            Assert.IsGreaterThan(0, persisted[i].Count, $"Node {i} sent a reply without persisting a state change.");
        }
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, server, and reply enumerator are all disposed in the finally block.")]
    public async Task FastWriteCommitsOverAPaddedLocalhostSocket()
    {
        //Both endpoints share one FramePadding policy, so the request/reply exchange must round-trip through
        //the padded wire format unchanged — the consensus outcome proves the inner real-length framing is
        //read back correctly after the writer zero-fills each frame to its bucket.
        FramePadding padding = FramePadding.PowersOfTwo(64);

        SerializeMessageDelegate<ConsensusRequest<string>> requestSerialize = ConsensusMessageJson.CreateRequestSerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<ConsensusRequest<string>> requestDeserialize = ConsensusMessageJson.CreateRequestDeserializer(element => element.GetString()!);
        SerializeMessageDelegate<ConsensusReply<string>> replySerialize = ConsensusMessageJson.CreateReplySerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<ConsensusReply<string>> replyDeserialize = ConsensusMessageJson.CreateReplyDeserializer(element => element.GetString()!);

        TcpListener listener = new(IPAddress.Loopback, 0);
        TcpClient? client = null;
        TcpClient? server = null;
        IAsyncEnumerator<ConsensusReply<string>>? replyEnumerator = null;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask();
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.CancellationToken).ConfigureAwait(false);
            server = await acceptTask.ConfigureAwait(false);

            ConsensusNode<string> node = new();
            NetworkStream serverStream = server.GetStream();
            MessageChannelReader<ConsensusRequest<string>> serverRequests = new(PipeReader.Create(serverStream), requestDeserialize, padding: padding);
            MessageChannelWriter<ConsensusReply<string>> serverReplies = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), replySerialize, padding: padding);
            Task nodeTask = node.RunAsync(serverRequests.ReadAllAsync(TestContext.CancellationToken), (reply, token) => serverReplies.WriteAsync(reply, token), cancellationToken: TestContext.CancellationToken);

            NetworkStream clientStream = client.GetStream();
            MessageChannelWriter<ConsensusRequest<string>> requestWriter = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), requestSerialize, padding: padding);
            MessageChannelReader<ConsensusReply<string>> replyReader = new(PipeReader.Create(clientStream), replyDeserialize, padding: padding);
            replyEnumerator = replyReader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
            IAsyncEnumerator<ConsensusReply<string>> replies = replyEnumerator;

            ConsensusEndpointDelegate<string> endpoint = async (request, token) =>
            {
                await requestWriter.WriteAsync(request, token).ConfigureAwait(false);
                await replies.MoveNextAsync().ConfigureAwait(false);

                return replies.Current;
            };

            FastProposer<string> proposer = new([endpoint]);

            (int acceptedCount, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "padded", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(1, acceptedCount);
            Assert.IsTrue(committed);

            client.Client.Shutdown(SocketShutdown.Send);

            await nodeTask.ConfigureAwait(false);
        }
        finally
        {
            if(replyEnumerator is not null)
            {
                await replyEnumerator.DisposeAsync().ConfigureAwait(false);
            }

            client?.Dispose();
            server?.Dispose();
            listener.Dispose();
        }
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The listener, client, and server are all disposed in the finally block.")]
    public async Task PaddedFramesLandOnBucketBoundariesOverASocket()
    {
        //Write padded frames of growing real length down one socket and parse the raw outer length prefixes off
        //the other socket: every observed wire frame must equal the policy's bucket for its real payload, which
        //is what a network observer is limited to seeing.
        FramePadding padding = FramePadding.PowersOfTwo(64);
        SerializeMessageDelegate<string> serialize = (message, output) => output.Write(Encoding.UTF8.GetBytes(message));
        string[] messages = [new string('x', 3), new string('x', 59), new string('x', 60), new string('x', 200)];

        TcpListener listener = new(IPAddress.Loopback, 0);
        TcpClient? client = null;
        TcpClient? server = null;

        try
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask();
            client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.CancellationToken).ConfigureAwait(false);
            server = await acceptTask.ConfigureAwait(false);

            NetworkStream clientStream = client.GetStream();
            MessageChannelWriter<string> writer = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), serialize, padding: padding);
            foreach(string message in messages)
            {
                await writer.WriteAsync(message, TestContext.CancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);

            //Read the raw bytes off the receiving socket and parse outer length prefixes without the channel's help.
            List<int> outerLengths = await ReadRawOuterFrameLengths(PipeReader.Create(server.GetStream())).ConfigureAwait(false);

            int[] expected = messages.Select(m => padding.PaddedLength(Encoding.UTF8.GetByteCount(m))).ToArray();
            Assert.AreSequenceEqual(expected, outerLengths.ToArray());
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            listener.Dispose();
        }
    }


    private async Task<List<int>> ReadRawOuterFrameLengths(PipeReader reader)
    {
        var bytes = new ArrayBufferWriter<byte>();
        while(true)
        {
            ReadResult result = await reader.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);
            foreach(ReadOnlyMemory<byte> segment in result.Buffer)
            {
                bytes.Write(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            if(result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync().ConfigureAwait(false);

        var lengths = new List<int>();
        ReadOnlySpan<byte> all = bytes.WrittenSpan;
        int offset = 0;
        while(offset < all.Length)
        {
            int outerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(all.Slice(offset, 4));
            lengths.Add(outerLength);
            offset += 4 + outerLength;
        }

        return lengths;
    }


    /// <summary>
    /// A versioned recorder host's decline is a host act with no protocol field, so a wire host reduces it to
    /// an opaque fault frame carrying the call's correlation and nothing else, and the next request on the
    /// same connection is answered normally — which a transport that dropped the decline could not do.
    /// </summary>
    [TestMethod]
    public async Task ADeclineOverASocketReachesTheProposerAsAFaultAndTheConnectionKeepsServing()
    {
        SerializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> requestSerialize =
            QuePaxaMessageJson.CreateVersionedRequestSerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>((writer, value) => writer.WriteStringValue(value)));
        DeserializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> requestDeserialize =
            QuePaxaMessageJson.CreateVersionedRequestDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(element => element.GetString()!));
        SerializeMessageDelegate<VersionedRecordReply<VersionedValue<string>>> replySerialize =
            QuePaxaMessageJson.CreateVersionedReplySerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>((writer, value) => writer.WriteStringValue(value)));
        DeserializeMessageDelegate<VersionedRecordReply<VersionedValue<string>>> replyDeserialize =
            QuePaxaMessageJson.CreateVersionedReplyDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(element => element.GetString()!));

        ReplicaId second = VersionedReplica(2);
        QuePaxaVersionedNode<string> host = new(VersionedMembership, Membership.Member(second), new VersionedValue<string>(new RegisterVersion(4UL), second, VersionedMembership, "committed"));
        QuePaxaVersionedRunner<string> runner = new(host);
        Task run = runner.RunAsync(cancellationToken: TestContext.CancellationToken);

        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> accept = listener.AcceptTcpClientAsync(TestContext.CancellationToken).AsTask();
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.CancellationToken).ConfigureAwait(false);
            using TcpClient server = await accept.ConfigureAwait(false);

            //The server answers EVERY frame, with the reply or with a fault frame carrying the id alone:
            //the opaque reduction the runner documents, since the decline names the live version in prose.
            NetworkStream serverStream = server.GetStream();
            MessageChannelReader<CorrelatedFrame> serverRequests = new(PipeReader.Create(serverStream), ReadFrame);
            MessageChannelWriter<CorrelatedFrame> serverResponses = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), WriteFrame);
            Task serving = Task.Run(async () =>
            {
                await foreach(CorrelatedFrame frame in serverRequests.ReadAllAsync(TestContext.CancellationToken).ConfigureAwait(false))
                {
                    CorrelatedFrame response;
                    try
                    {
                        VersionedRecordRequest<VersionedValue<string>> request = requestDeserialize(new ReadOnlySequence<byte>(frame.Payload!));
                        VersionedRecordReply<VersionedValue<string>> reply = await runner.RecordAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
                        var buffer = new ArrayBufferWriter<byte>();
                        replySerialize(reply, buffer);
                        response = new CorrelatedFrame(frame.Id, buffer.WrittenSpan.ToArray());
                    }
                    catch(Exception)
                    {
                        response = new CorrelatedFrame(frame.Id, null);
                    }

                    await serverResponses.WriteAsync(response, TestContext.CancellationToken).ConfigureAwait(false);
                }
            }, TestContext.CancellationToken);

            NetworkStream clientStream = client.GetStream();
            MessageChannelWriter<CorrelatedFrame> clientRequests = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), WriteFrame);
            MessageChannelReader<CorrelatedFrame> clientResponses = new(PipeReader.Create(clientStream), ReadFrame);
            IAsyncEnumerator<CorrelatedFrame> responses = clientResponses.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
            try
            {
                int nextId = 1;
                VersionedRecorderEndpointDelegate<VersionedValue<string>> endpoint = async (request, token) =>
                {
                    int id = nextId++;
                    var buffer = new ArrayBufferWriter<byte>();
                    requestSerialize(request, buffer);
                    await clientRequests.WriteAsync(new CorrelatedFrame(id, buffer.WrittenSpan.ToArray()), token).ConfigureAwait(false);
                    _ = await responses.MoveNextAsync().ConfigureAwait(false);
                    CorrelatedFrame answer = responses.Current;
                    Assert.AreEqual(id, answer.Id);
                    if(answer.Payload is null)
                    {
                        throw new IOException($"Call {answer.Id} faulted at the recorder host.");
                    }

                    return replyDeserialize(new ReadOnlySequence<byte>(answer.Payload));
                };

                IOException declined = await Assert.ThrowsExactlyAsync<IOException>(
                    async () => _ = await endpoint(VersionedSocketRequest(7UL, second), TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

                //The fault that crossed the wire is the client's own opaque text, so nothing of the host's
                //exception prose — which names the version it serves — ever left the process.
                Assert.AreEqual("Call 1 faulted at the recorder host.", declined.Message);

                VersionedRecordReply<VersionedValue<string>> reply = await endpoint(VersionedSocketRequest(5UL, second), TestContext.CancellationToken).ConfigureAwait(false);

                Assert.AreEqual(new RegisterVersion(5UL), reply.Version);

                client.Client.Shutdown(SocketShutdown.Send);
                await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await responses.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Dispose();
        }

        runner.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
    }


    private sealed record CorrelatedFrame(int Id, byte[]? Payload);


    private static void WriteFrame(CorrelatedFrame frame, IBufferWriter<byte> destination)
    {
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber("id", frame.Id);
        if(frame.Payload is null)
        {
            writer.WriteBoolean("fault", true);
        }
        else
        {
            writer.WritePropertyName("payload");
            writer.WriteRawValue(frame.Payload);
        }

        writer.WriteEndObject();
    }


    private static CorrelatedFrame ReadFrame(ReadOnlySequence<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        int id = document.RootElement.GetProperty("id").GetInt32();
        if(document.RootElement.TryGetProperty("payload", out JsonElement inner))
        {
            return new CorrelatedFrame(id, Encoding.UTF8.GetBytes(inner.GetRawText()));
        }

        return new CorrelatedFrame(id, null);
    }


    private static VersionedRecordRequest<VersionedValue<string>> VersionedSocketRequest(ulong version, ReplicaId owner)
    {
        RegisterVersion at = new(version);
        VersionedValue<string> record = new(at, owner, VersionedMembership, "value");
        PrioritizedProposal<VersionedValue<string>> proposal = new(new ProposalKey(ProposalPriority.Lowest, ProposerLane.For(owner)), record);

        return new VersionedRecordRequest<VersionedValue<string>>(at, new RecordRequest<VersionedValue<string>>(RecorderStep.RoundOnePhaseZero, proposal));
    }


    /// <summary>
    /// The membership the versioned records in this suite carry, minted from the order the versioned host runs
    /// under.
    /// </summary>
    private static QuePaxaConfiguration VersionedMembership { get; } =
        QuePaxaConfiguration.CreateGenesis(Membership.Of(VersionedReplica(1), VersionedReplica(2), VersionedReplica(3)));


    private static ReplicaId VersionedReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
