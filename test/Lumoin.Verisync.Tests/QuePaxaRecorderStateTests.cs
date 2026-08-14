using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Covers the recorder's durable seam: <see cref="QuePaxaRecorder{TValue}.ToState"/> snapshots the interval
/// summary register's four fields, <see cref="QuePaxaRecorder{TValue}.FromState"/> rebuilds a recorder a
/// proposer cannot distinguish from the one that crashed, and every relational rule that refuses a state no
/// recorder-driven register can hold.
/// </summary>
/// <remarks>
/// Every rejection below builds its state by hand rather than through a recorder, because a rule exists
/// precisely for the states no honest history reaches: a rule exercised only through recorder-produced states
/// would pass while asserting nothing. Each such state is otherwise well formed, so exactly one rule can be
/// what refuses it.
/// </remarks>
[TestClass]
internal sealed class QuePaxaRecorderStateTests
{
    /// <summary>
    /// Identities come from fixed bytes so that the owner half of every key tie-break is a property of the
    /// test rather than of the run.
    /// </summary>
    private static ReplicaId ReplicaA { get; } = Replica(1);

    /// <summary>The second identity, whose reserved claims a recorder led by the first declines.</summary>
    private static ReplicaId ReplicaB { get; } = Replica(2);

    /// <summary>The lane the restored recorders under test are configured with.</summary>
    private static ProposerLane LeaderLane { get; } = ProposerLane.For(ReplicaA);

    /// <summary>A lane on the other replica, which that configured leader does not cover.</summary>
    private static ProposerLane OtherReplicaLane { get; } = ProposerLane.For(ReplicaB);

    /// <summary>A second lane on the leader's own replica, which the binding declines because it binds a lane.</summary>
    private static ProposerLane SecondLaneOfLeaderReplica { get; } = new(ReplicaA, 1);

    /// <summary>The owners the reserved-priority rules are swept over, which is every shape of owner there is.</summary>
    private static ImmutableArray<ProposerLane> AllLanes { get; } = [LeaderLane, OtherReplicaLane, SecondLaneOfLeaderReplica];

    /// <summary>The round's first step, which three of the rules qualify on and only there.</summary>
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// The round trip, whose load-bearing half is the last two assertions rather than the field comparison.
    /// Matching fields prove the snapshot is complete; a restored recorder that answers a further record
    /// exactly as the original does is what makes the restart invisible to a proposer, which is the only
    /// reason the restore exists.
    /// </summary>
    [TestMethod]
    public void ToStateAndFromStateRoundTripARecorderThatAnswersIdentically()
    {
        //Two records at the round's first step part the aggregate from the first proposal, an advance by
        //exactly one carries that aggregate down as the prior aggregate, and a second record at the new step
        //parts them again, so all four durable fields are non-null and distinct.
        RecorderStep five = Four.Next();
        (QuePaxaRecorder<string> firstAtFour, _) = QuePaxaRecorder<string>.LedBy(LeaderLane).Record(Four, Ordinary(10, LeaderLane, "a"));
        (QuePaxaRecorder<string> foldedAtFour, _) = firstAtFour.Record(Four, Ordinary(20, OtherReplicaLane, "b"));
        (QuePaxaRecorder<string> firstAtFive, _) = foldedAtFour.Record(five, Ordinary(30, LeaderLane, "c"));
        (QuePaxaRecorder<string> original, _) = firstAtFive.Record(five, Ordinary(40, OtherReplicaLane, "d"));

        QuePaxaRecorderState<string> snapshot = original.ToState();

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"step={snapshot.Step.Value}, first={snapshot.First!.Key.Priority.Value}, aggregate={snapshot.CurrentAggregate!.Key.Priority.Value}, prior={snapshot.PriorAggregate!.Key.Priority.Value}"));

        Assert.AreEqual(five, snapshot.Step);
        Assert.AreEqual(Ordinary(30, LeaderLane, "c"), snapshot.First);
        Assert.AreEqual(Ordinary(40, OtherReplicaLane, "d"), snapshot.CurrentAggregate);
        Assert.AreEqual(Ordinary(20, OtherReplicaLane, "b"), snapshot.PriorAggregate);

        QuePaxaRecorder<string> restored = QuePaxaRecorder<string>.FromState(LeaderLane, snapshot);

        //The configured leader comes from the argument and never from the snapshot, because it is
        //configuration a deployment derives from committed state rather than protocol state a register
        //accumulates.
        Assert.AreEqual(LeaderLane, restored.ConfiguredLeader);
        Assert.AreEqual(five, restored.Step);
        Assert.AreEqual(snapshot, restored.ToState());

        //An identical re-delivery of the record that formed the aggregate is inert at both recorders, and
        //inert means the same instance back, which is the fact the node above reads to decide whether a reply
        //needs anything made durable first.
        (QuePaxaRecorder<string> originalInert, RecordSummary<string> originalInertSummary) = original.Record(five, Ordinary(40, OtherReplicaLane, "d"));
        (QuePaxaRecorder<string> restoredInert, RecordSummary<string> restoredInertSummary) = restored.Record(five, Ordinary(40, OtherReplicaLane, "d"));

        Assert.AreSame(original, originalInert);
        Assert.AreSame(restored, restoredInert);
        Assert.AreEqual(originalInertSummary, restoredInertSummary);

        //A record that advances the step answers with one summary and leaves one state, so a proposer reading
        //the reply cannot tell which of the two recorders served it.
        (QuePaxaRecorder<string> originalAfter, RecordSummary<string> originalSummary) = original.Record(five.Next(), Ordinary(50, LeaderLane, "e"));
        (QuePaxaRecorder<string> restoredAfter, RecordSummary<string> restoredSummary) = restored.Record(five.Next(), Ordinary(50, LeaderLane, "e"));

        Assert.AreEqual(originalSummary, restoredSummary);
        Assert.AreEqual(originalAfter.ToState(), restoredAfter.ToState());
    }


    /// <summary>
    /// The one-round-trip fast path across a restart, which is the register's whole quantitative claim. The
    /// snapshot here is the exact durable state the fast path leaves behind, and it is the shape three of the
    /// rules qualify on, so a rule widened past its step would make every fast-path snapshot refuse to restart.
    /// The decision at the end is what a restore has to preserve: a quorum containing a restored recorder's
    /// answer still decides at the step the round began at, so the restart costs no second round trip.
    /// </summary>
    [TestMethod]
    public void AFastPathSnapshotRestoresAndStillDecidesInOneRoundTrip()
    {
        PrioritizedProposal<string> reserved = Reserved(LeaderLane, "fast");
        (QuePaxaRecorder<string> recorded, _) = QuePaxaRecorder<string>.LedBy(LeaderLane).Record(Four, reserved);

        QuePaxaRecorderState<string> snapshot = recorded.ToState();

        //The state is the fast path's own and nothing arranged for the restore: the leader's honoured reserved
        //claim standing in both slots at the round's first step, with no carry.
        Assert.AreEqual(Four, snapshot.Step);
        Assert.AreEqual(reserved, snapshot.First);
        Assert.AreEqual(reserved, snapshot.CurrentAggregate);
        Assert.IsNull(snapshot.PriorAggregate);

        QuePaxaRecorder<string> restored = QuePaxaRecorder<string>.FromState(LeaderLane, snapshot);

        Assert.AreEqual(LeaderLane, restored.ConfiguredLeader);
        Assert.AreEqual(Four, restored.Step);
        Assert.AreEqual(snapshot, restored.ToState());

        //The proposer's retransmission after the restart re-delivers the claim the snapshot was taken after, and
        //the same instance back is the no-durability-write contract surviving the restart.
        (QuePaxaRecorder<string> inert, RecordSummary<string> restoredAnswer) = restored.Record(Four, reserved);

        Assert.AreSame(restored, inert);

        //A recorder that never restarted serves the rest of the quorum, so the restored recorder's answer is
        //load-bearing in the decision rather than carried by recorders that all stayed up.
        (_, RecordSummary<string> liveAnswer) = QuePaxaRecorder<string>.LedBy(LeaderLane).Record(Four, reserved);

        Assert.AreEqual(liveAnswer, restoredAnswer);

        QuePaxaRound<string> round = QuePaxaRound<string>.Begin(LeaderLane, LeaderLane, "fast");
        ImmutableArray<RecorderAnswer<string>> quorum =
        [
            new RecorderAnswer<string>(0, restoredAnswer),
            new RecorderAnswer<string>(1, liveAnswer)
        ];

        QuePaxaStepOutcome<string> outcome = round.Conclude(quorum, 3);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"decided at step {outcome.DecidedAt.Value} on {outcome.SummaryCount} answers of 3 recorders, one of them restored"));

        Assert.AreEqual(QuePaxaStepKind.Decided, outcome.Kind);
        Assert.AreEqual("fast", outcome.DecidedValue);
        Assert.AreEqual(LeaderLane, outcome.DecidedBy);
        Assert.AreEqual(round.Step, outcome.DecidedAt, "A decision above the step the round began at is more than one round trip.");
        Assert.AreEqual(Four, outcome.DecidedAt);
        Assert.IsNull(outcome.Next);
    }


    /// <summary>
    /// A register that ever left step zero carries a first proposal, because the advancing branch assigns one
    /// and the same-step branch preserves it. A restored step above zero with none is the corrupt snapshot the
    /// register's own remarks leave to this restore.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAStepAboveZeroWithNoFirstProposal()
    {
        QuePaxaRecorderState<string> state = new(Four.Next(), null, Ordinary(20, OtherReplicaLane, "b"), null);

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, state));
    }


    /// <summary>
    /// The recorder's step floor is round one phase zero, so no step below it is a state a recorder can hold.
    /// An unwritten recorder comes from <see cref="QuePaxaRecorder{TValue}.Leaderless"/> or
    /// <see cref="QuePaxaRecorder{TValue}.LedBy"/> rather than from a restore, and a snapshot returning at
    /// step zero is the crash the restore exists to prevent.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAStepBelowTheRoundsFirstStep()
    {
        //The unwritten register is internally consistent in every other respect and is refused for the step
        //alone, which is what makes the rule stronger than "a state the register's Record cannot produce".
        //It is also exactly what ToState returns for an unwritten recorder, so the pair is deliberately not
        //an inverse at the bottom of the range.
        QuePaxaRecorderState<string> unwritten = new(RecorderStep.Zero, null, null, null);

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, unwritten));
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, QuePaxaRecorder<string>.Leaderless.ToState()));

        //A register recorded at step zero directly folds an aggregate while its first proposal stays null.
        //Algorithm 3 permits that state and the recorder's floor puts it out of reach, so the step refuses it
        //before the first-proposal rule ever has to reason about it.
        QuePaxaRecorderState<string> foldedAtZero = new(RecorderStep.Zero, null, Ordinary(20, LeaderLane, "a"), null);

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, foldedAtZero));

        //The steps between zero and the floor are the ones a rule stated as "not zero" would admit. Each is
        //well formed for its own step and unreachable only because the recorder refuses to record there, and
        //the last of them would otherwise carry a prior aggregate that the round's first step must not hold.
        for(int step = 1; step < RecorderStep.RoundOnePhaseZero.Value; step++)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"below the floor at step {step}"));

            PrioritizedProposal<string> proposal = Ordinary(30, LeaderLane, "b");
            QuePaxaRecorderState<string> belowFloor = new(new RecorderStep(step), proposal, proposal, Ordinary(10, OtherReplicaLane, "c"));

            Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, belowFloor));
        }
    }


    /// <summary>
    /// The recorder downgrades a reserved claim from a lane other than its configured leader before the
    /// register sees it, so a restored first proposal at the round's first step can never hold one.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAForeignReservedFirstProposalAtTheRoundsFirstStep()
    {
        //This state and the aggregate half's differ only in which lane is the configured leader, and the pair
        //has to be built that way round. Putting the same foreign claim in both slots would leave the
        //aggregate half of the rule able to refuse the state, and the first half could then be deleted with
        //every test still passing. Here the aggregate is the leader's own honoured claim, so only the
        //first-proposal half can fire, and it dominates the first proposal so the ordering rule stays quiet.
        QuePaxaRecorderState<string> state = new(Four, Reserved(LeaderLane, "a"), Reserved(OtherReplicaLane, "b"), null);

        Assert.IsGreaterThan(state.First!.Key, state.CurrentAggregate!.Key, "The aggregate must dominate the first proposal, or the ordering rule could be what refuses this state.");
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(OtherReplicaLane, state));
    }


    /// <summary>
    /// The downgrade runs upstream of the fold, so both slots at the round's first step are drawn from the
    /// downgraded stream and the aggregate is covered as well as the first proposal.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAForeignReservedCurrentAggregateAtTheRoundsFirstStep()
    {
        //The leader's own claim stands in the first slot, so the state passes the first-proposal half of the
        //rule and only the aggregate half can refuse it. The aggregate also orders above the first proposal,
        //because the two share the reserved priority and the owner tie-break puts the other replica higher.
        QuePaxaRecorderState<string> state = new(Four, Reserved(LeaderLane, "a"), Reserved(OtherReplicaLane, "b"), null);

        Assert.IsGreaterThan(state.First!.Key, state.CurrentAggregate!.Key, "The aggregate must dominate the first proposal, or the ordering rule could be what refuses this state.");
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, state));
    }


    /// <summary>
    /// The advancing branch sets both slots from one proposal and the fold only ever replaces the aggregate,
    /// so a first proposal without an aggregate beside it is unreachable.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAFirstProposalWithNoCurrentAggregate()
    {
        //The priority is ordinary, so neither reserved-priority rule can be what refuses this state even
        //though it stands at the round's first step.
        QuePaxaRecorderState<string> state = new(Four, Ordinary(10, LeaderLane, "a"), null, null);

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, state));
    }


    /// <summary>
    /// The first proposal was recorded at the register's own step and the fold keeps the greatest key seen
    /// there, so the aggregate dominates it and an aggregate ordering below it is a reordered or forged
    /// snapshot.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsACurrentAggregateOrderingBelowTheFirstProposal()
    {
        QuePaxaRecorderState<string> state = new(Four, Ordinary(20, LeaderLane, "high"), Ordinary(10, OtherReplicaLane, "low"), null);

        Assert.IsLessThan(state.First!.Key, state.CurrentAggregate!.Key, "The aggregate must order below the first proposal, or the rule under test cannot fire.");
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, state));
    }


    /// <summary>
    /// Round one phase zero is step four and the step below it is step zero, so a recorder reaching the
    /// round's first step advanced there non-adjacently and the advancing branch cleared the carry. The rule
    /// holds absolutely, and it holds only under the recorder's step floor.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAPriorAggregateAtTheRoundsFirstStep()
    {
        //The two slots at the step carry one ordinary proposal, so no reserved-priority rule applies, the
        //aggregate does not order below the first proposal, and the prior aggregate is the only thing left to
        //refuse.
        QuePaxaRecorderState<string> state = new(Four, Ordinary(10, LeaderLane, "a"), Ordinary(10, LeaderLane, "a"), Ordinary(5, OtherReplicaLane, "carried"));

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, state));
    }


    /// <summary>
    /// The downgraded stream reaches one step past the round's first step through the carry, and one step only. A
    /// non-null prior aggregate there is that step's current aggregate brought down by an advance of exactly one,
    /// every other advance clearing the carry, so it is as free of foreign reserved claims as the aggregate it
    /// came from. Two steps up the carry comes from a step that records a reserved priority verbatim from any
    /// owner, which is where the rule stops.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAForeignReservedPriorAggregateOneStepAboveTheRoundsFirstStep()
    {
        RecorderStep five = Four.Next();

        Assert.IsTrue(five.IsNextAfter(Four), "The rule is stated on the step exactly one above the round's first step.");

        //One ordinary proposal stands in both slots at the step, so neither reserved-priority rule, the
        //aggregate-ordering rule nor the round's-first-step carry rule can be what refuses these states.
        PrioritizedProposal<string> ordinary = Ordinary(30, LeaderLane, "a");

        foreach(ProposerLane owner in AllLanes)
        {
            if(owner == LeaderLane)
            {
                continue;
            }

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"foreign reserved carry at step {five.Value} owned by {owner}"));

            QuePaxaRecorderState<string> foreignCarry = new(five, ordinary, ordinary, Reserved(owner, "carried"));

            Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, foreignCarry));
        }

        //The leader's own claim is honoured at the round's first step and becomes the carry on an advance by one,
        //so a recorder produces this state and the rule must let it through. Building it through the recorder is
        //what makes that a fact rather than an assertion about the rule's wording.
        (QuePaxaRecorder<string> atFour, _) = QuePaxaRecorder<string>.LedBy(LeaderLane).Record(Four, Reserved(LeaderLane, "honoured"));
        (QuePaxaRecorder<string> atFive, _) = atFour.Record(five, ordinary);
        QuePaxaRecorderState<string> produced = atFive.ToState();

        Assert.AreEqual(Reserved(LeaderLane, "honoured"), produced.PriorAggregate);
        Assert.AreEqual(five, QuePaxaRecorder<string>.FromState(LeaderLane, produced).Step);

        //The confinement, which is what pins the rule against being widened a step at a time: at the step above
        //the carry comes from a phase-zero step whose reserved claims are recorded verbatim, so the same foreign
        //claim restores there under the same configured leader.
        RecorderStep six = five.Next();
        QuePaxaRecorderState<string> higherCarry = new(six, ordinary, ordinary, Reserved(OtherReplicaLane, "carried"));

        QuePaxaRecorder<string> restored = QuePaxaRecorder<string>.FromState(LeaderLane, higherCarry);

        Assert.AreEqual(six, restored.Step);
        Assert.AreEqual(Reserved(OtherReplicaLane, "carried"), restored.Register.PriorAggregate);
    }


    /// <summary>
    /// The leaderless arm of the reserved-priority rule, which needs no rule of its own: the comparison
    /// against the configured leader is lifted over the null exactly as the recorder's downgrade lifts it, so
    /// a leaderless instance declines every owner at the round's first step.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAReservedFirstProposalFromEveryOwnerWhenTheInstanceIsLeaderless()
    {
        foreach(ProposerLane owner in AllLanes)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"leaderless restore of a reserved claim owned by {owner}"));

            QuePaxaRecorderState<string> state = new(Four, Reserved(owner, "v"), Reserved(owner, "v"), null);

            Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<string>.FromState(null, state));
        }
    }


    /// <summary>
    /// The narrowed downgrade rule restated on the restore. A reserved priority above the round's first step
    /// is recorded verbatim from any owner, so a restore must accept it from any owner and under any configured
    /// leader; a rule stated at every step would refuse states the library produces. This test is the pair to
    /// the leaderless rejection above, and together they fix the rule's step qualifier against a later
    /// widening.
    /// </summary>
    [TestMethod]
    public void FromStateAcceptsAReservedProposalAboveTheRoundsFirstStepFromEveryOwner()
    {
        RecorderStep five = Four.Next();
        ProposerLane?[] configurations = [LeaderLane, null];

        foreach(ProposerLane? configuredLeader in configurations)
        {
            foreach(ProposerLane owner in AllLanes)
            {
                QuePaxaRecorderState<string> state = new(five, Reserved(owner, "v"), Reserved(owner, "v"), null);

                QuePaxaRecorder<string> restored = QuePaxaRecorder<string>.FromState(configuredLeader, state);

                Assert.AreEqual(five, restored.Step);
                Assert.AreEqual(configuredLeader, restored.ConfiguredLeader);
                Assert.AreEqual(owner, restored.Register.First!.Key.Owner);
                Assert.IsTrue(restored.Register.First.Key.Priority.IsReserved);
                Assert.IsTrue(restored.Register.CurrentAggregate!.Key.Priority.IsReserved);
            }
        }

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"accepted a reserved claim at step {five.Value} from {AllLanes.Length} owners under {configurations.Length} leader configurations"));
    }


    /// <summary>A null state is refused, because a restore has nothing to rebuild from.</summary>
    [TestMethod]
    public void FromStateRejectsANullState()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => QuePaxaRecorder<string>.FromState(LeaderLane, null!));
    }


    /// <summary>
    /// A step outside the threshold clock's range is refused by <see cref="RecorderStep"/> itself, so no state
    /// carrying one can be assembled for the restore to refuse. Single values belong to their own types and
    /// the relational rules belong to the restore.
    /// </summary>
    [TestMethod]
    public void ARecorderStateCannotExpressAStepOutsideTheClocksRange()
    {
        ArgumentOutOfRangeException belowZero = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new QuePaxaRecorderState<string>(new RecorderStep(-1), null, null, null));
        ArgumentOutOfRangeException aboveMaximum = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new QuePaxaRecorderState<string>(new RecorderStep(RecorderStep.MaxValue.Value + 1), null, null, null));

        Assert.AreEqual("Value", belowZero.ParamName);
        Assert.AreEqual("Value", aboveMaximum.ParamName);
    }


    /// <summary>
    /// A negative lane is refused by <see cref="ProposerLane"/> itself, so no restored proposal's owner can
    /// carry one and the restore owes it no rule.
    /// </summary>
    [TestMethod]
    public void ARestoredProposalOwnerCannotExpressANegativeLane()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new ProposerLane(ReplicaA, -1));

        Assert.AreEqual("Lane", thrown.ParamName);
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
