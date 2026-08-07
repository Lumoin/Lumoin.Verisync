using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class HedgedFastWriterTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);
    private static ReplicaId R3 { get; } = Replica(3);
    private static ReplicaId Absent { get; } = Replica(9);

    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(40);


    [TestMethod]
    public async Task TheLeadingWriterSendsWithoutWaiting()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R1, clock);

        HedgedFastWriteOutcome outcome = await writer.TryWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.AreEqual(TimeSpan.Zero, outcome.Delay);
        Assert.AreEqual(5, outcome.AcceptedCount);
        Assert.IsTrue(outcome.IsCommitted);
    }


    [TestMethod]
    public async Task ALaterWriterSendsNothingUntilItsDelayElapses()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R3, clock);

        Task<HedgedFastWriteOutcome> pending = writer.TryWriteAsync(FastBallot.Fast(1), "y", TestContext.CancellationToken);

        //Nothing has reached an acceptor while the hedging delay runs, which is the whole point: the
        //earlier-scheduled writer has the fast round to itself for that window.
        Assert.IsTrue(cluster.Node(0).Acceptor.AcceptedBallot.IsZero);

        clock.Advance(2 * BaseDelay);
        HedgedFastWriteOutcome outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.AreEqual(2 * BaseDelay, outcome.Delay);
        Assert.IsTrue(outcome.IsCommitted);
    }


    [TestMethod]
    public async Task ObservedProgressStandsTheWriterDown()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R2, clock, (_, _) => ValueTask.FromResult(true));

        Task<HedgedFastWriteOutcome> pending = writer.TryWriteAsync(FastBallot.Fast(1), "y", TestContext.CancellationToken);
        clock.Advance(BaseDelay);
        HedgedFastWriteOutcome outcome = await pending.ConfigureAwait(false);

        //Standing down is not a failed write: nothing was sent, so no acceptor moved and the host owns the
        //decision to reissue the update against the value that did commit.
        Assert.IsFalse(outcome.Activated);
        Assert.AreEqual(0, outcome.AcceptedCount);
        Assert.IsFalse(outcome.IsCommitted);
        Assert.IsTrue(cluster.Node(0).Acceptor.AcceptedBallot.IsZero);
    }


    [TestMethod]
    public async Task AbsentProgressLetsTheWriterActivate()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R2, clock, (_, _) => ValueTask.FromResult(false));

        Task<HedgedFastWriteOutcome> pending = writer.TryWriteAsync(FastBallot.Fast(1), "y", TestContext.CancellationToken);
        clock.Advance(BaseDelay);
        HedgedFastWriteOutcome outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.IsTrue(outcome.IsCommitted);
    }


    [TestMethod]
    public async Task TheLeadingWriterNeverStandsDown()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R1, clock, (_, _) => ValueTask.FromResult(true));

        HedgedFastWriteOutcome outcome = await writer.TryWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken).ConfigureAwait(false);

        //The first writer in the schedule is the one everyone else hedges behind, so it sends immediately
        //without consulting a progress signal that could only describe its own round.
        Assert.IsTrue(outcome.Activated);
        Assert.IsTrue(outcome.IsCommitted);
    }


    [TestMethod]
    public async Task AZeroBaseDelayReproducesTheUnhedgedWrite()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(TimeSpan.Zero), R3, clock, (_, _) => ValueTask.FromResult(true));

        HedgedFastWriteOutcome outcome = await writer.TryWriteAsync(FastBallot.Fast(1), "y", TestContext.CancellationToken).ConfigureAwait(false);

        //With no delay there is no window in which progress could have been observed, so every writer
        //activates exactly as it does without a schedule.
        Assert.IsTrue(outcome.Activated);
        Assert.IsTrue(outcome.IsCommitted);
    }


    [TestMethod]
    public void AWriterOutsideItsScheduleIsRejected()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();

        Assert.ThrowsExactly<ArgumentException>(() => new HedgedFastWriter<string>(cluster.CreateProposer(), Schedule(BaseDelay), Absent, clock));
    }


    [TestMethod]
    public async Task AClassicBallotIsRejected()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R1, clock);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => writer.TryWriteAsync(FastBallot.Classic(1, R1), "x", TestContext.CancellationToken)).ConfigureAwait(false);
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
