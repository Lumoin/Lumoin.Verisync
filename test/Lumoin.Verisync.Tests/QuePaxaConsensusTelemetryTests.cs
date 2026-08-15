using Lumoin.Verisync.Core;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// What the consensus surface reports, read the way an application reads it.
/// </summary>
/// <remarks>
/// <para>
/// The subjects are that a write reports the status it established and the attempts it spent, that a readiness
/// report tells silence apart from a fault where the report itself deliberately does not, that the membership
/// is reported with the quorum it implies, and that both spans carry what they claim.
/// </para>
/// <para>
/// The meter and the activity source are process-wide, so the class does not run beside others and every
/// measurement is filtered on the chain dimension besides. The chain is the digest of a genesis member list,
/// so a member list no other class builds is a dimension value no other class emits.
/// </para>
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class QuePaxaConsensusTelemetryTests
{
    private const int AttemptsPerRecorder = 2;

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(51);
    private static ReplicaId Second { get; } = Replica(52);
    private static ReplicaId Third { get; } = Replica(53);
    private static ReplicaId Fourth { get; } = Replica(54);
    private static ReplicaId Outsider { get; } = Replica(59);

    private static ImmutableArray<ReplicaId> Order { get; } = [First, Second, Third];

    private static TimeSpan ProbeDeadline { get; } = TimeSpan.FromSeconds(5);


    [TestMethod]
    public async Task AWriteReportsTheStatusItEstablishedAndTheAttemptsItSpent()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using MetricCollector<long> writes = new(VerisyncMetrics.ConsensusWrites);
        using MetricCollector<int> attempts = new(VerisyncMetrics.ConsensusWriteAttempts);

        QuePaxaVersionedRegister<string> register = Register(cluster, First);
        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.IsTrue(outcome.TookFastPath);

        CollectedMeasurement<long> write = Only(writes, cluster);

        Assert.AreEqual(1L, write.Value);
        Assert.AreEqual(nameof(QuePaxaWriteStatus.Committed), write.Tags[VerisyncTelemetry.TagWriteStatus]);
        Assert.IsTrue((bool)write.Tags[VerisyncTelemetry.TagFastPath]!, "A one-round-trip commit was not reported as one, so the fast-path rate is measured over the wrong writes.");

        CollectedMeasurement<int> spent = Only(attempts, cluster);

        Assert.AreEqual(outcome.Attempts, spent.Value);
        Assert.AreEqual(nameof(QuePaxaWriteStatus.Committed), spent.Tags[VerisyncTelemetry.TagWriteStatus]);
    }


    /// <summary>
    /// A write refused for membership is counted under its own status, so the distribution separates the
    /// outcomes an operator acts on differently.
    /// </summary>
    /// <remarks>
    /// A counter that named every write the same would satisfy a row asserting only that something was
    /// counted, which is why a second status is asserted rather than one.
    /// </remarks>
    [TestMethod]
    public async Task AWriteRefusedForMembershipIsCountedUnderItsOwnStatus()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using MetricCollector<long> writes = new(VerisyncMetrics.ConsensusWrites);

        QuePaxaVersionedRegister<string> outsider = Register(cluster, Outsider);
        QuePaxaWriteOutcome<string> outcome = await outsider.WriteAsync(static _ => "a", maxAttempts: 3, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.OutsideConfiguration, outcome.Status);

        CollectedMeasurement<long> write = Only(writes, cluster);

        Assert.AreEqual(nameof(QuePaxaWriteStatus.OutsideConfiguration), write.Tags[VerisyncTelemetry.TagWriteStatus]);
        Assert.IsFalse((bool)write.Tags[VerisyncTelemetry.TagFastPath]!, "A write that never sent anything was reported as a fast-path decision.");
    }


    /// <summary>
    /// The membership a register runs under is reported with the quorum it implies, at construction and
    /// wherever a record moves it.
    /// </summary>
    /// <remarks>
    /// Both readings are asserted. A gauge recorded only at construction would still report four members after
    /// a change removed one, which is the reading an operator would act on.
    /// </remarks>
    [TestMethod]
    public async Task TheMembershipAndItsQuorumAreReportedWhereTheyMove()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);

        using MetricCollector<int> size = new(VerisyncMetrics.ConsensusMembershipSize);
        using MetricCollector<int> quorum = new(VerisyncMetrics.ConsensusMembershipQuorum);

        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: (committed, _, _) =>
        {
            cluster.LearnAll(committed);

            return ValueTask.CompletedTask;
        });

        Assert.AreEqual(4, Mine(size, cluster)[0].Value, "The membership a register starts under was never reported, so a cluster that never reconfigures reports nothing at all.");
        Assert.AreEqual(3, Mine(quorum, cluster)[0].Value);

        _ = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        _ = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(3, Mine(size, cluster)[^1].Value, "The membership installed by a change was never reported, so the reading an operator acts on is the one before it.");
        Assert.AreEqual(2, Mine(quorum, cluster)[^1].Value);
    }


    /// <summary>
    /// A member that answered nothing and one that faulted are told apart here, though the readiness report
    /// deliberately collapses them.
    /// </summary>
    /// <remarks>
    /// The report gives a gate one answer for both, because a gate cannot act differently on them. Someone
    /// diagnosing an outage can, and this is the only place that distinction survives. All three outcomes are
    /// asserted, since a probe count that reported every member the same would satisfy any weaker assertion.
    /// </remarks>
    [TestMethod]
    public async Task SilenceAndAFaultAreToldApartInTheProbeCountThoughTheReportCollapsesThem()
    {
        FakeTimeProvider clock = new();
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using MetricCollector<long> probes = new(VerisyncMetrics.ConsensusProbes);

        TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<MemberVersionReport> silent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        QuePaxaVersionedRegister<string> register = Register(cluster, First, clock: clock, observeMember: (member, token) =>
        {
            if(member.Equals(Second))
            {
                throw new IOException("This member's transport is down.");
            }

            if(!member.Equals(Third))
            {
                return new ValueTask<MemberVersionReport>(new MemberVersionReport(member, RegisterVersion.First));
            }

            _ = asked.TrySetResult();

            return new ValueTask<MemberVersionReport>(silent.Task);
        });

        Task<RegisterReadiness> reading = register.ReadReadinessAsync(ProbeDeadline, TestContext.CancellationToken);

        await asked.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        clock.Advance(ProbeDeadline);

        RegisterReadiness readiness = await reading.ConfigureAwait(false);

        //The report says the same thing about both, which is the contract the probe count is measured beside.
        Assert.IsFalse(readiness.Members[1].Reachable);
        Assert.IsFalse(readiness.Members[2].Reachable);

        Assert.AreEqual(VerisyncTelemetry.ProbeAnswered, Outcome(probes, cluster, First));
        Assert.AreEqual(VerisyncTelemetry.ProbeFaulted, Outcome(probes, cluster, Second));
        Assert.AreEqual(VerisyncTelemetry.ProbeTimedOut, Outcome(probes, cluster, Third));

        _ = silent.TrySetResult(new MemberVersionReport(Third, RegisterVersion.First));
    }


    /// <summary>
    /// A write raises a span carrying what it established, for a subscriber that subscribed by source name.
    /// </summary>
    /// <remarks>
    /// The listener subscribes exactly as an application's tracing configuration does, by comparing the source
    /// name, so a span emitted on some other source would not be seen here either.
    /// </remarks>
    [TestMethod]
    public async Task AWriteRaisesASpanCarryingWhatItEstablished()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using Spans spans = Spans.Listening();

        QuePaxaVersionedRegister<string> register = Register(cluster, First);
        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Activity span = spans.Single(VerisyncTelemetry.ActivityNameConsensusWrite);

        Assert.AreEqual(nameof(QuePaxaWriteStatus.Committed), span.GetTagItem(VerisyncTelemetry.TagWriteStatus));
        Assert.IsTrue((bool)span.GetTagItem(VerisyncTelemetry.TagFastPath)!);
        Assert.AreEqual(outcome.Attempts, span.GetTagItem(VerisyncTelemetry.ActivityWriteAttempts));
        Assert.IsNotNull(span.GetTagItem(VerisyncTelemetry.TagCluster));
    }


    /// <summary>
    /// A write that throws leaves its span marked as an error rather than closing it as though it succeeded.
    /// </summary>
    [TestMethod]
    public async Task AWriteThatThrowsMarksItsSpanAnError()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using Spans spans = Spans.Listening();

        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        //A reconfiguration before anything is committed has no value to carry forward and refuses.
        _ = await Assert.ThrowsExactlyAsync<ConsensusRefusedException>(
            async () => await register.ReconfigureAsync(current => current.Without(Third), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Activity span = spans.Single(VerisyncTelemetry.ActivityNameConsensusWrite);

        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
    }


    /// <summary>
    /// A readiness report raises a span carrying how many members were measured and how many answered.
    /// </summary>
    [TestMethod]
    public async Task AReadinessReportRaisesASpanCarryingWhatItMeasured()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        using Spans spans = Spans.Listening();

        QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: (member, token) => member.Equals(Third)
            ? throw new IOException("This member's transport is down.")
            : new ValueTask<MemberVersionReport>(new MemberVersionReport(member, RegisterVersion.First)));

        _ = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

        Activity span = spans.Single(VerisyncTelemetry.ActivityNameConsensusReadiness);

        Assert.AreEqual(3, span.GetTagItem(VerisyncTelemetry.ActivityMeasuredMembers));
        Assert.AreEqual(2, span.GetTagItem(VerisyncTelemetry.ActivityReachableMembers));
    }


    /// <summary>The measurements this chain reported, in the order they were reported.</summary>
    /// <typeparam name="T">The measurement type.</typeparam>
    /// <param name="collector">The collector to read.</param>
    /// <param name="cluster">The cluster whose chain identifies this test's own measurements.</param>
    /// <returns>Those measurements.</returns>
    private static List<CollectedMeasurement<T>> Mine<T>(MetricCollector<T> collector, VersionedQuePaxaCluster<string> cluster) where T : struct
    {
        string chain = Convert.ToHexStringLower(cluster.Genesis.Cluster.AsSpan());

        return [.. collector.GetMeasurementSnapshot().Where(measurement => Equals(measurement.Tags.GetValueOrDefault(VerisyncTelemetry.TagCluster), chain))];
    }


    /// <summary>The one measurement this chain reported.</summary>
    /// <typeparam name="T">The measurement type.</typeparam>
    /// <param name="collector">The collector to read.</param>
    /// <param name="cluster">The cluster whose chain identifies this test's own measurements.</param>
    /// <returns>That measurement.</returns>
    private static CollectedMeasurement<T> Only<T>(MetricCollector<T> collector, VersionedQuePaxaCluster<string> cluster) where T : struct => Mine(collector, cluster).Single();


    /// <summary>How one member's probe was reported.</summary>
    /// <param name="collector">The probe collector.</param>
    /// <param name="cluster">The cluster whose chain identifies this test's own measurements.</param>
    /// <param name="member">The member asked.</param>
    /// <returns>The outcome dimension that member's probe carried.</returns>
    private static object? Outcome(MetricCollector<long> collector, VersionedQuePaxaCluster<string> cluster, ReplicaId member)
    {
        string hex = Convert.ToHexStringLower(member.AsSpan());

        return Mine(collector, cluster)
            .Single(measurement => Equals(measurement.Tags.GetValueOrDefault(VerisyncTelemetry.TagMember), hex))
            .Tags[VerisyncTelemetry.TagProbeOutcome];
    }


    private static QuePaxaLeaderSchedule Schedule() => new(HedgingSchedule.Create(Order, TimeSpan.Zero));


    private static QuePaxaLeaderSchedule WiderSchedule() => new(HedgingSchedule.Create([First, Second, Third, Fourth], TimeSpan.Zero));


    private static QuePaxaVersionedRegister<string> Register(
        VersionedQuePaxaCluster<string> cluster,
        ReplicaId self,
        TimeProvider? clock = null,
        PublishCommittedRecordDelegate<string>? publish = null,
        ObserveMemberVersionDelegate? observeMember = null)
    {
        return new QuePaxaVersionedRegister<string>(
            cluster.Genesis,
            self,
            TimeSpan.Zero,
            cluster.Resolve,
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            clock ?? TimeProvider.System,
            publishCommittedRecord: publish,
            observeMemberVersion: observeMember);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// Collects the library's completed spans, subscribing the way an application's tracing configuration
    /// does.
    /// </summary>
    private sealed class Spans: IDisposable
    {
        private Lock Gate { get; } = new();

        private List<Activity> Stopped { get; } = [];

        private ActivityListener Listener { get; }


        private Spans()
        {
            Listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == VerisyncActivitySource.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Take
            };
        }


        /// <summary>Starts collecting.</summary>
        /// <returns>The collector, which stops when it is disposed.</returns>
        public static Spans Listening()
        {
            Spans spans = new();
            ActivitySource.AddActivityListener(spans.Listener);

            return spans;
        }


        /// <summary>The one span raised under <paramref name="name"/>.</summary>
        /// <param name="name">The activity name.</param>
        /// <returns>That span, which has already stopped and so carries every tag set on it.</returns>
        public Activity Single(string name)
        {
            lock(Gate)
            {
                return Stopped.Single(activity => activity.OperationName == name);
            }
        }


        /// <inheritdoc/>
        public void Dispose() => Listener.Dispose();


        private void Take(Activity activity)
        {
            lock(Gate)
            {
                Stopped.Add(activity);
            }
        }
    }
}
