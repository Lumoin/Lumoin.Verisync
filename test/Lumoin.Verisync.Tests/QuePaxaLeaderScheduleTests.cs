using Lumoin.Verisync.Core;
using System.Collections.Immutable;

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


    [TestMethod]
    public void WithNoPreviousWriterTheLeaderIsTheConfiguredOrdersFirstReplica()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.AreEqual(ProposerLane.For(First), schedule.LeaderFor(null));
        Assert.AreSequenceEqual(new[] { First, Second, Third }, schedule.ScheduleFor(null).Order);
    }


    //A writer at position zero would also pass an implementation that never rotates.
    [TestMethod]
    public void ThePreviousWriterLeadsTheNextVersionAndTheOrderRotatesToIt()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.AreEqual(ProposerLane.For(Second), schedule.LeaderFor(Second));

        //Asserting only the head would miss a right leader beside an unrotated order.
        Assert.AreSequenceEqual(new[] { Second, Third, First }, schedule.ScheduleFor(Second).Order);
        Assert.AreSequenceEqual(new[] { Third, First, Second }, schedule.ScheduleFor(Third).Order);
    }


    //The underlying rotation throws for this replica, so a host calling it directly would fault on
    //reconfiguration.
    [TestMethod]
    public void AWriterOutsideTheOrderYieldsALeaderlessInstanceRatherThanThrowing()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.IsNull(schedule.LeaderFor(Stranger));
        Assert.AreSequenceEqual(new[] { First, Second, Third }, schedule.ScheduleFor(Stranger).Order);

        Assert.ThrowsExactly<ArgumentException>(() => _ = schedule.Schedule.RotateTo(Stranger));
    }


    //The reserved priority is granted to a lane, not a replica, so two lanes of one replica cannot both
    //claim it.
    [TestMethod]
    public void TheDerivedLeaderIsAlwaysLaneZeroOfTheLeadingReplica()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        Assert.AreEqual(0, schedule.LeaderFor(null)!.Value.Lane);
        Assert.AreEqual(0, schedule.LeaderFor(Second)!.Value.Lane);

        Assert.AreNotEqual(new ProposerLane(Second, 1), schedule.LeaderFor(Second));
    }


    //The derivation is a function of committed state, so every replica reaches the same answer without a
    //message.
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


    //The delay is read from the rotated order, not the configured one. Hedging on the configured order would
    //wait behind a replica that no longer leads.
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
