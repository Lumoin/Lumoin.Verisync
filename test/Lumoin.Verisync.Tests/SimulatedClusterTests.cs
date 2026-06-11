using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

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
    public async Task PiggybackedNextBallotChainsTwoFastRoundsCoordinatorFree()
    {
        SimulatedCluster<string> cluster = new(5);
        FastProposer<string> proposer = cluster.CreateProposer();

        //The first fast write piggybacks fast(2), establishing the next fast round on every acceptor that
        //accepts — the recurring-fast-round handoff the original Fast CASPaxos design uses.
        (int firstAccepted, bool firstCommitted) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken, FastBallot.Fast(2)).ConfigureAwait(false);

        Assert.AreEqual(5, firstAccepted);
        Assert.IsTrue(firstCommitted);

        //The second write commits at fast(2) in a single round trip — no prepare, because the piggyback
        //already promised fast(2) on each acceptor.
        (int secondAccepted, bool secondCommitted) = await proposer.TryFastWriteAsync(FastBallot.Fast(2), "y", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(5, secondAccepted);
        Assert.IsTrue(secondCommitted);
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

        //A fast retry reuses the same fast ballot and value: the three earlier acceptors treat it
        //idempotently and the healed acceptor accepts it fresh. A higher fast round would be rejected —
        //only the pre-promised initial fast round is blind-writable.
        (int accepted, bool committedAfterHeal) = await proposer.TryFastWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

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
