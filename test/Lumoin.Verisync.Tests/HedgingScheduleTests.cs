using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class HedgingScheduleTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);
    private static ReplicaId R3 { get; } = Replica(3);
    private static ReplicaId Absent { get; } = Replica(9);

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(40);


    [TestMethod]
    public void TheFirstReplicaLeadsWithoutDelay()
    {
        HedgingSchedule schedule = Schedule(BaseDelay);

        Assert.AreEqual(R1, schedule.Leader);
        Assert.AreEqual(TimeSpan.Zero, schedule.DelayFor(R1));
    }


    [TestMethod]
    public void DelayScalesWithPosition()
    {
        HedgingSchedule schedule = Schedule(BaseDelay);

        Assert.AreEqual(0, schedule.PositionOf(R1));
        Assert.AreEqual(BaseDelay, schedule.DelayFor(R2));
        Assert.AreEqual(2 * BaseDelay, schedule.DelayFor(R3));
    }


    [TestMethod]
    public void AZeroBaseDelayActivatesEveryReplicaAtOnce()
    {
        //A hedging delay of zero is a legal setting, unlike a view-change timeout: it reproduces the
        //unhedged behaviour rather than dooming the system to a recovery loop.
        HedgingSchedule schedule = Schedule(TimeSpan.Zero);

        Assert.AreEqual(TimeSpan.Zero, schedule.DelayFor(R1));
        Assert.AreEqual(TimeSpan.Zero, schedule.DelayFor(R3));
    }


    [TestMethod]
    public void RotatingToTheLastWinnerPutsItFirstAndKeepsTheCyclicOrder()
    {
        HedgingSchedule rotated = Schedule(BaseDelay).RotateTo(R2);

        Assert.AreSequenceEqual(new[] { R2, R3, R1 }, rotated.Order);
        Assert.AreEqual(TimeSpan.Zero, rotated.DelayFor(R2));
        Assert.AreEqual(2 * BaseDelay, rotated.DelayFor(R1));
    }


    [TestMethod]
    public void RotatingToTheLeaderReturnsTheSameSchedule()
    {
        HedgingSchedule schedule = Schedule(BaseDelay);

        Assert.AreSame(schedule, schedule.RotateTo(R1));
    }


    [TestMethod]
    public void ChangingTheBaseDelayKeepsTheOrder()
    {
        HedgingSchedule adjusted = Schedule(BaseDelay).WithBaseDelay(TimeSpan.FromMilliseconds(10));

        Assert.AreSequenceEqual(new[] { R1, R2, R3 }, adjusted.Order);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), adjusted.DelayFor(R3));
    }


    [TestMethod]
    public void AReplicaOutsideTheScheduleIsRejected()
    {
        HedgingSchedule schedule = Schedule(BaseDelay);

        Assert.IsFalse(schedule.Contains(Absent));
        Assert.ThrowsExactly<ArgumentException>(() => schedule.DelayFor(Absent));
        Assert.ThrowsExactly<ArgumentException>(() => schedule.PositionOf(Absent));
        Assert.ThrowsExactly<ArgumentException>(() => schedule.RotateTo(Absent));
    }


    [TestMethod]
    public void AnEmptyOrderIsRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => HedgingSchedule.Create([], BaseDelay));
        Assert.ThrowsExactly<ArgumentException>(() => HedgingSchedule.Create(default, BaseDelay));
    }


    [TestMethod]
    public void ADuplicateReplicaIsRejected()
    {
        //A duplicate would give one replica two positions, so its delay would depend on which entry the
        //lookup found first.
        Assert.ThrowsExactly<ArgumentException>(() => HedgingSchedule.Create([R1, R2, R1], BaseDelay));
    }


    [TestMethod]
    public void ANegativeBaseDelayIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HedgingSchedule.Create([R1, R2], TimeSpan.FromMilliseconds(-1)));
    }


    [TestMethod]
    public void ABaseDelayThatWouldOverflowTheLastPositionIsRejected()
    {
        //Three replicas put the last position at twice the base delay, so the boundary is half the tick
        //range: exactly half still fits and one tick more does not.
        HedgingSchedule atTheBoundary = HedgingSchedule.Create([R1, R2, R3], TimeSpan.FromTicks(long.MaxValue / 2));
        Assert.AreEqual(TimeSpan.FromTicks(long.MaxValue - 1), atTheBoundary.DelayFor(R3));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HedgingSchedule.Create([R1, R2, R3], TimeSpan.FromTicks((long.MaxValue / 2) + 1)));
    }


    [TestMethod]
    public void ASingleReplicaScheduleAcceptsAnyBaseDelay()
    {
        //With one replica there is no later position to overflow.
        HedgingSchedule schedule = HedgingSchedule.Create([R1], TimeSpan.FromTicks(long.MaxValue));

        Assert.AreEqual(TimeSpan.Zero, schedule.DelayFor(R1));
    }


    private static HedgingSchedule Schedule(TimeSpan baseDelay)
    {
        ImmutableArray<ReplicaId> order = [R1, R2, R3];

        return HedgingSchedule.Create(order, baseDelay);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
