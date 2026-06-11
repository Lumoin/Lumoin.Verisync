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
