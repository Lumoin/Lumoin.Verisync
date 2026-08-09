using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The recorder's unit suite, and with it the whole of this slice's safety. One rule carries safety on its
/// own: a reserved-priority proposal whose owner is not the recorder's configured leader is recorded at the
/// lowest ordinary priority at the round's first step. It is a downgrade and not a drop, so the round
/// proceeds through the ordinary phases, the register keeps no holes, and no proposer is left reading a step
/// at which some recorder has no first proposal.
/// </summary>
[TestClass]
internal sealed class QuePaxaRecorderTests
{
    /// <summary>
    /// Identities come from fixed bytes so that the leader binding is exercised against a stable ordering
    /// rather than against whatever a generator produced.
    /// </summary>
    private static ReplicaId ReplicaA { get; } = Replica(1);

    /// <summary>The second identity, which owns the lane the downgrade declines.</summary>
    private static ReplicaId ReplicaB { get; } = Replica(2);

    /// <summary>The lane the recorders under test are configured with.</summary>
    private static ProposerLane LeaderLane { get; } = ProposerLane.For(ReplicaA);

    /// <summary>A lane on the other replica, which no recorder here honours.</summary>
    private static ProposerLane OtherReplicaLane { get; } = ProposerLane.For(ReplicaB);

    /// <summary>A second lane on the leader's own replica, which the binding declines because it binds a lane.</summary>
    private static ProposerLane SecondLaneOfLeaderReplica { get; } = new(ReplicaA, 1);

    /// <summary>The round's first step, which is the only step the downgrade applies at.</summary>
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    /// <summary>
    /// The downgrade carries a step qualifier and it is the round's first step alone. The reserved priority
    /// earns a decision only where the fast path reads it, so rewriting a declined claim above that step
    /// defends nothing, and it costs the one thing that matters: a rewrite is a second key for one logical
    /// proposal, so a recorder declining a claim its neighbours honour records a proposal none of them holds.
    /// </summary>
    [TestMethod]
    public void AReservedClaimIsRecordedVerbatimAboveTheRoundsFirstStep()
    {
        QuePaxaRecorder<string> led = QuePaxaRecorder<string>.LedBy(LeaderLane);
        QuePaxaRecorder<string> leaderless = QuePaxaRecorder<string>.Leaderless;
        RecorderStep five = Four.Next();

        (_, RecordSummary<string> ledAtFive) = led.Record(five, Reserved(OtherReplicaLane, "b"));
        (_, RecordSummary<string> leaderlessAtFive) = leaderless.Record(five, Reserved(LeaderLane, "a"));

        Assert.AreEqual(ProposalPriority.Reserved, ledAtFive.First!.Key.Priority);
        Assert.AreEqual(ProposalPriority.Reserved, leaderlessAtFive.First!.Key.Priority);

        //The leader's own claim is honoured above the first step as it always was, so a rule that had merely
        //been inverted rather than narrowed would show up here.
        (_, RecordSummary<string> leadersOwn) = led.Record(five.Next(), Reserved(LeaderLane, "a"));

        Assert.AreEqual(ProposalPriority.Reserved, leadersOwn.First!.Key.Priority);
    }


    /// <summary>
    /// The qualifier is the round's first step and not every phase zero, which are the same step only in
    /// round one. A later round's phase zero draws an ordinary priority per recorder, so no reserved claim
    /// originates there and a rule written against the phase alone would rewrite a carried template for
    /// nothing.
    /// </summary>
    [TestMethod]
    public void AReservedClaimIsRecordedVerbatimAtALaterRoundsPhaseZero()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);
        RecorderStep roundTwoPhaseZero = RecorderStep.FromRoundAndPhase(2, 0);

        (_, RecordSummary<string> summary) = recorder.Record(roundTwoPhaseZero, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(0, roundTwoPhaseZero.Phase);
        Assert.AreNotEqual(RecorderStep.RoundOnePhaseZero, roundTwoPhaseZero);
        Assert.AreEqual(ProposalPriority.Reserved, summary.First!.Key.Priority);
    }


    /// <summary>
    /// The property the qualifier buys, which is over a recorder set rather than over one recorder: two
    /// recorders that disagree about the leader record a carried template under one key. Without the
    /// qualifier the leaderless one rewrites it, the leader's single logical proposal exists under two keys,
    /// and a quorum built from the rewritten copy carries an ordinary proposal past the honoured one.
    /// </summary>
    [TestMethod]
    public void RecordersThatDisagreeAboutTheLeaderRecordACarriedTemplateUnderOneKey()
    {
        QuePaxaRecorder<string> led = QuePaxaRecorder<string>.LedBy(LeaderLane);
        QuePaxaRecorder<string> leaderless = QuePaxaRecorder<string>.Leaderless;
        PrioritizedProposal<string> carried = Reserved(LeaderLane, "a");
        RecorderStep five = Four.Next();

        (QuePaxaRecorder<string> afterLed, _) = led.Record(five, carried);
        (QuePaxaRecorder<string> afterLeaderless, _) = leaderless.Record(five, carried);

        Assert.AreEqual(carried, afterLed.Register.First);
        Assert.AreEqual(afterLed.Register.First, afterLeaderless.Register.First);
    }


    /// <summary>A leaderless recorder carries no configured leader, and a led one carries the lane it was given.</summary>
    [TestMethod]
    public void LeaderlessCarriesNoConfiguredLeaderAndLedByCarriesTheGivenLane()
    {
        Assert.IsNull(QuePaxaRecorder<string>.Leaderless.ConfiguredLeader);
        Assert.AreEqual(LeaderLane, QuePaxaRecorder<string>.LedBy(LeaderLane).ConfiguredLeader);
    }


    /// <summary>A fresh recorder is at step zero, and its step forwards to the register rather than being held twice.</summary>
    [TestMethod]
    public void AFreshRecorderIsAtZeroAndItsStepForwardsToTheRegister()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        Assert.AreEqual(RecorderStep.Zero, recorder.Step);
        Assert.AreEqual(recorder.Register.Step, recorder.Step);

        (QuePaxaRecorder<string> after, _) = recorder.Record(Four, Reserved(LeaderLane, "a"));

        //The step is forwarded rather than held twice, so the pair cannot drift.
        Assert.AreEqual(Four, after.Step);
        Assert.AreEqual(after.Register.Step, after.Step);
    }


    /// <summary>The configured leader's reserved claim is recorded at the reserved priority.</summary>
    [TestMethod]
    public void TheReservedClaimFromTheConfiguredLeaderIsRecordedAtReserved()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, RecordSummary<string> summary) = recorder.Record(Four, Reserved(LeaderLane, "a"));

        Assert.AreEqual(Reserved(LeaderLane, "a"), after.Register.First);
        Assert.IsTrue(after.Register.First!.Key.Priority.IsReserved);
        Assert.AreEqual(Reserved(LeaderLane, "a"), summary.First);
    }


    /// <summary>A reserved claim from another replica is recorded at the lowest ordinary priority at the round's first step.</summary>
    [TestMethod]
    public void AReservedClaimFromAnotherReplicaIsRecordedAtLowest()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, _) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, after.Register.First!.Key.Priority);
        Assert.AreEqual(OtherReplicaLane, after.Register.First.Key.Owner);
        Assert.AreEqual("b", after.Register.First.Value);
    }


    /// <summary>
    /// The binding is to a lane and not to a replica, so a second lane of the leader's own replica is
    /// declined: two lanes of the leader replica each claiming the reserved priority would reproduce the
    /// divergence hazard from inside the leader.
    /// </summary>
    [TestMethod]
    public void AReservedClaimFromAnotherLaneOfTheLeadersOwnReplicaIsRecordedAtLowest()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, _) = recorder.Record(Four, Reserved(SecondLaneOfLeaderReplica, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, after.Register.First!.Key.Priority);
        Assert.AreEqual(SecondLaneOfLeaderReplica, after.Register.First.Key.Owner);
        Assert.AreEqual(ReplicaA, after.Register.First.Key.Owner.Replica);
    }


    /// <summary>A leaderless recorder declines every reserved claim at the round's first step, whoever owns it.</summary>
    [TestMethod]
    public void ALeaderlessRecorderDowngradesEveryReservedClaim()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.Leaderless;

        (QuePaxaRecorder<string> fromA, _) = recorder.Record(Four, Reserved(LeaderLane, "a"));
        (QuePaxaRecorder<string> fromB, _) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, fromA.Register.First!.Key.Priority);
        Assert.AreEqual(ProposalPriority.Lowest, fromB.Register.First!.Key.Priority);
    }


    /// <summary>
    /// The declined proposal is recorded rather than dropped. Dropping would be a liveness hole and would
    /// leave a recorder with no first proposal at a step a proposer is about to read.
    /// </summary>
    [TestMethod]
    public void TheDowngradedProposalIsRecordedRatherThanDropped()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, RecordSummary<string> summary) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(Four, after.Step);
        Assert.IsNotNull(after.Register.First);
        Assert.IsNotNull(after.Register.CurrentAggregate);
        Assert.IsNotNull(summary.First);
        Assert.AreEqual("b", summary.First.Value);
    }


    /// <summary>
    /// The rule fires on the reserved priority alone. An ordinary proposal from any proposer is never
    /// rewritten, because the downgrade would then destroy the randomization liveness depends on.
    /// </summary>
    [TestMethod]
    public void AnOrdinaryPriorityIsRecordedUntouchedWhoeverOwnsIt()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> fromOther, _) = recorder.Record(Four, Ordinary(500, OtherReplicaLane, "b"));
        (QuePaxaRecorder<string> fromLeader, _) = recorder.Record(Four, Ordinary(500, LeaderLane, "a"));

        Assert.AreEqual(Ordinary(500, OtherReplicaLane, "b"), fromOther.Register.First);
        Assert.AreEqual(Ordinary(500, LeaderLane, "a"), fromLeader.Register.First);
    }


    /// <summary>
    /// The downgrade is what stops two reserved-priority proposals from coexisting at all: the leader's claim
    /// wins the aggregate outright once the rival's has landed on the lowest ordinary priority.
    /// </summary>
    [TestMethod]
    public void ADowngradedClaimLosesTheFoldToAnHonouredOne()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> afterRival, _) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));
        (QuePaxaRecorder<string> afterLeader, _) = afterRival.Record(Four, Reserved(LeaderLane, "a"));

        Assert.AreEqual("b", afterLeader.Register.First!.Value);
        Assert.AreEqual(Reserved(LeaderLane, "a"), afterLeader.Register.CurrentAggregate);
    }


    /// <summary>A step below round one phase zero is refused, because no request ever carries one.</summary>
    [TestMethod]
    public void AStepBelowRoundOnePhaseZeroThrows()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        for(int value = 0; value < RecorderStep.RoundOnePhaseZero.Value; value++)
        {
            RecorderStep step = new(value);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = recorder.Record(step, Ordinary(5, LeaderLane, "a")));
        }
    }


    /// <summary>A null proposal is refused.</summary>
    [TestMethod]
    public void ANullProposalThrows()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = recorder.Record(Four, null!));
    }


    /// <summary>
    /// Idempotence at the recorder, which is the re-send rule's unit form one layer up. A proposer may
    /// deliver a request to a recorder any number of times provided every delivery is identical, and what
    /// makes that safe is that a second identical record changes nothing: the aggregate already dominates the
    /// duplicate's key, the first proposal is not touched by the same-step branch at all, and the recorder
    /// therefore returns itself. The node above reads exactly this reference to decide whether anything needs
    /// persisting.
    /// </summary>
    [TestMethod]
    public void ARepeatedSameStepRecordReturnsTheSameRecorderInstance()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);
        PrioritizedProposal<string> proposal = Ordinary(5, LeaderLane, "a");

        (QuePaxaRecorder<string> once, RecordSummary<string> firstSummary) = recorder.Record(Four, proposal);
        (QuePaxaRecorder<string> twice, RecordSummary<string> secondSummary) = once.Record(Four, proposal);

        Assert.AreSame(once, twice);
        Assert.AreEqual(firstSummary, secondSummary);
    }


    /// <summary>
    /// The downgrade is applied identically on both deliveries, which is what makes a re-sent reserved claim
    /// inert rather than merely usually inert. The configured leader is fixed for the instance and the
    /// proposal is byte-identical, so the second delivery lands on the same lowest ordinary priority as the
    /// first, folds into an aggregate that already carries it, and leaves the recorder reference-identical. A
    /// downgrade that depended on anything but the owner and the step would show up here as a new instance.
    /// </summary>
    [TestMethod]
    public void ADuplicateReservedClaimFromANonLeaderIsDowngradedIdenticallyAndLeavesTheRecorderUnchanged()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);
        PrioritizedProposal<string> claim = Reserved(OtherReplicaLane, "b");

        (QuePaxaRecorder<string> once, RecordSummary<string> firstSummary) = recorder.Record(Four, claim);
        (QuePaxaRecorder<string> twice, RecordSummary<string> secondSummary) = once.Record(Four, claim);

        Assert.AreEqual(ProposalPriority.Lowest, once.Register.First!.Key.Priority);
        Assert.AreSame(once, twice);
        Assert.AreEqual(firstSummary, secondSummary);
        Assert.AreEqual(ProposalPriority.Lowest, twice.Register.CurrentAggregate!.Key.Priority);
    }


    /// <summary>
    /// The discriminating tie case. An equal key with a different value is the only shape that observes the
    /// fold's tie direction, because a strictly lower-keyed record returns the incumbent whichever way the
    /// tie is broken. The pair below violates the proposal key's uniqueness contract deliberately, in the
    /// idiom of the directed suite's two-firsts-sharing-a-key case, because that is what a host looks like
    /// once it has lost single-flight.
    /// </summary>
    [TestMethod]
    public void ASameStepRecordAtAnEqualKeyWithADifferentValueLeavesTheRecorderUnchanged()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);
        PrioritizedProposal<string> incumbent = Ordinary(42, LeaderLane, "v1");
        PrioritizedProposal<string> challenger = Ordinary(42, LeaderLane, "v2");

        (QuePaxaRecorder<string> once, _) = recorder.Record(Four, incumbent);
        (QuePaxaRecorder<string> twice, _) = once.Record(Four, challenger);

        Assert.AreEqual(incumbent.Key, challenger.Key);
        Assert.AreNotEqual(incumbent, challenger);
        Assert.AreSame(once, twice);
        Assert.AreEqual(incumbent, twice.Register.CurrentAggregate);
    }


    /// <summary>
    /// The other direction, so identity reads as a predicate rather than as a constant: a record that raises
    /// the aggregate produces a new recorder, which is what tells the node above that there is something to
    /// make durable.
    /// </summary>
    [TestMethod]
    public void AHigherKeyedSameStepRecordReturnsADifferentRecorderInstance()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> once, _) = recorder.Record(Four, Ordinary(5, LeaderLane, "a"));
        (QuePaxaRecorder<string> twice, _) = once.Record(Four, Ordinary(9, OtherReplicaLane, "b"));

        Assert.AreNotSame(once, twice);
        Assert.AreEqual(Ordinary(9, OtherReplicaLane, "b"), twice.Register.CurrentAggregate);
        Assert.AreEqual(Ordinary(5, LeaderLane, "a"), twice.Register.First);
    }


    /// <summary>A record below the current step leaves the recorder alone and answers with the current summary.</summary>
    [TestMethod]
    public void AStaleStepReturnsTheSameInstanceAndTheCurrentSummary()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);
        (QuePaxaRecorder<string> atFour, _) = recorder.Record(Four, Ordinary(5, LeaderLane, "a"));
        (QuePaxaRecorder<string> atFive, RecordSummary<string> summaryAtFive) = atFour.Record(Four.Next(), Ordinary(9, LeaderLane, "a"));

        (QuePaxaRecorder<string> afterStale, RecordSummary<string> staleSummary) = atFive.Record(Four, Ordinary(99, OtherReplicaLane, "stale"));

        Assert.AreSame(atFive, afterStale);
        Assert.AreEqual(summaryAtFive, staleSummary);
    }


    /// <summary>Builds a proposal carrying the reserved priority.</summary>
    /// <param name="owner">The lane that owns the proposal.</param>
    /// <param name="value">The proposed value.</param>
    /// <returns>The proposal.</returns>
    private static PrioritizedProposal<string> Reserved(ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.Reserved, owner), value);
    }


    /// <summary>Builds a proposal carrying an ordinary priority.</summary>
    /// <param name="priority">The ordinary priority.</param>
    /// <param name="owner">The lane that owns the proposal.</param>
    /// <param name="value">The proposed value.</param>
    /// <returns>The proposal.</returns>
    private static PrioritizedProposal<string> Ordinary(ulong priority, ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    /// <summary>Builds a replica identity from a single distinguishing byte.</summary>
    /// <param name="id">The distinguishing byte.</param>
    /// <returns>The identity.</returns>
    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
