using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class FastBallotTests
{
    private static ReplicaId R1 { get; } = Replica(1);


    [TestMethod]
    public void FastSortsBelowClassicOfSameRound()
    {
        Assert.IsTrue(FastBallot.Fast(1) < FastBallot.Classic(1, R1));
        Assert.IsTrue(FastBallot.Classic(1, R1) > FastBallot.Fast(1));
    }


    [TestMethod]
    public void HigherRoundSortsAbove()
    {
        Assert.IsTrue(FastBallot.Fast(1) < FastBallot.Fast(2));
        Assert.IsTrue(FastBallot.Classic(1, R1) < FastBallot.Fast(2));
    }


    [TestMethod]
    public void IsFastAndIsZeroClassify()
    {
        Assert.IsTrue(FastBallot.Fast(1).IsFast);
        Assert.IsFalse(FastBallot.Classic(1, R1).IsFast);
        Assert.IsTrue(FastBallot.Zero.IsZero);
        Assert.IsFalse(FastBallot.Fast(1).IsZero);
    }


    [TestMethod]
    public void FastRejectsNonPositiveRound()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FastBallot.Fast(0));
    }


    [TestMethod]
    public void ComparisonOperatorsAreConsistent()
    {
        FastBallot lower = FastBallot.Fast(1);
        FastBallot higher = FastBallot.Classic(1, R1);

        Assert.IsTrue(lower <= higher);
        Assert.IsTrue(higher >= lower);
        Assert.IsFalse(lower >= higher);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
