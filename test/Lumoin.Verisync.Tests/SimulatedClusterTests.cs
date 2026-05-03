using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class SimulatedClusterTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId R1 { get; } = Replica(1);


    [TestMethod]
    public async Task FastWriteCommitsWhenAllReachable()
    {
        SimulatedCluster<string> cluster = new(5);
        FastProposer<string> proposer = cluster.CreateProposer();

        (int accepted, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(5, accepted);
        Assert.IsTrue(committed);
    }


    [TestMethod]
    public async Task FastWriteStillCommitsWithOneAcceptorPartitioned()
    {
        SimulatedCluster<string> cluster = new(5);
        cluster.Partition(4);
        FastProposer<string> proposer = cluster.CreateProposer();

        (int accepted, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        //Four reachable acceptors meet the supermajority fast quorum of (3*5+3)/4 = 4.
        Assert.AreEqual(4, accepted);
        Assert.IsTrue(committed);
    }


    [TestMethod]
    public async Task FastWriteFailsWhenFastQuorumUnreachable()
    {
        SimulatedCluster<string> cluster = new(5);
        cluster.Partition(3);
        cluster.Partition(4);
        FastProposer<string> proposer = cluster.CreateProposer();

        (int accepted, bool committed) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        //Three reachable acceptors fall short of the fast quorum of four.
        Assert.AreEqual(3, accepted);
        Assert.IsFalse(committed);
    }


    [TestMethod]
    public async Task ClassicRecoveryCommitsWhenOnlyAMajorityIsReachable()
    {
        SimulatedCluster<string> cluster = new(5);
        cluster.Partition(3);
        cluster.Partition(4);
        FastProposer<string> proposer = cluster.CreateProposer();

        //The fast quorum (four) is unreachable, but the classic quorum (a majority of three) is reachable, so a
        //leadered recovery round still commits — the leaderless-to-leadered fallback under partition.
        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(1, R1), _ => "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }


    [TestMethod]
    public async Task ConcurrentProposersSplitThenRecoverTheWinner()
    {
        SimulatedCluster<string> cluster = new(5);

        //Two proposers race on the same fast ballot: three acceptors took "x", two took "y".
        for(int i = 0; i < 3; i++)
        {
            cluster.Node(i).Handle(new AcceptRequest<string>(FastBallot.Fast(1), "x"));
        }

        for(int i = 3; i < 5; i++)
        {
            cluster.Node(i).Handle(new AcceptRequest<string>(FastBallot.Fast(1), "y"));
        }

        FastProposer<string> proposer = cluster.CreateProposer();
        ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(1, R1), current => current!, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }


    [TestMethod]
    public async Task HealingAPartitionRestoresTheFastQuorum()
    {
        SimulatedCluster<string> cluster = new(5);
        cluster.Partition(3);
        cluster.Partition(4);
        FastProposer<string> proposer = cluster.CreateProposer();

        (_, bool committedWhilePartitioned) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsFalse(committedWhilePartitioned);

        cluster.Heal(3);

        (int accepted, bool committedAfterHeal) = await proposer.TryFastWriteAsync(FastBallot.Fast(2), "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(4, accepted);
        Assert.IsTrue(committedAfterHeal);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
