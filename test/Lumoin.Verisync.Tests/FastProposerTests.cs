using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class FastProposerTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId R1 { get; } = Replica(1);


    [TestMethod]
    public async Task FastWriteCommitsWhenUncontendedOverDirectEndpoints()
    {
        ConsensusNode<string>[] nodes = CreateNodes(5);
        FastProposer<string> proposer = new(nodes.Select(DirectEndpoint).ToArray());

        (int accepted, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(5, accepted);
        Assert.IsTrue(committed);
    }


    [TestMethod]
    public async Task RecoveryRecoversFastWinnerAfterSplit()
    {
        ConsensusNode<string>[] nodes = CreateNodes(5);

        //Model a split fast round: three acceptors took "x", two took "y" at the same fast ballot.
        for(int i = 0; i < 3; i++)
        {
            nodes[i].Handle(new AcceptRequest<string>(FastBallot.Fast(1), "x"));
        }

        for(int i = 3; i < 5; i++)
        {
            nodes[i].Handle(new AcceptRequest<string>(FastBallot.Fast(1), "y"));
        }

        FastProposer<string> proposer = new(nodes.Select(DirectEndpoint).ToArray());

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(1, R1), current => current!, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }


    [TestMethod]
    public async Task RecoveryReportsTheAcceptCount()
    {
        ConsensusNode<string>[] nodes = CreateNodes(5);
        FastProposer<string> proposer = new(nodes.Select(DirectEndpoint).ToArray());

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(1, R1), _ => "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual(5, outcome.AcceptedCount);
    }


    [TestMethod]
    public async Task RecoveryUnderPartitionReportsTheReducedCount()
    {
        ConsensusNode<string>[] nodes = CreateNodes(5);
        var endpoints = new ConsensusEndpointDelegate<string>[5];
        for(int i = 0; i < 5; i++)
        {
            int index = i;
            endpoints[i] = index >= 3
                ? (_, _) => throw new IOException($"acceptor {index} is partitioned")
                : DirectEndpoint(nodes[index]);
        }

        FastProposer<string> proposer = new(endpoints);

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(1, R1), _ => "x", TestContext.CancellationToken).ConfigureAwait(false);

        //The change is chosen on the classic quorum of three, but three is below the fast quorum of four, so
        //this is the arming rule's negative case: a next fast ballot piggybacked here would arm a round no
        //fast quorum could ever complete.
        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual(3, outcome.AcceptedCount);
        Assert.IsLessThan(proposer.FastQuorum, outcome.AcceptedCount);
    }


    [TestMethod]
    public async Task AFailedPrepareQuorumReportsNoAccepts()
    {
        ConsensusNode<string>[] nodes = CreateNodes(5);
        var endpoints = new ConsensusEndpointDelegate<string>[5];
        for(int i = 0; i < 5; i++)
        {
            int index = i;
            endpoints[i] = index >= 2
                ? (_, _) => throw new IOException($"acceptor {index} is partitioned")
                : DirectEndpoint(nodes[index]);
        }

        FastProposer<string> proposer = new(endpoints);

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(1, R1), _ => "x", TestContext.CancellationToken).ConfigureAwait(false);

        //Two promises fall short of the classic quorum, so no accept was ever sent and the count reports the
        //absence rather than a failure to reach a quorum with accepts in flight.
        Assert.IsFalse(outcome.IsChosen);
        Assert.AreEqual(0, outcome.AcceptedCount);
    }


    [TestMethod]
    public async Task FastWriteCommitsOverAsyncChannelTransport()
    {
        const int count = 5;
        ConsensusNode<string>[] nodes = CreateNodes(count);
        var requestChannels = new Channel<ConsensusRequest<string>>[count];
        var replyChannels = new Channel<ConsensusReply<string>>[count];
        var runTasks = new Task[count];
        var endpoints = new ConsensusEndpointDelegate<string>[count];

        for(int i = 0; i < count; i++)
        {
            Channel<ConsensusRequest<string>> requests = Channel.CreateUnbounded<ConsensusRequest<string>>();
            Channel<ConsensusReply<string>> replies = Channel.CreateUnbounded<ConsensusReply<string>>();
            requestChannels[i] = requests;
            replyChannels[i] = replies;

            runTasks[i] = nodes[i].RunAsync(
                requests.Reader.ReadAllAsync(TestContext.CancellationToken),
                (reply, token) => replies.Writer.WriteAsync(reply, token),
                cancellationToken: TestContext.CancellationToken);

            endpoints[i] = async (request, token) =>
            {
                await requests.Writer.WriteAsync(request, token).ConfigureAwait(false);

                return await replies.Reader.ReadAsync(token).ConfigureAwait(false);
            };
        }

        FastProposer<string> proposer = new(endpoints);

        (int accepted, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(5, accepted);
        Assert.IsTrue(committed);

        foreach(Channel<ConsensusRequest<string>> requests in requestChannels)
        {
            requests.Writer.Complete();
        }

        await Task.WhenAll(runTasks).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task FastWriteRejectsClassicBallot()
    {
        FastProposer<string> proposer = new(CreateNodes(3).Select(DirectEndpoint).ToArray());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await proposer.TryFastWriteAsync(FastBallot.Classic(1, R1), "x", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    [TestMethod]
    public void NodeHandleRejectsNullRequest()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ConsensusNode<string>().Handle(null!));
    }


    [TestMethod]
    public void ProposerRejectsEmptyAcceptorList()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new FastProposer<string>([]));
    }


    /// <summary>Pins that a fast write commits when EXACTLY a fast quorum accepts (4*accepted == 3*N), not only when it strictly exceeds that boundary.</summary>
    [TestMethod]
    public async Task FastWriteCommitsExactlyAtTheFastQuorumBoundary()
    {
        ConsensusNode<string>[] nodes = CreateNodes(4);
        var endpoints = new ConsensusEndpointDelegate<string>[4];
        for(int i = 0; i < 4; i++)
        {
            int index = i;
            endpoints[i] = index == 3
                ? (_, _) => throw new IOException($"acceptor {index} is partitioned")
                : DirectEndpoint(nodes[index]);
        }

        FastProposer<string> proposer = new(endpoints);

        (int accepted, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(3, accepted);
        Assert.IsTrue(committed);
    }


    /// <summary>Pins that the constructor rejects a null acceptor list with <see cref="ArgumentNullException"/> before ever inspecting its count.</summary>
    [TestMethod]
    public void ProposerRejectsNullAcceptorList()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new FastProposer<string>(null!));
    }


    /// <summary>
    /// Pins that recovery adopts the recovered value directly from the highest-ballot prepare reply when that
    /// ballot is classic, rather than re-tallying it as if it were a contended fast round.
    /// </summary>
    [TestMethod]
    public async Task RecoveryDoesNotTallyFastWinnersUnderAClassicHighestBallot()
    {
        ConsensusNode<string>[] nodes = CreateNodes(3);
        nodes[0].Handle(new AcceptRequest<string>(FastBallot.Classic(1, R1), "a"));
        nodes[1].Handle(new AcceptRequest<string>(FastBallot.Classic(1, R1), "b"));
        nodes[2].Handle(new AcceptRequest<string>(FastBallot.Classic(1, R1), "b"));

        FastProposer<string> proposer = new(nodes.Select(DirectEndpoint).ToArray());

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(2, R1), current => current!, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("a", outcome.Value);
    }


    /// <summary>Pins that RecoverAsync requires a classic (proposer-owned) ballot and rejects a fast one before contacting any acceptor.</summary>
    [TestMethod]
    public async Task RecoveryRejectsFastBallot()
    {
        FastProposer<string> proposer = new(CreateNodes(3).Select(DirectEndpoint).ToArray());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await proposer.RecoverAsync(FastBallot.Fast(1), current => current!, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that when the fast round split evenly, tallying keeps the FIRST value reaching the max vote count
    /// rather than letting a later value with an equal count overtake it.
    /// </summary>
    [TestMethod]
    public async Task RecoveryPicksTheFirstFastWinnerOnATiedVoteCount()
    {
        ConsensusNode<string>[] nodes = CreateNodes(4);
        nodes[0].Handle(new AcceptRequest<string>(FastBallot.Fast(1), "x"));
        nodes[1].Handle(new AcceptRequest<string>(FastBallot.Fast(1), "x"));
        nodes[2].Handle(new AcceptRequest<string>(FastBallot.Fast(1), "y"));
        nodes[3].Handle(new AcceptRequest<string>(FastBallot.Fast(1), "y"));

        FastProposer<string> proposer = new(nodes.Select(DirectEndpoint).ToArray());

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(2, R1), current => current!, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }


    /// <summary>Pins that RecoverAsync rejects a null change function before sending any prepare request.</summary>
    [TestMethod]
    public async Task RecoveryRejectsNullUpdate()
    {
        FastProposer<string> proposer = new(CreateNodes(3).Select(DirectEndpoint).ToArray());

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await proposer.RecoverAsync(FastBallot.Classic(1, R1), null!, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// Pins that when multiple acceptors report the SAME highest accepted (classic) ballot, recovery adopts
    /// the value from the first one encountered rather than letting a later tied reply overwrite it.
    /// </summary>
    [TestMethod]
    public async Task RecoveryPicksTheFirstAcceptorAtTheHighestAcceptedBallotOnATie()
    {
        ConsensusNode<string>[] nodes = CreateNodes(2);
        nodes[0].Handle(new AcceptRequest<string>(FastBallot.Classic(1, R1), "a"));
        nodes[1].Handle(new AcceptRequest<string>(FastBallot.Classic(1, R1), "b"));

        FastProposer<string> proposer = new(nodes.Select(DirectEndpoint).ToArray());

        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(2, R1), current => current!, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("a", outcome.Value);
    }


    /// <summary>Pins the exact fast-quorum arithmetic, floor((3N + 3) / 4), against a concrete acceptor count.</summary>
    [TestMethod]
    public void FastQuorumComputesSupermajorityOfThreeNPlusThreeOverFour()
    {
        FastProposer<string> proposer = new(CreateNodes(5).Select(DirectEndpoint).ToArray());

        Assert.AreEqual(4, proposer.FastQuorum);
    }


    /// <summary>Pins that an acceptor endpoint's OperationCanceledException propagates out of TryFastWriteAsync instead of being swallowed as an unreachable-acceptor no-response.</summary>
    [TestMethod]
    public async Task FastWritePropagatesCancellationInsteadOfTreatingItAsNoResponse()
    {
        ConsensusEndpointDelegate<string>[] endpoints = [(_, token) => throw new OperationCanceledException(token)];
        FastProposer<string> proposer = new(endpoints);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// The fast write does not resume on the synchronization context it was started on. Its continuation, after
    /// the acceptor round completes later, runs off that context, so a host that blocks the starting thread on
    /// the fast write cannot deadlock it.
    /// </summary>
    /// <remarks>
    /// The single endpoint returns a reply the test holds open, so the write parks on the acceptor round; the
    /// context is current only while the call runs synchronously, so a continuation reaches it only by having
    /// captured it. This covers every suspending await on the fast-write path: the endpoint call, the gather
    /// over all endpoints, and the outer await of the round.
    /// </remarks>
    [TestMethod]
    public async Task TheFastWriteDoesNotResumeOnTheCallersContextAfterAPendingAcceptorRound()
    {
        TaskCompletionSource<ConsensusReply<string>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsensusEndpointDelegate<string>[] endpoints = [(_, _) => new ValueTask<ConsensusReply<string>>(gate.Task)];
        FastProposer<string> proposer = new(endpoints);
        PostCountingSynchronizationContext context = new();

        Task<(int AcceptedCount, bool IsCommitted)> write = context.Start(
            () => proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken));

        Assert.IsFalse(write.IsCompleted);

        gate.SetResult(new AcceptReply<string>(true, FastBallot.Fast(1)));
        (int accepted, bool committed) = await write.ConfigureAwait(false);

        Assert.AreEqual(1, accepted);
        Assert.IsTrue(committed);
        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// Recovery does not resume on the synchronization context it was started on after a pending prepare round.
    /// The continuation after the prepare replies arrive later runs off that context.
    /// </summary>
    /// <remarks>
    /// The endpoint answers the accept round at once and holds the prepare round open, so the prepare round is
    /// the only await that parks the recovery here.
    /// </remarks>
    [TestMethod]
    public async Task RecoveryDoesNotResumeOnTheCallersContextAfterAPendingPrepareRound()
    {
        TaskCompletionSource<ConsensusReply<string>> prepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsensusEndpointDelegate<string>[] endpoints =
        [
            (request, _) => request is PrepareRequest<string>
                ? new ValueTask<ConsensusReply<string>>(prepareGate.Task)
                : new ValueTask<ConsensusReply<string>>(new AcceptReply<string>(true, FastBallot.Classic(1, R1)))
        ];
        FastProposer<string> proposer = new(endpoints);
        PostCountingSynchronizationContext context = new();

        Task<ChangeOutcome<string>> recover = context.Start(
            () => proposer.RecoverAsync(FastBallot.Classic(1, R1), _ => "x", TestContext.CancellationToken));

        Assert.IsFalse(recover.IsCompleted);

        prepareGate.SetResult(new PrepareReply<string>(true, FastBallot.Zero, null, FastBallot.Zero));
        ChangeOutcome<string> outcome = await recover.ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// Recovery does not resume on the synchronization context it was started on after a pending accept round.
    /// The prepare round completes at once, so the accept round is the await that parks the recovery, and its
    /// continuation runs off that context.
    /// </summary>
    [TestMethod]
    public async Task RecoveryDoesNotResumeOnTheCallersContextAfterAPendingAcceptRound()
    {
        TaskCompletionSource<ConsensusReply<string>> acceptGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsensusEndpointDelegate<string>[] endpoints =
        [
            (request, _) => request is PrepareRequest<string>
                ? new ValueTask<ConsensusReply<string>>(new PrepareReply<string>(true, FastBallot.Zero, null, FastBallot.Zero))
                : new ValueTask<ConsensusReply<string>>(acceptGate.Task)
        ];
        FastProposer<string> proposer = new(endpoints);
        PostCountingSynchronizationContext context = new();

        Task<ChangeOutcome<string>> recover = context.Start(
            () => proposer.RecoverAsync(FastBallot.Classic(1, R1), _ => "x", TestContext.CancellationToken));

        Assert.IsFalse(recover.IsCompleted);

        acceptGate.SetResult(new AcceptReply<string>(true, FastBallot.Classic(1, R1)));
        ChangeOutcome<string> outcome = await recover.ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
        Assert.AreEqual(0, context.Posts);
    }


    private static ConsensusNode<string>[] CreateNodes(int count)
    {
        var nodes = new ConsensusNode<string>[count];
        for(int i = 0; i < count; i++)
        {
            nodes[i] = new ConsensusNode<string>();
        }

        return nodes;
    }


    private static ConsensusEndpointDelegate<string> DirectEndpoint(ConsensusNode<string> node)
    {
        return (request, _) => ValueTask.FromResult(node.Handle(request));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
