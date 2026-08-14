using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The leader derivation's suite. This is where the reserved-priority defence gets its input, so the subjects
/// are the three arms the derivation has, the lane the answer is pinned to, and the two questions one rotated
/// schedule answers that must not be confused with each other.
/// </summary>
[TestClass]
internal sealed class QuePaxaLeaderScheduleTests
{
    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);
    private static ReplicaId Stranger { get; } = Replica(9);

    private static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(20);


    public TestContext TestContext { get; set; } = null!;


    [TestMethod]
    public void WithNoPreviousWriterTheLeaderIsTheConfiguredOrdersFirstReplica()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.AreEqual(ProposerLane.For(First), schedule.LeaderFor(null));
        Assert.AreSequenceEqual(new[] { First, Second, Third }, schedule.ScheduleFor(null).Order);
    }


    /// <summary>
    /// A writer at position zero would also pass an implementation that never rotates.
    /// </summary>
    [TestMethod]
    public void ThePreviousWriterLeadsTheNextVersionAndTheOrderRotatesToIt()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.AreEqual(ProposerLane.For(Second), schedule.LeaderFor(Second));

        //Asserting only the head would miss a right leader beside an unrotated order.
        Assert.AreSequenceEqual(new[] { Second, Third, First }, schedule.ScheduleFor(Second).Order);
        Assert.AreSequenceEqual(new[] { Third, First, Second }, schedule.ScheduleFor(Third).Order);
    }


    /// <summary>
    /// The underlying rotation throws for this replica, so a host calling it directly would fault on
    /// reconfiguration.
    /// </summary>
    [TestMethod]
    public void AWriterOutsideTheOrderYieldsALeaderlessInstanceRatherThanThrowing()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.IsNull(schedule.LeaderFor(Stranger));
        Assert.AreSequenceEqual(new[] { First, Second, Third }, schedule.ScheduleFor(Stranger).Order);

        Assert.ThrowsExactly<ArgumentException>(() => _ = schedule.Schedule.RotateTo(Stranger));
    }


    /// <summary>
    /// The reserved priority is granted to a lane, not a replica, so two lanes of one replica cannot both
    /// claim it.
    /// </summary>
    [TestMethod]
    public void TheDerivedLeaderIsAlwaysLaneZeroOfTheLeadingReplica()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.AreEqual(0, schedule.LeaderFor(null)!.Value.Lane);
        Assert.AreEqual(0, schedule.LeaderFor(Second)!.Value.Lane);

        Assert.AreNotEqual(new ProposerLane(Second, 1), schedule.LeaderFor(Second));
    }


    /// <summary>
    /// The derivation is a function of committed state, so every replica reaches the same answer without a
    /// message.
    /// </summary>
    [TestMethod]
    public void TheRecorderAndTheProposerReadTheSameDerivation()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        QuePaxaRecorder<string> led = schedule.RecorderFor<string>(Second);
        QuePaxaRecorder<string> leaderless = schedule.RecorderFor<string>(Stranger);

        Assert.AreEqual(schedule.LeaderFor(Second), led.ConfiguredLeader);
        Assert.IsNull(leaderless.ConfiguredLeader);

        PrioritizedProposal<string> claim = new(new ProposalKey(ProposalPriority.Reserved, schedule.LeaderFor(Second)!.Value), "v");
        (_, RecordSummary<string> summary) = led.Record(RecorderStep.RoundOnePhaseZero, claim);

        Assert.AreEqual(ProposalPriority.Reserved, summary.First!.Key.Priority);
    }


    /// <summary>
    /// A restore that took its leader from anywhere but the derivation would let two hosts serve one instance
    /// under different leaders, so the overload is compared with the derivation it must read on everything a
    /// proposer can observe.
    /// </summary>
    [TestMethod]
    public void TheRestoreOverloadDerivesTheSameLeaderAsADirectFromState()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        ProposerLane derived = schedule.LeaderFor(Second)!.Value;

        //A leader hand-wired from the configured order's head is a different lane here, which is what lets the
        //assertions below tell the derivation from a plausible substitute for it.
        Assert.AreNotEqual(ProposerLane.For(First), derived);

        //The snapshot stands at the round's first step with an ordinary first proposal, which is the shape the
        //restore accepts under every leader and therefore the shape a wrong lane passes through in silence.
        (QuePaxaRecorder<string> live, _) = schedule.RecorderFor<string>(Second).Record(
            RecorderStep.RoundOnePhaseZero,
            new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(10), derived), "a"));

        QuePaxaRecorderState<string> snapshot = live.ToState();

        QuePaxaRecorder<string> viaSchedule = schedule.RecorderFor<string>(Second, snapshot);
        QuePaxaRecorder<string> viaRecorder = QuePaxaRecorder<string>.FromState(schedule.LeaderFor(Second), snapshot);

        Assert.AreEqual(schedule.LeaderFor(Second), viaSchedule.ConfiguredLeader);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, viaSchedule.Step);
        Assert.AreEqual(snapshot, viaSchedule.ToState());

        //The reserved claim is the one record the configured leader decides, so a recorder restored under any
        //other lane downgrades it and answers with a different aggregate.
        PrioritizedProposal<string> claim = new(new ProposalKey(ProposalPriority.Reserved, derived), "v");
        (QuePaxaRecorder<string> afterSchedule, RecordSummary<string> scheduleSummary) = viaSchedule.Record(RecorderStep.RoundOnePhaseZero, claim);
        (QuePaxaRecorder<string> afterRecorder, RecordSummary<string> recorderSummary) = viaRecorder.Record(RecorderStep.RoundOnePhaseZero, claim);

        Assert.AreEqual(recorderSummary, scheduleSummary);
        Assert.AreEqual(afterRecorder.ToState(), afterSchedule.ToState());
        Assert.AreEqual(ProposalPriority.Reserved, afterSchedule.Register.CurrentAggregate!.Key.Priority);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"restored at step {viaSchedule.Step.Value} under lane {derived.Lane} of the previous writer"));
    }


    /// <summary>
    /// The leaderless answer is a derived fact and not a missing one, so the restore carries it through instead
    /// of falling back on the configured order's bootstrap leader.
    /// </summary>
    [TestMethod]
    public void TheRestoreOverloadPreservesALeaderlessDerivation()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.IsNull(schedule.LeaderFor(Stranger));

        (QuePaxaRecorder<string> live, _) = schedule.RecorderFor<string>(Stranger).Record(
            RecorderStep.RoundOnePhaseZero,
            new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(10), ProposerLane.For(Second)), "a"));

        QuePaxaRecorderState<string> snapshot = live.ToState();

        QuePaxaRecorder<string> viaSchedule = schedule.RecorderFor<string>(Stranger, snapshot);
        QuePaxaRecorder<string> viaRecorder = QuePaxaRecorder<string>.FromState(schedule.LeaderFor(Stranger), snapshot);

        Assert.IsNull(viaSchedule.ConfiguredLeader);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, viaSchedule.Step);
        Assert.AreEqual(snapshot, viaSchedule.ToState());

        //A restore falling back on the order's head would honour this claim, and the reserved priority
        //dominates every ordinary one, so an honoured claim would show up as a reserved aggregate.
        PrioritizedProposal<string> bootstrapClaim = new(new ProposalKey(ProposalPriority.Reserved, ProposerLane.For(First)), "v");
        (QuePaxaRecorder<string> afterSchedule, RecordSummary<string> scheduleSummary) = viaSchedule.Record(RecorderStep.RoundOnePhaseZero, bootstrapClaim);
        (QuePaxaRecorder<string> afterRecorder, RecordSummary<string> recorderSummary) = viaRecorder.Record(RecorderStep.RoundOnePhaseZero, bootstrapClaim);

        Assert.AreEqual(recorderSummary, scheduleSummary);
        Assert.AreEqual(afterRecorder.ToState(), afterSchedule.ToState());
        Assert.IsFalse(afterSchedule.Register.CurrentAggregate!.Key.Priority.IsReserved);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"leaderless restore declined a reserved claim and aggregated priority {afterSchedule.Register.CurrentAggregate.Key.Priority.Value}"));
    }


    /// <summary>
    /// The delay is read from the rotated order, not the configured one.
    /// </summary>
    /// <remarks>
    /// Hedging on the configured order would wait behind a replica that no longer leads.
    /// </remarks>
    [TestMethod]
    public void TheRotatedScheduleCarriesBothTheLeaderAndTheHedgingDelays()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        HedgingSchedule rotated = schedule.ScheduleFor(Second);

        Assert.AreEqual(TimeSpan.Zero, rotated.DelayFor(Second));
        Assert.AreEqual(BaseDelay, rotated.DelayFor(Third));
        Assert.AreEqual(BaseDelay + BaseDelay, rotated.DelayFor(First));

        //This is what a derivation reading the unrotated order would produce.
        Assert.AreEqual(TimeSpan.Zero, schedule.Schedule.DelayFor(First));
    }


    [TestMethod]
    public void TheDerivationRefusesANullSchedule()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaLeaderSchedule(null!));
    }


    private static QuePaxaLeaderSchedule Schedule()
    {
        ImmutableArray<ReplicaId> order = [First, Second, Third];

        return new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, BaseDelay));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
