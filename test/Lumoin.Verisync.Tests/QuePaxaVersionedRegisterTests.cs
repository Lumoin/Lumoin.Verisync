using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned register's suite. The subjects are the three outcomes a write can establish and how each is
/// retried, the two-part commit test, the hedging the leader schedule drives, and the guards that keep one
/// proposal key to one value.
/// </summary>
[TestClass]
internal sealed class QuePaxaVersionedRegisterTests
{
    private const int AttemptsPerRecorder = 2;

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);
    private static ReplicaId Stranger { get; } = Replica(9);

    private static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(40);

    private static int[] LaneZero { get; } = [0];
    private static int[] LanesZeroAndOne { get; } = [0, 1];
    private static int[] LanesZeroToTwo { get; } = [0, 1, 2];


    //The decision step is asserted because a commit at phase two would satisfy every other assertion here.
    [TestMethod]
    public async Task TheBootstrapLeaderCommitsTheFirstVersionOnTheFastPath()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.AreEqual(RegisterVersion.First, outcome.Version);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(First, outcome.Writer);
        Assert.IsTrue(outcome.TookFastPath);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, outcome.DecidedAt);
        Assert.AreEqual(1, outcome.Attempts);
        Assert.IsTrue(outcome.Activated);
    }


    [TestMethod]
    public async Task TheFastPathSurvivesThreeConsecutiveVersionsAsTheLeaderRotatesToEachWriter()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        for(int version = 1; version <= 3; version++)
        {
            QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync($"v{version}", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
            Assert.AreEqual(new RegisterVersion((ulong)version), outcome.Version);
            Assert.IsTrue(outcome.TookFastPath, $"Version {version} did not take the fast path, so the rotation lost the leader its reserved claim.");

            //Without dissemination the hosts stay on the version just decided and decline the next one.
            cluster.LearnAll(new VersionedValue<string>(outcome.Version, outcome.Writer!.Value, outcome.Value!));
        }

        Assert.AreEqual(new RegisterVersion(3UL), register.Committed!.Version);
    }


    //The change function records its input because a retry re-proposing the original value would still pass.
    [TestMethod]
    public async Task ASupersededWriteAdoptsTheWinnerAndRecomputesFromIt()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> other = Register(cluster, Second);

        //The retry has nowhere to go until the version that beat it has reached the hosts.
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: (committed, _) =>
        {
            cluster.LearnAll(committed);

            return ValueTask.CompletedTask;
        });

        QuePaxaWriteOutcome<string> theirs = await other.TryWriteAsync("theirs", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, theirs.Status);

        List<string?> seen = [];
        QuePaxaWriteOutcome<string> outcome = await register.WriteAsync(
            current =>
            {
                seen.Add(current);

                return $"{current ?? "<none>"}+mine";
            },
            maxAttempts: 2,
            TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreSequenceEqual(new string?[] { null, "theirs" }, seen);
        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.AreEqual("theirs+mine", outcome.Value);
        Assert.AreEqual(First, outcome.Writer);
        Assert.AreEqual(new RegisterVersion(2UL), outcome.Version);
        Assert.AreEqual(2, outcome.Attempts);
    }


    [TestMethod]
    public async Task ASupersededWriteReportsTheWinnersRecordRatherThanFailing()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> other = Register(cluster, Second);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        _ = await other.TryWriteAsync("theirs", TestContext.CancellationToken).ConfigureAwait(false);

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("mine", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Superseded, outcome.Status);
        Assert.IsFalse(outcome.IsCommitted);
        Assert.AreEqual("theirs", outcome.Value);
        Assert.AreEqual(Second, outcome.Writer);

        //A single attempt and no retry is what separates the two entry points.
        Assert.AreEqual(1, outcome.Attempts);
        Assert.AreEqual(new RegisterVersion(2UL), register.NextVersion);
    }


    //An undecided proposal may be decided later, so treating it as a conflict would abandon a live write. Two
    //of three hosts are partitioned so the quorum is missed while the third still records what it was asked,
    //which is what lets the lanes be read rather than inferred from the attempt count.
    [TestMethod]
    public async Task AnUndecidedWriteRetriesAtTheSameVersionOnAFreshLane()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        cluster.Partition(1);
        cluster.Partition(2);

        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        List<string?> seen = [];
        QuePaxaWriteOutcome<string> outcome = await register.WriteAsync(
            current =>
            {
                seen.Add(current);

                return "a";
            },
            maxAttempts: 3,
            TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);
        Assert.AreEqual(3, outcome.Attempts);
        Assert.AreEqual(RegisterVersion.First, outcome.Version);

        Assert.AreSequenceEqual(new string?[] { null, null, null }, seen);
        Assert.IsNull(register.Committed);

        Assert.AreSequenceEqual(
            LanesZeroToTwo,
            LanesAt(cluster, RegisterVersion.First, First),
            "The three attempts did not run on three distinct lanes, so two proposals shared a key at one version.");
    }


    //The lane is a property of the register and the version rather than of the call, so a caller cannot put a
    //second value under the first attempt's key by calling the single-attempt entry point again. For the
    //version's leader the key is the reserved priority on lane zero and so is identical whatever the value,
    //which makes reuse a silent divergence rather than a detectable one.
    [TestMethod]
    public async Task ASecondSingleAttemptWriteAtAnUndecidedVersionRunsOnAFreshLane()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        cluster.Partition(1);
        cluster.Partition(2);

        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        QuePaxaWriteOutcome<string> first = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        QuePaxaWriteOutcome<string> second = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, first.Status);
        Assert.AreEqual(QuePaxaWriteStatus.Undecided, second.Status);
        Assert.AreEqual(RegisterVersion.First, first.Version);
        Assert.AreEqual(RegisterVersion.First, second.Version);

        Assert.AreSequenceEqual(
            LanesZeroAndOne,
            LanesAt(cluster, RegisterVersion.First, First),
            "The second call reused the first attempt's lane, so one proposal key named two values.");
    }


    //A superseded attempt runs at a version this register has never proposed at, so its lane counter starts
    //again rather than continuing. Without this the lane would climb across versions and the reserved
    //priority, which is granted to lane zero alone, would be unreachable after the first retry.
    [TestMethod]
    public async Task TheLaneCounterStartsAgainAtAVersionThisRegisterHasNotProposedAt()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> other = Register(cluster, Second);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: (committed, _) =>
        {
            cluster.LearnAll(committed);

            return ValueTask.CompletedTask;
        });

        _ = await other.TryWriteAsync("theirs", TestContext.CancellationToken).ConfigureAwait(false);

        QuePaxaWriteOutcome<string> outcome = await register.WriteAsync(
            current => $"{current ?? "<none>"}+mine",
            maxAttempts: 2,
            TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.AreEqual(new RegisterVersion(2UL), outcome.Version);

        Assert.AreSequenceEqual(LaneZero, LanesAt(cluster, RegisterVersion.First, First), "The superseded attempt did not run on lane zero.");
        Assert.AreSequenceEqual(LaneZero, LanesAt(cluster, new RegisterVersion(2UL), First), "The attempt at the next version continued the previous version's lane counter.");
    }


    [TestMethod]
    public async Task ALaterWriterWaitsItsHedgingDelayAndTheLeaderDoesNot()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        FakeTimeProvider clock = new();
        QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock);

        Assert.AreEqual(2 * BaseDelay, later.Delay);

        Task<QuePaxaWriteOutcome<string>> pending = later.TryWriteAsync("y", TestContext.CancellationToken);

        Assert.AreEqual(0, cluster.Served[0]);

        clock.Advance(2 * BaseDelay);
        QuePaxaWriteOutcome<string> outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);

        QuePaxaVersionedRegister<string> leader = Register(cluster, First, clock);

        Assert.AreEqual(TimeSpan.Zero, leader.Delay);
    }


    [TestMethod]
    public async Task ADelayedWriterStandsDownWhenTheVersionIsAlreadyCommitted()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        FakeTimeProvider clock = new();
        QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock, _ => new ValueTask<RegisterVersion>(RegisterVersion.First));

        Task<QuePaxaWriteOutcome<string>> pending = later.TryWriteAsync("y", TestContext.CancellationToken);
        clock.Advance(2 * BaseDelay);
        QuePaxaWriteOutcome<string> outcome = await pending.ConfigureAwait(false);

        Assert.IsFalse(outcome.Activated);
        Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);
        Assert.AreEqual(0, cluster.Served[0], "A writer that stood down still sent a request.");
    }


    //This is the only vector that fails if the stand-down is hoisted out of the delay guard.
    [TestMethod]
    public async Task AZeroBaseDelayActivatesEveryWriterEvenWhenTheVersionIsReportedCommitted()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(TimeSpan.Zero), 3);
        FakeTimeProvider clock = new();
        QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock, _ => new ValueTask<RegisterVersion>(RegisterVersion.First), TimeSpan.Zero);

        QuePaxaWriteOutcome<string> outcome = await later.TryWriteAsync("y", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
    }


    //This pins the liveness cost that the single-live-instance rule pays for its safety.
    [TestMethod]
    public async Task AWriteReachesAQuorumOnlyWhereThePreviousVersionHasBeenDisseminated()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        QuePaxaWriteOutcome<string> first = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, first.Status);

        VersionedValue<string> committed = new(first.Version, first.Writer!.Value, first.Value!);

        //One host learns, which leaves the quorum one short: two of three must serve.
        _ = cluster.LearnAt(0, committed);

        QuePaxaWriteOutcome<string> starved = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, starved.Status);
        Assert.IsNotEmpty(cluster.Declined);

        _ = cluster.LearnAt(1, committed);

        QuePaxaWriteOutcome<string> served = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, served.Status);
        Assert.AreEqual(new RegisterVersion(2UL), served.Version);
    }


    //Counting a reply from another instance would decide on a set that is a minority of the instance it names.
    [TestMethod]
    public async Task AVersionSwappingTransportYieldsNoDecisionRatherThanAWrongOne()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        VersionedRecorderEndpointDelegate<VersionedValue<string>>[] honest = cluster.Endpoints();
        var swapped = new VersionedRecorderEndpointDelegate<VersionedValue<string>>[honest.Length];
        for(int index = 0; index < honest.Length; index++)
        {
            VersionedRecorderEndpointDelegate<VersionedValue<string>> inner = honest[index];
            swapped[index] = async (request, token) =>
            {
                VersionedRecordReply<VersionedValue<string>> reply = await inner(request, token).ConfigureAwait(false);

                return reply with { Version = reply.Version.Next() };
            };
        }

        QuePaxaVersionedRegister<string> register = new(
            swapped,
            First,
            Schedule(),
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            TimeProvider.System);

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);
    }


    [TestMethod]
    public async Task AConcurrentWriteIsRefusedRatherThanSharingALane()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        FakeTimeProvider clock = new();
        QuePaxaVersionedRegister<string> register = Register(cluster, Third, clock);

        Task<QuePaxaWriteOutcome<string>> pending = register.TryWriteAsync("a", TestContext.CancellationToken);

        //One proposal key naming two values is what the key's uniqueness contract forbids.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        clock.Advance(2 * BaseDelay);
        _ = await pending.ConfigureAwait(false);
    }


    [TestMethod]
    public void TheConstructorRefusesAReplicaOutsideTheScheduleAndAnEmptyRecorderSet()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        Assert.ThrowsExactly<ArgumentException>(() => _ = new QuePaxaVersionedRegister<string>(
            cluster.Endpoints(),
            Stranger,
            Schedule(),
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            TimeProvider.System));

        Assert.ThrowsExactly<ArgumentException>(() => _ = new QuePaxaVersionedRegister<string>(
            [],
            First,
            Schedule(),
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            TimeProvider.System));
    }


    [TestMethod]
    public async Task AnAttemptBudgetBelowOneIsRefused()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await register.WriteAsync(static _ => "a", 0, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await register.WriteAsync(static _ => "a", -1, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await register.WriteAsync(null!, 1, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    //One honest host settles it because a committed record is a decided fact under the crash-fault model.
    [TestMethod]
    public async Task AReadAdoptsTheHighestRecordAnyHostReports()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        VersionedValue<string> committed = new(new RegisterVersion(7UL), Second, "learned");
        _ = cluster.LearnAt(2, committed);

        ReadCommittedRecordDelegate<string>[] readers = new ReadCommittedRecordDelegate<string>[3];
        for(int index = 0; index < 3; index++)
        {
            int host = index;
            readers[index] = _ => new ValueTask<VersionedValue<string>?>(cluster.Host(host).Committed);
        }

        QuePaxaVersionedRegister<string> register = new(
            cluster.Endpoints(),
            First,
            Schedule(),
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            TimeProvider.System,
            readCommittedRecords: readers);

        Assert.IsNull(register.Committed);

        VersionedValue<string>? read = await register.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(committed, read);
        Assert.AreEqual(new RegisterVersion(8UL), register.NextVersion);
    }


    private static QuePaxaVersionedRegister<string> Register(
        VersionedQuePaxaCluster<string> cluster,
        ReplicaId self,
        TimeProvider? clock = null,
        ObserveCommittedVersionDelegate? observe = null,
        TimeSpan? baseDelay = null,
        PublishCommittedRecordDelegate<string>? publish = null)
    {
        return new QuePaxaVersionedRegister<string>(
            cluster.Endpoints(),
            self,
            Schedule(baseDelay ?? BaseDelay),
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            clock ?? TimeProvider.System,
            observe,
            publishCommittedRecord: publish);
    }


    //The distinct lanes one replica proposed on at one version, in the order they first arrived. A proposal
    //key repeats across the steps of one attempt, so the distinct lanes are what count the attempts.
    private static int[] LanesAt(VersionedQuePaxaCluster<string> cluster, RegisterVersion version, ReplicaId replica)
    {
        return [.. cluster.Recorded
            .Where(entry => entry.Version == version && entry.Key.Owner.Replica == replica)
            .Select(entry => entry.Key.Owner.Lane)
            .Distinct()];
    }


    private static QuePaxaLeaderSchedule Schedule(TimeSpan? baseDelay = null)
    {
        ImmutableArray<ReplicaId> order = [First, Second, Third];

        return new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, baseDelay ?? BaseDelay));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
