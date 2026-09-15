using Lumoin.Verisync.Core;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class ConsensusNodeTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId R1 { get; } = Replica(1);

    private static string[] ExpectedPersistThenReplyEvents { get; } = ["persisted@1", "replied@1", "persisted@2", "replied@2"];


    [TestMethod]
    public async Task PersistDelegateRunsBeforeEachReplyForChangingRequests()
    {
        //A prepare and a following accept both change the acceptor, so each must be persisted before its
        //reply is sent. The shared event log records the strict "persist then reply" interleaving per request.
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<string> events = [];
        List<FastAcceptor<string>> persisted = [];
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (acceptor, _) =>
        {
            persisted.Add(acceptor);
            events.Add($"persisted@{persisted.Count}");

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);
            events.Add($"replied@{replies.Count}");

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(new AcceptRequest<string>(FastBallot.Classic(2, R1), "v"), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreSequenceEqual(ExpectedPersistThenReplyEvents, events);

        //Each persisted state is the new acceptor state for that request: the promise, then the accept.
        Assert.AreEqual(FastBallot.Classic(2, R1), persisted[0].Promised);
        Assert.AreEqual(FastBallot.Classic(2, R1), persisted[1].AcceptedBallot);
        Assert.AreEqual("v", persisted[1].AcceptedValue);

        //The persisted instance is the very state observable on the node, not a copy.
        Assert.AreSame(node.Acceptor, persisted[1]);
    }


    [TestMethod]
    public async Task RejectedRequestIsNotPersisted()
    {
        //A prepare below the promise is rejected and returns the same immutable acceptor, so there is
        //nothing new to persist — only the first, promise-raising prepare is.
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<FastAcceptor<string>> persisted = [];
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (acceptor, _) =>
        {
            persisted.Add(acceptor);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(1, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        //Both replies are still sent; only the state-changing prepare is persisted.
        Assert.HasCount(2, replies);
        Assert.HasCount(1, persisted);
        Assert.IsTrue(((PrepareReply<string>)replies[0]).Promised);
        Assert.IsFalse(((PrepareReply<string>)replies[1]).Promised);
    }


    [TestMethod]
    public async Task ThrowingPersistDelegatePreventsTheReply()
    {
        //An unpersisted promise must never be observable, so a failing persist throws before the reply is
        //sent and the exception propagates out of RunAsync.
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (_, _) => throw new InvalidOperationException("durable store unavailable");

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsEmpty(replies);
    }


    [TestMethod]
    public async Task ARedeliveryAfterAFailedPersistWritesAgainBeforeItAnswers()
    {
        //A re-delivery after a failed write is the one place where "did the state change" and "is the state
        //durable" come apart. The accept advances the acceptor, the write fails, and the reply is correctly
        //withheld. The proposer then re-delivers the identical accept, which the idempotent-retry branch
        //answers from the same instance — so a gate that asked whether this request changed the state would
        //skip the write and announce an accept that never reached the disk. The gate compares against what
        //was persisted, not against what the request found.
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<ConsensusReply<string>> replies = [];
        List<FastAcceptor<string>> persisted = [];
        int attempts = 0;

        //The first write fails and every later one succeeds, which is a disk that was briefly full.
        ValueTask Persist(FastAcceptor<string> acceptor, CancellationToken token)
        {
            attempts++;
            if(attempts == 1)
            {
                throw new IOException("the durable store is full");
            }

            persisted.Add(acceptor);

            return ValueTask.CompletedTask;
        }

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        //A classic ballot above the initial promise is the accept-without-prepare case, so a single request
        //both advances the acceptor and is answered idempotently from the same instance when re-delivered.
        AcceptRequest<string> request = new(FastBallot.Classic(2, R1), "v");

        await requests.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, Persist, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsEmpty(replies);
        Assert.IsEmpty(persisted);

        //The host restarts the loop on the same node, which is its only option, and the proposer re-delivers
        //the identical request. The acceptor is unchanged by it, and the write must still happen.
        FastAcceptor<string> afterFailedWrite = node.Acceptor;
        Channel<ConsensusRequest<string>> redelivered = Channel.CreateUnbounded<ConsensusRequest<string>>();

        await redelivered.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        redelivered.Writer.Complete();

        await node.RunAsync(redelivered.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, Persist, TestContext.CancellationToken).ConfigureAwait(false);

        //The redelivery answered from the very instance the failed write left behind. This is the premise
        //that makes it the path where the two gates diverge; if the retry ever allocated a fresh instance,
        //this test would pass under either gate and pin nothing.
        Assert.AreSame(afterFailedWrite, node.Acceptor);

        Assert.HasCount(1, persisted);
        Assert.HasCount(1, replies);
        Assert.AreSame(node.Acceptor, persisted[0]);
        Assert.IsTrue(((AcceptReply<string>)replies[0]).Accepted);

        //A third identical delivery is genuinely durable already, so it costs no further write and still
        //answers: the gate is durability and not paranoia.
        Channel<ConsensusRequest<string>> again = Channel.CreateUnbounded<ConsensusRequest<string>>();

        await again.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        again.Writer.Complete();

        await node.RunAsync(again.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, Persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, persisted);
        Assert.HasCount(2, replies);
    }


    [TestMethod]
    public async Task AStaleRequestToAFreshNodeCostsNoWrite()
    {
        //A fresh node's acceptor and its durable baseline are the same initial singleton, so a request the
        //acceptor rejects outright leaves nothing to write and the reply goes out alone. A baseline that
        //started anywhere else would persist a state no request produced on the first rejection.
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<FastAcceptor<string>> persisted = [];
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (acceptor, _) =>
        {
            persisted.Add(acceptor);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        //A fast-ballot prepare is rejected from any state, the initial one included, without changing it.
        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.InitialFast()), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, replies);
        Assert.IsEmpty(persisted);
        Assert.IsFalse(((PrepareReply<string>)replies[0]).Promised);
    }


    [TestMethod]
    public async Task NullPersistDelegateSendsRepliesImmediately()
    {
        //Omitting the persist delegate reproduces the in-memory behavior: every reply is sent, nothing is
        //persisted, and the node's state still advances exactly as Handle dictates.
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<ConsensusReply<string>> replies = [];

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        await requests.Writer.WriteAsync(new AcceptRequest<string>(FastBallot.Classic(2, R1), "v"), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(2, replies);
        Assert.IsTrue(((PrepareReply<string>)replies[0]).Promised);
        Assert.IsTrue(((AcceptReply<string>)replies[1]).Accepted);
        Assert.AreEqual("v", node.Acceptor.AcceptedValue);
    }


    [TestMethod]
    public void ASeededNodeStartsFromTheAcceptorItWasGiven()
    {
        //The restored acceptor is the node's state itself, not a template it copies from; the gate below
        //reads reference identity, so the seam must hand the instance through unchanged.
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), "v");
        FastAcceptor<string> restored = FastAcceptor<string>.FromState(accepted.ToState());

        ConsensusNode<string> node = new(restored);

        Assert.AreSame(restored, node.Acceptor);
    }


    [TestMethod]
    public async Task ASeededNodeTreatsItsRestoredAcceptorAsAlreadyDurable()
    {
        //The restored acceptor came from the bytes the host had already written, so the node owes no write
        //for it: a redelivery the restored acceptor answers idempotently costs exactly zero writes — never
        //"at most one", because a baseline that started anywhere but the restored instance would put the
        //first reply on the durable-write path. The restored state is deliberately not the initial one, so a
        //baseline reset to the initial acceptor is caught unconditionally.
        AcceptRequest<string> request = new(FastBallot.Classic(2, R1), "v");
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(request.Ballot, request.Value);
        FastAcceptor<string> restored = FastAcceptor<string>.FromState(accepted.ToState());

        ConsensusNode<string> node = new(restored);
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<FastAcceptor<string>> persisted = [];
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (acceptor, _) =>
        {
            persisted.Add(acceptor);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(request, TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsEmpty(persisted);
        Assert.HasCount(1, replies);
        Assert.IsTrue(((AcceptReply<string>)replies[0]).Accepted);
    }


    [TestMethod]
    public async Task TheFirstChangingRequestOnASeededNodeCostsExactlyOneWrite()
    {
        //Seeding moves only where the gate's two references start; the first request that advances the
        //acceptor past the restored state pays the ordinary one write before its reply.
        (FastAcceptor<string> accepted, _) = FastAcceptor<string>.Initial.Accept(FastBallot.Classic(2, R1), "v");
        FastAcceptor<string> restored = FastAcceptor<string>.FromState(accepted.ToState());

        ConsensusNode<string> node = new(restored);
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        List<FastAcceptor<string>> persisted = [];
        List<ConsensusReply<string>> replies = [];

        PersistAcceptorDelegate<string> persist = (acceptor, _) =>
        {
            persisted.Add(acceptor);

            return ValueTask.CompletedTask;
        };

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(5, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, persist, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, persisted);
        Assert.HasCount(1, replies);
        Assert.AreSame(node.Acceptor, persisted[0]);
        Assert.IsTrue(((PrepareReply<string>)replies[0]).Promised);
    }


    [TestMethod]
    public void AParameterlessNodeStartsAtTheInitialAcceptor()
    {
        //The parameterless path chains through the seeding constructor over the initial singleton, so a
        //fresh node's acceptor is the very instance every other fresh node starts from.
        ConsensusNode<string> node = new();

        Assert.AreSame(FastAcceptor<string>.Initial, node.Acceptor);
    }


    [TestMethod]
    public void TheSeedingConstructorRefusesANullAcceptor()
    {
        ArgumentNullException refusal = Assert.ThrowsExactly<ArgumentNullException>(() => new ConsensusNode<string>(null!));

        Assert.AreEqual("acceptor", refusal.ParamName);
    }


    /// <summary>A request kind the node does not know, used to reach Handle's fallthrough refusal.</summary>
    private sealed record UnknownRequest : ConsensusRequest<string>;


    /// <summary>Pins that <see cref="ConsensusNode{TValue}.RunAsync"/> validates the reply sink before
    /// consuming any request, refusing a null sink with the "sendReply" parameter name.</summary>
    [TestMethod]
    public async Task RunAsyncRefusesANullReplySink()
    {
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        requests.Writer.Complete();

        ArgumentNullException refusal = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), null!, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual("sendReply", refusal.ParamName);
    }


    /// <summary>Pins that <see cref="ConsensusNode{TValue}.Handle"/> refuses any request that is neither
    /// a <see cref="PrepareRequest{TValue}"/> nor an <see cref="AcceptRequest{TValue}"/>, naming the
    /// "request" parameter.</summary>
    [TestMethod]
    public void HandleRejectsAnUnknownRequestKind()
    {
        ConsensusNode<string> node = new();
        UnknownRequest request = new();

        ArgumentException refusal = Assert.ThrowsExactly<ArgumentException>(() => node.Handle(request));

        Assert.AreEqual("request", refusal.ParamName);
    }


    /// <summary>Pins that <see cref="ConsensusNode{TValue}.RunAsync"/> validates its request stream
    /// before consuming it, refusing a null stream with the "requests" parameter name.</summary>
    [TestMethod]
    public async Task RunAsyncRefusesANullRequestStream()
    {
        ConsensusNode<string> node = new();

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token) => ValueTask.CompletedTask;

        ArgumentNullException refusal = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await node.RunAsync(null!, SendReply, cancellationToken: TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual("requests", refusal.ParamName);
    }


    /// <summary>Pins that <see cref="AcceptReply{TValue}.Ballot"/> carries the request's own ballot when
    /// the accept succeeds, and the acceptor's current promise -- not the request's ballot -- when it is
    /// rejected, exactly as the XML doc on <see cref="AcceptReply{TValue}.Ballot"/> specifies.</summary>
    [TestMethod]
    public void AcceptReplyReportsTheAcceptedBallotOnlyWhenAccepted()
    {
        ConsensusNode<string> node = new();

        //A classic accept with a piggybacked next fast ballot succeeds and raises the promise past its own
        //ballot, so an accepted reply that echoed the new promise instead of the request's ballot would be
        //caught here.
        AcceptRequest<string> succeeding = new(FastBallot.Classic(2, R1), "v", FastBallot.Fast(5));
        AcceptReply<string> acceptedReply = (AcceptReply<string>)node.Handle(succeeding);

        Assert.IsTrue(acceptedReply.Accepted);
        Assert.AreEqual(FastBallot.Classic(2, R1), acceptedReply.Ballot);
        Assert.AreEqual(FastBallot.Fast(5), node.Acceptor.Promised);

        //A classic accept below the new promise is rejected outright, so a rejected reply that echoed the
        //request's own ballot instead of the acceptor's promise would be caught here.
        AcceptRequest<string> rejected = new(FastBallot.Classic(3, R1), "other");
        AcceptReply<string> rejectedReply = (AcceptReply<string>)node.Handle(rejected);

        Assert.IsFalse(rejectedReply.Accepted);
        Assert.AreEqual(FastBallot.Fast(5), rejectedReply.Ballot);
    }


    /// <summary>
    /// The loop does not resume on the synchronization context it was started on. The continuation after a
    /// durable write that completes later, and the one after a reply that completes later, both run off that
    /// context, so a host that blocks the starting thread on the loop cannot deadlock it.
    /// </summary>
    /// <remarks>
    /// Each half parks the loop at exactly one await while the counting context is current. The first half parks
    /// it on the persist, with a reply sink that completes at once; the second parks it on the reply, with no
    /// durability hook. Both request streams are filled and completed before the loop starts, so reading them
    /// never parks it.
    /// </remarks>
    [TestMethod]
    public async Task TheLoopDoesNotResumeOnTheCallersContextAfterAPendingWriteOrAPendingReply()
    {
        ConsensusNode<string> persisting = new();
        Channel<ConsensusRequest<string>> persistingRequests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        TaskCompletionSource writeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCountingSynchronizationContext writeContext = new();
        List<ConsensusReply<string>> persistingReplies = [];
        int writes = 0;

        PersistAcceptorDelegate<string> persist = (_, _) =>
        {
            writes++;

            return new ValueTask(writeGate.Task);
        };

        ValueTask SendAtOnce(ConsensusReply<string> reply, CancellationToken token)
        {
            persistingReplies.Add(reply);

            return ValueTask.CompletedTask;
        }

        await persistingRequests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        persistingRequests.Writer.Complete();

        Task persistingRun = writeContext.Start(() => persisting.RunAsync(persistingRequests.Reader.ReadAllAsync(TestContext.CancellationToken), SendAtOnce, persist, TestContext.CancellationToken));

        //The loop is parked on the persist: the hook ran once and no reply has left yet.
        Assert.AreEqual(1, writes);
        Assert.IsEmpty(persistingReplies);
        Assert.IsFalse(persistingRun.IsCompleted);

        writeGate.SetResult();
        await persistingRun.ConfigureAwait(false);

        Assert.HasCount(1, persistingReplies);
        Assert.AreEqual(0, writeContext.Posts);

        ConsensusNode<string> replying = new();
        Channel<ConsensusRequest<string>> replyingRequests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        TaskCompletionSource replyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PostCountingSynchronizationContext replyContext = new();
        List<ConsensusReply<string>> replyingReplies = [];

        ValueTask SendWhenReleased(ConsensusReply<string> reply, CancellationToken token)
        {
            replyingReplies.Add(reply);

            return new ValueTask(replyGate.Task);
        }

        await replyingRequests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
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
    /// in the loop that can park it here, and only a continuation captured there can post back to the context.
    /// </remarks>
    [TestMethod]
    public async Task TheLoopDoesNotResumeOnTheCallersContextAfterAPendingRequestRead()
    {
        ConsensusNode<string> node = new();
        Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
        PostCountingSynchronizationContext context = new();
        List<ConsensusReply<string>> replies = [];

        ValueTask SendReply(ConsensusReply<string> reply, CancellationToken token)
        {
            replies.Add(reply);

            return ValueTask.CompletedTask;
        }

        //Nothing has been written yet, so the loop parks on the request read itself.
        Task run = context.Start(() => node.RunAsync(requests.Reader.ReadAllAsync(TestContext.CancellationToken), SendReply, cancellationToken: TestContext.CancellationToken));

        Assert.IsFalse(run.IsCompleted);
        Assert.IsEmpty(replies);

        await requests.Writer.WriteAsync(new PrepareRequest<string>(FastBallot.Classic(2, R1)), TestContext.CancellationToken).ConfigureAwait(false);
        requests.Writer.Complete();

        await run.ConfigureAwait(false);

        Assert.HasCount(1, replies);
        Assert.AreEqual(0, context.Posts);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
