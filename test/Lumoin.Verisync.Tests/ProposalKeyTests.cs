using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The proposal key's unit suite, which also covers <see cref="ProposerLane"/> because the key's whole
/// order rests on the lane's. The key is Appendix A's tiebreaking approach: priority first, then the
/// proposer identity, so the order is total whenever the identities are distinct. The lane exists because
/// a key must identify at most one value inside an instance, and two concurrent callers on one replica
/// would otherwise attach one key to two values.
/// </summary>
[TestClass]
internal sealed class ProposalKeyTests
{
    /// <summary>
    /// Identities are built from fixed bytes rather than generated, so A sorts below B by ReplicaId's
    /// lexicographic byte order and the direction of every tie-break is a property of the test rather than of
    /// the run.
    /// </summary>
    private static ReplicaId ReplicaA { get; } = Replica(1);
    private static ReplicaId ReplicaB { get; } = Replica(2);


    [TestMethod]
    public void TheFixedReplicasSortInTheDirectionTheseTestsAssume()
    {
        Assert.IsTrue(ReplicaA < ReplicaB);
    }


    [TestMethod]
    public void ANegativeLaneThrowsAtConstruction()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new ProposerLane(ReplicaA, -1));
    }


    [TestMethod]
    public void ANegativeLaneThrowsThroughAWithExpression()
    {
        //The validating accessor is what makes the copy path as safe as the constructor; without it a
        //restamped lane could carry a value the constructor would have refused.
        ProposerLane lane = ProposerLane.For(ReplicaA);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = lane with { Lane = -1 });
    }


    [TestMethod]
    public void ForIsLaneZero()
    {
        ProposerLane lane = ProposerLane.For(ReplicaA);

        Assert.AreEqual(ReplicaA, lane.Replica);
        Assert.AreEqual(0, lane.Lane);
        Assert.AreEqual(new ProposerLane(ReplicaA, 0), lane);
    }


    [TestMethod]
    public void TheDefaultLaneIsLaneZeroOfTheAllZeroReplica()
    {
        //The zero value is degenerate rather than invalid, and it has to be legal because no accessor can
        //defend a default.
        ProposerLane lane = default;

        Assert.AreEqual(0, lane.Lane);
        Assert.AreEqual(Replica(0), lane.Replica);
    }


    [TestMethod]
    public void LaneOrderingIsReplicaThenLane()
    {
        ProposerLane lowReplicaHighLane = new(ReplicaA, 99);
        ProposerLane highReplicaLowLane = new(ReplicaB, 0);

        //The replica dominates: a high lane on the lower replica still sorts below lane zero of the higher.
        Assert.IsTrue(lowReplicaHighLane < highReplicaLowLane);
        Assert.IsTrue(highReplicaLowLane > lowReplicaHighLane);
        Assert.IsLessThan(0, lowReplicaHighLane.CompareTo(highReplicaLowLane));
    }


    [TestMethod]
    public void TwoLanesOfOneReplicaAreOrderedAndDistinct()
    {
        ProposerLane first = new(ReplicaA, 0);
        ProposerLane second = new(ReplicaA, 1);

        Assert.AreNotEqual(first, second);
        Assert.IsTrue(first < second);
        Assert.IsTrue(first <= second);
        Assert.IsTrue(second >= first);
        Assert.IsFalse(second < first);
        Assert.AreEqual(0, first.CompareTo(first));
    }


    [TestMethod]
    public void PriorityDominatesOwner()
    {
        ProposalKey lowPriorityHighOwner = new(new ProposalPriority(10), ProposerLane.For(ReplicaB));
        ProposalKey highPriorityLowOwner = new(new ProposalPriority(20), ProposerLane.For(ReplicaA));

        Assert.IsTrue(lowPriorityHighOwner < highPriorityLowOwner);
        Assert.IsTrue(highPriorityLowOwner > lowPriorityHighOwner);
    }


    [TestMethod]
    public void OwnerBreaksAPriorityTie()
    {
        ProposalKey byA = new(new ProposalPriority(10), ProposerLane.For(ReplicaA));
        ProposalKey byB = new(new ProposalPriority(10), ProposerLane.For(ReplicaB));

        Assert.AreNotEqual(byA, byB);
        Assert.IsTrue(byA < byB);
        Assert.IsLessThan(0, byA.CompareTo(byB));
        Assert.IsGreaterThan(0, byB.CompareTo(byA));
    }


    [TestMethod]
    public void ALaneDifferenceAloneOrdersTwoKeysOfOneReplica()
    {
        //This is the reason the reserved priority is granted to a lane rather than to a replica: two lanes of
        //one replica are distinct proposer identities and the order over them must be total.
        ProposalKey laneZero = new(ProposalPriority.Reserved, new ProposerLane(ReplicaA, 0));
        ProposalKey laneOne = new(ProposalPriority.Reserved, new ProposerLane(ReplicaA, 1));

        Assert.AreNotEqual(laneZero, laneOne);
        Assert.IsTrue(laneZero < laneOne);
    }


    [TestMethod]
    public void TheOrderIsTotalOverDistinctKeys()
    {
        ProposalKey[] keys =
        [
            new(ProposalPriority.None, ProposerLane.For(ReplicaA)),
            new(ProposalPriority.Lowest, ProposerLane.For(ReplicaA)),
            new(ProposalPriority.Lowest, ProposerLane.For(ReplicaB)),
            new(new ProposalPriority(7), new ProposerLane(ReplicaA, 1)),
            new(new ProposalPriority(7), new ProposerLane(ReplicaB, 0)),
            new(ProposalPriority.Reserved, ProposerLane.For(ReplicaA)),
            new(ProposalPriority.Reserved, ProposerLane.For(ReplicaB))
        ];

        //The array is written in ascending order, so every earlier key must compare strictly below every
        //later one and the relation must be antisymmetric on each pair.
        for(int i = 0; i < keys.Length; i++)
        {
            for(int j = i + 1; j < keys.Length; j++)
            {
                Assert.IsLessThan(0, keys[i].CompareTo(keys[j]));
                Assert.IsGreaterThan(0, keys[j].CompareTo(keys[i]));
                Assert.IsTrue(keys[i] < keys[j]);
                Assert.IsTrue(keys[j] > keys[i]);
                Assert.AreNotEqual(keys[i], keys[j]);
            }

            Assert.AreEqual(0, keys[i].CompareTo(keys[i]));
            Assert.IsTrue(keys[i] <= keys[i]);
            Assert.IsTrue(keys[i] >= keys[i]);
        }
    }


    [TestMethod]
    public void WithPriorityReplacesThePriorityAndKeepsTheOwner()
    {
        ProposalKey key = new(ProposalPriority.Reserved, new ProposerLane(ReplicaA, 3));

        ProposalKey restamped = key.WithPriority(ProposalPriority.Lowest);

        Assert.AreEqual(ProposalPriority.Lowest, restamped.Priority);
        Assert.AreEqual(key.Owner, restamped.Owner);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
