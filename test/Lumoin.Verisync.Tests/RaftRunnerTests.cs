using Lumoin.Verisync.Core;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Drives the message-driven runner over an in-memory three-node cluster: the send delegate routes each
/// envelope to the target runner's <see cref="RaftRunner{TCommand}.SubmitAsync"/>, and quiescence is awaited
/// by polling the per-node applied collections under a bounded timeout (never an unbounded spin). Covers
/// election, propose-and-commit, the non-leader propose fault, persist-before-send ordering, and restart
/// catch-up.
/// </summary>
[TestClass]
internal sealed class RaftRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static ReplicaId N1 { get; } = Replica(1);
    private static ReplicaId N2 { get; } = Replica(2);
    private static ReplicaId N3 { get; } = Replica(3);

    private static ImmutableArray<ReplicaId> Members { get; } = [N1, N2, N3];

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task ElectionTriggerElectsExactlyOneLeaderAndTheOthersFollow()
    {
        //One triggered campaign on N1 must yield a single leader for the term and demote the peers to followers.
        Cluster cluster = StartCluster(TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable clusterCleanup = cluster.ConfigureAwait(false);

        await cluster.Runner(N1).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);

        await WaitUntilAsync(() => cluster.Node(N1).Role == RaftRole.Leader, TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(
            () => cluster.Node(N2).Role == RaftRole.Follower && cluster.Node(N3).Role == RaftRole.Follower,
            TestContext.CancellationToken).ConfigureAwait(false);

        int leaders = Members.Count(id => cluster.Node(id).Role == RaftRole.Leader);
        Assert.AreEqual(1, leaders);
        Assert.AreEqual(N1, cluster.Node(N1).Id);
    }


    [TestMethod]
    public async Task ProposeOnTheLeaderCommitsAndAppliesTheSameSequenceOnEveryNode()
    {
        //Three proposals through the leader must commit on all three nodes, and every node's apply seam must
        //observe the identical (index, command) sequence in order: no gaps, no reordering, no duplicates.
        Cluster cluster = StartCluster(TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable clusterCleanup = cluster.ConfigureAwait(false);

        await cluster.Runner(N1).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(() => cluster.Node(N1).Role == RaftRole.Leader, TestContext.CancellationToken).ConfigureAwait(false);

        long first = await cluster.Runner(N1).ProposeAsync("alpha", TestContext.CancellationToken).ConfigureAwait(false);
        long second = await cluster.Runner(N1).ProposeAsync("beta", TestContext.CancellationToken).ConfigureAwait(false);
        long third = await cluster.Runner(N1).ProposeAsync("gamma", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);
        Assert.AreEqual(3, third);

        //Several heartbeats carry the advanced LeaderCommit out so the followers apply too.
        await DriveUntilAllAppliedAsync(cluster, 3, TestContext.CancellationToken).ConfigureAwait(false);

        (long Index, string Command)[] expected = [(1, "alpha"), (2, "beta"), (3, "gamma")];
        foreach(ReplicaId id in Members)
        {
            CollectionAssert.AreEqual(expected, cluster.Applied(id).ToArray());
        }
    }


    [TestMethod]
    public async Task ProposeOnAFollowerFaultsWithInvalidOperation()
    {
        //A propose addressed to a non-leader must fault its own task with InvalidOperationException and never
        //tear down the consumer loop.
        Cluster cluster = StartCluster(TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable clusterCleanup = cluster.ConfigureAwait(false);

        await cluster.Runner(N1).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(() => cluster.Node(N2).Role == RaftRole.Follower, TestContext.CancellationToken).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => cluster.Runner(N2).ProposeAsync("nope", TestContext.CancellationToken)).ConfigureAwait(false);

        //The faulted propose left the follower's loop alive: a later election it wins still functions.
        await cluster.Runner(N2).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(() => cluster.Node(N2).Role == RaftRole.Leader, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task PersistSnapshotIsObservedBeforeTheCorrespondingOutboundSendOnTheSameNode()
    {
        //The HANDLE -> PERSIST -> APPLY -> SEND sequence means that on the leader, the persist of the proposed
        //state lands before any outbound AppendEntries that carries it. A single shared event log records each
        //delegate firing tagged with the node, and the first send for the leader must be preceded by a persist.
        var events = new ConcurrentQueue<string>();
        Cluster cluster = StartCluster(TestContext.CancellationToken, events);
        await using ConfiguredAsyncDisposable clusterCleanup = cluster.ConfigureAwait(false);

        await cluster.Runner(N1).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(() => cluster.Node(N1).Role == RaftRole.Leader, TestContext.CancellationToken).ConfigureAwait(false);

        await cluster.Runner(N1).ProposeAsync("durable", TestContext.CancellationToken).ConfigureAwait(false);
        await DriveUntilAllAppliedAsync(cluster, 1, TestContext.CancellationToken).ConfigureAwait(false);

        //Restrict to the leader's own events; for that node, no send may appear without a persist before it.
        string[] leaderEvents = events.Where(e => e.EndsWith(":1", StringComparison.Ordinal)).ToArray();
        int firstSend = Array.FindIndex(leaderEvents, e => e.StartsWith("send:", StringComparison.Ordinal));
        Assert.IsGreaterThan(-1, firstSend);
        int persistBefore = Array.FindLastIndex(leaderEvents, firstSend, e => e.StartsWith("persist:", StringComparison.Ordinal));
        Assert.IsGreaterThan(-1, persistBefore);
    }


    [TestMethod]
    public async Task ARestartedFollowerCatchesUpAndAppliesTheFullSequence()
    {
        //A follower restarted from its last persisted state (a fresh node via FromState behind a fresh runner)
        //rejoins, and a heartbeat catches it up so it applies the same committed content. Apply is at-least-once
        //across restart, so convergence of the applied content — not the invocation count — is the assertion.
        var events = new ConcurrentQueue<string>();
        Cluster cluster = StartCluster(TestContext.CancellationToken, events);
        await using ConfiguredAsyncDisposable clusterCleanup = cluster.ConfigureAwait(false);

        await cluster.Runner(N1).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(() => cluster.Node(N1).Role == RaftRole.Leader, TestContext.CancellationToken).ConfigureAwait(false);

        await cluster.Runner(N1).ProposeAsync("one", TestContext.CancellationToken).ConfigureAwait(false);
        await cluster.Runner(N1).ProposeAsync("two", TestContext.CancellationToken).ConfigureAwait(false);
        await DriveUntilAllAppliedAsync(cluster, 2, TestContext.CancellationToken).ConfigureAwait(false);

        //Restart N2 from its last captured persisted state behind a fresh runner and applied collection.
        await cluster.RestartFromLastPersistedAsync(N2, TestContext.CancellationToken).ConfigureAwait(false);

        //Heartbeats from the leader catch the restarted follower up; it converges on the same applied content.
        await DriveUntilAppliedAsync(cluster, N2, 2, TestContext.CancellationToken).ConfigureAwait(false);

        (long Index, string Command)[] expected = [(1, "one"), (2, "two")];
        CollectionAssert.AreEqual(expected, cluster.Applied(N2).ToArray());
        CollectionAssert.AreEqual(expected, cluster.Applied(N1).ToArray());
    }


    [TestMethod]
    public async Task ApplyIsInOrderWithNoGapsOrDuplicatesWithinAProcessLifetime()
    {
        //Within one process lifetime the apply seam is exactly-once and in order: the observed indices on every
        //node must be 1, 2, ..., n with no repeats and no holes.
        Cluster cluster = StartCluster(TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable clusterCleanup = cluster.ConfigureAwait(false);

        await cluster.Runner(N1).TriggerElectionAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await WaitUntilAsync(() => cluster.Node(N1).Role == RaftRole.Leader, TestContext.CancellationToken).ConfigureAwait(false);

        for(int i = 0; i < 5; i++)
        {
            await cluster.Runner(N1).ProposeAsync($"cmd-{i}", TestContext.CancellationToken).ConfigureAwait(false);
        }

        await DriveUntilAllAppliedAsync(cluster, 5, TestContext.CancellationToken).ConfigureAwait(false);

        foreach(ReplicaId id in Members)
        {
            long[] indices = cluster.Applied(id).Select(entry => entry.Index).ToArray();
            long[] expected = [1, 2, 3, 4, 5];
            CollectionAssert.AreEqual(expected, indices);
        }
    }


    //--- Harness --------------------------------------------------------------------------------------------

    //Spins up a runner per member wired into a shared in-memory cluster, returning a handle that owns the run
    //tasks and tears them down on disposal.
    private static Cluster StartCluster(CancellationToken cancellationToken, ConcurrentQueue<string>? events = null)
    {
        Cluster cluster = new(Members, events);
        cluster.Start(cancellationToken);

        return cluster;
    }


    //Drives heartbeats from whichever node is leader until every node has applied at least the target count,
    //bounded by the timeout. Heartbeats are host-triggered, so the test stands in for the host's timer.
    private static async Task DriveUntilAllAppliedAsync(Cluster cluster, int target, CancellationToken cancellationToken)
    {
        await DriveAsync(
            cluster,
            () => Members.All(id => cluster.Applied(id).Count >= target),
            cancellationToken).ConfigureAwait(false);
    }


    //Drives heartbeats until one specific node has applied at least the target count.
    private static async Task DriveUntilAppliedAsync(Cluster cluster, ReplicaId node, int target, CancellationToken cancellationToken)
    {
        await DriveAsync(
            cluster,
            () => cluster.Applied(node).Count >= target,
            cancellationToken).ConfigureAwait(false);
    }


    //The shared drive loop: trigger a heartbeat from the current leader, then check the predicate, repeating
    //under a bounded timeout. The timeout — not a fixed sleep — is the synchronization boundary.
    private static async Task DriveAsync(Cluster cluster, Func<bool> satisfied, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while(stopwatch.Elapsed < Timeout)
        {
            if(satisfied())
            {
                return;
            }

            foreach(ReplicaId id in Members)
            {
                if(cluster.Node(id).Role == RaftRole.Leader)
                {
                    await cluster.Runner(id).TriggerHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        Assert.IsTrue(satisfied(), "The cluster did not reach the expected applied state within the timeout.");
    }


    //Polls a predicate under the bounded timeout without driving heartbeats; for conditions that resolve from
    //already-queued work (election outcomes, role transitions).
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while(stopwatch.Elapsed < Timeout)
        {
            if(condition())
            {
                return;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        Assert.IsTrue(condition(), "The cluster did not reach the expected condition within the timeout.");
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    //An in-memory cluster of runners keyed by replica id. The send delegate routes envelopes to the target
    //runner's SubmitAsync; per-node applied sequences, last persisted snapshots, and an optional shared event
    //log let the tests observe convergence and ordering.
    private sealed class Cluster: IAsyncDisposable
    {
        private readonly ImmutableArray<ReplicaId> members;
        private readonly ConcurrentQueue<string>? events;
        private readonly Dictionary<ReplicaId, RaftNode<string>> nodes = [];
        private readonly Dictionary<ReplicaId, RaftRunner<string>> runners = [];
        private readonly Dictionary<ReplicaId, Task> runTasks = [];
        private readonly Dictionary<ReplicaId, ConcurrentQueue<(long Index, string Command)>> applied = [];
        private readonly Dictionary<ReplicaId, RaftNodeState<string>> lastPersisted = [];
        private readonly Dictionary<ReplicaId, int> tags = [];


        public Cluster(ImmutableArray<ReplicaId> members, ConcurrentQueue<string>? events)
        {
            this.members = members;
            this.events = events;
            for(int i = 0; i < members.Length; i++)
            {
                ReplicaId id = members[i];
                nodes[id] = new RaftNode<string>(id, members);
                runners[id] = new RaftRunner<string>(nodes[id]);
                applied[id] = new ConcurrentQueue<(long, string)>();
                tags[id] = i + 1;
            }
        }


        public RaftNode<string> Node(ReplicaId id) => nodes[id];


        public RaftRunner<string> Runner(ReplicaId id) => runners[id];


        public ConcurrentQueue<(long Index, string Command)> Applied(ReplicaId id) => applied[id];


        public void Start(CancellationToken cancellationToken)
        {
            foreach(ReplicaId id in members)
            {
                runTasks[id] = runners[id].RunAsync(MakeSend(id), MakePersist(id), MakeApply(id), cancellationToken);
            }
        }


        //Restarts a node from its last persisted state behind a fresh node, runner, and applied queue: the
        //commit index is volatile so the restored node rediscovers it from the leader's heartbeats.
        public async Task RestartFromLastPersistedAsync(ReplicaId id, CancellationToken cancellationToken)
        {
            runners[id].Complete();
            await runTasks[id].ConfigureAwait(false);

            RaftNodeState<string> state = lastPersisted[id];
            nodes[id] = RaftNode<string>.FromState(id, members, state);
            runners[id] = new RaftRunner<string>(nodes[id]);
            applied[id] = new ConcurrentQueue<(long, string)>();
            runTasks[id] = runners[id].RunAsync(MakeSend(id), MakePersist(id), MakeApply(id), cancellationToken);
        }


        public async ValueTask DisposeAsync()
        {
            foreach(ReplicaId id in members)
            {
                runners[id].Complete();
            }

            foreach(ReplicaId id in members)
            {
                try
                {
                    await runTasks[id].ConfigureAwait(false);
                }
                catch(OperationCanceledException)
                {
                    //A cancelled run is an expected shutdown path; the test owns the token's lifetime.
                }
            }
        }


        private SendRaftEnvelopeDelegate<string> MakeSend(ReplicaId from)
        {
            int tag = tags[from];

            return async (to, envelope, cancellationToken) =>
            {
                events?.Enqueue($"send:{tag}");

                try
                {
                    //Route straight to the target runner's producer queue; an unbounded channel write does not block.
                    await runners[to].SubmitAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch(ChannelClosedException)
                {
                    //A completed peer is a stopped node; dropping the message is exactly what the network does.
                }
            };
        }


        private PersistRaftStateDelegate<string> MakePersist(ReplicaId id)
        {
            int tag = tags[id];

            return (state, _) =>
            {
                events?.Enqueue($"persist:{tag}");
                lastPersisted[id] = state;

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
    }
}
