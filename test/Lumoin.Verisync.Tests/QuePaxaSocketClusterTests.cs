using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// QuePaxa consensus over real localhost sockets: the bare record family and the versioned envelope both
/// serialized through their JSON codecs across loopback TCP, per the standing rule that integration tests
/// serialize over a real transport rather than run in process. The subjects are whole rounds — a leaderless
/// decision, the leader's one-step fast path with durability hooks wired, and a versioned write followed by
/// a learn and the next version's write — because the codec round-trip tests already pin the bytes and only
/// a decision proves the protocol survives the transport's framing end to end.
/// </summary>
[TestClass]
internal sealed class QuePaxaSocketClusterTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, clients, servers, and enumerators are tracked in lists and disposed in the finally block.")]
    public async Task ALeaderlessRoundDecidesOverLocalhostSockets()
    {
        const int count = 3;

        SerializeMessageDelegate<RecordRequest<string>> requestSerialize = QuePaxaMessageJson.CreateRequestSerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<RecordRequest<string>> requestDeserialize = QuePaxaMessageJson.CreateRequestDeserializer(element => element.GetString()!);
        SerializeMessageDelegate<RecordReply<string>> replySerialize = QuePaxaMessageJson.CreateReplySerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<RecordReply<string>> replyDeserialize = QuePaxaMessageJson.CreateReplyDeserializer(element => element.GetString()!);

        var listeners = new List<TcpListener>();
        var clients = new List<TcpClient>();
        var servers = new List<TcpClient>();
        var replyEnumerators = new List<IAsyncEnumerator<RecordReply<string>>>();
        var gates = new List<SemaphoreSlim>();
        var nodeTasks = new List<Task>();

        try
        {
            (listeners, clients, servers) = await ConnectedPairs(count).ConfigureAwait(false);

            for(int i = 0; i < count; i++)
            {
                QuePaxaNode<string> node = new(QuePaxaRecorder<string>.Leaderless);
                NetworkStream serverStream = servers[i].GetStream();
                MessageChannelReader<RecordRequest<string>> requests = new(PipeReader.Create(serverStream), requestDeserialize);
                MessageChannelWriter<RecordReply<string>> replies = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), replySerialize);
                nodeTasks.Add(node.RunAsync(requests.ReadAllAsync(TestContext.CancellationToken), (reply, token) => replies.WriteAsync(reply, token), cancellationToken: TestContext.CancellationToken));
            }

            var endpoints = new RecorderEndpointDelegate<string>[count];
            for(int i = 0; i < count; i++)
            {
                NetworkStream clientStream = clients[i].GetStream();
                MessageChannelWriter<RecordRequest<string>> requestWriter = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), requestSerialize);
                MessageChannelReader<RecordReply<string>> replyReader = new(PipeReader.Create(clientStream), replyDeserialize);
                IAsyncEnumerator<RecordReply<string>> replies = replyReader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
                replyEnumerators.Add(replies);

                //One request/reply exchange at a time per connection: a proposer that abandons a slow
                //recorder mid-step still asks it again at the next step, and an overlapping write would
                //interleave frames on the shared pipe while the abandoned call's reply is still in flight.
                SemaphoreSlim gate = new(1, 1);
                gates.Add(gate);

                endpoints[i] = async (request, token) =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await requestWriter.WriteAsync(request, token).ConfigureAwait(false);
                        _ = await replies.MoveNextAsync().ConfigureAwait(false);

                        return replies.Current;
                    }
                    finally
                    {
                        _ = gate.Release();
                    }
                };
            }

            SeededPrioritySource source = new(21);
            QuePaxaProposer<string> proposer = new(endpoints, ProposerLane.For(First), source.Next, attemptsPerRecorder: 2);

            QuePaxaOutcome<string> outcome = await proposer.ProposeAsync(null, "wired", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsTrue(outcome.IsDecided);
            Assert.AreEqual("wired", outcome.Value);
            Assert.AreEqual(ProposerLane.For(First), outcome.DecidedBy);

            foreach(TcpClient client in clients)
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }

            await Task.WhenAll(nodeTasks).ConfigureAwait(false);
        }
        finally
        {
            await DisposeTransport(replyEnumerators, gates, clients, servers, listeners).ConfigureAwait(false);
        }
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, clients, servers, and enumerators are tracked in lists and disposed in the finally block.")]
    public async Task TheLeaderFastPathDecidesAtTheFirstStepOverSocketsWithPersistHooksWired()
    {
        const int count = 3;
        ProposerLane leader = ProposerLane.For(First);

        SerializeMessageDelegate<RecordRequest<string>> requestSerialize = QuePaxaMessageJson.CreateRequestSerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<RecordRequest<string>> requestDeserialize = QuePaxaMessageJson.CreateRequestDeserializer(element => element.GetString()!);
        SerializeMessageDelegate<RecordReply<string>> replySerialize = QuePaxaMessageJson.CreateReplySerializer<string>((writer, value) => writer.WriteStringValue(value));
        DeserializeMessageDelegate<RecordReply<string>> replyDeserialize = QuePaxaMessageJson.CreateReplyDeserializer(element => element.GetString()!);

        //One concurrent sink per node so the per-node persist counts can be asserted independently.
        var persisted = new ConcurrentQueue<QuePaxaRecorder<string>>[count];
        for(int i = 0; i < count; i++)
        {
            persisted[i] = new ConcurrentQueue<QuePaxaRecorder<string>>();
        }

        var listeners = new List<TcpListener>();
        var clients = new List<TcpClient>();
        var servers = new List<TcpClient>();
        var replyEnumerators = new List<IAsyncEnumerator<RecordReply<string>>>();
        var gates = new List<SemaphoreSlim>();
        var nodeTasks = new List<Task>();

        try
        {
            (listeners, clients, servers) = await ConnectedPairs(count).ConfigureAwait(false);

            for(int i = 0; i < count; i++)
            {
                QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(leader));
                NetworkStream serverStream = servers[i].GetStream();
                MessageChannelReader<RecordRequest<string>> requests = new(PipeReader.Create(serverStream), requestDeserialize);
                MessageChannelWriter<RecordReply<string>> replies = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), replySerialize);

                //The hook is the stand-in for a durable write, and it must fire before the reply crosses
                //the wire: a fast-path decision rests on the recorded first proposal surviving a crash.
                ConcurrentQueue<QuePaxaRecorder<string>> sink = persisted[i];
                PersistRecorderDelegate<string> persist = (recorder, _) =>
                {
                    sink.Enqueue(recorder);

                    return ValueTask.CompletedTask;
                };

                nodeTasks.Add(node.RunAsync(requests.ReadAllAsync(TestContext.CancellationToken), (reply, token) => replies.WriteAsync(reply, token), persist, TestContext.CancellationToken));
            }

            var endpoints = new RecorderEndpointDelegate<string>[count];
            for(int i = 0; i < count; i++)
            {
                NetworkStream clientStream = clients[i].GetStream();
                MessageChannelWriter<RecordRequest<string>> requestWriter = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), requestSerialize);
                MessageChannelReader<RecordReply<string>> replyReader = new(PipeReader.Create(clientStream), replyDeserialize);
                IAsyncEnumerator<RecordReply<string>> replies = replyReader.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
                replyEnumerators.Add(replies);

                SemaphoreSlim gate = new(1, 1);
                gates.Add(gate);

                endpoints[i] = async (request, token) =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await requestWriter.WriteAsync(request, token).ConfigureAwait(false);
                        _ = await replies.MoveNextAsync().ConfigureAwait(false);

                        return replies.Current;
                    }
                    finally
                    {
                        _ = gate.Release();
                    }
                };
            }

            SeededPrioritySource source = new(34);
            QuePaxaProposer<string> proposer = new(endpoints, leader, source.Next, attemptsPerRecorder: 2);

            QuePaxaOutcome<string> outcome = await proposer.ProposeAsync(leader, "fast", TestContext.CancellationToken).ConfigureAwait(false);

            //The one-round-trip commit is the register's whole quantitative claim, and here it happened
            //across a real wire: the reserved claim survived serialization and the decision came at the
            //round's first step.
            Assert.IsTrue(outcome.IsDecided);
            Assert.AreEqual("fast", outcome.Value);
            Assert.AreEqual(leader, outcome.DecidedBy);
            Assert.AreEqual(RecorderStep.RoundOnePhaseZero, outcome.DecidedAt);

            foreach(TcpClient client in clients)
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }

            await Task.WhenAll(nodeTasks).ConfigureAwait(false);
        }
        finally
        {
            await DisposeTransport(replyEnumerators, gates, clients, servers, listeners).ConfigureAwait(false);
        }

        //Every recorder recorded the leader's proposal, so every node's durability hook fired before its
        //reply left the process: an unpersisted first proposal never crossed the wire.
        for(int i = 0; i < count; i++)
        {
            Assert.IsGreaterThan(0, persisted[i].Count, $"Node {i} sent a reply without persisting a recorder change.");
        }
    }


    /// <summary>
    /// The versioned envelope's whole life cycle over sockets: a write decides version one on the bootstrap
    /// leader's fast path, the committed record travels to every host as a learn, and the next version's
    /// write completes against the hosts that learned it — three socket-served runners behind correlated
    /// frames, so a host fault would surface as a faulted call rather than a hung connection.
    /// </summary>
    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, clients, servers, and enumerators are tracked in lists and disposed in the finally block.")]
    public async Task AVersionedWriteDecidesOverSocketsAndTheNextVersionFollowsALearn()
    {
        const int count = 3;

        SerializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> requestSerialize =
            QuePaxaMessageJson.CreateVersionedRequestSerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>((writer, value) => writer.WriteStringValue(value)));
        DeserializeMessageDelegate<VersionedRecordRequest<VersionedValue<string>>> requestDeserialize =
            QuePaxaMessageJson.CreateVersionedRequestDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(element => element.GetString()!));
        SerializeMessageDelegate<VersionedRecordReply<VersionedValue<string>>> replySerialize =
            QuePaxaMessageJson.CreateVersionedReplySerializer(QuePaxaMessageJson.CreateVersionedValueWriter<string>((writer, value) => writer.WriteStringValue(value)));
        DeserializeMessageDelegate<VersionedRecordReply<VersionedValue<string>>> replyDeserialize =
            QuePaxaMessageJson.CreateVersionedReplyDeserializer(QuePaxaMessageJson.CreateVersionedValueReader<string>(element => element.GetString()!));

        ImmutableArray<ReplicaId> order = [First, Second, Third];
        QuePaxaLeaderSchedule schedule = new(HedgingSchedule.Create(order, TimeSpan.FromMilliseconds(20)));

        var runners = new QuePaxaVersionedRunner<string>[count];
        var runTasks = new Task[count];
        for(int i = 0; i < count; i++)
        {
            runners[i] = new QuePaxaVersionedRunner<string>(new QuePaxaVersionedNode<string>(schedule));
            runTasks[i] = runners[i].RunAsync(cancellationToken: TestContext.CancellationToken);
        }

        var listeners = new List<TcpListener>();
        var clients = new List<TcpClient>();
        var servers = new List<TcpClient>();
        var responseEnumerators = new List<IAsyncEnumerator<CorrelatedFrame>>();
        var gates = new List<SemaphoreSlim>();
        var servingTasks = new List<Task>();

        try
        {
            (listeners, clients, servers) = await ConnectedPairs(count).ConfigureAwait(false);

            for(int i = 0; i < count; i++)
            {
                QuePaxaVersionedRunner<string> runner = runners[i];
                NetworkStream serverStream = servers[i].GetStream();
                MessageChannelReader<CorrelatedFrame> serverRequests = new(PipeReader.Create(serverStream), ReadFrame);
                MessageChannelWriter<CorrelatedFrame> serverResponses = new(PipeWriter.Create(serverStream, new StreamPipeWriterOptions(leaveOpen: true)), WriteFrame);
                servingTasks.Add(Task.Run(async () =>
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
                }, TestContext.CancellationToken));
            }

            var endpoints = new VersionedRecorderEndpointDelegate<VersionedValue<string>>[count];
            for(int i = 0; i < count; i++)
            {
                NetworkStream clientStream = clients[i].GetStream();
                MessageChannelWriter<CorrelatedFrame> clientRequests = new(PipeWriter.Create(clientStream, new StreamPipeWriterOptions(leaveOpen: true)), WriteFrame);
                MessageChannelReader<CorrelatedFrame> clientResponses = new(PipeReader.Create(clientStream), ReadFrame);
                IAsyncEnumerator<CorrelatedFrame> responses = clientResponses.ReadAllAsync(TestContext.CancellationToken).GetAsyncEnumerator(TestContext.CancellationToken);
                responseEnumerators.Add(responses);

                SemaphoreSlim gate = new(1, 1);
                gates.Add(gate);

                int nextId = 1;
                endpoints[i] = async (request, token) =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
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
                    }
                    finally
                    {
                        _ = gate.Release();
                    }
                };
            }

            SeededPrioritySource source = new(55);
            QuePaxaVersionedRegister<string> register = new(endpoints, First, schedule, source.Next, attemptsPerRecorder: 2, TimeProvider.System);

            QuePaxaWriteOutcome<string> firstWrite = await register.TryWriteAsync("one", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, firstWrite.Status);
            Assert.AreEqual(RegisterVersion.First, firstWrite.Version);
            Assert.AreEqual("one", firstWrite.Value);
            Assert.IsTrue(firstWrite.TookFastPath, "The bootstrap leader's reserved claim did not survive the wire.");

            //Dissemination is explicit, as in a deployment: every host learns the committed record through
            //its runner's queue, and only then can the next version gather a quorum.
            VersionedValue<string> committed = new(firstWrite.Version, firstWrite.Writer!.Value, firstWrite.Value!);
            for(int i = 0; i < count; i++)
            {
                Assert.IsTrue(await runners[i].LearnAsync(committed, TestContext.CancellationToken).ConfigureAwait(false));
            }

            QuePaxaWriteOutcome<string> secondWrite = await register.TryWriteAsync("two", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, secondWrite.Status);
            Assert.AreEqual(new RegisterVersion(2UL), secondWrite.Version);
            Assert.AreEqual("two", secondWrite.Value);

            foreach(TcpClient client in clients)
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }

            await Task.WhenAll(servingTasks).ConfigureAwait(false);
        }
        finally
        {
            await DisposeTransport(responseEnumerators, gates, clients, servers, listeners).ConfigureAwait(false);

            foreach(QuePaxaVersionedRunner<string> runner in runners)
            {
                runner.Complete();
            }

            await Task.WhenAll(runTasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }
    }


    private async Task<(List<TcpListener> Listeners, List<TcpClient> Clients, List<TcpClient> Servers)> ConnectedPairs(int count)
    {
        var listeners = new List<TcpListener>();
        var clients = new List<TcpClient>();

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

        List<TcpClient> servers = [.. await Task.WhenAll(acceptTasks).ConfigureAwait(false)];

        return (listeners, clients, servers);
    }


    private static async Task DisposeTransport<TReply>(
        List<IAsyncEnumerator<TReply>> enumerators,
        List<SemaphoreSlim> gates,
        List<TcpClient> clients,
        List<TcpClient> servers,
        List<TcpListener> listeners)
    {
        foreach(IAsyncEnumerator<TReply> enumerator in enumerators)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        foreach(SemaphoreSlim gate in gates)
        {
            gate.Dispose();
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


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
