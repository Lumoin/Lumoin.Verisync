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
/// election, propose-and-commit, the non-leader propose fault, persist-before-send ordering, restart
/// catch-up, the abandonment of pending and in-flight proposals when the loop faults or is cancelled, and the
/// fail-fast producer contract after <see cref="RaftRunner{TCommand}.Complete"/>.
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


    [TestMethod]
    public async Task AFaultingHookFaultsThePendingAndInFlightProposalsAndLaterProposalsFailFast()
    {
        //A persist hook that throws (the documented fail-closed path for a broken durable store) ends the
        //loop; the in-flight proposal and every queued one must fault instead of hanging forever, and a
        //proposal issued after the fault must fail fast on the completed channel. Every await on a proposal
        //task is bounded through WaitAsync so a regression to the orphaning behaviour fails instead of
        //hanging the suite.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        RaftNode<string> node = new(N1, [N1]);
        RaftRunner<string> runner = new(node);

        //The election persists once before any proposal, so the gate arms on the second persist — the first
        //proposal's — and the trigger/propose FIFO order makes the sequence deterministic without polling.
        int persistCalls = 0;
        TaskCompletionSource persistEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource persistGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PersistRaftStateDelegate<string> persist = async (_, _) =>
        {
            if(Interlocked.Increment(ref persistCalls) == 1)
            {
                return;
            }

            persistEntered.TrySetResult();
            await persistGate.Task.ConfigureAwait(false);

            throw new IOException("The durable store failed.");
        };

        Task run = runner.RunAsync(DiscardSend, persist, null, cancellationToken);

        //A lone node is its own majority, so the triggered election makes it leader before the proposals.
        await runner.TriggerElectionAsync(cancellationToken).ConfigureAwait(false);

        Task<long> inFlight = runner.ProposeAsync("first", cancellationToken);
        await persistEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        //The loop is parked inside the first proposal's persist, so the second proposal stays queued.
        Task<long> queued = runner.ProposeAsync("second", cancellationToken);
        Assert.IsFalse(inFlight.IsCompleted);
        Assert.IsFalse(queued.IsCompleted);

        persistGate.SetResult();

        //The loop surfaces the store failure, and both proposals fault with the abandonment exception that
        //carries the same loop failure as its inner exception.
        IOException loopFault = await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(cancellationToken)).ConfigureAwait(false);
        InvalidOperationException inFlightFault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inFlight.WaitAsync(cancellationToken)).ConfigureAwait(false);
        InvalidOperationException queuedFault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queued.WaitAsync(cancellationToken)).ConfigureAwait(false);
        Assert.AreSame(loopFault, inFlightFault.InnerException);
        Assert.AreSame(loopFault, queuedFault.InnerException);

        await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.ProposeAsync("third", cancellationToken).WaitAsync(cancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task CancellationMidDispatchCancelsTheInFlightAndQueuedProposals()
    {
        //Cancellation that lands between a proposal's dequeue and its result — here inside its persist hook —
        //must cancel that in-flight proposal along with the queued ones, not leave it forever pending with
        //neither a result nor a cancellation. The timeout source bounds every await through WaitAsync so a
        //regression to the orphaning behaviour fails instead of hanging the suite; a timeout also surfaces as
        //TaskCanceledException, so the IsCanceled asserts on the proposal tasks are the discriminating check.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        using CancellationTokenSource stopSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token);

        RaftNode<string> node = new(N1, [N1]);
        RaftRunner<string> runner = new(node);

        int persistCalls = 0;
        TaskCompletionSource persistEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PersistRaftStateDelegate<string> persist = async (_, persistToken) =>
        {
            if(Interlocked.Increment(ref persistCalls) == 1)
            {
                return;
            }

            persistEntered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, persistToken).ConfigureAwait(false);
        };

        Task run = runner.RunAsync(DiscardSend, persist, null, stopSource.Token);

        await runner.TriggerElectionAsync(stopSource.Token).ConfigureAwait(false);

        Task<long> inFlight = runner.ProposeAsync("hangs", stopSource.Token);
        await persistEntered.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        Task<long> queued = runner.ProposeAsync("waits", stopSource.Token);

        await stopSource.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => run.WaitAsync(timeoutSource.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => inFlight.WaitAsync(timeoutSource.Token)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => queued.WaitAsync(timeoutSource.Token)).ConfigureAwait(false);
        Assert.IsTrue(inFlight.IsCanceled);
        Assert.IsTrue(queued.IsCanceled);
    }


    [TestMethod]
    public async Task ProposalsAndTriggersFailFastAfterComplete()
    {
        //Complete() is the documented wind-down; afterwards the producers fault with ChannelClosedException —
        //the documented fail-fast — rather than enqueuing work no loop will ever dispatch.
        RaftNode<string> node = new(N1, [N1]);
        RaftRunner<string> runner = new(node);
        Task run = runner.RunAsync(DiscardSend, null, null, TestContext.CancellationToken);

        runner.Complete();
        await run.ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.ProposeAsync("late", TestContext.CancellationToken)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.TriggerHeartbeatAsync(TestContext.CancellationToken).AsTask()).ConfigureAwait(false);
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


    private static ValueTask DiscardSend(ReplicaId to, RaftEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
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
