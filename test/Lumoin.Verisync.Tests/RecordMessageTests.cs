using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The wire message family's unit suite: the record request a proposer sends and the record reply a recorder
/// answers with. Both types are the codec boundary, so their validation is where a malformed decode is
/// stopped; every rule below exists because the state it refuses is one the core would otherwise have to
/// tolerate deeper in, where the fail-closed answer is an exception documented as meaning the register is
/// corrupt.
/// </summary>
[TestClass]
internal sealed class RecordMessageTests
{
    /// <summary>
    /// Identities from fixed bytes, so no assertion below depends on which way a generated pair happened to
    /// sort.
    /// </summary>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    /// <summary>
    /// THE REQUEST IS THE WIRE BOUNDARY, so it refuses the three shapes a decoder can produce and the core
    /// cannot represent.
    /// </summary>
    /// <remarks>
    /// A null proposal has no protocol meaning at all; a step below round one phase zero is the only illegal
    /// step the step type itself cannot refuse, and refusing it here is what lets the node trust its input;
    /// and the absent priority is the aggregate fold's identity, which is never drawn and never sent, so a
    /// request carrying it would put the identity element on the wire.
    /// </remarks>
    [TestMethod]
    public void ARequestRefusesANullProposalAStepBelowTheFirstStepAndTheAbsentPriority()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new RecordRequest<string>(Four, null!));
        Assert.ThrowsExactly<ArgumentException>(() => _ = new RecordRequest<string>(Four, Absent(LaneA, "a")));

        for(int value = 0; value < RecorderStep.RoundOnePhaseZero.Value; value++)
        {
            RecorderStep step = new(value);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RecordRequest<string>(step, Ordinary(5, LaneA, "a")));
        }
    }


    /// <summary>
    /// THE BOUNDARY IS INCLUSIVE. Round one phase zero is the protocol's first step and the one at which the
    /// reserved priority means anything, so a floor that refused it would refuse the fast path itself.
    /// </summary>
    [TestMethod]
    public void ARequestAcceptsTheFirstStepExactly()
    {
        RecordRequest<string> request = new(RecorderStep.RoundOnePhaseZero, Ordinary(5, LaneA, "a"));

        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, request.Step);
        Assert.AreEqual(Ordinary(5, LaneA, "a"), request.Proposal);
    }


    /// <summary>
    /// A RESERVED PRIORITY ABOVE THE FIRST STEP IS LEGAL AND MUST NOT BE VALIDATED AWAY. It looks wrong and is
    /// not: when the fast path fails, the phase-zero template becomes the best of the first proposals, which
    /// may be the leader's own reserved-priority proposal, and phases one to three then send that template
    /// untouched.
    /// </summary>
    /// <remarks>
    /// The model does exactly this, assigning the proposal for a recorder from the template whenever the send
    /// does not randomize. A validator refusing it would deadlock the protocol on its own most common
    /// contended path, which is why this test exists rather than the check.
    /// </remarks>
    [TestMethod]
    public void ARequestAcceptsAReservedPriorityAboveTheFirstStep()
    {
        RecordRequest<string> spread = new(RecorderStep.FromRoundAndPhase(1, 1), Reserved(LaneA, "a"));
        RecordRequest<string> decide = new(RecorderStep.FromRoundAndPhase(1, 2), Reserved(LaneA, "a"));

        Assert.AreEqual(ProposalPriority.Reserved, spread.Proposal.Key.Priority);
        Assert.AreEqual(ProposalPriority.Reserved, decide.Proposal.Key.Priority);
    }


    /// <summary>
    /// A WITH EXPRESSION REVALIDATES. A positional record's primary-constructor assignment writes the backing
    /// field directly and no accessor runs for it, so validation stated only in the constructor would be
    /// bypassed by every copy; the validating init accessor is what makes the refusal a property of the type
    /// rather than of one construction site.
    /// </summary>
    [TestMethod]
    public void ARequestRevalidatesUnderAWithExpression()
    {
        RecordRequest<string> request = new(Four, Ordinary(5, LaneA, "a"));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = request with { Step = new RecorderStep(3) });
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = request with { Proposal = null! });
        Assert.ThrowsExactly<ArgumentException>(() => _ = request with { Proposal = Absent(LaneA, "a") });

        //A legal copy still copies, so the validation refuses the illegal shape rather than the expression.
        RecordRequest<string> advanced = request with { Step = Four.Next() };

        Assert.AreEqual(Four.Next(), advanced.Step);
        Assert.AreEqual(request.Proposal, advanced.Proposal);
    }


    /// <summary>
    /// THE REPLY'S FIRST PROPOSAL IS NON-NULLABLE AND ITS STEP HAS THE SAME FLOOR AS THE REQUEST'S. The state a
    /// nullable first would represent is unreachable through a node: the recorder refuses any step below round
    /// one phase zero, an initial register sits at step zero, so the first request a recorder ever takes lands
    /// on the advancing branch and sets the first proposal.
    /// </summary>
    /// <remarks>
    /// Validating it at the message boundary is what keeps the conclusion's own null check a backstop rather
    /// than a live path, because a malformed reply would otherwise abort a proposal with an exception
    /// documented as meaning the register state is corrupt.
    /// </remarks>
    [TestMethod]
    public void AReplyRefusesANullFirstProposalAndAStepBelowTheFirstStep()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new RecordReply<string>(Four, null!, null));

        for(int value = 0; value < RecorderStep.RoundOnePhaseZero.Value; value++)
        {
            RecorderStep step = new(value);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RecordReply<string>(step, Ordinary(5, LaneA, "a"), null));
        }
    }


    [TestMethod]
    public void AReplyRevalidatesUnderAWithExpression()
    {
        RecordReply<string> reply = new(Four, Ordinary(5, LaneA, "a"), null);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = reply with { Step = new RecorderStep(3) });
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = reply with { First = null! });

        //The prior aggregate stays nullable under a copy, because a skipped step legitimately clears it.
        RecordReply<string> cleared = reply with { PriorAggregate = null };

        Assert.IsNull(cleared.PriorAggregate);
    }


    /// <summary>
    /// THE ABSENT PRIOR AGGREGATE IS THE SKIPPED-STEP CASE and is a legal reply rather than a degenerate one: a
    /// recorder that advanced by more than one never gathered the intervening aggregate, so carrying a value
    /// from it would hand a proposer something no quorum served.
    /// </summary>
    [TestMethod]
    public void AReplyRoundTripsItsThreeFieldsWithAnAbsentPriorAggregate()
    {
        RecordReply<string> skipped = new(Four.Next(), Ordinary(5, LaneA, "a"), null);
        RecordReply<string> carried = new(Four.Next(), Ordinary(5, LaneA, "a"), Ordinary(9, LaneB, "b"));

        Assert.AreEqual(Four.Next(), skipped.Step);
        Assert.AreEqual(Ordinary(5, LaneA, "a"), skipped.First);
        Assert.IsNull(skipped.PriorAggregate);

        Assert.AreEqual(Ordinary(9, LaneB, "b"), carried.PriorAggregate);
    }


    /// <summary>
    /// EQUALITY IS BY VALUE ON BOTH MESSAGES, which the codec slice relies on for its round-trip tests and
    /// which the re-send rule relies on for its own: a re-delivery is permitted exactly when it is identical,
    /// and identical is decided by record equality including the proposal's priority.
    /// </summary>
    [TestMethod]
    public void EqualityOnBothMessagesIsByValueAndSeparatesTheDrawnPriority()
    {
        RecordRequest<string> request = new(Four, Ordinary(5, LaneA, "a"));
        RecordRequest<string> sameRequest = new(Four, Ordinary(5, LaneA, "a"));
        RecordRequest<string> otherPriority = new(Four, Ordinary(6, LaneA, "a"));
        RecordRequest<string> otherStep = new(Four.Next(), Ordinary(5, LaneA, "a"));

        Assert.AreEqual(request, sameRequest);
        Assert.AreEqual(request.GetHashCode(), sameRequest.GetHashCode());
        Assert.AreNotEqual(request, otherPriority);
        Assert.AreNotEqual(request, otherStep);

        RecordReply<string> reply = new(Four, Ordinary(5, LaneA, "a"), Ordinary(9, LaneB, "b"));
        RecordReply<string> sameReply = new(Four, Ordinary(5, LaneA, "a"), Ordinary(9, LaneB, "b"));
        RecordReply<string> withoutCarry = new(Four, Ordinary(5, LaneA, "a"), null);

        Assert.AreEqual(reply, sameReply);
        Assert.AreEqual(reply.GetHashCode(), sameReply.GetHashCode());
        Assert.AreNotEqual(reply, withoutCarry);
    }


    private static PrioritizedProposal<string> Ordinary(ulong priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    private static PrioritizedProposal<string> Reserved(ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.Reserved, owner), value);
    }


    private static PrioritizedProposal<string> Absent(ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.None, owner), value);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
