using Lumoin.Verisync.Core;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

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
/// isolation, restart-from-persisted-state, the abandonment of pending and in-flight proposals when the loop
/// faults or is cancelled, and the fail-fast producer contract after <see cref="RaftRunner{TCommand}.Complete"/>.
/// </summary>
[TestClass]
internal sealed class RaftRunnerTests
{
    private static ReplicaId N1 { get; } = Replica(1);
    private static ReplicaId N2 { get; } = Replica(2);
    private static ReplicaId N3 { get; } = Replica(3);

    private static ImmutableArray<ReplicaId> Members { get; } = [N1, N2, N3];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public async Task TriggerElectionRoutesTheCampaignAndElectsTheLeader()
    {
        //A triggered campaign plus one granting peer reply (self-vote and one peer is a 2-of-3 majority) drives
        //the runner's node to leader for term one, and the campaign was broadcast (a vote request was sent).
        Runner runner = new(new RaftNode<string>(N1, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        await runner.TriggerElectionAsync().ConfigureAwait(false);
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(Term.First, true))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        Assert.AreEqual(RaftRole.Leader, runner.Node.Role);
        Assert.AreEqual(Term.First, runner.Node.CurrentTerm);
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
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(Term.First, true))).ConfigureAwait(false);

        Task<LogIndex> first = runner.ProposeAsync("alpha");
        Task<LogIndex> second = runner.ProposeAsync("beta");
        Task<LogIndex> third = runner.ProposeAsync("gamma");

        await runner.SubmitAsync(RaftEnvelope<string>.ForAppendReply(N2, new AppendEntriesReply(Term.First, true, new LogIndex(3)))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        Assert.AreEqual(LogIndex.First, await first.ConfigureAwait(false));
        Assert.AreEqual(new LogIndex(2), await second.ConfigureAwait(false));
        Assert.AreEqual(new LogIndex(3), await third.ConfigureAwait(false));

        (LogIndex Index, string Command)[] expected = [(LogIndex.First, "alpha"), (new LogIndex(2), "beta"), (new LogIndex(3), "gamma")];
        Assert.AreSequenceEqual(expected, runner.Applied.ToArray());
    }


    [TestMethod]
    public async Task ProposeOnAFollowerFaultsWithInvalidOperationAndTheLoopSurvives()
    {
        //A propose addressed to a non-leader faults its own task with InvalidOperationException; a second
        //propose queued behind it faults the same way, proving the first fault did not tear down the consumer
        //loop, and the run task itself completes normally.
        Runner runner = new(new RaftNode<string>(N2, Members), TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable cleanup = runner.ConfigureAwait(false);

        Task<LogIndex> first = runner.ProposeAsync("nope");
        Task<LogIndex> second = runner.ProposeAsync("still-nope");

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
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(Term.First, true))).ConfigureAwait(false);
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
        await runner.SubmitAsync(RaftEnvelope<string>.ForVoteReply(N2, new RequestVoteReply(Term.First, true))).ConfigureAwait(false);

        for(int i = 0; i < 5; i++)
        {
            _ = runner.ProposeAsync($"cmd-{i}");
        }

        await runner.SubmitAsync(RaftEnvelope<string>.ForAppendReply(N2, new AppendEntriesReply(Term.First, true, new LogIndex(5)))).ConfigureAwait(false);
        await runner.DrainAsync().ConfigureAwait(false);

        LogIndex[] indices = runner.Applied.Select(entry => entry.Index).ToArray();
        LogIndex[] expected = [LogIndex.First, new(2), new(3), new(4), new(5)];
        Assert.AreSequenceEqual(expected, indices);
    }


    [TestMethod]
    public async Task ARestartedNodeReappliesFromPersistedStateOnCatchUp()
    {
        //A follower restarted from its last persisted state (a fresh node via FromState behind a fresh runner)
        //rejoins, and a heartbeat catches it up so it reapplies the committed content. Apply is at-least-once
        //across restart, so convergence of the applied content is the assertion.
        var entries = ImmutableArray.Create(new RaftLogEntry<string>(Term.First, "one"), new RaftLogEntry<string>(Term.First, "two"));
        (LogIndex Index, string Command)[] expected = [(LogIndex.First, "one"), (new LogIndex(2), "two")];
        RaftNodeState<string> persisted;

        //First lifetime: the follower receives the two entries with a leader commit of two, applies them, and
        //persists a durable state captured for the restart.
        {
            Runner original = new(new RaftNode<string>(N2, Members), TestContext.CancellationToken);
            await using ConfiguredAsyncDisposable cleanup = original.ConfigureAwait(false);

            await original.SubmitAsync(RaftEnvelope<string>.ForAppendRequest(N1, new AppendEntriesRequest<string>(Term.First, N1, LogIndex.BeforeFirst, Term.Zero, entries, new LogIndex(2)))).ConfigureAwait(false);
            await original.DrainAsync().ConfigureAwait(false);

            Assert.AreSequenceEqual(expected, original.Applied.ToArray());
            persisted = original.LastPersisted ?? throw new InvalidOperationException("The follower never persisted a state to restart from.");
        }

        //Second lifetime: a fresh runner over the restored node. The commit index is volatile, so the restored
        //node rediscovers it from the leader's heartbeat and reapplies the committed prefix (at-least-once).
        {
            Runner restarted = new(RaftNode<string>.FromState(N2, Members, persisted), TestContext.CancellationToken);
            await using ConfiguredAsyncDisposable cleanup = restarted.ConfigureAwait(false);

            await restarted.SubmitAsync(RaftEnvelope<string>.ForAppendRequest(N1, new AppendEntriesRequest<string>(Term.First, N1, new LogIndex(2), Term.First, [], new LogIndex(2)))).ConfigureAwait(false);
            await restarted.DrainAsync().ConfigureAwait(false);

            Assert.AreSequenceEqual(expected, restarted.Applied.ToArray());
        }
    }


    [TestMethod]
    public async Task AFaultingHookFaultsThePendingAndInFlightProposalsAndLaterProposalsFailFast()
    {
        //A persist hook that throws (the documented fail-closed path for a broken durable store) ends the
        //loop; the in-flight proposal and every queued one must fault instead of hanging forever, and a
        //proposal issued after the fault must fail fast on the completed channel. Every await on a proposal
        //task is bounded through WaitAsync so a regression to the orphaning behaviour fails instead of
        //hanging the suite. This scenario drives a raw runner because it needs a bespoke persist hook the
        //shared harness does not expose.
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

        Task<LogIndex> inFlight = runner.ProposeAsync("first", cancellationToken);
        await persistEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        //The loop is parked inside the first proposal's persist, so the second proposal stays queued.
        Task<LogIndex> queued = runner.ProposeAsync("second", cancellationToken);
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

        Task<LogIndex> inFlight = runner.ProposeAsync("hangs", stopSource.Token);
        await persistEntered.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        Task<LogIndex> queued = runner.ProposeAsync("waits", stopSource.Token);

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


    [TestMethod]
    public async Task RunAsyncWithANullSendFailsClosedInsteadOfHangingAPendingProposal()
    {
        //A proposal enqueued before the runner starts would hang forever if RunAsync(null) threw its argument
        //validation without completing the writer. The fix fails closed exactly as an early loop exit does, so
        //the pending proposal faults and a later proposal fails fast. Every await is bounded through WaitAsync
        //so a regression to the hanging behaviour fails instead of stalling the suite.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        RaftNode<string> node = new(N1, [N1]);
        RaftRunner<string> runner = new(node);

        Task<LogIndex> pending = runner.ProposeAsync("orphan", cancellationToken);

        ArgumentNullException validation = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            () => runner.RunAsync(null!, null, null, cancellationToken)).ConfigureAwait(false);

        //The pending proposal faults with the abandonment exception carrying the validation failure as its inner.
        InvalidOperationException pendingFault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pending.WaitAsync(cancellationToken)).ConfigureAwait(false);
        Assert.AreSame(validation, pendingFault.InnerException);

        //The writer is completed, so a proposal issued afterwards fails fast rather than hanging on a dead runner.
        await Assert.ThrowsExactlyAsync<ChannelClosedException>(
            () => runner.ProposeAsync("late", cancellationToken).WaitAsync(cancellationToken)).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task CancellationAttributesTheRunnerTokenToTheCancelledProposals()
    {
        //When the runner's own token cancels the loop mid-dispatch, the cancelled in-flight and queued proposals
        //must carry that token as their cancellation cause rather than a token-less cancellation that loses the
        //attribution. The timeout source bounds every await so a regression fails instead of hanging the suite.
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

        Task<LogIndex> inFlight = runner.ProposeAsync("hangs", stopSource.Token);
        await persistEntered.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        Task<LogIndex> queued = runner.ProposeAsync("waits", stopSource.Token);

        await stopSource.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => run.WaitAsync(timeoutSource.Token)).ConfigureAwait(false);
        TaskCanceledException inFlightCancel = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => inFlight.WaitAsync(timeoutSource.Token)).ConfigureAwait(false);
        TaskCanceledException queuedCancel = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => queued.WaitAsync(timeoutSource.Token)).ConfigureAwait(false);
        Assert.AreEqual(stopSource.Token, inFlightCancel.CancellationToken);
        Assert.AreEqual(stopSource.Token, queuedCancel.CancellationToken);
    }


    [TestMethod]
    public async Task AHookCancellationUnrelatedToTheRunnerTokenFaultsTheProposalsInsteadOfCancelling()
    {
        //A hook that throws OperationCanceledException for its own reasons — carrying a token unrelated to the
        //runner's, while the runner token is never signalled — is a hook failure, not a clean stop, so the loop
        //must FAULT the pending proposals rather than cancel them. That is the misclassification the narrowed
        //catch filter closes. Every await is bounded through WaitAsync.
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        RaftNode<string> node = new(N1, [N1]);
        RaftRunner<string> runner = new(node);

        using CancellationTokenSource hookSource = new();
        OperationCanceledException hookFailure = new(hookSource.Token);
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
            await hookSource.CancelAsync().ConfigureAwait(false);

            throw hookFailure;
        };

        Task run = runner.RunAsync(DiscardSend, persist, null, cancellationToken);

        await runner.TriggerElectionAsync(cancellationToken).ConfigureAwait(false);

        Task<LogIndex> inFlight = runner.ProposeAsync("first", cancellationToken);
        await persistEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<LogIndex> queued = runner.ProposeAsync("second", cancellationToken);

        persistGate.SetResult();

        //The hook's OperationCanceledException ends the loop and surfaces on the run task as a cancellation; how
        //it surfaces there is not the subject, so it is swallowed bounded. The proposals are the subject.
        try
        {
            await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch(OperationCanceledException)
        {
            //Expected: the loop re-throws the hook's OCE, cancelling the run task.
        }

        //Because the runner token was never signalled, the proposals FAULT with the wrapping exception carrying
        //the hook's OCE as its inner, rather than cancelling on a token that was not the runner's.
        InvalidOperationException inFlightFault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inFlight.WaitAsync(cancellationToken)).ConfigureAwait(false);
        InvalidOperationException queuedFault = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queued.WaitAsync(cancellationToken)).ConfigureAwait(false);
        Assert.AreSame(hookFailure, inFlightFault.InnerException);
        Assert.AreSame(hookFailure, queuedFault.InnerException);
        Assert.IsFalse(inFlight.IsCanceled);
        Assert.IsFalse(queued.IsCanceled);
    }


    private static ValueTask DiscardSend(ReplicaId to, RaftEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// A single real RaftRunner wired to capturing delegates.
    /// </summary>
    /// <remarks>
    /// Work is enqueued through the runner's own producer API; DrainAsync completes the channel and awaits the
    /// consumer loop, so every queued item is processed in FIFO order before control returns — a deterministic
    /// completion signal, never a timed wait. The capturing delegates run on the loop and their queues are read
    /// only after the run task has completed.
    /// </remarks>
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

        public ConcurrentQueue<(LogIndex Index, string Command)> Applied { get; } = new();

        /// <summary>
        /// Records each persist and send firing in order; the persist-before-send invariant is read off this.
        /// </summary>
        public ConcurrentQueue<string> Events { get; } = new();

        public RaftNodeState<string>? LastPersisted { get; private set; }

        public long SendCount => Interlocked.Read(ref sends);


        public ValueTask TriggerElectionAsync() => runner.TriggerElectionAsync();

        public ValueTask SubmitAsync(RaftEnvelope<string> envelope) => runner.SubmitAsync(envelope);

        public Task<LogIndex> ProposeAsync(string command) => runner.ProposeAsync(command);


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


        private ValueTask Apply(LogIndex index, string command, CancellationToken cancellationToken)
        {
            Applied.Enqueue((index, command));

            return ValueTask.CompletedTask;
        }
    }
}
