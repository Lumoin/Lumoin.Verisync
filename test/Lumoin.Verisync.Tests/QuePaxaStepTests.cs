using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The directed protocol suite: rules a generated harness reaches only by luck, each with a constructed
/// scenario. Several of them guard rules an implementation could drop while staying green everywhere
/// else - sub-quorum records that must persist, the owner an outcome names, and the round boundary, which
/// the one-round model cannot see at all and where this suite is the only evidence there is.
/// </summary>
/// <remarks>
/// Every priority in this file comes from a scripted source, and every replica identity from fixed bytes,
/// so each scenario is one behaviour rather than a family of them. A round value is stepped at most once
/// anywhere below, which is the contract the model enforces with its send flag and which an immutable round
/// value cannot enforce on its own.
/// </remarks>
[TestClass]
internal sealed class QuePaxaStepTests
{
    private static ReplicaId ReplicaA { get; } = Replica(1);
    private static ReplicaId ReplicaB { get; } = Replica(2);

    private static ProposerLane LaneA { get; } = ProposerLane.For(ReplicaA);
    private static ProposerLane LaneB { get; } = ProposerLane.For(ReplicaB);

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;

    private static ImmutableArray<int> AllThree { get; } = [0, 1, 2];
    private static ImmutableArray<int> FirstTwo { get; } = [0, 1];
    private static ImmutableArray<int> LastTwo { get; } = [1, 2];
    private static ImmutableArray<int> Outer { get; } = [0, 2];


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// SUB-QUORUM RECORDS PERSIST. Every reached recorder has recorded whatever happens next, which is what
    /// Lemma C.5's Case 2 turns on: a message that arrives is recorded even when the proposer never assembles
    /// a quorum.
    /// </summary>
    /// <remarks>
    /// An implementation that discarded the mutated recorders on a sub-quorum return would otherwise pass the
    /// entire suite.
    /// </remarks>
    [TestMethod]
    public void ASubQuorumStepStillRecordsAndALaterGatherObservesIt()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);
        var source = new ScriptedPrioritySource(10, 11, 12);
        QuePaxaRound<string> leaderRound = QuePaxaRound<string>.Begin(LaneA, LaneA, "a");

        ImmutableArray<int> single = [0];
        (QuePaxaRegister<string> afterMiss, QuePaxaStepOutcome<string> missed) = register.Step(leaderRound, single, source.Next);

        Assert.AreEqual(QuePaxaStepKind.QuorumMissed, missed.Kind);
        Assert.AreEqual(1, missed.SummaryCount);
        Assert.IsNull(missed.Next);
        Assert.IsGreaterThan(missed.SummaryCount, register.Quorum);

        //The reached recorder holds the proposal as its first, and the recorders the step never reached did
        //not move at all.
        PrioritizedProposal<string> survivingFirst = afterMiss.Recorders[0].Register.First!;

        Assert.AreEqual(Four, afterMiss.Recorders[0].Step);
        Assert.AreEqual(ProposalPriority.Reserved, survivingFirst.Key.Priority);
        Assert.AreEqual(LaneA, survivingFirst.Key.Owner);
        Assert.AreEqual("a", survivingFirst.Value);
        Assert.AreEqual(RecorderStep.Zero, afterMiss.Recorders[1].Step);
        Assert.AreEqual(RecorderStep.Zero, afterMiss.Recorders[2].Step);

        //A second proposer's later gather observes the surviving record: the reserved first outranks every
        //ordinary draw, so it is what the fall-through carries forward.
        QuePaxaRound<string> otherRound = QuePaxaRound<string>.Begin(LaneB, null, "b");
        (_, QuePaxaStepOutcome<string> gathered) = afterMiss.Step(otherRound, AllThree, source.Next);

        Assert.AreEqual(QuePaxaStepKind.Advanced, gathered.Kind);
        Assert.AreEqual(3, gathered.SummaryCount);
        Assert.AreEqual(afterMiss.Recorders[0].Register.First, gathered.Next!.Proposal);
        Assert.AreEqual("a", gathered.Next.Proposal.Value);
        Assert.AreEqual(LaneA, gathered.Next.Proposal.Key.Owner);
    }


    /// <summary>
    /// CATCH-UP. Lemma C.2 makes every summary's step at least the requested one, so "not all equal" implies
    /// "some greater"; the landing state is one some proposer reached without catching up, and taking the
    /// greatest reply with the lowest recorder index breaking a tie is one of the choices the model's
    /// adversary already admits, so the determinism is a refinement rather than a deviation.
    /// </summary>
    [TestMethod]
    public void AStepBehindTheRecordersCatchesUpToTheGreatestSummaryStep()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        var source = new ScriptedPrioritySource(1000, 1001, 1002, 2000, 2001, 2002);

        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");
        (register, QuePaxaStepOutcome<string> atFour) = register.Step(round, AllThree, source.Next);
        (register, QuePaxaStepOutcome<string> atFive) = register.Step(atFour.Next!, AllThree, source.Next);
        (register, QuePaxaStepOutcome<string> atSix) = register.Step(atFive.Next!, AllThree, source.Next);

        Assert.AreEqual(QuePaxaStepKind.Decided, atSix.Kind);
        foreach(QuePaxaRecorder<string> recorder in register.Recorders)
        {
            Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), recorder.Step);
        }

        QuePaxaRegister<string> before = register;
        QuePaxaRound<string> behind = QuePaxaRound<string>.Begin(LaneB, null, "b");
        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> caughtUp) = register.Step(behind, AllThree, source.Next);

        Assert.AreEqual(QuePaxaStepKind.CaughtUp, caughtUp.Kind);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), caughtUp.Next!.Step);
        Assert.AreEqual(after.Recorders[0].Register.First, caughtUp.Next.Proposal);
        Assert.AreEqual("a", caughtUp.Next.Proposal.Value);
        Assert.AreEqual(LaneA, caughtUp.Next.Proposal.Key.Owner);
        Assert.AreEqual(LaneB, caughtUp.Next.Proposer);

        //The behind step is obsolete at every recorder, so each returns its own unchanged instance.
        for(int i = 0; i < after.RecorderCount; i++)
        {
            Assert.AreSame(before.Recorders[i], after.Recorders[i]);
        }
    }


    /// <summary>
    /// EXHAUSTION. A spent step budget is terminal for the instance while a quorum miss is retryable, and the
    /// two call for opposite responses at the same call site, so they are separate kinds and are distinguished
    /// here by kind alone rather than by the breadth measurement.
    /// </summary>
    [TestMethod]
    public void AStepAtTheTopOfTheBudgetIsExhaustedRatherThanAdvanced()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        var source = new ScriptedPrioritySource();
        PrioritizedProposal<string> proposal = Ordinary(100, LaneA, "a");
        QuePaxaRound<string> exhausted = QuePaxaRound<string>.Begin(LaneA, null, "a") with { Step = RecorderStep.MaxValue, Proposal = proposal };

        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> outcome) = register.Step(exhausted, AllThree, source.Next);

        Assert.AreEqual(QuePaxaStepKind.Exhausted, outcome.Kind);
        Assert.IsNull(outcome.Next);
        Assert.IsGreaterThan(register.Quorum - 1, outcome.SummaryCount);
        Assert.AreEqual(RecorderStep.Zero, outcome.DecidedAt);
        Assert.IsNull(outcome.DecidedBy);
        Assert.IsNull(outcome.DecidedValue);

        //The step still recorded at every reached recorder, exactly as any other step does.
        foreach(QuePaxaRecorder<string> recorder in after.Recorders)
        {
            Assert.AreEqual(RecorderStep.MaxValue, recorder.Step);
            Assert.AreEqual(proposal, recorder.Register.First);
        }

        //A phase-three step at the top of the budget consumes no entropy, so the exhaustion is not an artefact
        //of a source running dry.
        Assert.AreEqual(0, source.DrawCount);
    }


    /// <summary>
    /// THE DECIDED OWNER. The outcome names the OWNER of the decided proposal, not the proposer that observed
    /// the decision, and nothing else in the suite distinguishes the two.
    /// </summary>
    /// <remarks>
    /// A caller reads it to learn that someone else's value was chosen and that it must re-read and
    /// re-propose, which is the reason this outcome type exists rather than the CASPaxos one being reused.
    /// </remarks>
    [TestMethod]
    public void DecidedByIsTheProposalsOwnerRatherThanTheProposerThatObservedTheDecision()
    {
        CarrySetup setup = BuildPhaseThreeCarry();

        //The phase-three carry hands B a proposal owned by A; the priority is redrawn across the round
        //boundary but the owner rides along with the value.
        (QuePaxaRegister<string> register, QuePaxaStepOutcome<string> atSeven) = setup.Register.Step(setup.RoundAtSeven, FirstTwo, setup.Source.Next);
        Assert.AreEqual(QuePaxaStepKind.Advanced, atSeven.Kind);
        Assert.AreEqual(setup.LeadersProposal, atSeven.Next!.Proposal);

        (register, QuePaxaStepOutcome<string> atEight) = register.Step(atSeven.Next, FirstTwo, setup.Source.Next);
        (register, QuePaxaStepOutcome<string> atNine) = register.Step(atEight.Next!, FirstTwo, setup.Source.Next);
        (_, QuePaxaStepOutcome<string> atTen) = register.Step(atNine.Next!, FirstTwo, setup.Source.Next);

        Assert.AreEqual(QuePaxaStepKind.Decided, atTen.Kind);
        Assert.AreEqual("a", atTen.DecidedValue);
        Assert.AreEqual(LaneA, atTen.DecidedBy);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(2, 2), atTen.DecidedAt);

        //The stepping proposer is B throughout: the decision is reported against the value's owner, and the
        //observer is nowhere in the outcome.
        Assert.AreEqual(LaneB, atNine.Next!.Proposer);
        Assert.AreNotEqual(atNine.Next.Proposer, atTen.DecidedBy);
    }


    /// <summary>
    /// THE ROUND BOUNDARY, three rules the one-round model cannot see.
    /// </summary>
    /// <remarks>
    /// The carry sets the template to the greatest prior aggregate, the carried proposal keeps ITS OWNER
    /// across the boundary, and every phase-zero send above the first step is redrawn - including the
    /// configured leader's, because the redraw condition's step disjunct fires for everyone after step four.
    /// </remarks>
    [TestMethod]
    public void TheRoundBoundaryCarriesTheGreatestPriorAggregateKeepsItsOwnerAndRedrawsForEveryone()
    {
        CarrySetup setup = BuildPhaseThreeCarry();

        //Rule one: the phase-three template becomes the greatest prior aggregate across the summaries, which
        //is the carry the model's carry-existent negative exists to protect.
        (QuePaxaRegister<string> register, QuePaxaStepOutcome<string> atSeven) = setup.Register.Step(setup.RoundAtSeven, FirstTwo, setup.Source.Next);
        Assert.AreEqual(QuePaxaStepKind.Advanced, atSeven.Kind);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(2, 0), atSeven.Next!.Step);
        Assert.AreEqual(setup.LeadersProposal, atSeven.Next.Proposal);
        Assert.AreEqual(LaneA, atSeven.Next.Proposal.Key.Owner);
        Assert.AreEqual(LaneB, atSeven.Next.Proposer);

        //Rule two: crossing the boundary redraws the priority and leaves the owner attached to the proposal,
        //so a proposal carried forward from another proposer keeps that proposer's identity. A restamping
        //implementation would rewrite the owner here and would have to redo Lemma C.10's Case 2 argument.
        (QuePaxaRegister<string> afterEight, QuePaxaStepOutcome<string> atEight) = register.Step(atSeven.Next, FirstTwo, setup.Source.Next);
        Assert.AreEqual(QuePaxaStepKind.Advanced, atEight.Kind);
        foreach(int index in FirstTwo)
        {
            PrioritizedProposal<string> recorded = afterEight.Recorders[index].Register.First!;

            Assert.AreEqual(LaneA, recorded.Key.Owner);
            Assert.AreEqual("a", recorded.Value);
            Assert.AreNotEqual(setup.LeadersProposal.Key.Priority, recorded.Key.Priority);
            Assert.IsTrue(recorded.Key.Priority.IsOrdinary);
        }

        //Rule three, on its own register because it needs the CONFIGURED LEADER to be the one stepping: at
        //step eight the leader's own send is redrawn to an ordinary priority. An implementation that redraws
        //only when the proposer does not claim leadership lets the leader re-claim the reserved priority in
        //round two, deviates from Algorithm 4, and produces no divergence to give itself away.
        QuePaxaRegister<string> leadered = QuePaxaRegister<string>.LedBy(3, LaneA);
        var leaderSource = new ScriptedPrioritySource(300, 301, 302);
        QuePaxaRound<string> claimingAtEight = QuePaxaRound<string>.Begin(LaneA, LaneA, "a") with { Step = RecorderStep.FromRoundAndPhase(2, 0) };

        Assert.IsTrue(claimingAtEight.ClaimsLeadership);
        Assert.AreEqual(ProposalPriority.Reserved, claimingAtEight.Proposal.Key.Priority);

        (QuePaxaRegister<string> afterLeader, QuePaxaStepOutcome<string> leaderOutcome) = leadered.Step(claimingAtEight, AllThree, leaderSource.Next);

        Assert.AreEqual(QuePaxaStepKind.Advanced, leaderOutcome.Kind);
        Assert.AreEqual(3, leaderSource.DrawCount);
        ulong[] expectedPriorities = [300, 301, 302];
        for(int index = 0; index < afterLeader.RecorderCount; index++)
        {
            PrioritizedProposal<string> recorded = afterLeader.Recorders[index].Register.First!;

            Assert.IsFalse(recorded.Key.Priority.IsReserved);
            Assert.IsTrue(recorded.Key.Priority.IsOrdinary);
            Assert.AreEqual(expectedPriorities[index], recorded.Key.Priority.Value);
            Assert.AreEqual(LaneA, recorded.Key.Owner);
        }
    }


    /// <summary>
    /// THE PER-RECORDER DRAW. Phase zero redraws the priority PER RECORDER; a single draw shared across
    /// recorders is a plausible implementation, would consume one draw instead of k, and would collapse the
    /// independence the liveness argument rests on.
    /// </summary>
    [TestMethod]
    public void PhaseZeroConsumesOneDrawPerRecorderAndRecordsDistinctPriorities()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        var source = new ScriptedPrioritySource(100, 101, 102);
        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LaneA, null, "a");

        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> outcome) = register.Step(round, AllThree, source.Next);

        Assert.AreEqual(3, source.DrawCount);

        var recordedPriorities = new HashSet<ulong>();
        ulong[] expectedPriorities = [100, 101, 102];
        for(int index = 0; index < after.RecorderCount; index++)
        {
            PrioritizedProposal<string> recorded = after.Recorders[index].Register.First!;

            Assert.AreEqual(expectedPriorities[index], recorded.Key.Priority.Value);
            Assert.IsTrue(recordedPriorities.Add(recorded.Key.Priority.Value), "Two recorders were served the same drawn priority.");
        }

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"recorders={after.RecorderCount}, draws={source.DrawCount}, distinctPriorities={recordedPriorities.Count}"));

        //A phase-one send carries the template unchanged, so no further entropy is consumed.
        (_, _) = after.Step(outcome.Next!, AllThree, source.Next);
        Assert.AreEqual(3, source.DrawCount);
    }


    /// <summary>
    /// A MISTAKEN PROPOSER STILL FAST-DECIDES, which is the executable form of the proposer-side owner check
    /// that was removed.
    /// </summary>
    /// <remarks>
    /// B believes it leads and is configured nowhere, so its own claim is declined at every recorder; it then
    /// gathers a uniform set of the real leader's reserved firsts and MUST decide that value. A proposer-side
    /// check of the winner's owner against the proposer's own belief would refuse this decision and continue
    /// into a global state the checker never visited.
    /// </remarks>
    [TestMethod]
    public void AProposerWithAMistakenBeliefStillFastDecidesTheConfiguredLeadersValue()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);
        var source = new ScriptedPrioritySource();

        QuePaxaRound<string> leaderRound = QuePaxaRound<string>.Begin(LaneA, LaneA, "a");
        (QuePaxaRegister<string> populated, QuePaxaStepOutcome<string> leaderOutcome) = register.Step(leaderRound, AllThree, source.Next);
        Assert.AreEqual(QuePaxaStepKind.Decided, leaderOutcome.Kind);

        QuePaxaRound<string> mistakenRound = QuePaxaRound<string>.Begin(LaneB, LaneB, "b");
        Assert.IsTrue(mistakenRound.ClaimsLeadership);

        (_, QuePaxaStepOutcome<string> mistakenOutcome) = populated.Step(mistakenRound, AllThree, source.Next);

        Assert.AreEqual(QuePaxaStepKind.Decided, mistakenOutcome.Kind);
        Assert.AreEqual("a", mistakenOutcome.DecidedValue);
        Assert.AreEqual(LaneA, mistakenOutcome.DecidedBy);
        Assert.AreEqual(Four, mistakenOutcome.DecidedAt);
        Assert.AreNotEqual(mistakenRound.Proposer, mistakenOutcome.DecidedBy);

        //Neither proposer redrew: both claimed leadership at the first step, which is the only step at which
        //the reserved priority means anything.
        Assert.AreEqual(0, source.DrawCount);
    }


    /// <summary>
    /// WHAT THE RULE COSTS THE DEPLOYMENT, as an executable negative. Recorders that disagree about the
    /// configured leader honour two different reserved claims, so two reserved-priority proposals coexist -
    /// the state the downgrade exists to make unreachable, and the state the agreement hazard is built from.
    /// </summary>
    [TestMethod]
    public void RecordersConfiguredWithDifferentLeadersHonourTwoReservedClaimsAtOnce()
    {
        ImmutableArray<QuePaxaRecorder<string>> recorders =
        [
            QuePaxaRecorder<string>.LedBy(LaneA),
            QuePaxaRecorder<string>.LedBy(LaneB),
            QuePaxaRecorder<string>.LedBy(LaneA)
        ];
        QuePaxaRegister<string> register = QuePaxaRegister<string>.FromRecorders(recorders);
        var source = new ScriptedPrioritySource();

        (QuePaxaRegister<string> afterA, QuePaxaStepOutcome<string> outcomeA) = register.Step(QuePaxaRound<string>.Begin(LaneA, LaneA, "a"), Outer, source.Next);
        Assert.AreEqual(QuePaxaStepKind.Decided, outcomeA.Kind);

        ImmutableArray<int> middle = [1];
        (QuePaxaRegister<string> afterB, QuePaxaStepOutcome<string> outcomeB) = afterA.Step(QuePaxaRound<string>.Begin(LaneB, LaneB, "b"), middle, source.Next);
        Assert.AreEqual(QuePaxaStepKind.QuorumMissed, outcomeB.Kind);

        //Two reserved firsts, owned by two different lanes, alive in one register at one step.
        PrioritizedProposal<string> firstAtZero = afterB.Recorders[0].Register.First!;
        PrioritizedProposal<string> firstAtOne = afterB.Recorders[1].Register.First!;

        Assert.AreEqual(ProposalPriority.Reserved, firstAtZero.Key.Priority);
        Assert.AreEqual(LaneA, firstAtZero.Key.Owner);
        Assert.AreEqual(ProposalPriority.Reserved, firstAtOne.Key.Priority);
        Assert.AreEqual(LaneB, firstAtOne.Key.Owner);
        Assert.AreNotEqual(firstAtZero, firstAtOne);
    }


    /// <summary>
    /// The contended run that carries B to a phase-three boundary holding A's proposal.
    /// </summary>
    /// <remarks>
    /// Both proposers are leaderless believers, so every phase-zero send is redrawn and the whole ordering is
    /// decided by the scripted priorities: B draws 10 at each of recorders zero and one, A draws 90 at each of
    /// recorders one and two. A's higher key wins every fold it takes part in, B's step at six sees a prior
    /// aggregate that is not its own template and so does not decide, and A's step at six decides its own
    /// value. B is then at step seven with recorder one carrying A's aggregate, which is the carry the
    /// boundary tests read.
    /// </remarks>
    private static CarrySetup BuildPhaseThreeCarry()
    {
        QuePaxaRegister<string> register = QuePaxaRegister<string>.WithRecorders(3);
        var source = new ScriptedPrioritySource(10, 10, 90, 90, 200, 200);

        QuePaxaRound<string> bRound = QuePaxaRound<string>.Begin(LaneB, null, "b");
        QuePaxaRound<string> aRound = QuePaxaRound<string>.Begin(LaneA, null, "a");

        (register, QuePaxaStepOutcome<string> bAtFour) = register.Step(bRound, FirstTwo, source.Next);
        (register, QuePaxaStepOutcome<string> aAtFour) = register.Step(aRound, LastTwo, source.Next);
        (register, QuePaxaStepOutcome<string> bAtFive) = register.Step(bAtFour.Next!, FirstTwo, source.Next);
        (register, QuePaxaStepOutcome<string> aAtFive) = register.Step(aAtFour.Next!, LastTwo, source.Next);
        (register, QuePaxaStepOutcome<string> bAtSix) = register.Step(bAtFive.Next!, FirstTwo, source.Next);
        (register, QuePaxaStepOutcome<string> aAtSix) = register.Step(aAtFive.Next!, LastTwo, source.Next);

        //B must reach step seven WITHOUT deciding, or the carry it is built for is unreachable.
        Assert.AreEqual(QuePaxaStepKind.Advanced, bAtSix.Kind);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 3), bAtSix.Next!.Step);
        Assert.AreEqual(QuePaxaStepKind.Decided, aAtSix.Kind);
        Assert.AreEqual(LaneA, aAtSix.DecidedBy);

        return new CarrySetup(register, bAtSix.Next, Ordinary(90, LaneA, "a"), source);
    }


    /// <summary>
    /// PHASE TWO DECIDES ONLY ON A PRESENT PRIOR AGGREGATE. The non-null guard looks like defensive noise
    /// because no protocol-driven round reaches phase two against recorders that all skipped the previous
    /// step, but weakening it to treat an absent aggregate as a match lets a proposer decide with no evidence
    /// at all, and two proposers holding different templates would then decide differently.
    /// </summary>
    /// <remarks>
    /// Recorders stepped straight from nothing to phase two skip the intervening step, so the skipped-step
    /// rule leaves every prior aggregate absent, which is the state that discriminates the guard.
    /// </remarks>
    [TestMethod]
    public void PhaseTwoDoesNotDecideWhenEveryPriorAggregateIsAbsent()
    {
        var source = new ScriptedPrioritySource(31, 32);
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);

        QuePaxaRound<string> decide = QuePaxaRound<string>.Begin(LaneA, LaneA, "a") with { Step = RecorderStep.FromRoundAndPhase(1, 2) };
        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> gathered) = register.Step(decide, FirstTwo, source.Next);

        Assert.IsNull(after.Recorders[0].Register.PriorAggregate);
        Assert.IsNull(after.Recorders[1].Register.PriorAggregate);
        Assert.AreNotEqual(QuePaxaStepKind.Decided, gathered.Kind);
        Assert.AreEqual(QuePaxaStepKind.Advanced, gathered.Kind);
    }


    /// <summary>
    /// THE FAST-PATH TEST IS WHOLE-PROPOSAL AND NOT WHOLE-KEY. Two proposals that share a key and differ in
    /// value are what a host looks like when it has lost single-flight, and the proposal key's uniqueness
    /// contract is a surface obligation that nothing in this slice enforces.
    /// </summary>
    /// <remarks>
    /// Under a key-only test the gather below is uniform and the fast path fires, returning whichever value
    /// the implementation happened to read first; under whole-proposal equality it refuses. Without this case
    /// the comparison could be weakened to keys with no test noticing, because every other scenario in the
    /// suite honours the contract.
    /// </remarks>
    [TestMethod]
    public void TheFastPathRefusesTwoFirstsThatShareAKeyAndDifferInValue()
    {
        var source = new ScriptedPrioritySource(11, 12, 13);
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);

        //One lane, two values, both claiming the leadership it really holds: the keys collide exactly.
        (register, _) = register.Step(QuePaxaRound<string>.Begin(LaneA, LaneA, "a"), [0], source.Next);
        (register, _) = register.Step(QuePaxaRound<string>.Begin(LaneA, LaneA, "b"), [1], source.Next);

        PrioritizedProposal<string> firstAtZero = register.Recorders[0].Register.First!;
        PrioritizedProposal<string> firstAtOne = register.Recorders[1].Register.First!;

        Assert.AreEqual(firstAtZero.Key, firstAtOne.Key);
        Assert.AreNotEqual(firstAtZero.Value, firstAtOne.Value);

        (_, QuePaxaStepOutcome<string> gathered) = register.Step(QuePaxaRound<string>.Begin(LaneA, LaneA, "c"), FirstTwo, source.Next);

        Assert.AreNotEqual(QuePaxaStepKind.Decided, gathered.Kind);
        Assert.AreEqual(QuePaxaStepKind.Advanced, gathered.Kind);
    }


    /// <summary>
    /// THE PHASE-TWO TEST IS WHOLE-PROPOSAL AND NOT WHOLE-KEY, and here it is safety rather than tidiness.
    /// </summary>
    /// <remarks>
    /// The greatest prior aggregate below shares the template's key and carries a different value, which is
    /// the same lost-single-flight shape. A key-only test decides, and two proposers holding the two colliding
    /// values could then decide differently; whole-proposal equality lets at most one of them through.
    /// </remarks>
    [TestMethod]
    public void PhaseTwoRefusesAPriorAggregateThatSharesTheTemplateKeyAndDiffersInValue()
    {
        var source = new ScriptedPrioritySource(21, 22);
        QuePaxaRegister<string> register = QuePaxaRegister<string>.LedBy(3, LaneA);

        //Phase one spreads the colliding value, so the step-five aggregate becomes the prior aggregate the
        //phase-two gather reads.
        QuePaxaRound<string> spread = QuePaxaRound<string>.Begin(LaneA, LaneA, "b") with { Step = RecorderStep.FromRoundAndPhase(1, 1) };
        (register, _) = register.Step(spread, FirstTwo, source.Next);

        QuePaxaRound<string> decide = QuePaxaRound<string>.Begin(LaneA, LaneA, "a") with { Step = RecorderStep.FromRoundAndPhase(1, 2) };
        (QuePaxaRegister<string> after, QuePaxaStepOutcome<string> gathered) = register.Step(decide, FirstTwo, source.Next);

        PrioritizedProposal<string> prior = after.Recorders[0].Register.PriorAggregate!;

        Assert.AreEqual(decide.Proposal.Key, prior.Key);
        Assert.AreNotEqual(decide.Proposal.Value, prior.Value);
        Assert.AreNotEqual(QuePaxaStepKind.Decided, gathered.Kind);
        Assert.AreEqual(QuePaxaStepKind.Advanced, gathered.Kind);
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


    private sealed record CarrySetup(
        QuePaxaRegister<string> Register,
        QuePaxaRound<string> RoundAtSeven,
        PrioritizedProposal<string> LeadersProposal,
        ScriptedPrioritySource Source);


    /// <summary>
    /// A fixed sequence rather than a seeded stream: every comparison these scenarios turn on is constructed,
    /// so running past the end of the script means the scenario consumed entropy it was not designed to and
    /// fails loudly rather than drifting into a different behaviour.
    /// </summary>
    private sealed class ScriptedPrioritySource
    {
        private int index;

        public ScriptedPrioritySource(params ulong[] script) => Script = script;


        public int DrawCount { get; private set; }


        private ulong[] Script { get; }


        public ProposalPriority Next()
        {
            if(index >= Script.Length)
            {
                throw new InvalidOperationException("The scripted priority source ran out of draws.");
            }

            DrawCount++;

            return new ProposalPriority(Script[index++]);
        }
    }
}
