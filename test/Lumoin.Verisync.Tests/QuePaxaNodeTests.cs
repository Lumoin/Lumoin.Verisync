using Lumoin.Verisync.Core;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The recorder node's suite: the boundary between the immutable recorder value and the stateful runtime.
/// The node is where the safety rule crosses the message boundary for the first time, and where the
/// persist-before-reply sequencing lives, so the two subjects here are the field-for-field translation from
/// summary to reply and the exactness of the "the state changed" predicate that decides whether a request is
/// made durable at all.
/// </summary>
[TestClass]
internal sealed class QuePaxaNodeTests
{
    private static ProposerLane LeaderLane { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane OtherLane { get; } = ProposerLane.For(Replica(2));

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;

    private static string[] ExpectedPersistThenReplyEvents { get; } = ["persisted@1", "replied@1", "persisted@2", "replied@2"];


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// THE TRANSLATION IS FIELD FOR FIELD AND INVENTS NOTHING. The summary is a core type and the reply is a
    /// wire type, and they are allowed to diverge, but what the node does is restate the three fields the
    /// interval summary register returned for this very request.
    /// </summary>
    /// <remarks>
    /// The second request is at the next step so the carried prior aggregate is non-null, which is the only
    /// field a translation could silently drop while staying green on a first-record-only test.
    /// </remarks>
    [TestMethod]
    public void HandleTranslatesTheSummaryIntoAReplyFieldForField()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        QuePaxaRecorder<string> before = node.Recorder;

        RecordReply<string> atFour = node.Handle(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")));
        (QuePaxaRecorder<string> expectedAtFour, RecordSummary<string> summaryAtFour) = before.Record(Four, Ordinary(5, LeaderLane, "a"));

        Assert.AreEqual(summaryAtFour.Step, atFour.Step);
        Assert.AreEqual(summaryAtFour.First, atFour.First);
        Assert.AreEqual(summaryAtFour.PriorAggregate, atFour.PriorAggregate);
        Assert.IsNull(atFour.PriorAggregate);
        Assert.AreEqual(expectedAtFour.Step, node.Recorder.Step);
        Assert.AreNotSame(before, node.Recorder);

        RecordReply<string> atFive = node.Handle(new RecordRequest<string>(Four.Next(), Ordinary(9, OtherLane, "b")));

        //Advancing by exactly one carries the aggregate accumulated at the step below, so the reply's third
        //field is the one the phase-two and phase-three gathers read.
        Assert.AreEqual(Four.Next(), atFive.Step);
        Assert.AreEqual(Ordinary(9, OtherLane, "b"), atFive.First);
        Assert.AreEqual(Ordinary(5, LeaderLane, "a"), atFive.PriorAggregate);
        Assert.AreEqual(node.Recorder.Register.PriorAggregate, atFive.PriorAggregate);
    }


    /// <summary>
    /// Each recorder transition advances the node's generation by one, while identical and stale requests
    /// leave the generation unchanged.
    /// </summary>
    [TestMethod]
    public void HandleAdvancesItsGenerationOnlyWhenTheRecorderChanges()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        RecordRequest<string> request = new(Four, Ordinary(5, LeaderLane, "a"));

        Assert.AreEqual(0UL, node.Generation);

        _ = node.Handle(request);

        Assert.AreEqual(1UL, node.Generation);

        _ = node.Handle(request);

        Assert.AreEqual(1UL, node.Generation);

        _ = node.Handle(new RecordRequest<string>(Four.Next(), Ordinary(9, OtherLane, "b")));

        Assert.AreEqual(2UL, node.Generation);

        _ = node.Handle(request);

        Assert.AreEqual(2UL, node.Generation);
        TestContext.WriteLine($"Two recorder transitions and two unchanged requests leave generation {node.Generation}.");
    }


    /// <summary>
    /// THE CONSTRUCTOR TAKES THE RECORDER RATHER THAN DEFAULTING, and the reason is the whole of the slice's
    /// safety.
    /// </summary>
    /// <remarks>
    /// A Fast CASPaxos acceptor has no configuration and its node may start from the initial value; a QuePaxa
    /// recorder carries the configured leader, so a node that defaulted to a leaderless recorder would
    /// silently downgrade every reserved claim and turn every fast path into a three-step round.
    /// </remarks>
    [TestMethod]
    public void TheNodeTakesItsRecorderAtConstructionAndRefusesANullOne()
    {
        QuePaxaNode<string> led = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        QuePaxaNode<string> leaderless = new(QuePaxaRecorder<string>.Leaderless);

        Assert.AreEqual(LeaderLane, led.Recorder.ConfiguredLeader);
        Assert.IsNull(leaderless.Recorder.ConfiguredLeader);
        Assert.AreEqual(RecorderStep.Zero, led.Recorder.Step);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaNode<string>(null!));
    }


    [TestMethod]
    public void HandleRefusesANullRequest()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = node.Handle(null!));
    }


    /// <summary>
    /// PERSIST BEFORE REPLY, PER REQUEST THAT CHANGES THE RECORDER. What must be durable before a reply escapes
    /// is the recorder's step and its first proposal at that step: Lemma C.10's argument that the first
    /// proposal of a step is never overwritten is what the fast path rests on, and a restarted recorder that
    /// came back at step zero would accept a fresh first proposal for a step whose original first proposal a
    /// proposer has already read.
    /// </summary>
    /// <remarks>
    /// The shared event log records the strict interleaving per request rather than two independent counts,
    /// because two counts cannot tell the ordering apart.
    /// </remarks>
    [TestMethod]
    public async Task PersistDelegateRunsBeforeEachReplyForChangingRequests()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        List<string> events = [];
        List<QuePaxaRecorder<string>> persisted = [];
        List<RecordReply<string>> replies = [];

        PersistRecorderDelegate<string> persist = (recorder, _) =>
        {
            persisted.Add(recorder);
            events.Add($"persisted@{persisted.Count}");

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(RecordReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);
            events.Add($"replied@{replies.Count}");

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(new RecordRequest<string>(Four.Next(), Ordinary(9, OtherLane, "b")), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreSequenceEqual(ExpectedPersistThenReplyEvents, events);

        //Each persisted state is the new recorder state for that request, and the last one is the very state
        //observable on the node rather than a copy.
        Assert.AreEqual(Four, persisted[0].Step);
        Assert.AreEqual(Four.Next(), persisted[1].Step);
        Assert.AreSame(node.Recorder, persisted[1]);
    }


    /// <summary>
    /// An advancing request is persisted once, and its identical re-delivery leaves the generation unchanged
    /// while still receiving a reply. A generation that is already durable requires no further write.
    /// </summary>
    /// <remarks>
    /// Under a re-send rule that permits identical re-delivery, this is the common case rather than a
    /// curiosity, which is why the node's staleness test cannot be approximated.
    /// </remarks>
    [TestMethod]
    public async Task ADuplicateRequestAtTheSameStepIsNotPersistedAndIsStillAnswered()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        List<QuePaxaRecorder<string>> persisted = [];
        List<RecordReply<string>> replies = [];

        PersistRecorderDelegate<string> persist = (recorder, _) =>
        {
            persisted.Add(recorder);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(RecordReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        RecordRequest<string> request = new(Four, Ordinary(5, LeaderLane, "a"));
        await requests.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        QuePaxaRecorder<string> beforeAnything = node.Recorder;
        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        //Both replies are sent and they are equal as records, which is the identity property the re-send rule
        //rests on; only the first, state-changing delivery is made durable.
        Assert.HasCount(2, replies);
        Assert.AreEqual(replies[0], replies[1]);
        Assert.HasCount(1, persisted);
        Assert.AreSame(node.Recorder, persisted[0]);
        Assert.AreNotSame(beforeAnything, node.Recorder);
        Assert.AreEqual(1UL, node.Generation);
        TestContext.WriteLine($"Two identical deliveries produced {persisted.Count} write and generation {node.Generation}.");
    }


    /// <summary>
    /// A stale request leaves the generation unchanged, requires no further write, and receives the current
    /// summary.
    /// </summary>
    [TestMethod]
    public async Task AStaleRequestIsNotPersistedAndIsStillAnswered()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        List<QuePaxaRecorder<string>> persisted = [];
        List<RecordReply<string>> replies = [];

        PersistRecorderDelegate<string> persist = (recorder, _) =>
        {
            persisted.Add(recorder);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(RecordReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new RecordRequest<string>(Four.Next(), Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(99, OtherLane, "stale")), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(2, replies);
        Assert.AreEqual(replies[0], replies[1]);
        Assert.HasCount(1, persisted);
        Assert.AreEqual(Four.Next(), node.Recorder.Step);
        Assert.AreEqual(1UL, node.Generation);
    }


    /// <summary>
    /// AN UNPERSISTED RECORD MUST NEVER BE OBSERVABLE, so a failing persist throws before the reply is sent and
    /// the exception propagates out of the loop.
    /// </summary>
    /// <remarks>
    /// That is the fail-closed reading: a node whose durable store is gone cannot keep answering, because
    /// every answer it gives is a promise about state it may lose.
    /// </remarks>
    [TestMethod]
    public async Task AThrowingPersistDelegatePreventsTheReply()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        List<RecordReply<string>> replies = [];

        PersistRecorderDelegate<string> persist = (_, _) => throw new InvalidOperationException("durable store unavailable");

        ValueTask SendReply(RecordReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsEmpty(replies);
    }


    /// <summary>
    /// An identical re-delivery after a failed write retries the write before answering, without advancing
    /// the generation again.
    /// </summary>
    /// <remarks>
    /// A failed persist leaves the node's generation ahead of its persisted generation and withholds the
    /// reply. Restarting the loop on the same node preserves that debt, so the identical re-delivery retries
    /// the write even though it changes nothing. Once the write succeeds, another identical delivery answers
    /// without persisting again.
    /// </remarks>
    [TestMethod]
    public async Task ARedeliveryAfterAFailedPersistWritesAgainBeforeItAnswers()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        List<RecordReply<string>> replies = [];
        List<QuePaxaRecorder<string>> persisted = [];
        int attempts = 0;

        //The first write fails and every later one succeeds, which is a disk that was briefly full.
        ValueTask Persist(QuePaxaRecorder<string> recorder, CancellationToken token)
        {
            attempts++;
            if(attempts == 1)
            {
                throw new IOException("the durable store is full");
            }

            persisted.Add(recorder);

            return ValueTask.CompletedTask;
        }

        ValueTask SendReply(RecordReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        RecordRequest<string> request = new(Four, Ordinary(5, LeaderLane, "a"));

        await requests.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, Persist, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsEmpty(replies);
        Assert.IsEmpty(persisted);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(1UL, node.Generation);

        //The host restarts the loop on the same node, which is its only option, and the proposer re-delivers
        //the identical request. The recorder is unchanged by it, and the write must still happen.
        Channel<RecordRequest<string>> redelivered = Channel.CreateUnbounded<RecordRequest<string>>();

        await redelivered.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        redelivered.Writer.Complete();

        await node.RunAsync(redelivered.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, Persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, persisted);
        Assert.HasCount(1, replies);
        Assert.AreSame(node.Recorder, persisted[0]);
        Assert.AreEqual(Four, replies[0].Step);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1UL, node.Generation);

        //A third identical delivery is already durable, so it answers without another write.
        Channel<RecordRequest<string>> again = Channel.CreateUnbounded<RecordRequest<string>>();

        await again.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        again.Writer.Complete();

        await node.RunAsync(again.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, Persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, persisted);
        Assert.HasCount(2, replies);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1UL, node.Generation);
        TestContext.WriteLine($"Three identical deliveries produced {attempts} write attempts and {replies.Count} replies.");
    }


    /// <summary>
    /// A THROWING REPLY SINK PROPAGATES AND ENDS THE LOOP. A node whose transport has failed cannot keep
    /// serving requests, so the second request in the stream is never applied; asserting the recorder's step is
    /// what distinguishes "the loop ended" from "the loop swallowed the failure and carried on".
    /// </summary>
    [TestMethod]
    public async Task AThrowingReplySinkPropagatesAndEndsTheLoop()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();

        static ValueTask SendReply(RecordReply<string> reply, CancellationToken token) => throw new IOException("the reply transport is gone");

        await requests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(new RecordRequest<string>(Four.Next(), Ordinary(9, OtherLane, "b")), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(Four, node.Recorder.Step);
    }


    [TestMethod]
    public async Task RunAsyncRefusesANullRequestStreamAndANullReplySink()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        requests.Writer.Complete();

        static ValueTask SendReply(RecordReply<string> reply, CancellationToken token) => ValueTask.CompletedTask;

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await node.RunAsync(null!, SendReply, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), null!, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// THE DOWNGRADE CROSSES THE MESSAGE BOUNDARY, and this is the first place it does. A leaderless node
    /// declines every reserved claim and records the proposal at the lowest ordinary priority, so the reply a
    /// proposer reads reports the priority the recorder actually wrote.
    /// </summary>
    /// <remarks>
    /// There is no rejection field to read and there must not be one: refusal preserves agreement only by
    /// wedging the refused proposer, while the downgrade preserves it with the loser running all four phases
    /// against a live register.
    /// </remarks>
    [TestMethod]
    public void ALeaderlessNodeDowngradesAReservedClaimAndStillAnswers()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.Leaderless);

        RecordReply<string> reply = node.Handle(new RecordRequest<string>(Four, Reserved(OtherLane, "b")));

        Assert.AreEqual(ProposalPriority.Lowest, reply.First.Key.Priority);
        Assert.AreEqual(OtherLane, reply.First.Key.Owner);
        Assert.AreEqual("b", reply.First.Value);
        Assert.AreEqual(Four, reply.Step);
        Assert.AreEqual(Four, node.Recorder.Step);
    }


    /// <summary>
    /// THE OTHER HALF OF THE SAME RULE, and the two are separate tests because a node that downgraded
    /// everything would pass the first one alone.
    /// </summary>
    /// <remarks>
    /// The configured leader's own claim is honoured, which is what makes the fast path a single step; the
    /// difference between the two replies is the recorded priority and nothing else, because the reply shape
    /// does not depend on whether the claim was honoured.
    /// </remarks>
    [TestMethod]
    public void ANodeLedByTheClaimantHonoursTheReservedClaim()
    {
        QuePaxaNode<string> led = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        QuePaxaNode<string> declining = new(QuePaxaRecorder<string>.LedBy(OtherLane));

        RecordReply<string> honoured = led.Handle(new RecordRequest<string>(Four, Reserved(LeaderLane, "a")));
        RecordReply<string> declined = declining.Handle(new RecordRequest<string>(Four, Reserved(LeaderLane, "a")));

        Assert.AreEqual(ProposalPriority.Reserved, honoured.First.Key.Priority);
        Assert.AreEqual(LeaderLane, honoured.First.Key.Owner);
        Assert.AreEqual(ProposalPriority.Lowest, declined.First.Key.Priority);
        Assert.AreEqual(LeaderLane, declined.First.Key.Owner);

        //The owner and the value ride through the downgrade untouched, so the two replies differ in the
        //priority alone.
        Assert.AreEqual(honoured.First.Value, declined.First.Value);
        Assert.AreEqual(honoured.Step, declined.Step);
    }


    /// <summary>
    /// The loop does not resume on the synchronization context it was started on. The continuation after a
    /// durable write that completes later, and the one after a reply that completes later, both run off that
    /// context, so a host that blocks the starting thread on the loop cannot deadlock it.
    /// </summary>
    /// <remarks>
    /// Each half parks the loop at exactly one await while the counting context is current. The first half parks
    /// it on the write, with a reply sink that completes at once; the second parks it on the reply, with no
    /// durability hook. Both request streams are filled and completed before the loop starts, so reading them
    /// never parks it.
    /// </remarks>
    [TestMethod]
    public async Task TheLoopDoesNotResumeOnTheCallersContextAfterAPendingWriteOrAPendingReply()
    {
        QuePaxaNode<string> persisting = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> persistingRequests = Channel.CreateUnbounded<RecordRequest<string>>();
        TaskCompletionSource writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCountingSynchronizationContext writeContext = new();
        List<RecordReply<string>> persistingReplies = [];
        int writes = 0;

        PersistRecorderDelegate<string> persist = (_, _) =>
        {
            writes++;

            return new ValueTask(writeGate.Task);
        };

        ValueTask SendAtOnce(RecordReply<string> reply, CancellationToken token)
        {
            persistingReplies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await persistingRequests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        persistingRequests.Writer.Complete();

        Task persistingRun = writeContext.Start(() => persisting.RunAsync(persistingRequests.Reader.ReadAllAsync(TestContext.CancellationToken), SendAtOnce, persist, TestContext.CancellationToken));

        //The loop is parked on the write: the hook ran once and no reply has left yet.
        Assert.AreEqual(1, writes);
        Assert.IsEmpty(persistingReplies);
        Assert.IsFalse(persistingRun.IsCompleted);

        writeGate.SetResult();
        await persistingRun.ConfigureAwait(false);

        Assert.HasCount(1, persistingReplies);
        Assert.AreEqual(0, writeContext.Posts);

        QuePaxaNode<string> replying = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> replyingRequests = Channel.CreateUnbounded<RecordRequest<string>>();
        TaskCompletionSource replyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCountingSynchronizationContext replyContext = new();
        List<RecordReply<string>> replyingReplies = [];

        ValueTask SendWhenReleased(RecordReply<string> reply, CancellationToken token)
        {
            replyingReplies.Add(reply);

            return new ValueTask(replyGate.Task);
        }

        await replyingRequests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        replyingRequests.Writer.Complete();

        Task replyingRun = replyContext.Start(() => replying.RunAsync(replyingRequests.Reader.ReadAllAsync(TestContext.CancellationToken), SendWhenReleased, cancellationToken: TestContext.CancellationToken));

        //The loop is parked on the reply: the sink holds it and has not released it.
        Assert.HasCount(1, replyingReplies);
        Assert.IsFalse(replyingRun.IsCompleted);

        replyGate.SetResult();
        await replyingRun.ConfigureAwait(false);

        Assert.AreEqual(0, replyContext.Posts);
    }


    /// <summary>
    /// The loop does not resume on the synchronization context it was started on after a pending request read,
    /// the third of its awaits. Nothing is written to the request channel before the loop starts, so the very
    /// first request read is what parks it, and the loop must resume off the context rather than posting back.
    /// </summary>
    /// <remarks>
    /// The persist hook is omitted and the reply sink completes at once, so the request read is the only await
    /// that can park the loop, and only a continuation captured there can post back to the context.
    /// </remarks>
    [TestMethod]
    public async Task TheLoopDoesNotResumeOnTheCallersContextAfterAPendingRequestRead()
    {
        QuePaxaNode<string> node = new(QuePaxaRecorder<string>.LedBy(LeaderLane));
        Channel<RecordRequest<string>> requests = Channel.CreateUnbounded<RecordRequest<string>>();
        PostCountingSynchronizationContext context = new();
        List<RecordReply<string>> replies = [];

        ValueTask SendReply(RecordReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        //Nothing has been written yet, so the loop parks on the request read itself.
        Task run = context.Start(() => node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, cancellationToken: TestContext.CancellationToken));

        Assert.IsFalse(run.IsCompleted);
        Assert.IsEmpty(replies);

        await requests.Writer.WriteAsync(new RecordRequest<string>(Four, Ordinary(5, LeaderLane, "a")), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await run.ConfigureAwait(false);

        Assert.HasCount(1, replies);
        Assert.AreEqual(0, context.Posts);
    }


    private static PrioritizedProposal<string> Ordinary(ulong priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    private static PrioritizedProposal<string> Reserved(ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.Reserved, owner), value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
