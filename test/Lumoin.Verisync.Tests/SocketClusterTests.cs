using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

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

                //The hook records each state-changing acceptor before its reply is sent: a stand-in for the
                //fsync a durable host would do. RunAsync only invokes it when the request changed the acceptor.
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
            CollectionAssert.AreEqual(expected, outerLengths.ToArray());
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
}
