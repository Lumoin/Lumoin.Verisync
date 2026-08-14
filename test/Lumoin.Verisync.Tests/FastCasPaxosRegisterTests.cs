using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class FastCasPaxosRegisterTests
{
    private static ReplicaId R1 { get; } = Replica(1);


    [TestMethod]
    public void QuorumSizesFollowFastPaxos()
    {
        FastCasPaxosRegister<string> five = FastCasPaxosRegister<string>.WithAcceptors(5);

        Assert.AreEqual(4, five.FastQuorum);
        Assert.AreEqual(3, five.ClassicQuorum);
        Assert.AreEqual(5, five.AcceptorCount);
    }


    [TestMethod]
    public void WithAcceptorsRejectsNonPositiveCount()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => FastCasPaxosRegister<string>.WithAcceptors(0));
    }


    [TestMethod]
    public void UncontendedFastWriteReachesFastQuorum()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);

        (_, int accepted) = register.ProposeFast(FastBallot.Fast(1), "x");

        Assert.AreEqual(5, accepted);
        Assert.IsTrue(register.IsFastQuorum(accepted));
    }


    [TestMethod]
    public void ProposeFastRejectsClassicBallot()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);

        Assert.ThrowsExactly<ArgumentException>(() => register.ProposeFast(FastBallot.Classic(1, R1), "x"));
    }


    [TestMethod]
    public void SplitFastRoundMissesFastQuorum()
    {
        ImmutableHashSet<int> majority = [0, 1, 2];
        ImmutableHashSet<int> minority = [3, 4];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);

        (FastCasPaxosRegister<string> afterX, int xCount) = register.ProposeFastReaching(FastBallot.Fast(1), "x", majority);
        (FastCasPaxosRegister<string> afterY, int yCount) = afterX.ProposeFastReaching(FastBallot.Fast(1), "y", minority);

        Assert.AreEqual(3, xCount);
        Assert.AreEqual(2, yCount);
        Assert.IsFalse(afterY.IsFastQuorum(xCount));
        Assert.IsFalse(afterY.IsFastQuorum(yCount));
    }


    /// <summary>
    /// The count a caller compares against the fast quorum must be a count of distinct acceptors.
    /// </summary>
    /// <remarks>
    /// Four of five is a fast quorum here, so a repeat that was folded in rather than refused would
    /// manufacture one.
    /// </remarks>
    [TestMethod]
    public void ThreeDistinctAcceptorsCannotReportAFastQuorum()
    {
        ImmutableHashSet<int> three = [0, 1, 2];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);

        (FastCasPaxosRegister<string> after, int accepted) = register.ProposeFastReaching(FastBallot.Fast(1), "x", three);

        Assert.AreEqual(3, accepted);
        Assert.IsFalse(after.IsFastQuorum(accepted));
        Assert.AreEqual(4, after.FastQuorum);
    }


    [TestMethod]
    public void RecoveryRecoversTheFastRoundWinner()
    {
        ImmutableHashSet<int> majority = [0, 1, 2];
        ImmutableHashSet<int> minority = [3, 4];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);
        (FastCasPaxosRegister<string> afterX, _) = register.ProposeFastReaching(FastBallot.Fast(1), "x", majority);
        (FastCasPaxosRegister<string> afterY, _) = afterX.ProposeFastReaching(FastBallot.Fast(1), "y", minority);

        (_, ChangeOutcome<string> outcome) = afterY.Recover(FastBallot.Classic(1, R1), current => current!);

        //"x" reached three acceptors, "y" two; recovery must preserve the dominant value.
        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }


    [TestMethod]
    public void RecoveryOnFreshRegisterAppliesUpdateToDefault()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);

        (_, ChangeOutcome<string> outcome) = register.Recover(FastBallot.Classic(1, R1), _ => "first");

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("first", outcome.Value);
        Assert.AreEqual(3, outcome.AcceptedCount);
    }


    [TestMethod]
    public void UncontendedFastValueIsRecoverable()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);
        (FastCasPaxosRegister<string> afterWrite, _) = register.ProposeFast(FastBallot.Fast(1), "x");

        (_, ChangeOutcome<string> outcome) = afterWrite.Recover(FastBallot.Classic(2, R1), current => current!);

        Assert.AreEqual("x", outcome.Value);
    }


    [TestMethod]
    public void RecoverRejectsFastBallot()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);

        Assert.ThrowsExactly<ArgumentException>(() => register.Recover(FastBallot.Fast(1), current => current!));
    }


    [TestMethod]
    public void RecoverRejectsNullUpdate()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);

        Assert.ThrowsExactly<ArgumentNullException>(() => register.Recover(FastBallot.Classic(1, R1), null!));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
