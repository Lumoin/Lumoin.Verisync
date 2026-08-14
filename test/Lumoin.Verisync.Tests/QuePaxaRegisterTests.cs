using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The register's unit suite: the argument contract of <c>Step</c>, the quorum arithmetic, the round
/// template, and the two uncontended shapes <c>Propose</c> can demonstrate. A single synchronous proposer
/// never observes a split, so nothing here says anything about contention; that is the directed suite's
/// job and the agreement harness's.
/// </summary>
[TestClass]
internal sealed class QuePaxaRegisterTests
{
    /// <summary>
    /// Identities from fixed bytes: no test below may depend on which way a generated pair happened to sort.
    /// </summary>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));

    private static ImmutableArray<int> AllThree { get; } = [0, 1, 2];


    [TestMethod]
    public void QuorumIsAStrictMajorityOfTheRecorders()
    {
        //A strict majority is exactly what the proofs need; a deployment sized above 2f + 1 may use larger
        //quorums and stays safe, but is never required to.
        Assert.AreEqual(1, QuePaxaRegister<string>.WithRecorders(1).Quorum);
        Assert.AreEqual(2, QuePaxaRegister<string>.WithRecorders(2).Quorum);
        Assert.AreEqual(2, QuePaxaRegister<string>.WithRecorders(3).Quorum);
        Assert.AreEqual(3, QuePaxaRegister<string>.WithRecorders(4).Quorum);
        Assert.AreEqual(3, QuePaxaRegister<string>.WithRecorders(5).Quorum);
        Assert.AreEqual(4, QuePaxaRegister<string>.WithRecorders(7).Quorum);
    }


    [TestMethod]
    public void WithRecordersBuildsLeaderlessRecordersAndLedByBuildsConfiguredOnes()
    {
        QuePaxaRegister<string> leaderless = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRegister<string> leadered = QuePaxaRegister<string>.LedBy(3, LaneA);

        Assert.AreEqual(3, leaderless.RecorderCount);
        Assert.HasCount(3, leaderless.Recorders);
        foreach(QuePaxaRecorder<string> recorder in leaderless.Recorders)
        {
            Assert.IsNull(recorder.ConfiguredLeader);
        }

        Assert.AreEqual(3, leadered.RecorderCount);
        foreach(QuePaxaRecorder<string> recorder in leadered.Recorders)
        {
            Assert.AreEqual(LaneA, recorder.ConfiguredLeader);
        }
    }


    [TestMethod]
    public void TheFactoriesRejectDegenerateRecorderSets()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = QuePaxaRegister<string>.WithRecorders(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = QuePaxaRegister<string>.WithRecorders(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = QuePaxaRegister<string>.LedBy(0, LaneA));
        Assert.ThrowsExactly<ArgumentException>(() => _ = QuePaxaRegister<string>.FromRecorders(default));
        Assert.ThrowsExactly<ArgumentException>(() => _ = QuePaxaRegister<string>.FromRecorders([]));
    }


    [TestMethod]
    public void FromRecordersTakesTheRecordersAsGiven()
    {
        //This is the factory a test uses to build a register whose recorders disagree about the leader, which
        //is the deployment failure the reserved-priority rule costs money to avoid.
        ImmutableArray<QuePaxaRecorder<string>> recorders =
        [
            QuePaxaRecorder<string>.LedBy(LaneA),
            QuePaxaRecorder<string>.LedBy(LaneB),
            QuePaxaRecorder<string>.Leaderless
        ];

        QuePaxaRegister<string> register = QuePaxaRegister<string>.FromRecorders(recorders);

        Assert.AreEqual(3, register.RecorderCount);
        Assert.AreEqual(2, register.Quorum);
        Assert.AreEqual(LaneA, register.Recorders[0].ConfiguredLeader);
        Assert.AreEqual(LaneB, register.Recorders[1].ConfiguredLeader);
        Assert.IsNull(register.Recorders[2].ConfiguredLeader);
    }


    [TestMethod]
    public void BeginStartsAtStepFourAndClaimsTheReservedPriorityOnlyWhenItBelievesItLeads()
    {
        QuePaxaRound<string> claiming = QuePaxaRound<string>.Begin(LaneA, LaneA, "a");
        QuePaxaRound<string> notClaiming = QuePaxaRound<string>.Begin(LaneA, LaneB, "a");
        QuePaxaRound<string> leaderless = QuePaxaRound<string>.Begin(LaneA, null, "a");

        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, claiming.Step);
        Assert.IsTrue(claiming.ClaimsLeadership);
        Assert.AreEqual(ProposalPriority.Reserved, claiming.Proposal.Key.Priority);
        Assert.AreEqual(LaneA, claiming.Proposal.Key.Owner);
        Assert.AreEqual("a", claiming.Proposal.Value);

        //The placeholder priority is never sent, because phase zero redraws for every proposer that does not
        //claim leadership.
        Assert.IsFalse(notClaiming.ClaimsLeadership);
        Assert.AreEqual(ProposalPriority.None, notClaiming.Proposal.Key.Priority);
        Assert.IsFalse(leaderless.ClaimsLeadership);
        Assert.AreEqual(ProposalPriority.None, leaderless.Proposal.Key.Priority);
    }


    [TestMethod]
    public void TheConfiguredAndBelievingLeaderDecidesAtStepFour()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);
        var source = new SeededPrioritySource(11);

        (_, QuePaxaOutcome<string> outcome) = register.Propose(LaneA, LaneA, "a", source.Next);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(LaneA, outcome.DecidedBy);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, outcome.DecidedAt);
        Assert.AreEqual(1, outcome.Steps);

        //The reserved claim is not redrawn at the first step, so the fast path costs no entropy at all.
        Assert.AreEqual(0, source.DrawCount);
    }


    [TestMethod]
    public void ANonLeaderAloneDecidesAtStepSix()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        var source = new SeededPrioritySource(12);

        (QuePaxaRegister<string> after, QuePaxaOutcome<string> outcome) = register.Propose(LaneA, null, "a", source.Next);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(LaneA, outcome.DecidedBy);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), outcome.DecidedAt);
        Assert.AreEqual(3, outcome.Steps);

        //Propose always reaches every recorder, so it never observes a quorum miss and every recorder moved.
        foreach(QuePaxaRecorder<string> recorder in after.Recorders)
        {
            Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), recorder.Step);
        }
    }


    [TestMethod]
    public void AProposerThatClaimsLeadershipWithoutBeingTheConfiguredLeaderNeverDecidesAtStepFour()
    {
        //The claim is declined at every recorder and lands on the lowest ordinary priority, so the identical
        //firsts the fast path needs are present but are not reserved and the round runs its ordinary phases.
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);
        var source = new SeededPrioritySource(13);

        (QuePaxaRegister<string> after, QuePaxaOutcome<string> outcome) = register.Propose(LaneB, LaneB, "b", source.Next);

        Assert.IsTrue(outcome.IsDecided);
        Assert.AreNotEqual(RecorderStep.RoundOnePhaseZero, outcome.DecidedAt);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), outcome.DecidedAt);
        Assert.AreEqual("b", outcome.Value);
        Assert.AreEqual(LaneB, outcome.DecidedBy);
        Assert.AreEqual(ProposalPriority.Lowest, after.Recorders[0].Register.First!.Key.Priority);
    }


    [TestMethod]
    public void ADuplicateRecorderIndexThrows()
    {
        //A duplicate would double-count toward the quorum, which is a silently weaker quorum rather than an
        //error the caller would ever notice.
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        var source = new SeededPrioritySource(14);

        Assert.ThrowsExactly<ArgumentException>(() => _ = register.Step(round, [0, 0], source.Next));
        Assert.ThrowsExactly<ArgumentException>(() => _ = register.Step(round, [0, 1, 0], source.Next));
    }


    [TestMethod]
    public void AnOutOfRangeRecorderIndexThrows()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        var source = new SeededPrioritySource(15);

        Assert.ThrowsExactly<ArgumentException>(() => _ = register.Step(round, [-1], source.Next));
        Assert.ThrowsExactly<ArgumentException>(() => _ = register.Step(round, [3], source.Next));
        Assert.ThrowsExactly<ArgumentException>(() => _ = register.Step(round, [0, 1, 3], source.Next));
    }


    [TestMethod]
    public void ADefaultRecorderIndicesThrows()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        var source = new SeededPrioritySource(16);

        Assert.ThrowsExactly<ArgumentException>(() => _ = register.Step(round, default, source.Next));
    }


    [TestMethod]
    public void AnEmptyButNonDefaultRecorderIndicesFallsThroughToAQuorumMiss()
    {
        //An empty set models a step whose every message was lost, which the protocol must tolerate rather
        //than refuse.
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        var source = new SeededPrioritySource(17);

        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> outcome) = register.Step(round, [], source.Next);

        Assert.AreEqual(QuePaxaStepKind.QuorumMissed, outcome.Kind);
        Assert.AreEqual(0, outcome.SummaryCount);
        Assert.IsNull(outcome.Next);
        Assert.IsNull(outcome.DecidedBy);
        Assert.IsNull(outcome.DecidedValue);
        Assert.AreEqual(RecorderStep.Zero, outcome.DecidedAt);
        Assert.AreEqual(0, source.DrawCount);
        foreach(QuePaxaRecorder<string> recorder in after.Recorders)
        {
            Assert.AreEqual(RecorderStep.Zero, recorder.Step);
        }
    }


    [TestMethod]
    public void ANullRoundOrDrawPriorityThrows()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        var source = new SeededPrioritySource(18);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = register.Step(null!, AllThree, source.Next));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = register.Step(round, AllThree, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = register.Propose(LaneA, null, "a", null!));
    }


    [TestMethod]
    public void ADrawnReservedPriorityThrowsBeforeItIsRecorded()
    {
        //A source returning the reserved priority forges a leader claim, so the send is refused rather than
        //downgraded at the far end.
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = register.Step(round, AllThree, () => ProposalPriority.Reserved));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = register.Propose(LaneA, null, "a", () => ProposalPriority.Reserved));
    }


    [TestMethod]
    public void ADrawnNonePriorityThrowsBeforeItIsRecorded()
    {
        //None is the identity of the aggregate and is never drawn and never sent; a source that returns it is
        //a protocol violation rather than a degenerate but legal draw.
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = register.Step(round, AllThree, () => ProposalPriority.None));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = register.Propose(LaneA, null, "a", () => ProposalPriority.None));
    }


    [TestMethod]
    public void AnUndecidedOutcomeCarriesZeroAsItsDecidedStep()
    {
        //Zero is a step no request ever carries, so it cannot be mistaken for a real decision step and the
        //field stays non-nullable.
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        var source = new SeededPrioritySource(19);

        (_, QuePaxaStepOutcome<string> outcome) = register.Step(round, AllThree, source.Next);

        Assert.AreEqual(QuePaxaStepKind.Advanced, outcome.Kind);
        Assert.IsNotNull(outcome.Next);
        Assert.AreEqual(RecorderStep.Zero, outcome.DecidedAt);
        Assert.IsNull(outcome.DecidedBy);
        Assert.IsNull(outcome.DecidedValue);
        Assert.AreEqual(3, outcome.SummaryCount);
    }


    [TestMethod]
    public void QuorumMissedIsTheZeroValuedStepKindSoADefaultOutcomeReadsAsAFailure()
    {
        //The claim is that the enum's zero member is the missed quorum, so that a default-constructed outcome
        //reads as a failure. It is asserted by round-tripping the underlying value through the enum, because
        //an assertion whose operands are both compile-time constants is folded and stops testing anything.
        QuePaxaStepKind fromZero = (QuePaxaStepKind)Enum.ToObject(typeof(QuePaxaStepKind), 0);

        Assert.AreEqual(QuePaxaStepKind.QuorumMissed, fromZero);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// Xorshift64 rather than the cryptographic source: every priority in these tests is reproducible from its
    /// seed, so a failing run replays the identical draws.
    /// </summary>
    private sealed class SeededPrioritySource
    {
        private ulong state;

        public SeededPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        public int DrawCount { get; private set; }


        public ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            DrawCount++;

            //The two reserved endpoints are excluded so the source honours the delegate's contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }
}
