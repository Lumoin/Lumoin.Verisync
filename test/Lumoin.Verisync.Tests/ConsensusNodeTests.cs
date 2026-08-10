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


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
