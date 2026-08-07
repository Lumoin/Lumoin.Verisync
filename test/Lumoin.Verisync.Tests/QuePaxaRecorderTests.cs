using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The recorder's unit suite, and with it the whole of this slice's safety. ONE rule carries safety on its
/// own: a reserved-priority proposal whose owner is not the recorder's configured leader is recorded at the
/// lowest ordinary priority. It is a DOWNGRADE and not a drop, so the round proceeds through the ordinary
/// phases, the register keeps no holes, and no proposer is left reading a step at which some recorder has
/// no first proposal.
/// </summary>
[TestClass]
internal sealed class QuePaxaRecorderTests
{
    //Identities come from fixed bytes so that the leader binding is exercised against a stable ordering
    //rather than against whatever a generator produced.
    private static ReplicaId ReplicaA { get; } = Replica(1);
    private static ReplicaId ReplicaB { get; } = Replica(2);

    private static ProposerLane LeaderLane { get; } = ProposerLane.For(ReplicaA);
    private static ProposerLane OtherReplicaLane { get; } = ProposerLane.For(ReplicaB);
    private static ProposerLane SecondLaneOfLeaderReplica { get; } = new(ReplicaA, 1);

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    //THE DOWNGRADE CARRIES NO STEP QUALIFIER. It is stated for every step at or above round one phase zero,
    //not for the first step alone, and the difference is invisible everywhere else in this suite because the
    //phase-zero redraw makes a reserved priority above the first step unreachable through the register. Once
    //the step arrives from a network the reachability argument is gone: a proposer that sends the reserved
    //priority at a later step would otherwise have the claim honoured, and an honoured claim dominates every
    //fold and every prior aggregate carried after it.
    [TestMethod]
    public void AReservedClaimFromAnotherReplicaIsRecordedAtLowestAtEveryStepAndNotOnlyAtTheFirst()
    {
        QuePaxaRecorder<string> led = QuePaxaRecorder<string>.LedBy(LeaderLane);
        QuePaxaRecorder<string> leaderless = QuePaxaRecorder<string>.Leaderless;
        RecorderStep five = Four.Next();

        (led, RecordSummary<string> ledAtFive) = led.Record(five, Reserved(OtherReplicaLane, "b"));
        (leaderless, RecordSummary<string> leaderlessAtFive) = leaderless.Record(five, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, ledAtFive.First!.Key.Priority);
        Assert.AreEqual(ProposalPriority.Lowest, leaderlessAtFive.First!.Key.Priority);

        //The leader's own claim is still honoured at a later step, so the rule declines by owner and never by
        //step.
        (_, RecordSummary<string> leadersOwn) = led.Record(five.Next(), Reserved(LeaderLane, "a"));

        Assert.AreEqual(ProposalPriority.Reserved, leadersOwn.First!.Key.Priority);
    }


    [TestMethod]
    public void LeaderlessCarriesNoConfiguredLeaderAndLedByCarriesTheGivenLane()
    {
        Assert.IsNull(QuePaxaRecorder<string>.Leaderless.ConfiguredLeader);
        Assert.AreEqual(LeaderLane, QuePaxaRecorder<string>.LedBy(LeaderLane).ConfiguredLeader);
    }


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


    [TestMethod]
    public void TheReservedClaimFromTheConfiguredLeaderIsRecordedAtReserved()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, RecordSummary<string> summary) = recorder.Record(Four, Reserved(LeaderLane, "a"));

        Assert.AreEqual(Reserved(LeaderLane, "a"), after.Register.First);
        Assert.IsTrue(after.Register.First!.Key.Priority.IsReserved);
        Assert.AreEqual(Reserved(LeaderLane, "a"), summary.First);
    }


    [TestMethod]
    public void AReservedClaimFromAnotherReplicaIsRecordedAtLowest()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, _) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, after.Register.First!.Key.Priority);
        Assert.AreEqual(OtherReplicaLane, after.Register.First.Key.Owner);
        Assert.AreEqual("b", after.Register.First.Value);
    }


    [TestMethod]
    public void AReservedClaimFromAnotherLaneOfTheLeadersOwnReplicaIsRecordedAtLowest()
    {
        //The binding is to a LANE, not to a replica: two lanes of the leader replica each claiming the
        //reserved priority would reproduce the divergence hazard from inside the leader.
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, _) = recorder.Record(Four, Reserved(SecondLaneOfLeaderReplica, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, after.Register.First!.Key.Priority);
        Assert.AreEqual(SecondLaneOfLeaderReplica, after.Register.First.Key.Owner);
        Assert.AreEqual(ReplicaA, after.Register.First.Key.Owner.Replica);
    }


    [TestMethod]
    public void ALeaderlessRecorderDowngradesEveryReservedClaim()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.Leaderless;

        (QuePaxaRecorder<string> fromA, _) = recorder.Record(Four, Reserved(LeaderLane, "a"));
        (QuePaxaRecorder<string> fromB, _) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(ProposalPriority.Lowest, fromA.Register.First!.Key.Priority);
        Assert.AreEqual(ProposalPriority.Lowest, fromB.Register.First!.Key.Priority);
    }


    [TestMethod]
    public void TheDowngradedProposalIsRecordedRatherThanDropped()
    {
        //Dropping would be a liveness hole and would leave a recorder with no first proposal at a step a
        //proposer is about to read; the downgrade keeps the register free of holes.
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> after, RecordSummary<string> summary) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));

        Assert.AreEqual(Four, after.Step);
        Assert.IsNotNull(after.Register.First);
        Assert.IsNotNull(after.Register.CurrentAggregate);
        Assert.IsNotNull(summary.First);
        Assert.AreEqual("b", summary.First.Value);
    }


    [TestMethod]
    public void AnOrdinaryPriorityIsRecordedUntouchedWhoeverOwnsIt()
    {
        //The rule fires on the reserved priority alone; an ordinary proposal from any proposer is never
        //rewritten, because the downgrade would then destroy the randomization liveness depends on.
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> fromOther, _) = recorder.Record(Four, Ordinary(500, OtherReplicaLane, "b"));
        (QuePaxaRecorder<string> fromLeader, _) = recorder.Record(Four, Ordinary(500, LeaderLane, "a"));

        Assert.AreEqual(Ordinary(500, OtherReplicaLane, "b"), fromOther.Register.First);
        Assert.AreEqual(Ordinary(500, LeaderLane, "a"), fromLeader.Register.First);
    }


    [TestMethod]
    public void ADowngradedClaimLosesTheFoldToAnHonouredOne()
    {
        //The downgrade is what stops two reserved-priority proposals from coexisting at all: the leader's
        //claim wins the aggregate outright once the rival's has landed on the lowest ordinary priority.
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        (QuePaxaRecorder<string> afterRival, _) = recorder.Record(Four, Reserved(OtherReplicaLane, "b"));
        (QuePaxaRecorder<string> afterLeader, _) = afterRival.Record(Four, Reserved(LeaderLane, "a"));

        Assert.AreEqual("b", afterLeader.Register.First!.Value);
        Assert.AreEqual(Reserved(LeaderLane, "a"), afterLeader.Register.CurrentAggregate);
    }


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


    [TestMethod]
    public void ANullProposalThrows()
    {
        QuePaxaRecorder<string> recorder = QuePaxaRecorder<string>.LedBy(LeaderLane);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = recorder.Record(Four, null!));
    }


    //IDEMPOTENCE AT THE RECORDER, which is the re-send rule's unit form one layer up. A proposer may deliver
    //a request to a recorder any number of times provided every delivery is IDENTICAL, and what makes that
    //safe is that a second identical record changes nothing: the aggregate already dominates the duplicate's
    //key, the first proposal is not touched by the same-step branch at all, and the recorder therefore
    //returns ITSELF. The node above reads exactly this reference to decide whether anything needs persisting.
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


    //THE DOWNGRADE IS APPLIED IDENTICALLY ON BOTH DELIVERIES, which is what makes a re-sent reserved claim
    //inert rather than merely usually inert. The configured leader is fixed for the instance and the proposal
    //is byte-identical, so the second delivery lands on the same lowest ordinary priority as the first, folds
    //into an aggregate that already carries it, and leaves the recorder reference-identical. A downgrade that
    //depended on anything but the owner would show up here as a new instance.
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


    //THE DISCRIMINATING TIE CASE. An EQUAL KEY WITH A DIFFERENT VALUE is the only shape that observes the
    //fold's tie direction: a strictly lower-keyed record returns the incumbent whichever way the tie is
    //broken. The pair below violates the proposal key's uniqueness contract deliberately, in the idiom of the
    //directed suite's two-firsts-sharing-a-key case, because that is what a host looks like once it has lost
    //single-flight.
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


    //THE OTHER DIRECTION, so identity reads as a predicate rather than as a constant: a record that raises
    //the aggregate produces a new recorder, which is what tells the node above that there is something to
    //make durable.
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


    private static PrioritizedProposal<string> Reserved(ProposerLane owner, string value)
    {
        return new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.Reserved, owner), value);
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
