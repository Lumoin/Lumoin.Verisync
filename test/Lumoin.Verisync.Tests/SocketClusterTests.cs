using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;

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
                nodeTasks.Add(node.RunAsync(requests.ReadAllAsync(TestContext.CancellationToken), (reply, token) => replies.WriteAsync(reply, token), TestContext.CancellationToken));
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
}
