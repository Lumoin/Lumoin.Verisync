using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The round's own suite: the three members that carry the safety core now that both drivers share it. The
/// conclusion is PUBLIC, because a host writing its own transport needs it without needing either driver, and
/// being public is exactly why its argument validation is not optional and is tested here rather than through
/// a driver that cannot reach it.
/// </summary>
/// <remarks>
/// Two rules below are unreachable through the asynchronous proposer by construction — a duplicate answer per
/// recorder and a recorder index outside the configured range — because the proposer keeps at most one
/// outstanding call per recorder per step and never calls one that has already answered. That is why they are
/// tested here: a host assembling the answer array itself is the caller the checks exist for.
/// </remarks>
[TestClass]
internal sealed class QuePaxaRoundTests
{
    /// <summary>
    /// Identities from fixed bytes so that A sorts below B, because the catch-up tie-break below must be
    /// settled by the recorder index and by nothing that a generated pair could reorder.
    /// </summary>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    /// <summary>
    /// A DUPLICATE RECORDER INDEX WOULD DOUBLE-COUNT TOWARD THE QUORUM, and the quorum test is the model's sole
    /// guard on acting at all: two proposers whose answer sets do not intersect can decide different values,
    /// and the whole agreement argument is that two majorities intersect.
    /// </summary>
    /// <remarks>
    /// The request-side check the register already carries refuses a duplicate before anything is recorded;
    /// this one is what the quorum arithmetic actually needs, because the conclusion accepts an answer array a
    /// host assembled.
    /// </remarks>
    [TestMethod]
    public void ConcludeRefusesADuplicateRecorderIndex()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        ImmutableArray<RecorderAnswer<string>> duplicated =
        [
            Answer(0, Four, Ordinary(5, LaneA, "a")),
            Answer(0, Four, Ordinary(9, LaneB, "b"))
        ];

        Assert.ThrowsExactly<ArgumentException>(() => _ = round.Conclude(duplicated, 3));
    }


    [TestMethod]
    public void ConcludeRefusesADefaultArrayANullElementAndAnOutOfRangeRecorderIndex()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        ImmutableArray<RecorderAnswer<string>> withNull = [Answer(0, Four, Ordinary(5, LaneA, "a")), null!];
        ImmutableArray<RecorderAnswer<string>> aboveRange = [Answer(0, Four, Ordinary(5, LaneA, "a")), Answer(3, Four, Ordinary(9, LaneB, "b"))];

        Assert.ThrowsExactly<ArgumentException>(() => _ = round.Conclude(default, 3));
        Assert.Throws<ArgumentException>(() => _ = round.Conclude(withNull, 3));
        Assert.ThrowsExactly<ArgumentException>(() => _ = round.Conclude(aboveRange, 3));
    }


    [TestMethod]
    public void ConcludeRefusesARecorderCountBelowOne()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        ImmutableArray<RecorderAnswer<string>> answers = [Answer(0, Four, Ordinary(5, LaneA, "a"))];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = round.Conclude(answers, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = round.Conclude(answers, -1));
    }


    /// <summary>
    /// AN ANSWER DEFENDS ITSELF, because a positional record synthesizes no validation at all and the type is
    /// constructed by hosts that are neither driver.
    /// </summary>
    /// <remarks>
    /// A negative recorder index cannot address a recorder and a null summary reaches the phase dispatch as a
    /// null dereference rather than as an argument complaint.
    /// </remarks>
    [TestMethod]
    public void AnAnswerRefusesANegativeRecorderIndexAndANullSummaryUnderConstructionAndUnderWith()
    {
        RecordSummary<string> summary = new(Four, Ordinary(5, LaneA, "a"), null);
        RecorderAnswer<string> answer = new(0, summary);

        Assert.AreEqual(0, answer.Recorder);
        Assert.AreEqual(summary, answer.Summary);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RecorderAnswer<string>(-1, summary));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new RecorderAnswer<string>(0, null!));

        //The initializer of a positional record writes the backing field directly and no accessor runs for
        //it, so a copy would bypass a check stated only in the constructor.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = answer with { Recorder = -1 });
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = answer with { Summary = null! });
    }


    /// <summary>
    /// A SUB-MAJORITY ANSWER SET IS A MISSED QUORUM AND NOT A DECISION, at every recorder count.
    /// </summary>
    /// <remarks>
    /// This is the pin on the quorum being DERIVED from the recorder count rather than supplied: a caller
    /// passing a sub-majority quorum would otherwise reach the phase-two decision on a reply set that need not
    /// intersect another proposer's, and two proposers would then decide different values with no intersecting
    /// majority between them.
    /// </remarks>
    [TestMethod]
    public void ASubMajorityAnswerSetMissesTheQuorumAtEveryRecorderCount()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");

        AssertMissedQuorum(round, 3, 1);
        AssertMissedQuorum(round, 5, 1);
        AssertMissedQuorum(round, 5, 2);
        AssertMissedQuorum(round, 7, 3);

        //The boundary in the other direction, so the arithmetic is pinned rather than merely refused: a bare
        //majority concludes.
        QuePaxaStepOutcome<string> atTheBoundary = round.Conclude(AnswersFor(2), 3);

        Assert.AreNotEqual(QuePaxaStepKind.QuorumMissed, atTheBoundary.Kind);
        Assert.AreEqual(2, atTheBoundary.SummaryCount);
    }


    /// <summary>
    /// THE QUORUM IS A FLOOR AND A LARGER RECORDER SET RAISES IT. The same three answers conclude against three
    /// recorders and miss against seven, which is what makes the second parameter the RECORDER COUNT rather
    /// than a caller-supplied quorum: a caller cannot weaken the guard by naming a smaller number, because the
    /// number it names is what the guard is computed from.
    /// </summary>
    [TestMethod]
    public void TheDerivedQuorumRisesWithTheRecorderCountRatherThanBeingSupplied()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        ImmutableArray<RecorderAnswer<string>> answers = AnswersFor(3);

        QuePaxaStepOutcome<string> againstThree = round.Conclude(answers, 3);
        QuePaxaStepOutcome<string> againstSeven = round.Conclude(answers, 7);

        Assert.AreNotEqual(QuePaxaStepKind.QuorumMissed, againstThree.Kind);
        Assert.AreEqual(QuePaxaStepKind.QuorumMissed, againstSeven.Kind);
        Assert.AreEqual(3, againstSeven.SummaryCount);
        Assert.IsNull(againstSeven.Next);
    }


    /// <summary>
    /// THE CATCH-UP TIE AMONG EQUAL STEPS IS BROKEN BY THE LOWEST RECORDER INDEX, so that a run is reproducible.
    /// </summary>
    /// <remarks>
    /// Two recorders sit at the same greatest step holding DIFFERENT first proposals, and recorder one's key
    /// is the greater of the two, so an implementation breaking the tie by the greatest key, by the highest
    /// index, or by array position takes the other value. The array is concluded in both orders, because a
    /// rule that held only for one ordering would be array position wearing the index rule's name.
    /// </remarks>
    [TestMethod]
    public void TheCatchUpTieAmongEqualStepsTakesTheLowestRecorderIndex()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneB, null, "b");
        RecorderStep ahead = RecorderStep.FromRoundAndPhase(1, 2);
        RecorderAnswer<string> fromZero = Answer(0, ahead, Ordinary(5, LaneA, "fromZero"));
        RecorderAnswer<string> fromOne = Answer(1, ahead, Ordinary(9, LaneA, "fromOne"));

        Assert.IsTrue(fromOne.Summary.First!.Key > fromZero.Summary.First!.Key, "The pin needs the higher key at the higher index or it discriminates nothing.");

        QuePaxaStepOutcome<string> ascending = round.Conclude([fromZero, fromOne], 3);
        QuePaxaStepOutcome<string> descending = round.Conclude([fromOne, fromZero], 3);

        Assert.AreEqual(QuePaxaStepKind.CaughtUp, ascending.Kind);
        Assert.AreEqual(ahead, ascending.Next!.Step);
        Assert.AreEqual("fromZero", ascending.Next.Proposal.Value, "The equal-step tie must be broken by the lowest recorder index.");
        Assert.AreEqual(QuePaxaStepKind.CaughtUp, descending.Kind);
        Assert.AreEqual("fromZero", descending.Next!.Proposal.Value, "The tie-break must not depend on the order the answers were assembled in.");

        //The caught-up round keeps the proposer's own identity and its belief; only the step and the template
        //move.
        Assert.AreEqual(LaneB, ascending.Next.Proposer);
        Assert.IsNull(ascending.Next.BelievedLeader);
    }


    /// <summary>
    /// A STEP STRICTLY ABOVE THE ROUND'S IS WHAT CATCHING UP MEANS, and an answer at the round's own step is
    /// not one.
    /// </summary>
    /// <remarks>
    /// Lemma C.2 makes every summary's step at least the requested one, so an answer that is not at the
    /// requested step is above it, which is why the comparison is against the round's step rather than against
    /// the other answers.
    /// </remarks>
    [TestMethod]
    public void AnAnswerAtTheRoundsOwnStepIsNotACatchUp()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");

        QuePaxaStepOutcome<string> outcome = round.Conclude(AnswersFor(2), 3);

        Assert.AreNotEqual(QuePaxaStepKind.CaughtUp, outcome.Kind);
        Assert.AreEqual(QuePaxaStepKind.Advanced, outcome.Kind);
        Assert.AreEqual(Four.Next(), outcome.Next!.Step);
    }


    /// <summary>
    /// LEMMA C.2 IS A PRECONDITION AND NOT AN ASSUMPTION. The catch-up rule reads "no summary above my step" as
    /// "every summary at my step", which holds only because a recorder advances to the requested step before it
    /// answers.
    /// </summary>
    /// <remarks>
    /// A host driving its own transport can supply an answer from below the round's step — a reply correlated
    /// to the wrong call, or a cached one — and without this check the answer would be counted toward this
    /// step's quorum with a stale aggregate. At phase two that is a decision taken on a majority that never
    /// gathered at the deciding step, which is silently wrong rather than loudly wrong: no exception, and two
    /// proposers free to decide different values.
    /// </remarks>
    [TestMethod]
    public void ConcludeRefusesAnAnswerFromBelowTheRoundsStep()
    {
        RecorderStep phaseTwo = RecorderStep.FromRoundAndPhase(1, 2);
        var template = new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(9), LaneA), "a");
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a") with { Step = phaseTwo, Proposal = template };
        ImmutableArray<RecorderAnswer<string>> withStale =
        [
            new RecorderAnswer<string>(0, new RecordSummary<string>(phaseTwo, Ordinary(5, LaneA, "a"), template)),
            new RecorderAnswer<string>(1, new RecordSummary<string>(Four, Ordinary(5, LaneB, "b"), template))
        ];

        //Without the precondition this set decides: the greatest prior aggregate equals the template on the
        //strength of one genuine phase-two answer plus one stale one.
        ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => _ = round.Conclude(withStale, 3));

        Assert.Contains("below the round's step", refused.Message, StringComparison.Ordinal);

        //The same two answers both at the round's step do decide, so the refusal above is the step and nothing
        //else about the shape of the set.
        ImmutableArray<RecorderAnswer<string>> bothAtStep =
        [
            new RecorderAnswer<string>(0, new RecordSummary<string>(phaseTwo, Ordinary(5, LaneA, "a"), template)),
            new RecorderAnswer<string>(1, new RecordSummary<string>(phaseTwo, Ordinary(5, LaneB, "b"), template))
        ];

        Assert.AreEqual(QuePaxaStepKind.Decided, round.Conclude(bothAtStep, 3).Kind);
    }


    /// <summary>
    /// THE FAST PATH IS RESTRICTED TO THE FIRST STEP, which is stricter than the model's guard on any
    /// phase-zero step.
    /// </summary>
    /// <remarks>
    /// The restriction costs nothing through either driver, because every phase-zero send above the first step
    /// is redrawn to an ordinary priority and so no recorder's first proposal at such a step can carry the
    /// reserved one; and it is safe in any case, because a restriction can only refuse a decision rather than
    /// add one. The conclusion is public, so a host assembling answers itself is the caller that reaches the
    /// difference, and this is where the argument stops being an argument.
    /// </remarks>
    [TestMethod]
    public void TheFastPathDoesNotFireAtAPhaseZeroStepAboveTheFirstOne()
    {
        RecorderStep roundTwoPhaseZero = RecorderStep.FromRoundAndPhase(2, 0);
        var reserved = new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.Reserved, LaneA), "a");
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, LaneA, "a") with { Step = roundTwoPhaseZero };
        ImmutableArray<RecorderAnswer<string>> uniformlyReserved =
        [
            Answer(0, roundTwoPhaseZero, reserved),
            Answer(1, roundTwoPhaseZero, reserved),
            Answer(2, roundTwoPhaseZero, reserved)
        ];

        QuePaxaStepOutcome<string> outcome = round.Conclude(uniformlyReserved, 3);

        //Uniform and reserved, so the only thing refusing the decision is the step.
        Assert.AreEqual(QuePaxaStepKind.Advanced, outcome.Kind);
        Assert.AreNotEqual(QuePaxaStepKind.Decided, outcome.Kind);
        Assert.AreEqual(roundTwoPhaseZero.Next(), outcome.Next!.Step);
        Assert.AreEqual(reserved, outcome.Next.Proposal);

        //The same answers at the first step DO decide, so the test above cannot pass because the fast path
        //was unreachable for some other reason.
        QuePaxaRound<string> atFirstStep = QuePaxaRound<string>.Begin(LaneA, LaneA, "a");
        ImmutableArray<RecorderAnswer<string>> atFour =
        [
            Answer(0, Four, reserved),
            Answer(1, Four, reserved),
            Answer(2, Four, reserved)
        ];

        QuePaxaStepOutcome<string> decided = atFirstStep.Conclude(atFour, 3);

        Assert.AreEqual(QuePaxaStepKind.Decided, decided.Kind);
        Assert.AreEqual("a", decided.DecidedValue);
        Assert.AreEqual(Four, decided.DecidedAt);
    }


    /// <summary>
    /// A RECORDER ABOVE STEP ZERO ALWAYS HOLDS A FIRST PROPOSAL, so a null one means the register state is
    /// corrupt and reporting it is the fail-closed reading.
    /// </summary>
    /// <remarks>
    /// The message boundary refuses a null first before it reaches here, which is what keeps this a genuine
    /// backstop rather than a live path; a host assembling answers itself is the one caller that can still
    /// reach it.
    /// </remarks>
    [TestMethod]
    public void ConcludeReportsAnAnswerAboveStepZeroThatCarriesNoFirstProposal()
    {
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        ImmutableArray<RecorderAnswer<string>> answers =
        [
            new RecorderAnswer<string>(0, new RecordSummary<string>(RecorderStep.FromRoundAndPhase(1, 2), null, null)),
            new RecorderAnswer<string>(1, new RecordSummary<string>(RecorderStep.FromRoundAndPhase(1, 2), null, null))
        ];

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = round.Conclude(answers, 3));
    }


    /// <summary>
    /// WHETHER A SEND REDRAWS IS A FACT ABOUT THE PROTOCOL POSITION AND NOT ABOUT THE DRIVER, which is why it
    /// lives on the round: two drivers computing it independently is how the two would drift.
    /// </summary>
    /// <remarks>
    /// The reserved priority is claimed at the first step alone and only by a proposer that believes it leads;
    /// every other phase-zero send carries a fresh draw, INCLUDING the leader's own sends in every later
    /// round. That last arm is exercised by no checked configuration, because every concrete configuration
    /// bounds the step budget below round two, so it rests on a directed test rather than on the model.
    /// </remarks>
    [TestMethod]
    public void RedrawsPriorityIsPhaseZeroExceptTheBelievingLeadersOwnFirstStep()
    {
        QuePaxaRound<string> claiming = QuePaxaRound<string>.Begin(LaneA, LaneA, "a");
        QuePaxaRound<string> leaderless = QuePaxaRound<string>.Begin(LaneA, null, "a");
        QuePaxaRound<string> believingAnother = QuePaxaRound<string>.Begin(LaneA, LaneB, "a");

        Assert.IsFalse(claiming.RedrawsPriority);
        Assert.IsTrue(leaderless.RedrawsPriority);
        Assert.IsTrue(believingAnother.RedrawsPriority);

        //Phases one, two and three send the template untouched, whoever is proposing.
        Assert.IsFalse((claiming with { Step = RecorderStep.FromRoundAndPhase(1, 1) }).RedrawsPriority);
        Assert.IsFalse((claiming with { Step = RecorderStep.FromRoundAndPhase(1, 2) }).RedrawsPriority);
        Assert.IsFalse((claiming with { Step = RecorderStep.FromRoundAndPhase(1, 3) }).RedrawsPriority);
        Assert.IsFalse((leaderless with { Step = RecorderStep.FromRoundAndPhase(1, 1) }).RedrawsPriority);

        //The round boundary: at round two phase zero the configured leader redraws like everyone else, so it
        //cannot re-claim the reserved priority in a later round.
        Assert.IsTrue((claiming with { Step = RecorderStep.FromRoundAndPhase(2, 0) }).RedrawsPriority);
        Assert.IsTrue((leaderless with { Step = RecorderStep.FromRoundAndPhase(2, 0) }).RedrawsPriority);
    }


    /// <summary>
    /// ONE CALL IS ONE RECORDER'S SEND. Phase zero redraws the priority PER RECORDER, so calling this once per
    /// step and broadcasting the result is a protocol defect rather than an optimization: a single draw shared
    /// across recorders collapses the independence the liveness argument rests on.
    /// </summary>
    /// <remarks>
    /// The owner and the value ride through the redraw untouched, because a proposal carried forward from
    /// another proposer keeps that proposer's identity.
    /// </remarks>
    [TestMethod]
    public void NextSendDrawsOncePerCallAndKeepsTheOwnerAndValue()
    {
        ulong[] script = [100, 101];
        int index = 0;
        ProposalPrioritySourceDelegate source = () => new ProposalPriority(script[index++]);

        QuePaxaRound<string> leaderless = QuePaxaRound<string>.Begin(LaneA, null, "a");

        PrioritizedProposal<string> toFirstRecorder = leaderless.NextSend(source);
        PrioritizedProposal<string> toSecondRecorder = leaderless.NextSend(source);

        Assert.AreEqual(2, index);
        Assert.AreEqual(100UL, toFirstRecorder.Key.Priority.Value);
        Assert.AreEqual(101UL, toSecondRecorder.Key.Priority.Value);
        Assert.AreEqual(LaneA, toFirstRecorder.Key.Owner);
        Assert.AreEqual(LaneA, toSecondRecorder.Key.Owner);
        Assert.AreEqual("a", toFirstRecorder.Value);
        Assert.AreEqual("a", toSecondRecorder.Value);
    }


    /// <summary>
    /// A SEND THAT DOES NOT REDRAW CARRIES THE TEMPLATE ITSELF, which is what makes the fast path free of
    /// entropy and what makes a phase-one spread identical at every recorder.
    /// </summary>
    /// <remarks>
    /// A source that throws proves the claim rather than merely permitting it.
    /// </remarks>
    [TestMethod]
    public void NextSendReturnsTheTemplateUntouchedWhenTheRoundDoesNotRedraw()
    {
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("A send that does not redraw must not touch the priority source.");
        QuePaxaRound<string> claiming = QuePaxaRound<string>.Begin(LaneA, LaneA, "a");
        QuePaxaRound<string> spreading = claiming with { Step = RecorderStep.FromRoundAndPhase(1, 1) };

        Assert.AreSame(claiming.Proposal, claiming.NextSend(never));
        Assert.AreSame(spreading.Proposal, spreading.NextSend(never));
    }


    /// <summary>
    /// A PRIORITY SOURCE MUST RETURN AN ORDINARY PRIORITY. The absent priority is the aggregate fold's identity
    /// and the reserved priority forges a leader claim, so neither may be drawn; refusing them here is what
    /// keeps the contract stated on the source delegate enforceable at the one place the source is consulted.
    /// </summary>
    [TestMethod]
    public void NextSendRefusesANonOrdinaryDrawAndANullSource()
    {
        QuePaxaRound<string> leaderless = QuePaxaRound<string>.Begin(LaneA, null, "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = leaderless.NextSend(static () => ProposalPriority.Reserved));
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = leaderless.NextSend(static () => ProposalPriority.None));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = leaderless.NextSend(null!));
    }


    private static void AssertMissedQuorum(QuePaxaRound<string> round, int recorderCount, int answerCount)
    {
        QuePaxaStepOutcome<string> outcome = round.Conclude(AnswersFor(answerCount), recorderCount);

        Assert.AreEqual(QuePaxaStepKind.QuorumMissed, outcome.Kind);
        Assert.AreEqual(answerCount, outcome.SummaryCount);
        Assert.IsNull(outcome.Next);
        Assert.AreEqual(RecorderStep.Zero, outcome.DecidedAt);
        Assert.IsNull(outcome.DecidedBy);
    }


    /// <summary>
    /// Distinct keys and distinct values per recorder, so a phase-zero gather over them is never uniform and no
    /// assertion above can be satisfied by a fast path firing for the wrong reason.
    /// </summary>
    private static ImmutableArray<RecorderAnswer<string>> AnswersFor(int answerCount)
    {
        var builder = ImmutableArray.CreateBuilder<RecorderAnswer<string>>(answerCount);
        for(int i = 0; i < answerCount; i++)
        {
            builder.Add(Answer(i, Four, Ordinary(10UL + (ulong)i, LaneA, $"v{i}")));
        }

        return builder.ToImmutable();
    }


    private static RecorderAnswer<string> Answer(int recorder, RecorderStep step, PrioritizedProposal<string> first)
    {
        return new RecorderAnswer<string>(recorder, new RecordSummary<string>(step, first, null));
    }


    private static PrioritizedProposal<string> Ordinary(ulong priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
