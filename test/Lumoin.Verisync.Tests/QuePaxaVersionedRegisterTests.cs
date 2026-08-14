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
    private static ReplicaId Fourth { get; } = Replica(4);
    private static ReplicaId Stranger { get; } = Replica(9);

    private static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The membership a register over this suite's agreed order stamps on its records, so a record built here
    /// compares equal to one the register wrote.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis([First, Second, Third]);

    private static int[] LaneZero { get; } = [0];
    private static int[] LanesZeroAndOne { get; } = [0, 1];
    private static int[] LanesZeroToTwo { get; } = [0, 1, 2];


    /// <summary>
    /// The decision step is asserted because a commit at phase two would satisfy every other assertion here.
    /// </summary>
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
            cluster.LearnAll(new VersionedValue<string>(outcome.Version, outcome.Writer!.Value, Configuration, outcome.Value!));
        }

        Assert.AreEqual(new RegisterVersion(3UL), register.Committed!.Version);
    }


    /// <summary>
    /// The change function records its input because a retry re-proposing the original value would still pass.
    /// </summary>
    [TestMethod]
    public async Task ASupersededWriteAdoptsTheWinnerAndRecomputesFromIt()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> other = Register(cluster, Second);

        //The retry has nowhere to go until the version that beat it has reached the hosts.
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: (committed, _, _) =>
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


    /// <summary>
    /// An undecided proposal may be decided later, so treating it as a conflict would abandon a live write.
    /// </summary>
    /// <remarks>
    /// Two of three hosts are partitioned so the quorum is missed while the third still records what it was
    /// asked, which is what lets the lanes be read rather than inferred from the attempt count.
    /// </remarks>
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


    /// <summary>
    /// The lane is a property of the register and the version rather than of the call, so a caller cannot put
    /// a second value under the first attempt's key by calling the single-attempt entry point again.
    /// </summary>
    /// <remarks>
    /// For the version's leader the key is the reserved priority on lane zero and so is identical whatever the
    /// value, which makes reuse a silent divergence rather than a detectable one.
    /// </remarks>
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


    /// <summary>
    /// A superseded attempt runs at a version this register has never proposed at, so its lane counter starts
    /// again rather than continuing.
    /// </summary>
    /// <remarks>
    /// Without this the lane would climb across versions and the reserved priority, which is granted to lane
    /// zero alone, would be unreachable after the first retry.
    /// </remarks>
    [TestMethod]
    public async Task TheLaneCounterStartsAgainAtAVersionThisRegisterHasNotProposedAt()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> other = Register(cluster, Second);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: (committed, _, _) =>
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

        Assert.AreEqual(2 * BaseDelay, later.Delay!.Value);

        Task<QuePaxaWriteOutcome<string>> pending = later.TryWriteAsync("y", TestContext.CancellationToken);

        Assert.AreEqual(0, cluster.Served[0]);

        clock.Advance(2 * BaseDelay);
        QuePaxaWriteOutcome<string> outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated);
        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);

        QuePaxaVersionedRegister<string> leader = Register(cluster, First, clock);

        Assert.AreEqual(TimeSpan.Zero, leader.Delay!.Value);
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


    /// <summary>
    /// This is the only vector that fails if the stand-down is hoisted out of the delay guard.
    /// </summary>
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


    /// <summary>
    /// This pins the liveness cost that the single-live-instance rule pays for its safety.
    /// </summary>
    [TestMethod]
    public async Task AWriteReachesAQuorumOnlyWhereThePreviousVersionHasBeenDisseminated()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        QuePaxaWriteOutcome<string> first = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, first.Status);

        VersionedValue<string> committed = new(first.Version, first.Writer!.Value, Configuration, first.Value!);

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


    /// <summary>
    /// Counting a reply from another instance would decide on a set that is a minority of the instance it
    /// names.
    /// </summary>
    [TestMethod]
    public async Task AVersionSwappingTransportYieldsNoDecisionRatherThanAWrongOne()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, resolve: member =>
        {
            VersionedRecorderEndpointDelegate<VersionedValue<string>> inner = cluster.Resolve(member);

            return async (request, token) =>
            {
                VersionedRecordReply<VersionedValue<string>> reply = await inner(request, token).ConfigureAwait(false);

                return reply with { Version = reply.Version.Next() };
            };
        });

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);
    }


    /// <summary>
    /// Two slots answered by one host would count one replica twice, and a quorum counted over distinct
    /// members is what majority intersection rests on.
    /// </summary>
    /// <remarks>
    /// Two of the three slots are pointed at the first host, so an unchecked register sees three answers where
    /// one replica answered and commits on a minority of one.
    /// </remarks>
    [TestMethod]
    public async Task AReplyFromAMemberOtherThanTheOneAddressedIsRefusedRatherThanCounted()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, resolve: _ => cluster.Resolve(First));

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);
        Assert.IsNull(register.Committed);

        //The one honest slot did answer, so the write failed on the identity of the other two rather than on
        //a transport that carried nothing.
        Assert.IsGreaterThan(0, cluster.Served[0]);
        Assert.AreEqual(0, cluster.Served[1]);
        Assert.AreEqual(0, cluster.Served[2]);
    }


    /// <summary>
    /// A round that decided a record carrying another version faults the write rather than being adopted.
    /// </summary>
    /// <remarks>
    /// The envelope guards refuse a transport that mis-answers the wrapper, and this is the arm they cannot
    /// see: every envelope is honest while the record inside each gathered proposal carries another version,
    /// so the round concludes a decision the instance was never addressed with. The write faults before
    /// adoption, because a misrouted decision let into the committed state would set the next instance's
    /// leader.
    /// </remarks>
    [TestMethod]
    public async Task ADecisionCarryingAnotherVersionFaultsTheWriteRatherThanBeingAdopted()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, resolve: member =>
        {
            VersionedRecorderEndpointDelegate<VersionedValue<string>> inner = cluster.Resolve(member);

            return async (request, token) =>
            {
                VersionedRecordReply<VersionedValue<string>> reply = await inner(request, token).ConfigureAwait(false);

                return reply with { Reply = Reversion(reply.Reply) };
            };
        });

        InvalidOperationException reported = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.Contains("decided a record carrying version", reported.Message);
        Assert.IsNull(register.Committed, "A record the write refused was adopted anyway.");

        static PrioritizedProposal<VersionedValue<string>> Rewrap(PrioritizedProposal<VersionedValue<string>> proposal)
        {
            return proposal with { Value = proposal.Value with { Version = proposal.Value.Version.Next() } };
        }

        static RecordReply<VersionedValue<string>> Reversion(RecordReply<VersionedValue<string>> reply)
        {
            return reply with
            {
                First = Rewrap(reply.First),
                PriorAggregate = reply.PriorAggregate is { } prior ? Rewrap(prior) : null,
            };
        }
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
    public void TheConstructorRefusesAMissingGenesisResolverPriorityOrClock()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedRegister<string>(
            null!, First, BaseDelay, cluster.Resolve, ProposalPriority.Cryptographic, AttemptsPerRecorder, TimeProvider.System));

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedRegister<string>(
            cluster.Genesis, First, BaseDelay, null!, ProposalPriority.Cryptographic, AttemptsPerRecorder, TimeProvider.System));

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedRegister<string>(
            cluster.Genesis, First, BaseDelay, cluster.Resolve, null!, AttemptsPerRecorder, TimeProvider.System));

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedRegister<string>(
            cluster.Genesis, First, BaseDelay, cluster.Resolve, ProposalPriority.Cryptographic, AttemptsPerRecorder, null!));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new QuePaxaVersionedRegister<string>(
            cluster.Genesis, First, BaseDelay, cluster.Resolve, ProposalPriority.Cryptographic, 0, TimeProvider.System));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new QuePaxaVersionedRegister<string>(
            cluster.Genesis, First, -BaseDelay, cluster.Resolve, ProposalPriority.Cryptographic, AttemptsPerRecorder, TimeProvider.System));
    }


    /// <summary>
    /// A register for a replica the membership does not list is how a joiner starts and what a removed replica
    /// becomes, so construction cannot refuse it and the write reports it per version instead.
    /// </summary>
    /// <remarks>
    /// The write spends no attempt and sends nothing at all, which is what separates a settled refusal from an
    /// unlucky round.
    /// </remarks>
    [TestMethod]
    public async Task AWriteFromOutsideTheMembershipIsReportedWithoutSpendingAnAttempt()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> outsider = Register(cluster, Stranger);

        Assert.IsNull(outsider.Delay, "A replica outside the membership has no position in the hedging order and so no delay.");

        QuePaxaWriteOutcome<string> outcome = await outsider.WriteAsync(static _ => "a", maxAttempts: 3, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.OutsideConfiguration, outcome.Status);
        Assert.AreEqual(RegisterVersion.First, outcome.Version);
        Assert.AreEqual(0, outcome.Attempts, "A refusal that cannot change with a retry burned the retry budget.");
        Assert.IsFalse(outcome.Activated);
        Assert.IsNull(outcome.Writer);
        Assert.IsEmpty(cluster.Recorded, "An outsider's write reached a recorder.");
        Assert.IsNull(outsider.Committed);
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


    /// <summary>
    /// One honest host settles it because a committed record is a decided fact under the crash-fault model.
    /// </summary>
    [TestMethod]
    public async Task AReadAdoptsTheHighestRecordAnyHostReports()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        VersionedValue<string> committed = new(new RegisterVersion(7UL), Second, Configuration, "learned");
        _ = cluster.LearnAt(2, committed);

        QuePaxaVersionedRegister<string> register = new(
            cluster.Genesis,
            First,
            BaseDelay,
            cluster.Resolve,
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            TimeProvider.System,
            resolveCommittedRecordReader: cluster.ResolveReader);

        Assert.IsNull(register.Committed);

        VersionedValue<string>? read = await register.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(committed, read);
        Assert.AreEqual(new RegisterVersion(8UL), register.NextVersion);
    }


    /// <summary>
    /// The record the register learns while it is parked moves both the version and the membership, so an
    /// attempt that resolved anything after its delay would send the version it computed before the delay to
    /// the recorder set of a membership it learned after it.
    /// </summary>
    [TestMethod]
    public async Task AnAttemptAddressesTheInstanceItCapturedEvenWhenTheRecordMovesDuringTheDelay()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        FakeTimeProvider clock = new();
        QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock);

        Task<QuePaxaWriteOutcome<string>> pending = later.TryWriteAsync("y", TestContext.CancellationToken);

        Assert.AreEqual(0, cluster.Served[0], "The parked writer sent before its delay expired.");

        //A record that both closes the captured version and removes this replica from the membership: an
        //attempt reading the field again would address version two under a two-member set it is not in.
        VersionedValue<string> moved = new(RegisterVersion.First, Second, Configuration.Without(Third), "elsewhere");

        Assert.IsTrue(later.Learn(moved));
        Assert.AreEqual(new RegisterVersion(2UL), later.NextVersion);
        Assert.IsFalse(later.ActiveConfiguration.Contains(Third));

        clock.Advance(2 * BaseDelay);
        QuePaxaWriteOutcome<string> outcome = await pending.ConfigureAwait(false);

        Assert.IsTrue(outcome.Activated, "The attempt stood down instead of addressing its captured instance.");
        Assert.AreEqual(RegisterVersion.First, outcome.Version);
        Assert.AreNotEqual(QuePaxaWriteStatus.OutsideConfiguration, outcome.Status);
        Assert.IsNotEmpty(cluster.Recorded);
        Assert.IsTrue(
            cluster.Recorded.All(entry => entry.Version == RegisterVersion.First),
            "A request left for a version other than the captured one, so the attempt re-resolved after its delay.");
        Assert.IsGreaterThan(0, cluster.Served[2], "The third host was not addressed, so the attempt used the membership it learned during the delay.");
    }


    /// <summary>
    /// The quorum is the endpoint array's own length, so a member left out because nothing resolves it does
    /// not make the cluster smaller: it makes the majority smaller.
    /// </summary>
    /// <remarks>
    /// The second arm is the one that separates a full-length array from a filtered one, because a filtered
    /// array of one decides on one host.
    /// </remarks>
    [TestMethod]
    public async Task AnUnresolvableMemberKeepsItsSlotSoTheQuorumStaysTheMembershipsOwn()
    {
        VersionedQuePaxaCluster<string> tolerant = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> withOneUnresolved = Register(tolerant, First, resolve: member => member.Equals(Third)
            ? throw new InvalidOperationException("No route to the third member.")
            : tolerant.Resolve(member));

        QuePaxaWriteOutcome<string> tolerated = await withOneUnresolved.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, tolerated.Status, "Two of three members resolve, which is the membership's own quorum.");
        Assert.AreEqual(0, tolerant.Served[2]);

        VersionedQuePaxaCluster<string> starved = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> withTwoUnresolved = Register(starved, First, resolve: member => member.Equals(First)
            ? starved.Resolve(member)
            : throw new InvalidOperationException($"No route to member {member}."));

        QuePaxaWriteOutcome<string> missed = await withTwoUnresolved.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, missed.Status, "One resolvable member of three decided, so the quorum was counted over the resolved members rather than over the membership.");
        Assert.IsNull(withTwoUnresolved.Committed);
    }


    /// <summary>
    /// A prohibition is absent code, so it is pinned by what the records actually carry.
    /// </summary>
    /// <remarks>
    /// Both arms matter: the membership a chain starts with is carried forward, and so is one a change
    /// installed.
    /// </remarks>
    [TestMethod]
    public async Task AnOrdinaryWriteCarriesTheMembershipForwardUnchanged()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);
        RecordingPublisher publisher = new(cluster);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: publisher.PublishAsync);

        _ = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        _ = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(cluster.Genesis, register.Committed!.NextConfiguration, "An ordinary write changed the membership it inherited.");

        QuePaxaWriteOutcome<string> shrunk = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, shrunk.Status);

        QuePaxaConfiguration installed = register.ActiveConfiguration;

        Assert.IsFalse(installed.Contains(Fourth));

        _ = await register.TryWriteAsync("c", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(installed, register.Committed!.NextConfiguration, "An ordinary write after a change did not carry the installed membership forward.");
        Assert.AreEqual("c", register.Committed.Value);
    }


    /// <summary>
    /// A change that keeps the writer keeps that writer leading, so growing or shrinking the membership around
    /// it must cost the next instance nothing.
    /// </summary>
    /// <remarks>
    /// Only the decision step separates the two paths.
    /// </remarks>
    [TestMethod]
    public async Task AConfigurationChangeThatKeepsTheWriterKeepsTheOneRoundTripPath()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);
        RecordingPublisher publisher = new(cluster);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: publisher.PublishAsync);

        QuePaxaWriteOutcome<string> first = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(first.TookFastPath);

        QuePaxaWriteOutcome<string> shrunk = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, shrunk.Status);
        Assert.IsTrue(shrunk.TookFastPath, "The reconfiguring write itself lost the leader's reserved claim.");
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, shrunk.DecidedAt);

        QuePaxaWriteOutcome<string> after = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, after.Status);
        Assert.IsTrue(after.TookFastPath, "The write after a membership change that kept the writer took the ordinary phases.");
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, after.DecidedAt);

        //Self-removal is the case where the two memberships answer differently. The instance that removes the
        //writer still runs under the membership that has it, so that write keeps the reserved claim and only
        //the instance after it is leaderless; a leader read off the membership being installed would give it
        //up a version early and take the ordinary phases for nothing.
        QuePaxaWriteOutcome<string> leaving = await register.ReconfigureAsync(current => current.Without(First), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, leaving.Status);
        Assert.IsTrue(leaving.TookFastPath, "The write that removed its own writer gave up the reserved claim a version early.");
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, leaving.DecidedAt);

        QuePaxaWriteOutcome<string> departed = await register.TryWriteAsync("c", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.OutsideConfiguration, departed.Status);
    }


    [TestMethod]
    public async Task AReconfigurationInstallsTheMembershipAndCarriesTheValueForward()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);
        RecordingPublisher publisher = new(cluster);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: publisher.PublishAsync);

        _ = await register.TryWriteAsync("carried", TestContext.CancellationToken).ConfigureAwait(false);

        QuePaxaWriteOutcome<string> outcome = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.AreEqual(new RegisterVersion(2UL), outcome.Version);
        Assert.AreEqual("carried", outcome.Value, "A reconfiguration wrote a value of its own instead of carrying the committed one.");
        Assert.AreEqual(First, outcome.Writer);
        Assert.AreEqual(1, outcome.Attempts);

        Assert.AreSequenceEqual(new[] { First, Second, Third }, register.ActiveConfiguration.Members);
        Assert.AreEqual(cluster.Genesis.Cluster, register.ActiveConfiguration.Cluster, "A membership change minted a new chain.");
        Assert.AreSequenceEqual(new[] { First, Second, Third }, cluster.Host(0).ActiveConfiguration.Members);
    }


    [TestMethod]
    public async Task AReconfigurationIsRefusedBeforeAnythingIsCommitted()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsEmpty(cluster.Recorded);
    }


    [TestMethod]
    public async Task AReconfigurationToTheInstalledMembershipWritesNothing()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);
        RecordingPublisher publisher = new(cluster);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: publisher.PublishAsync);

        _ = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);
        _ = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        int served = cluster.Recorded.Count;
        QuePaxaWriteOutcome<string> again = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 3, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, again.Status);
        Assert.AreEqual(0, again.Attempts, "A change the membership already carries ran a consensus instance.");
        Assert.IsFalse(again.Activated);
        Assert.AreEqual(new RegisterVersion(2UL), again.Version);
        Assert.HasCount(served, cluster.Recorded, "A change that installs nothing put a request on the wire.");
    }


    /// <summary>
    /// Publish runs after the decision is taken and after it is learned, so a caller told its write failed
    /// would retry a write that landed.
    /// </summary>
    [TestMethod]
    public async Task AThrowingPublisherLeavesTheDecidedWriteDecided()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        int entered = 0;
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: (_, _, _) =>
        {
            entered++;

            throw new IOException("Every push target is unreachable.");
        });

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(1, entered, "The publisher was never entered, so the vector pinned nothing.");
        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.AreEqual("a", outcome.Value);
        Assert.AreEqual(RegisterVersion.First, register.Committed!.Version);
    }


    /// <summary>
    /// The stub fires the caller's own token and throws bound to it, which is the one fault a guard written to
    /// re-raise cancellation would let through.
    /// </summary>
    [TestMethod]
    public async Task ACancellingPublisherLeavesTheDecidedWriteDecided()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        using CancellationTokenSource writing = new();
        int entered = 0;
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: async (_, _, token) =>
        {
            entered++;
            await writing.CancelAsync().ConfigureAwait(false);

            throw new OperationCanceledException("The push was cancelled under the caller's own token.", token);
        });

        QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", writing.Token).ConfigureAwait(false);

        Assert.AreEqual(1, entered, "The publisher was never entered, so the vector pinned nothing.");
        Assert.IsTrue(writing.IsCancellationRequested, "The stub did not fire the caller's token, so nothing tested the cancellation arm.");
        Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        Assert.AreEqual("a", outcome.Value);
    }


    /// <summary>
    /// Two arms of one rule.
    /// </summary>
    /// <remarks>
    /// At a boundary the audience is the union, so a leaver is handed the record that removed it and a joiner
    /// is handed the record that admitted it; at an ordinary decide the union degenerates, and the arm runs
    /// immediately after a completed removal so a departed member exists to be wrongly included. Every
    /// assertion is by key on the content rather than on the length.
    /// </remarks>
    [TestMethod]
    public async Task TheAudienceIsTheUnionAtABoundaryAndTheMembershipAtAnOrdinaryDecide()
    {
        VersionedQuePaxaCluster<string> cluster = new(WiderSchedule(), 4);
        RecordingPublisher publisher = new(cluster);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: publisher.PublishAsync);

        _ = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

        ImmutableArray<ReplicaId> ordinary = publisher.Audiences[0];

        TestContext.WriteLine($"ordinary decide under genesis: {Describe(ordinary)}");
        Assert.Contains(Fourth, ordinary, "The genesis membership's own member was left out of an ordinary audience.");

        _ = await register.ReconfigureAsync(current => current.Without(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        ImmutableArray<ReplicaId> removal = publisher.Audiences[1];

        TestContext.WriteLine($"boundary removing the fourth member: {Describe(removal)}");
        Assert.Contains(Fourth, removal, "The departing member was not handed the record that removed it.");
        Assert.Contains(First, removal);
        Assert.Contains(Second, removal);
        Assert.Contains(Third, removal);

        _ = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

        ImmutableArray<ReplicaId> afterRemoval = publisher.Audiences[2];

        TestContext.WriteLine($"ordinary decide after the boundary: {Describe(afterRemoval)}");
        Assert.DoesNotContain(Fourth, afterRemoval, "A member a completed change removed is still being pushed to, so the outgoing half is stale.");
        Assert.Contains(First, afterRemoval);
        Assert.Contains(Second, afterRemoval);
        Assert.Contains(Third, afterRemoval);

        _ = await register.ReconfigureAsync(current => current.Without(Second).With(Fourth), maxAttempts: 1, TestContext.CancellationToken).ConfigureAwait(false);

        ImmutableArray<ReplicaId> replacement = publisher.Audiences[3];

        TestContext.WriteLine($"boundary replacing the second member with the fourth: {Describe(replacement)}");
        Assert.Contains(Second, replacement, "The leaver was dropped from the union.");
        Assert.Contains(Fourth, replacement, "The joiner was dropped from the union.");
        Assert.Contains(First, replacement);
        Assert.Contains(Third, replacement);
        Assert.AreSequenceEqual(new[] { First, Third, Fourth }, register.ActiveConfiguration.Members);
    }


    [TestMethod]
    public async Task AReadinessReportNamesEveryMemberAndWhetherAQuorumHasLearned()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: (member, _) => member.Equals(Third)
            ? throw new IOException("The third member is unreachable.")
            : new ValueTask<MemberVersionReport>(new MemberVersionReport(member, member.Equals(First) ? new RegisterVersion(4UL) : new RegisterVersion(3UL))));

        RegisterReadiness readiness = await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(cluster.Genesis, readiness.Configuration);
        Assert.AreEqual(2, readiness.Reachable);
        Assert.AreSequenceEqual(new[] { First, Second, Third }, readiness.Members.Select(member => member.Member));

        MemberReadiness unreachable = readiness.Members.Single(member => member.Member.Equals(Third));

        Assert.IsFalse(unreachable.Reachable);
        Assert.IsNull(unreachable.Version, "An unreachable member was reported as one that has learned nothing.");
        Assert.AreEqual(new RegisterVersion(3UL), readiness.Members.Single(member => member.Member.Equals(Second)).Version);

        Assert.IsTrue(readiness.QuorumHasLearned(new RegisterVersion(3UL)));
        Assert.IsFalse(readiness.QuorumHasLearned(new RegisterVersion(4UL)), "A quorum was claimed at a version one member reported below.");
    }


    [TestMethod]
    public async Task AReadinessReportIsRefusedWhenNoMemberVersionQueryWasSupplied()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    /// <summary>
    /// A version probe answered by another member fails the report rather than being counted.
    /// </summary>
    /// <remarks>
    /// Two probe routes landing on one host let one replica fill two slots of a report counted over distinct
    /// members, and a decommission gate cleared on it would retire a host on fewer distinct answers than the
    /// arithmetic claims. The refusal is loud rather than a null slot, because the defect is the deployment's
    /// endpoint map and never any member's availability.
    /// </remarks>
    [TestMethod]
    public async Task AReadinessProbeAnsweredByAnotherMemberFailsTheReportRatherThanCounting()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: (_, _) =>
            new ValueTask<MemberVersionReport>(new MemberVersionReport(First, new RegisterVersion(3UL))));

        InvalidOperationException reported = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.Contains("was answered by", reported.Message);
    }


    /// <summary>
    /// Readiness is measurable over a membership the register does not yet run under.
    /// </summary>
    /// <remarks>
    /// The incoming side of an admission is gated before any register runs under it, so the report takes the
    /// membership as an argument: the joiner is asked by name, its unreachability is its answer, and the
    /// quorum arithmetic is the incoming membership's own. A membership of another chain is refused, because
    /// a report over it would answer a question about a different register.
    /// </remarks>
    [TestMethod]
    public async Task ReadinessIsMeasurableOverAnIncomingMembershipBeforeTheChangeCommits()
    {
        VersionedQuePaxaCluster<string> cluster = new(Schedule(), 3);
        QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: (member, _) => member.Equals(Fourth)
            ? throw new IOException("The joiner is not serving yet.")
            : new ValueTask<MemberVersionReport>(new MemberVersionReport(member, new RegisterVersion(2UL))));

        QuePaxaConfiguration incoming = cluster.Genesis.With(Fourth);
        RegisterReadiness readiness = await register.ReadReadinessAsync(incoming, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(incoming, readiness.Configuration);
        Assert.AreSequenceEqual(new[] { First, Second, Third, Fourth }, readiness.Members.Select(member => member.Member));
        Assert.IsFalse(readiness.Members.Single(member => member.Member.Equals(Fourth)).Reachable);
        Assert.IsTrue(readiness.QuorumHasLearned(new RegisterVersion(2UL)), "Three of four answered at the version, which is the incoming membership's own quorum.");

        QuePaxaConfiguration foreign = QuePaxaConfiguration.CreateGenesis([First, Second]);
        ArgumentException refused = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await register.ReadReadinessAsync(foreign, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.Contains("another chain", refused.Message);
    }


    private static string Describe(ImmutableArray<ReplicaId> audience)
    {
        return string.Join(", ", audience.Select(member => Convert.ToHexStringLower(member.AsSpan())[..4]));
    }


    private static QuePaxaVersionedRegister<string> Register(
        VersionedQuePaxaCluster<string> cluster,
        ReplicaId self,
        TimeProvider? clock = null,
        ObserveCommittedVersionDelegate? observe = null,
        TimeSpan? baseDelay = null,
        PublishCommittedRecordDelegate<string>? publish = null,
        ResolveRecorderEndpointDelegate<string>? resolve = null,
        ObserveMemberVersionDelegate? observeMember = null)
    {
        return new QuePaxaVersionedRegister<string>(
            cluster.Genesis,
            self,
            baseDelay ?? BaseDelay,
            resolve ?? cluster.Resolve,
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            clock ?? TimeProvider.System,
            observe,
            publishCommittedRecord: publish,
            observeMemberVersion: observeMember);
    }


    /// <summary>
    /// A publisher that tells the hosts the audience names and records every audience it was handed, so a
    /// test reads what the register computed rather than what the hosts ended up holding.
    /// </summary>
    private sealed class RecordingPublisher(VersionedQuePaxaCluster<string> cluster)
    {
        /// <summary>Every audience handed over, in the order the writes decided.</summary>
        public List<ImmutableArray<ReplicaId>> Audiences { get; } = [];


        public ValueTask PublishAsync(VersionedValue<string> committed, ImmutableArray<ReplicaId> audience, CancellationToken cancellationToken)
        {
            Audiences.Add(audience);
            foreach(ReplicaId member in audience)
            {
                _ = cluster.LearnAtMember(member, committed);
            }

            return ValueTask.CompletedTask;
        }
    }


    /// <summary>
    /// The distinct lanes one replica proposed on at one version, in the order they first arrived.
    /// </summary>
    /// <remarks>
    /// A proposal key repeats across the steps of one attempt, so the distinct lanes are what count the
    /// attempts.
    /// </remarks>
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


    /// <summary>
    /// A four-replica order, which is what a membership change needs: a shrink has somewhere to shrink to and
    /// a replacement has an identity to admit that the three-replica suite has no host for.
    /// </summary>
    private static QuePaxaLeaderSchedule WiderSchedule(TimeSpan? baseDelay = null)
    {
        ImmutableArray<ReplicaId> order = [First, Second, Third, Fourth];

        return new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, baseDelay ?? BaseDelay));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
