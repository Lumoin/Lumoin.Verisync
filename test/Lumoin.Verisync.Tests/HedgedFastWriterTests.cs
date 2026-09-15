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


    /// <summary>
    /// The constructor's null-guard on the schedule rejects a null <see cref="HedgingSchedule"/> with
    /// <see cref="ArgumentNullException"/> naming the "schedule" parameter, before ever consulting it.
    /// </summary>
    [TestMethod]
    public void ANullScheduleIsRejected()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();

        ArgumentNullException refused = Assert.ThrowsExactly<ArgumentNullException>(() => new HedgedFastWriter<string>(cluster.CreateProposer(), null!, R1, clock));

        Assert.AreEqual("schedule", refused.ParamName);
    }


    /// <summary>
    /// A classic ballot is rejected immediately, before the hedging delay runs — a writer scheduled to wait
    /// must still throw <see cref="ArgumentException"/> rather than silently standing down once its delay
    /// elapses and progress is observed.
    /// </summary>
    [TestMethod]
    public async Task AClassicBallotIsRejectedBeforeTheHedgingDelayRuns()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R2, clock, (_, _) => ValueTask.FromResult(true));

        Task<HedgedFastWriteOutcome> pending = writer.TryWriteAsync(FastBallot.Classic(1, R1), "y", TestContext.CancellationToken);
        clock.Advance(BaseDelay);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => pending).ConfigureAwait(false);
    }


    /// <summary>
    /// The constructor's null-guard on the injected clock rejects a null <see cref="TimeProvider"/> with
    /// <see cref="ArgumentNullException"/> naming the "timeProvider" parameter.
    /// </summary>
    [TestMethod]
    public void ANullTimeProviderIsRejected()
    {
        SimulatedCluster<string> cluster = new(5);

        ArgumentNullException refused = Assert.ThrowsExactly<ArgumentNullException>(() => new HedgedFastWriter<string>(cluster.CreateProposer(), Schedule(BaseDelay), R1, null!));

        Assert.AreEqual("timeProvider", refused.ParamName);
    }


    /// <summary>
    /// The constructor's null-guard on the proposer rejects a null <see cref="FastProposer{TValue}"/> with
    /// <see cref="ArgumentNullException"/> naming the "proposer" parameter.
    /// </summary>
    [TestMethod]
    public void ANullProposerIsRejected()
    {
        FakeTimeProvider clock = new();

        ArgumentNullException refused = Assert.ThrowsExactly<ArgumentNullException>(() => new HedgedFastWriter<string>(null!, Schedule(BaseDelay), R1, clock));

        Assert.AreEqual("proposer", refused.ParamName);
    }


    /// <summary>
    /// The hedging delay does not resume on the synchronization context the write was started on. The
    /// continuation after the delay elapses runs off that context, so a host that blocks the starting thread on
    /// a hedged write cannot deadlock it while the delay is outstanding.
    /// </summary>
    /// <remarks>
    /// A later-scheduled writer with no progress signal parks on its delay as the first await; advancing the
    /// clock releases it, and the fast write that follows completes at once over the in-process cluster, so the
    /// delay is the only await that captures the context here.
    /// </remarks>
    [TestMethod]
    public async Task TheHedgingDelayDoesNotResumeOnTheCallersContext()
    {
        SimulatedCluster<string> cluster = new(5);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(cluster.CreateProposer(), Schedule(BaseDelay), R3, clock);
        PostCountingSynchronizationContext context = new();

        Task<HedgedFastWriteOutcome> pending = context.Start(() => writer.TryWriteAsync(FastBallot.Fast(1), "y", TestContext.CancellationToken));

        //The writer is parked on its hedging delay; nothing has run past it yet.
        Assert.IsFalse(pending.IsCompleted);

        clock.Advance(2 * BaseDelay);
        HedgedFastWriteOutcome outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.IsTrue(outcome.IsCommitted);
        Assert.AreEqual(0, context.Posts);
    }


    /// <summary>
    /// The fast write does not resume on the synchronization context the write was started on. For the leading
    /// writer, which sends without waiting, the fast write is the first await; its continuation, after the
    /// acceptor round completes later, runs off that context.
    /// </summary>
    /// <remarks>
    /// The single endpoint holds its reply open, so the leading writer parks on the acceptor round; releasing it
    /// completes the write off the context.
    /// </remarks>
    [TestMethod]
    public async Task TheFastWriteDoesNotResumeOnTheCallersContext()
    {
        TaskCompletionSource<ConsensusReply<string>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsensusEndpointDelegate<string>[] endpoints = [(_, _) => new ValueTask<ConsensusReply<string>>(gate.Task)];
        FastProposer<string> proposer = new(endpoints);
        FakeTimeProvider clock = new();
        HedgedFastWriter<string> writer = new(proposer, Schedule(BaseDelay), R1, clock);
        PostCountingSynchronizationContext context = new();

        Task<HedgedFastWriteOutcome> pending = context.Start(() => writer.TryWriteAsync(FastBallot.Fast(1), "x", TestContext.CancellationToken));

        //The leading writer has a zero delay and sends at once, so with no reply yet it parks on the acceptor round.
        Assert.IsFalse(pending.IsCompleted);

        gate.SetResult(new AcceptReply<string>(true, FastBallot.Fast(1)));
        HedgedFastWriteOutcome outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.IsTrue(outcome.IsCommitted);
        Assert.AreEqual(0, context.Posts);
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
