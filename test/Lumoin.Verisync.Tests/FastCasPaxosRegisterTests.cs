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


    /// <summary>
    /// An acceptor index equal to the acceptor count is out of range: valid indices end one below it.
    /// </summary>
    [TestMethod]
    public void ProposeFastReachingRejectsAnIndexEqualToAcceptorCount()
    {
        ImmutableHashSet<int> indices = [3];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => register.ProposeFastReaching(FastBallot.Fast(1), "x", indices));
        Assert.AreEqual("acceptorIndices", exception.ParamName);
    }


    /// <summary>
    /// The fast-quorum predicate is closed at its lower boundary: a count where 4*acceptedCount exactly equals
    /// 3*Acceptors.Length must already count as a fast quorum, not only counts strictly above it.
    /// </summary>
    [TestMethod]
    public void IsFastQuorumIncludesTheExactBoundaryCount()
    {
        FastCasPaxosRegister<string> four = FastCasPaxosRegister<string>.WithAcceptors(4);

        //4 * 3 == 3 * 4 exactly: the boundary where >= and > disagree.
        Assert.IsTrue(four.IsFastQuorum(3));
    }


    /// <summary>
    /// A negative acceptor index is refused with the parameter name naming the indices set.
    /// </summary>
    [TestMethod]
    public void ProposeFastReachingRejectsANegativeIndex()
    {
        ImmutableHashSet<int> indices = [-1];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => register.ProposeFastReaching(FastBallot.Fast(1), "x", indices));
        Assert.AreEqual("acceptorIndices", exception.ParamName);
    }


    /// <summary>
    /// Recovery tallies the fast-round winner by counted votes, not by which acceptor the prepare loop visits
    /// first: a minority sitting at the lower indices must not be recovered over a later majority.
    /// </summary>
    [TestMethod]
    public void RecoveryTalliesTheFastRoundWinnerRegardlessOfAcceptorIndexOrder()
    {
        ImmutableHashSet<int> minority = [0, 1];
        ImmutableHashSet<int> majority = [2, 3, 4];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);
        (FastCasPaxosRegister<string> afterMinority, _) = register.ProposeFastReaching(FastBallot.Fast(1), "y", minority);
        (FastCasPaxosRegister<string> afterMajority, _) = afterMinority.ProposeFastReaching(FastBallot.Fast(1), "x", majority);

        (_, ChangeOutcome<string> outcome) = afterMajority.Recover(FastBallot.Classic(1, R1), current => current!);

        //The minority ("y") sits at the LOWER indices the prepare loop visits first; only an actual tally, not
        //the first value seen, can recover the majority ("x") here.
        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }


    /// <summary>
    /// A classic recovery at a ballot below every acceptor's current promise is refused before any acceptor is
    /// touched: the outcome must report not-chosen with zero accepts, never a chosen value from an unreached quorum.
    /// </summary>
    [TestMethod]
    public void RecoverAtALowerBallotAfterAHigherOneReportsNotChosen()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);
        (FastCasPaxosRegister<string> afterHigh, ChangeOutcome<string> highOutcome) = register.Recover(FastBallot.Classic(2, R1), _ => "first");
        Assert.IsTrue(highOutcome.IsChosen);

        //Every acceptor is now promised to round 2; a round-1 recovery is refused by every prepare.
        (_, ChangeOutcome<string> lowOutcome) = afterHigh.Recover(FastBallot.Classic(1, R1), _ => "second");

        Assert.IsFalse(lowOutcome.IsChosen);
        Assert.AreEqual(0, lowOutcome.AcceptedCount);
    }


    /// <summary>
    /// Recovering at a classic ballot that has already carried a different value is refused at the accept step:
    /// a ballot may carry only one value, so the second recovery must report not-chosen with zero accepts.
    /// </summary>
    [TestMethod]
    public void RecoverRejectsAReusedBallotCarryingADifferentValue()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);
        (FastCasPaxosRegister<string> afterFirst, ChangeOutcome<string> first) = register.Recover(FastBallot.Classic(1, R1), _ => "first");
        Assert.IsTrue(first.IsChosen);

        //Re-using the SAME classic ballot with an update that produces a different value: every acceptor
        //already holds this exact ballot against "first", so the accept step must reject all of them.
        (_, ChangeOutcome<string> second) = afterFirst.Recover(FastBallot.Classic(1, R1), _ => "second");

        Assert.IsFalse(second.IsChosen);
        Assert.AreEqual(0, second.AcceptedCount);
        Assert.IsNull(second.Value);
    }


    /// <summary>
    /// At two acceptors the classic quorum equals the acceptor count, so a fully-promised, fully-accepted
    /// recovery sits exactly at the quorum boundary rather than strictly above it.
    /// </summary>
    [TestMethod]
    public void RecoverSucceedsAtExactlyTwoOfTwoAcceptors()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(2);

        (_, ChangeOutcome<string> outcome) = register.Recover(FastBallot.Classic(1, R1), _ => "first");

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("first", outcome.Value);
        Assert.AreEqual(2, outcome.AcceptedCount);
    }


    /// <summary>
    /// A null set of acceptor indices is refused before any acceptor is touched.
    /// </summary>
    [TestMethod]
    public void ProposeFastReachingRejectsNullIndices()
    {
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(3);

        Assert.ThrowsExactly<ArgumentNullException>(() => register.ProposeFastReaching(FastBallot.Fast(1), "x", null!));
    }


    /// <summary>
    /// Recovery's tally only counts responses at the highest accepted ballot: an acceptor that never received a
    /// fast proposal, and so still reports the zero ballot, must not be tallied alongside the fast-round winner.
    /// </summary>
    [TestMethod]
    public void RecoveryTallyIgnoresAcceptorsAtALowerAcceptedBallot()
    {
        ImmutableHashSet<int> reached = [0, 1];
        FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(5);
        (FastCasPaxosRegister<string> afterFast, _) = register.ProposeFastReaching(FastBallot.Fast(1), "x", reached);

        //Acceptors 2, 3 and 4 never received the fast proposal and still stand at the zero ballot: three
        //zero-ballot responses outnumber the two real "x" responses, so a tally that failed to filter by
        //ballot would recover the untouched acceptors' default value instead.
        (_, ChangeOutcome<string> outcome) = afterFast.Recover(FastBallot.Classic(1, R1), current => current!);

        Assert.IsTrue(outcome.IsChosen);
        Assert.AreEqual("x", outcome.Value);
    }
}
