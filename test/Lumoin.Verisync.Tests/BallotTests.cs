using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class BallotTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void OrdersByRoundFirst()
    {
        Assert.IsTrue(new Ballot(1, R2) < new Ballot(2, R1));
        Assert.IsTrue(new Ballot(2, R1) > new Ballot(1, R2));
    }


    [TestMethod]
    public void BreaksRoundTieByProposer()
    {
        //R1 ([1]) sorts before R2 ([2]).
        Assert.IsTrue(new Ballot(1, R1) < new Ballot(1, R2));
    }


    [TestMethod]
    public void ComparisonOperatorsAreConsistent()
    {
        Ballot lower = new(1, R1);
        Ballot higher = new(2, R1);

        Assert.IsTrue(lower <= higher);
        Assert.IsTrue(higher >= lower);
        Assert.IsFalse(lower >= higher);
    }


    [TestMethod]
    public void EqualityByRoundAndProposer()
    {
        Assert.AreEqual(new Ballot(3, R1), new Ballot(3, R1));
        Assert.AreNotEqual(new Ballot(3, R1), new Ballot(3, R2));
    }


    [TestMethod]
    public void RejectsRoundBelowOne()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Ballot(0, R1));

        //A negative round, as an overflowed counter would produce, must be rejected at construction.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Ballot(-1, R1));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
