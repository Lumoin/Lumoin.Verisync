using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The interval summary register's unit suite: Algorithm 3's three cases, one test each, plus the shape
/// rules the cases rest on. The register is constant space and immutable, so a record returns a new
/// register and a summary taken from that register AFTER the update; a record below the current step is
/// obsolete and returns the same instance.
/// </summary>
[TestClass]
internal sealed class IntervalSummaryRegisterTests
{
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    /// <summary>
    /// THE FOLD COMPARES WHOLE KEYS AND NOT PRIORITIES ALONE. A tie on priority is settled by the owner, which
    /// is Appendix A's tiebreaking approach and the only thing that keeps the fold total when two proposals
    /// carry the same priority.
    /// </summary>
    /// <remarks>
    /// Every other deterministic fold in this suite uses distinct priorities, so a comparison narrowed to the
    /// priority would fold the same way there and only diverge where two owners meet at one priority - which
    /// is exactly what a leaderless round produces, since every declined reserved claim lands on the same
    /// lowest ordinary priority.
    /// </remarks>
    [TestMethod]
    public void ThePriorityTieIsSettledByTheOwnerInBothArrivalOrders()
    {
        PrioritizedProposal<string> lower = Proposal(42, LaneA, "x");
        PrioritizedProposal<string> higher = Proposal(42, LaneB, "y");

        (IntervalSummaryRegister<string> forward, _) = IntervalSummaryRegister<string>.Initial.Record(Four, lower);
        (forward, _) = forward.Record(Four, higher);

        (IntervalSummaryRegister<string> backward, _) = IntervalSummaryRegister<string>.Initial.Record(Four, higher);
        (backward, _) = backward.Record(Four, lower);

        Assert.AreEqual(higher, forward.CurrentAggregate);
        Assert.AreEqual(higher, backward.CurrentAggregate);
    }


    [TestMethod]
    public void InitialIsAtZeroWithNoProposals()
    {
        IntervalSummaryRegister<string> register = IntervalSummaryRegister<string>.Initial;

        Assert.AreEqual(RecorderStep.Zero, register.Step);
        Assert.IsNull(register.First);
        Assert.IsNull(register.CurrentAggregate);
        Assert.IsNull(register.PriorAggregate);
    }


    [TestMethod]
    public void TheFirstRecordAdvancesAndSetsBothFirstAndTheAggregate()
    {
        PrioritizedProposal<string> proposal = Proposal(5, LaneA, "a");

        (IntervalSummaryRegister<string> register, RecordSummary<string> summary) = IntervalSummaryRegister<string>.Initial.Record(Four, proposal);

        Assert.AreEqual(Four, register.Step);
        Assert.AreEqual(proposal, register.First);
        Assert.AreEqual(proposal, register.CurrentAggregate);

        //The advance from Zero to step four skips steps one through three, so nothing is carried forward.
        Assert.IsNull(register.PriorAggregate);
        Assert.AreEqual(new RecordSummary<string>(Four, proposal, null), summary);
    }


    [TestMethod]
    public void ASameStepRecordFoldsTheAggregateAndLeavesFirstUntouched()
    {
        //First is assigned only on the advancing branch, which is Algorithm 3 literally: a later arrival at
        //the same step raises the aggregate but can never displace the proposal that got there first.
        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));

        PrioritizedProposal<string> greater = Proposal(9, LaneB, "b");
        (IntervalSummaryRegister<string> twice, RecordSummary<string> summary) = once.Record(Four, greater);

        Assert.AreEqual(Four, twice.Step);
        Assert.AreEqual(Proposal(5, LaneA, "a"), twice.First);
        Assert.AreEqual(greater, twice.CurrentAggregate);
        Assert.AreEqual(Proposal(5, LaneA, "a"), summary.First);
        Assert.IsNull(summary.PriorAggregate);
    }


    [TestMethod]
    public void ASameStepRecordWithALowerKeyKeepsTheIncumbentAggregate()
    {
        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(9, LaneA, "a"));

        (IntervalSummaryRegister<string> twice, _) = once.Record(Four, Proposal(5, LaneB, "b"));

        Assert.AreEqual(Proposal(9, LaneA, "a"), twice.CurrentAggregate);
        Assert.AreEqual(Proposal(9, LaneA, "a"), twice.First);

        //No field would have changed, so the register returns itself rather than allocating a copy of its own
        //state; that identity is the predicate the recorder and the node above it both read.
        Assert.AreSame(once, twice);
    }


    /// <summary>
    /// IDEMPOTENCE, WHICH IS THE UNIT FORM OF THE RE-SEND RULE. A second IDENTICAL record at the register's own
    /// step folds into an aggregate that already dominates it, so no field would change and the register
    /// returns ITSELF.
    /// </summary>
    /// <remarks>
    /// The layers above read exactly that: the recorder decides whether it changed by reference, and the node
    /// decides whether to persist by the same test, so a retransmission on a lossy link costs no durable write
    /// that makes nothing durable.
    /// </remarks>
    [TestMethod]
    public void ARepeatedSameStepRecordReturnsTheSameInstance()
    {
        PrioritizedProposal<string> proposal = Proposal(5, LaneA, "a");
        (IntervalSummaryRegister<string> once, RecordSummary<string> firstSummary) = IntervalSummaryRegister<string>.Initial.Record(Four, proposal);

        (IntervalSummaryRegister<string> twice, RecordSummary<string> secondSummary) = once.Record(Four, proposal);

        Assert.AreSame(once, twice);
        Assert.AreEqual(firstSummary, secondSummary);
        Assert.AreEqual(proposal, twice.First);
        Assert.AreEqual(proposal, twice.CurrentAggregate);
    }


    /// <summary>
    /// THE FOLD KEEPS THE INCUMBENT ON AN EXACT KEY TIE, and an EQUAL KEY WITH A DIFFERENT VALUE is the only
    /// shape that observes the tie direction at all.
    /// </summary>
    /// <remarks>
    /// A strictly lower-keyed record returns the incumbent under both directions and discriminates nothing;
    /// here the incumbent survives and the register is unchanged under the tie-keeping direction, while a fold
    /// preferring the challenger takes the new value and allocates. The two proposals violate the proposal
    /// key's uniqueness contract deliberately, which is what a host looks like once it has lost single-flight,
    /// and that contract is a surface obligation a constant-space register cannot police.
    /// </remarks>
    [TestMethod]
    public void ASameStepRecordAtAnEqualKeyWithADifferentValueReturnsTheSameInstance()
    {
        ProposalKey shared = new(new ProposalPriority(42), LaneA);
        PrioritizedProposal<string> incumbent = new(shared, "v1");
        PrioritizedProposal<string> challenger = new(shared, "v2");

        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, incumbent);
        (IntervalSummaryRegister<string> twice, _) = once.Record(Four, challenger);

        Assert.AreEqual(incumbent.Key, challenger.Key);
        Assert.AreNotEqual(incumbent, challenger);
        Assert.AreSame(once, twice);
        Assert.AreEqual(incumbent, twice.CurrentAggregate);
    }


    /// <summary>
    /// THE OTHER DIRECTION, so the same-instance rule reads as a predicate rather than as a constant.
    /// </summary>
    /// <remarks>
    /// A higher-keyed record at the same step changes the aggregate, so a new register is allocated and the
    /// reference test above reports a real change.
    /// </remarks>
    [TestMethod]
    public void AHigherKeyedSameStepRecordReturnsADifferentInstance()
    {
        (IntervalSummaryRegister<string> once, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));

        (IntervalSummaryRegister<string> twice, _) = once.Record(Four, Proposal(9, LaneB, "b"));

        Assert.AreNotSame(once, twice);
        Assert.AreEqual(Proposal(9, LaneB, "b"), twice.CurrentAggregate);
        Assert.AreEqual(Proposal(5, LaneA, "a"), twice.First);
    }


    [TestMethod]
    public void AdvancingByExactlyOneCarriesTheAggregateAsThePriorAggregate()
    {
        (IntervalSummaryRegister<string> register, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));
        (register, _) = register.Record(Four, Proposal(9, LaneB, "b"));

        PrioritizedProposal<string> advancing = Proposal(7, LaneA, "c");
        (IntervalSummaryRegister<string> advanced, RecordSummary<string> summary) = register.Record(Four.Next(), advancing);

        //The carry is the AGGREGATE at the previous step, which is neither the first proposal recorded there
        //nor the proposal that advanced the register.
        Assert.AreEqual(Proposal(9, LaneB, "b"), advanced.PriorAggregate);
        Assert.AreEqual(advancing, advanced.First);
        Assert.AreEqual(advancing, advanced.CurrentAggregate);
        Assert.AreEqual(Four.Next(), advanced.Step);
        Assert.AreEqual(new RecordSummary<string>(Four.Next(), advancing, Proposal(9, LaneB, "b")), summary);
    }


    [TestMethod]
    public void AdvancingByMoreThanOneClearsThePriorAggregate()
    {
        //A skipped step means the proposer never gathered the intervening aggregate, so carrying it would
        //hand a reader a value from a step no quorum served.
        (IntervalSummaryRegister<string> register, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(9, LaneA, "a"));

        PrioritizedProposal<string> advancing = Proposal(7, LaneB, "b");
        (IntervalSummaryRegister<string> advanced, RecordSummary<string> summary) = register.Record(Four.Next().Next(), advancing);

        Assert.IsNull(advanced.PriorAggregate);
        Assert.AreEqual(advancing, advanced.First);
        Assert.AreEqual(advancing, advanced.CurrentAggregate);
        Assert.IsNull(summary.PriorAggregate);
    }


    [TestMethod]
    public void AStaleRecordChangesNothingAndReturnsTheSameInstance()
    {
        (IntervalSummaryRegister<string> register, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));
        (IntervalSummaryRegister<string> advanced, RecordSummary<string> advancedSummary) = register.Record(Four.Next(), Proposal(9, LaneB, "b"));

        (IntervalSummaryRegister<string> afterStale, RecordSummary<string> staleSummary) = advanced.Record(Four, Proposal(99, LaneA, "stale"));

        Assert.AreSame(advanced, afterStale);
        Assert.AreEqual(advancedSummary, staleSummary);
        Assert.AreEqual(Four.Next(), afterStale.Step);
        Assert.AreEqual(Proposal(9, LaneB, "b"), afterStale.First);
        Assert.AreEqual(Proposal(9, LaneB, "b"), afterStale.CurrentAggregate);
    }


    [TestMethod]
    public void TheSummaryIsTakenFromTheRegisterAfterTheUpdate()
    {
        //Lemma C.2 makes the summary's step at least the requested one, which is what makes catch-up the only
        //alternative to advancing; the summary is therefore read after the record, never before.
        (IntervalSummaryRegister<string> register, RecordSummary<string> summary) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));

        Assert.AreEqual(register.Step, summary.Step);
        Assert.AreEqual(register.First, summary.First);
        Assert.AreEqual(register.PriorAggregate, summary.PriorAggregate);

        (IntervalSummaryRegister<string> advanced, RecordSummary<string> advancedSummary) = register.Record(Four.Next(), Proposal(9, LaneB, "b"));
        Assert.AreEqual(advanced.Step, advancedSummary.Step);
        Assert.AreEqual(advanced.First, advancedSummary.First);
        Assert.AreEqual(advanced.PriorAggregate, advancedSummary.PriorAggregate);
    }


    [TestMethod]
    public void TheCurrentAggregateIsNotPartOfTheSummary()
    {
        //The constant-space contract is that a proposer reads an aggregate one step after it accumulated, as
        //the prior aggregate; a summary that carried the current one would let a reader see a half-formed
        //step. The register exposes it, the summary does not, so a fresh advance shows a non-null current
        //aggregate beside a null carried one.
        (IntervalSummaryRegister<string> register, RecordSummary<string> summary) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));

        Assert.IsNotNull(register.CurrentAggregate);
        Assert.IsNull(summary.PriorAggregate);
        Assert.AreEqual(register.First, summary.First);
    }


    [TestMethod]
    public void ARegisterAboveZeroAlwaysHasANonNullFirst()
    {
        //The invariant that replaces a defensive clause: Initial is at Zero, every request is at or above
        //step four, so the first record a register ever takes falls into the advancing case and sets First.
        (IntervalSummaryRegister<string> register, _) = IntervalSummaryRegister<string>.Initial.Record(Four, Proposal(5, LaneA, "a"));

        Assert.IsTrue(register.Step > RecorderStep.Zero);
        Assert.IsNotNull(register.First);

        (IntervalSummaryRegister<string> folded, _) = register.Record(Four, Proposal(9, LaneB, "b"));
        Assert.IsNotNull(folded.First);

        (IntervalSummaryRegister<string> advanced, _) = folded.Record(Four.Next(), Proposal(3, LaneA, "c"));
        Assert.IsNotNull(advanced.First);
    }


    private static PrioritizedProposal<string> Proposal(ulong priority, ProposerLane owner, string value)
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
