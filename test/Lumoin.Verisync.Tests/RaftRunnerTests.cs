using Lumoin.Verisync.Core;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Drives the message-driven runner deterministically: each test enqueues every work item the scenario needs
/// — host triggers, peer envelopes, proposals — then completes the runner's channel and awaits
/// <see cref="RaftRunner{TCommand}.RunAsync"/>, which drains the single-consumer queue in order and returns.
/// Draining the FIFO to completion is the synchronization boundary, never a wall clock: there is no
/// <c>Stopwatch</c>, no <c>Task.Delay</c>, no poll-until-deadline. The only timeout is the overall test
/// timeout carried by <see cref="TestContext.CancellationToken"/>, a hang-guard rather than a settling
/// mechanism. Peers are synthetic — their replies are fed in directly and the runner's outbound sends are
/// captured — because the cluster-wide protocol (election safety, replication, commit, Figure 8) is proven
/// deterministically against the synchronous core in <c>RaftNodeTests</c>; here the subject is the runner's
/// own contract: single-consumer ordering, persist-before-send, the in-order apply seam, proposal fault
/// isolation, and restart-from-persisted-state.
/// </summary>
[TestClass]
internal sealed class RaftRunnerTests
{
    private static ReplicaId N1 { get; } = Replica(1);
    private static ReplicaId N2 { get; } = Replica(2);
    private static ReplicaId N3 { get; } = Replica(3);

    private static ImmutableArray<ReplicaId> Members { get; } = [N1, N2, N3];

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task TriggerElectionRoutesTheCampaignAndElectsTheLeader()
    {
        //A triggered campaign plus one granting peer reply (self-vote and one peer is a 2-of-3 majority) drives
        //the runner's node to leader for term one, and the campaign was broadcast (a vote request was sent).
        Runner runner = new(new RaftNode<string>(N1, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        await runner.TriggerElectionAsync().ConfigureAwait(false);
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(1, true))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        Assert.AreEqual(RaftRole.Leader, runner.Node.Role);
        Assert.AreEqual(1, runner.Node.CurrentTerm);
        Assert.IsGreaterThan(0L, runner.SendCount);
    }


    [TestMethod]
    public async Task ProposeOnTheLeaderCommitsAndAppliesInOrder()
    {
        //Three proposals on the leader take indices one, two, three; one follower acknowledging match index
        //three is a majority for the current-term entries, so the commit point advances and the apply seam
        //observes the identical (index, command) sequence in order — no gaps, no reordering, no duplicates.
        Runner runner = new(new RaftNode<string>(N1, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        await runner.TriggerElectionAsync().ConfigureAwait(false);
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(1, true))).ConfigureAwait(false);

        Task<long> first = runner.ProposeAsync("alpha");
        Task<long> second = runner.ProposeAsync("beta");
        Task<long> third = runner.ProposeAsync("gamma");

        await runner.SubmitAsync(RaftEnvelope<string>.ForAppendReply(N2, new AppendEntriesReply(1, true, 3))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        Assert.AreEqual(1, await first.ConfigureAwait(false));
        Assert.AreEqual(2, await second.ConfigureAwait(false));
        Assert.AreEqual(3, await third.ConfigureAwait(false));

        (long Index, string Command)[] expected = [(1, "alpha"), (2, "beta"), (3, "gamma")];
        CollectionAssert.AreEqual(expected, runner.Applied.ToArray());
    }


    [TestMethod]
    public async Task ProposeOnAFollowerFaultsWithInvalidOperationAndTheLoopSurvives()
    {
        //A propose addressed to a non-leader faults its own task with InvalidOperationException; a second
        //propose queued behind it faults the same way, proving the first fault did not tear down the consumer
        //loop, and the run task itself completes normally.
        Runner runner = new(new RaftNode<string>(N2, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        Task<long> first = runner.ProposeAsync("nope");
        Task<long> second = runner.ProposeAsync("still-nope");

        await runner.DrainAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second).ConfigureAwait(false);
        Assert.AreEqual(RaftRole.Follower, runner.Node.Role);
    }


    [TestMethod]
    public async Task PersistIsObservedBeforeTheOutboundSend()
    {
        //The HANDLE -> PERSIST -> APPLY -> SEND sequence means the durable snapshot lands before any outbound
        //envelope that could carry it. Across the runner's recorded delegate firings the first send must be
        //preceded by a persist, so a peer never observes state that is not yet durable.
        Runner runner = new(new RaftNode<string>(N1, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        await runner.TriggerElectionAsync().ConfigureAwait(false);
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(1, true))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        string[] events = runner.Events.ToArray();
        int firstPersist = Array.IndexOf(events, "persist");
        int firstSend = Array.IndexOf(events, "send");
        Assert.IsGreaterThan(-1, firstPersist);
        Assert.IsGreaterThan(-1, firstSend);
        Assert.IsLessThan(firstSend, firstPersist);
    }


    [TestMethod]
    public async Task ApplyIsInOrderWithNoGapsOrDuplicatesWithinAProcessLifetime()
    {
        //Within one process lifetime the apply seam is exactly-once and in order: with five committed entries
        //the observed indices must be 1, 2, 3, 4, 5 with no repeats and no holes.
        Runner runner = new(new RaftNode<string>(N1, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        await runner.TriggerElectionAsync().ConfigureAwait(false);
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(1, true))).ConfigureAwait(false);

        for(int i = 0; i < 5; i++)
        {
            _ = runner.ProposeAsync($"cmd-{i}");
        }

        await runner.SubmitAsync(RaftEnvelope<string>.ForAppendReply(N2, new AppendEntriesReply(1, true, 5))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        long[] indices = runner.Applied.Select(entry => entry.Index).ToArray();
        long[] expected = [1, 2, 3, 4, 5];
        CollectionAssert.AreEqual(expected, indices);
    }


    [TestMethod]
    public async Task ARestartedNodeReappliesFromPersistedStateOnCatchUp()
    {
        //A follower restarted from its last persisted state (a fresh node via FromState behind a fresh runner)
        //rejoins, and a heartbeat catches it up so it reapplies the committed content. Apply is at-least-once
        //across restart, so convergence of the applied content is the assertion.
        var entries = ImmutableArray.Create(new RaftLogEntry<string>(1, "one"), new RaftLogEntry<string>(1, "two"));
        (long Index, string Command)[] expected = [(1, "one"), (2, "two")];
        RaftNodeState<string> persisted;

        //First lifetime: the follower receives the two entries with a leader commit of two, applies them, and
        //persists a durable state captured for the restart.
        {
            Runner original = new(new RaftNode<string>(N2, Members), TestContext.CancellationToken);
            await using ConfiguredAsyncDisposable cleanup = original.ConfigureAwait(false);

            await original.SubmitAsync(RaftEnvelope<string>.ForAppendRequest(N1, new AppendEntriesRequest<string>(1, N1, 0, 0, entries, 2))).ConfigureAwait(false);
            await original.DrainAsync().ConfigureAwait(false);

            CollectionAssert.AreEqual(expected, original.Applied.ToArray());
            persisted = original.LastPersisted ?? throw new InvalidOperationException("The follower never persisted a state to restart from.");
        }

        //Second lifetime: a fresh runner over the restored node. The commit index is volatile, so the restored
        //node rediscovers it from the leader's heartbeat and reapplies the committed prefix (at-least-once).
        {
            Runner restarted = new(RaftNode<string>.FromState(N2, Members, persisted), TestContext.CancellationToken);
            await using ConfiguredAsyncDisposable cleanup = restarted.ConfigureAwait(false);

            await restarted.SubmitAsync(RaftEnvelope<string>.ForAppendRequest(N1, new AppendEntriesRequest<string>(1, N1, 2, 1, [], 2))).ConfigureAwait(false);
            await restarted.DrainAsync().ConfigureAwait(false);

            CollectionAssert.AreEqual(expected, restarted.Applied.ToArray());
        }
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    //A single real RaftRunner wired to capturing delegates. Work is enqueued through the runner's own producer
    //API; DrainAsync completes the channel and awaits the consumer loop, so every queued item is processed in
    //FIFO order before control returns — a deterministic completion signal, never a timed wait. The capturing
    //delegates run on the loop and their queues are read only after the run task has completed.
    private sealed class Runner: IAsyncDisposable
    {
        private readonly RaftRunner<string> runner;
        private readonly Task runTask;
        private long sends;


        public Runner(RaftNode<string> node, CancellationToken cancellationToken)
        {
            Node = node;
            runner = new RaftRunner<string>(node);
            runTask = runner.RunAsync(Send, Persist, Apply, cancellationToken);
        }


        public RaftNode<string> Node { get; }

        public ConcurrentQueue<(long Index, string Command)> Applied { get; } = new();

        //Records each persist and send firing in order; the persist-before-send invariant is read off this.
        public ConcurrentQueue<string> Events { get; } = new();

        public RaftNodeState<string>? LastPersisted { get; private set; }

        public long SendCount => Interlocked.Read(ref sends);


        public ValueTask TriggerElectionAsync() => runner.TriggerElectionAsync();

        public ValueTask SubmitAsync(RaftEnvelope<string> envelope) => runner.SubmitAsync(envelope);

        public Task<long> ProposeAsync(string command) => runner.ProposeAsync(command);


        public async Task DrainAsync()
        {
            runner.Complete();
            await runTask.ConfigureAwait(false);
        }


        public async ValueTask DisposeAsync()
        {
            runner.Complete();

            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch(OperationCanceledException)
            {
                //A cancelled run is an expected shutdown path; the test owns the token's lifetime.
            }
        }


        private ValueTask Send(ReplicaId to, RaftEnvelope<string> envelope, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sends);
            Events.Enqueue("send");

            return ValueTask.CompletedTask;
        }


        private ValueTask Persist(RaftNodeState<string> state, CancellationToken cancellationToken)
        {
            LastPersisted = state;
            Events.Enqueue("persist");

            return ValueTask.CompletedTask;
        }


        private ValueTask Apply(long index, string command, CancellationToken cancellationToken)
        {
            Applied.Enqueue((index, command));

            return ValueTask.CompletedTask;
        }
    }
}
