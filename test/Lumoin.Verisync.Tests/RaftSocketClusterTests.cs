using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Drives the runner over a real localhost socket mesh with <see cref="RaftJson"/> envelope codecs and persist
/// hooks, mirroring <see cref="SocketClusterTests"/> plumbing and cleanup discipline: every node owns one
/// listener, one directed outbound link to each peer, and an inbound reader loop per peer that feeds the local
/// runner. The class must survive three consecutive full runs, so the timeouts are generous and no fixed sleep
/// is used as synchronization.
/// </summary>
[TestClass]
internal sealed class RaftSocketClusterTests
{
    private const int NodeCount = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, sockets, runners, and reader loops are tracked in the mesh and disposed in its DisposeAsync.")]
    public async Task ThreeSocketNodesElectProposeAndApplyTheSameSequence()
    {
        //An election then three proposals through the leader must replicate over real sockets so every node
        //applies the identical command sequence.
        SocketMesh mesh = await SocketMesh.BuildAsync(NodeCount, TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable meshCleanup = mesh.ConfigureAwait(false);

        ReplicaId leader = await ElectAnyLeaderAsync(mesh, TestContext.CancellationToken).ConfigureAwait(false);

        await mesh.Runner(leader).ProposeAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        await mesh.Runner(leader).ProposeAsync("b", TestContext.CancellationToken).ConfigureAwait(false);
        await mesh.Runner(leader).ProposeAsync("c", TestContext.CancellationToken).ConfigureAwait(false);

        await DriveUntilAsync(mesh, () => mesh.Members.All(id => Commands(mesh.Applied(id)).Length >= 3), TestContext.CancellationToken).ConfigureAwait(false);

        string[] expected = ["a", "b", "c"];
        foreach(ReplicaId id in mesh.Members)
        {
            CollectionAssert.AreEqual(expected, Commands(mesh.Applied(id)));
        }
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "All listeners, sockets, runners, and reader loops are tracked in the mesh and disposed in its DisposeAsync.")]
    public async Task ARestartedSocketFollowerConvergesToTheSameAppliedContent()
    {
        //After commitment, stop one follower's runner, restore a fresh node from its captured persisted state,
        //reconnect, heartbeat, and assert it converges to the same applied content. Apply is at-least-once
        //across restart, so the assertion is on the final applied LOG CONTENT, never the invocation count.
        SocketMesh mesh = await SocketMesh.BuildAsync(NodeCount, TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable meshCleanup = mesh.ConfigureAwait(false);

        ReplicaId leader = await ElectAnyLeaderAsync(mesh, TestContext.CancellationToken).ConfigureAwait(false);

        await mesh.Runner(leader).ProposeAsync("x", TestContext.CancellationToken).ConfigureAwait(false);
        await mesh.Runner(leader).ProposeAsync("y", TestContext.CancellationToken).ConfigureAwait(false);
        await DriveUntilAsync(mesh, () => mesh.Members.All(id => Commands(mesh.Applied(id)).Length >= 2), TestContext.CancellationToken).ConfigureAwait(false);

        ReplicaId follower = mesh.Members.First(id => id != leader);
        await mesh.RestartFromLastPersistedAsync(follower, TestContext.CancellationToken).ConfigureAwait(false);

        //Heartbeats from the leader catch the restarted follower up over the reconnected link.
        await DriveUntilAsync(mesh, () => Commands(mesh.Applied(follower)).Length >= 2, TestContext.CancellationToken).ConfigureAwait(false);

        string[] expected = ["x", "y"];
        CollectionAssert.AreEqual(expected, Commands(mesh.Applied(follower)));
        CollectionAssert.AreEqual(expected, Commands(mesh.Applied(leader)));

        //The persist hook fired on the restored node too: a durable host always saw the state before output.
        Assert.IsGreaterThan(0, mesh.PersistCount(follower));
    }


    //--- Helpers --------------------------------------------------------------------------------------------

    //Triggers an election on the first node and drives heartbeats until exactly one node is leader, returning it.
    private static async Task<ReplicaId> ElectAnyLeaderAsync(SocketMesh mesh, CancellationToken cancellationToken)
    {
        await mesh.Runner(mesh.Members[0]).TriggerElectionAsync(cancellationToken).ConfigureAwait(false);
        await DriveUntilAsync(mesh, () => mesh.Members.Any(id => mesh.Node(id).Role == RaftRole.Leader), cancellationToken).ConfigureAwait(false);

        return mesh.Members.First(id => mesh.Node(id).Role == RaftRole.Leader);
    }


    //Drives heartbeats from the current leader and polls the predicate under the bounded timeout. The timeout —
    //not a fixed sleep — is the synchronization boundary; the short delay is only loop back-off.
    private static async Task DriveUntilAsync(SocketMesh mesh, Func<bool> satisfied, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while(stopwatch.Elapsed < Timeout)
        {
            if(satisfied())
            {
                return;
            }

            foreach(ReplicaId id in mesh.Members)
            {
                if(mesh.Node(id).Role == RaftRole.Leader)
                {
                    await mesh.Runner(id).TriggerHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        Assert.IsTrue(satisfied(), "The socket cluster did not reach the expected state within the timeout.");
    }


    //Collapses the applied entries to one command per committed index, in index order: apply is at-least-once
    //across a restart, so de-duplicating by index keeps the comparison about converged content, not counts.
    private static string[] Commands(IReadOnlyCollection<(long Index, string Command)> applied)
    {
        return applied
            .GroupBy(entry => entry.Index)
            .OrderBy(group => group.Key)
            .Select(group => group.First().Command)
            .ToArray();
    }


    //A localhost socket mesh of runners. Each node has its own listener and a directed outbound link to every
    //peer; a peer's inbound link is read by a loop that feeds the local runner's SubmitAsync. Everything is
    //tracked so DisposeAsync can tear it all down even if a test fails mid-flight.
    private sealed class SocketMesh: IAsyncDisposable
    {
        private static readonly SerializeMessageDelegate<RaftEnvelope<string>> SerializeEnvelope =
            RaftJson.CreateEnvelopeSerializer<string>((writer, value) => writer.WriteStringValue(value));
        private static readonly DeserializeMessageDelegate<RaftEnvelope<string>> DeserializeEnvelope =
            RaftJson.CreateEnvelopeDeserializer<string>(element => element.GetString()!);

        private readonly ImmutableArray<ReplicaId> members;
        private readonly CancellationToken cancellationToken;
        private readonly List<TcpListener> listeners = [];
        private readonly List<TcpClient> sockets = [];
        private readonly List<Task> readerLoops = [];
        private readonly Dictionary<ReplicaId, RaftNode<string>> nodes = [];
        private readonly Dictionary<ReplicaId, RaftRunner<string>> runners = [];
        private readonly Dictionary<ReplicaId, Task> runTasks = [];
        private readonly Dictionary<ReplicaId, ConcurrentQueue<(long Index, string Command)>> applied = [];
        private readonly Dictionary<ReplicaId, RaftNodeState<string>> lastPersisted = [];
        private readonly Dictionary<ReplicaId, int> persistCounts = [];
        private readonly Dictionary<(ReplicaId From, ReplicaId To), MessageChannelWriter<RaftEnvelope<string>>> outbound = [];


        private SocketMesh(ImmutableArray<ReplicaId> members, CancellationToken cancellationToken)
        {
            this.members = members;
            this.cancellationToken = cancellationToken;
        }


        public ImmutableArray<ReplicaId> Members => members;


        public RaftNode<string> Node(ReplicaId id) => nodes[id];


        public RaftRunner<string> Runner(ReplicaId id) => runners[id];


        public ConcurrentQueue<(long Index, string Command)> Applied(ReplicaId id) => applied[id];


        public int PersistCount(ReplicaId id) => persistCounts[id];


        //Stands up the full mesh: a listener per node, a directed socket per ordered pair, the reader loops
        //that feed each node, and a started runner per node wired to send through the outbound writers.
        public static async Task<SocketMesh> BuildAsync(int count, CancellationToken cancellationToken)
        {
            ImmutableArray<ReplicaId> members = [.. Enumerable.Range(1, count).Select(i => Replica((byte)i))];
            SocketMesh mesh = new(members, cancellationToken);

            try
            {
                await mesh.ConnectAndStartAsync().ConfigureAwait(false);

                return mesh;
            }
            catch
            {
                await mesh.DisposeAsync().ConfigureAwait(false);

                throw;
            }
        }


        //Restarts a node from its last persisted state behind a fresh node, runner, and applied queue, leaving
        //the existing socket links in place so heartbeats flow straight back to the rejoined runner.
        public async Task RestartFromLastPersistedAsync(ReplicaId id, CancellationToken token)
        {
            runners[id].Complete();
            await runTasks[id].ConfigureAwait(false);

            RaftNodeState<string> state = lastPersisted[id];
            nodes[id] = RaftNode<string>.FromState(id, members, state);
            runners[id] = new RaftRunner<string>(nodes[id]);
            applied[id] = new ConcurrentQueue<(long, string)>();
            runTasks[id] = runners[id].RunAsync(MakeSend(id), MakePersist(id), MakeApply(id), token);
        }


        public async ValueTask DisposeAsync()
        {
            foreach(RaftRunner<string> runner in runners.Values)
            {
                runner.Complete();
            }

            foreach(Task runTask in runTasks.Values)
            {
                await SwallowAsync(runTask).ConfigureAwait(false);
            }

            //Shutting the send half lets every reader loop observe its peer ending and complete cleanly.
            foreach(TcpClient socket in sockets)
            {
                try
                {
                    socket.Client.Shutdown(SocketShutdown.Send);
                }
                catch(SocketException)
                {
                    //The peer may already be gone; tearing down regardless.
                }
                catch(ObjectDisposedException)
                {
                    //Already disposed by an earlier failure path.
                }
            }

            foreach(Task readerLoop in readerLoops)
            {
                await SwallowAsync(readerLoop).ConfigureAwait(false);
            }

            foreach(TcpClient socket in sockets)
            {
                socket.Dispose();
            }

            foreach(TcpListener listener in listeners)
            {
                listener.Dispose();
            }
        }


        private async Task ConnectAndStartAsync()
        {
            //One listener per node on an ephemeral loopback port.
            var ports = new Dictionary<ReplicaId, int>();
            foreach(ReplicaId id in members)
            {
                TcpListener listener = new(IPAddress.Loopback, 0);
                listener.Start();
                listeners.Add(listener);
                ports[id] = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            //For every ordered pair (from -> to) the connector dials the acceptor's listener. Each node accepts
            //exactly one inbound socket per peer; the accept and connect tasks run together to avoid a deadlock.
            var acceptByListener = new Dictionary<ReplicaId, List<Task<TcpClient>>>();
            for(int i = 0; i < members.Length; i++)
            {
                acceptByListener[members[i]] = [];
                for(int peer = 0; peer < members.Length - 1; peer++)
                {
                    acceptByListener[members[i]].Add(listeners[i].AcceptTcpClientAsync(cancellationToken).AsTask());
                }
            }

            var connectByPair = new Dictionary<(ReplicaId From, ReplicaId To), Task<TcpClient>>();
            foreach(ReplicaId from in members)
            {
                foreach(ReplicaId to in members)
                {
                    if(from == to)
                    {
                        continue;
                    }

                    TcpClient client = new();
                    sockets.Add(client);
                    connectByPair[(from, to)] = ConnectAsync(client, ports[to]);
                }
            }

            await Task.WhenAll(connectByPair.Values).ConfigureAwait(false);
            foreach(List<Task<TcpClient>> accepts in acceptByListener.Values)
            {
                await Task.WhenAll(accepts).ConfigureAwait(false);
            }

            //The connector side owns the outbound writer for its directed edge.
            foreach(((ReplicaId From, ReplicaId To) pair, Task<TcpClient> connectTask) in connectByPair)
            {
                TcpClient client = await connectTask.ConfigureAwait(false);
                NetworkStream stream = client.GetStream();
                outbound[pair] = new MessageChannelWriter<RaftEnvelope<string>>(
                    PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)), SerializeEnvelope);
            }

            //Construct nodes, runners, sinks before reader loops so a freshly delivered envelope finds a runner.
            for(int i = 0; i < members.Length; i++)
            {
                ReplicaId id = members[i];
                nodes[id] = new RaftNode<string>(id, members);
                runners[id] = new RaftRunner<string>(nodes[id]);
                applied[id] = new ConcurrentQueue<(long, string)>();
                persistCounts[id] = 0;
            }

            //Each accepted inbound socket gets a reader loop that feeds the owning node's runner.
            for(int i = 0; i < members.Length; i++)
            {
                ReplicaId owner = members[i];
                foreach(Task<TcpClient> acceptTask in acceptByListener[owner])
                {
                    TcpClient server = await acceptTask.ConfigureAwait(false);
                    sockets.Add(server);
                    readerLoops.Add(ReadInboundAsync(owner, server));
                }
            }

            //Start the runners last so their consumer loops are live before any heartbeat or propose is issued.
            foreach(ReplicaId id in members)
            {
                runTasks[id] = runners[id].RunAsync(MakeSend(id), MakePersist(id), MakeApply(id), cancellationToken);
            }
        }


        //Reads framed envelopes off one inbound socket and submits each to the owning node's current runner; a
        //runner restart swaps runners[owner], so the lookup happens per message rather than being captured.
        private async Task ReadInboundAsync(ReplicaId owner, TcpClient server)
        {
            NetworkStream stream = server.GetStream();
            MessageChannelReader<RaftEnvelope<string>> reader = new(PipeReader.Create(stream), DeserializeEnvelope);
            await foreach(RaftEnvelope<string> envelope in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await runners[owner].SubmitAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch(ChannelClosedException)
                {
                    //The restart window: the owner's runner has completed and its replacement is not yet
                    //swapped in. Dropping the message models a stopped node; the leader re-sends.
                }
            }
        }


        private SendRaftEnvelopeDelegate<string> MakeSend(ReplicaId from)
        {
            return (to, envelope, token) => outbound[(from, to)].WriteAsync(envelope, token);
        }


        private PersistRaftStateDelegate<string> MakePersist(ReplicaId id)
        {
            return (state, _) =>
            {
                lastPersisted[id] = state;
                persistCounts[id]++;

                return ValueTask.CompletedTask;
            };
        }


        private ApplyCommittedDelegate<string> MakeApply(ReplicaId id)
        {
            ConcurrentQueue<(long Index, string Command)> sink = applied[id];

            return (index, command, _) =>
            {
                sink.Enqueue((index, command));

                return ValueTask.CompletedTask;
            };
        }


        private async Task<TcpClient> ConnectAsync(TcpClient client, int port)
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);

            return client;
        }


        private static async Task SwallowAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
                //Expected on token cancellation or a completed channel during teardown.
            }
            catch(InvalidOperationException)
            {
                //A reader loop whose peer vanished mid-frame ends this way during teardown.
            }
            catch(IOException)
            {
                //A socket torn down under an in-flight read or write surfaces as an IO error during teardown.
            }
        }


        private static ReplicaId Replica(byte id)
        {
            Span<byte> buffer = stackalloc byte[ReplicaId.Size];
            buffer[0] = id;

            return ReplicaId.FromSpan(buffer);
        }
    }
}
