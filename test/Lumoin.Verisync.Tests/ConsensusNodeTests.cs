using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

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

        CollectionAssert.AreEqual(ExpectedPersistThenReplyEvents, events);

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
