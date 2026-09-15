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


    /// <summary>Pins that the <c>&gt;=</c> operator holds for two ballots that compare equal.</summary>
    [TestMethod]
    public void GreaterOrEqualHoldsForEqualBallots()
    {
        FastBallot a = FastBallot.Classic(1, R1);
        FastBallot b = FastBallot.Classic(1, R1);

        Assert.IsTrue(a >= b);
    }


    /// <summary>Pins that <see cref="FastBallot.Classic"/> rejects a non-positive round, matching the guard on <see cref="FastBallot.Fast"/>.</summary>
    [TestMethod]
    public void ClassicRejectsNonPositiveRound()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FastBallot.Classic(0, R1));
    }


    /// <summary>Pins that the <c>&lt;=</c> operator holds for two ballots that compare equal.</summary>
    [TestMethod]
    public void LessOrEqualHoldsForEqualBallots()
    {
        FastBallot a = FastBallot.Fast(1);
        FastBallot b = FastBallot.Fast(1);

        Assert.IsTrue(a <= b);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
