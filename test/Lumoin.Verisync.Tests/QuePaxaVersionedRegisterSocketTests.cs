using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned register's integration scenarios over real loopback TCP, one runner-backed recorder host
/// per connection, per the standing rule that a wire-codec protocol is exercised over a real transport and
/// not only in process.
/// </summary>
/// <remarks>
/// <para>
/// Ported here are ten of the scenarios below, which are the fast path across three consecutive versions, the
/// two superseded outcomes, the undecided retry on a fresh lane, the two hedging vectors and the zero-delay
/// activation, the dissemination-bound quorum, the version-swapping transport, and the catch-up read. With
/// them goes the bare family's re-delivery after a failed persist, which lives in
/// <see cref="QuePaxaSocketClusterTests"/> beside the other bare-family rounds.
/// </para>
/// <para>
/// The two readiness scenarios are native here rather than ported. A readiness report is about who can be
/// reached and what they hold, and both halves of that are transport facts: the member that answers and holds
/// nothing and the member whose endpoints fault are one report apart and cannot be told apart by any reading
/// of the protocol. The in-memory bench separates behind from reachable by holding a host's dissemination
/// while it answers everything; this one cuts the route to a host that answers everything, which is the same
/// separation from the other side. The wire-crossing dissemination scenario is native for the same shape of
/// reason: its subject is the receive leg itself — a committed record crossing the wire into a host's own
/// learn — which every in-process push deliberately shortcuts.
/// </para>
/// <para>
/// Staying in process by design are the law suites, <see cref="QuePaxaAgreementLawTests"/>,
/// <see cref="QuePaxaInterleavingLawTests"/> and
/// <see cref="QuePaxaVersionedRegisterLinearizabilityTests"/>, and the unit suites for the recorder, the recorder state, the
/// round, the step, the proposer, the register, the node, the versioned node, the versioned node state, the
/// runner, the leader schedule, and the codecs and envelopes. Those pin laws and single-component contracts
/// where the transport is not a variable. Staying with them are the register's key-uniqueness and guard
/// vectors: <c>ASecondSingleAttemptWriteAtAnUndecidedVersionRunsOnAFreshLane</c>,
/// <c>TheLaneCounterStartsAgainAtAVersionThisRegisterHasNotProposedAt</c>,
/// <c>AConcurrentWriteIsRefusedRatherThanSharingALane</c>, and the constructor and attempt-budget refusals,
/// all of which are register-internal and transport-blind. Staying with them is
/// <c>TheBootstrapLeaderCommitsTheFirstVersionOnTheFastPath</c>, whose decision is the first iteration of
/// the three-version port here while its fine-grained pins, <c>DecidedAt</c>, <c>Attempts</c> and
/// <c>Activated</c>, are register-internal. <see cref="QuePaxaCodecDecisionTests"/> stays for a reason of
/// its own: it runs a whole decision with the codec in the loop to pin the codec's instance boundary, which
/// is reachable in process, and its wire-level analogue is the leaderless round in
/// <see cref="QuePaxaSocketClusterTests"/>.
/// </para>
/// <para>
/// This is the two-bench separation the cluster documentation carries:
/// <see cref="QuePaxaVersionedRegisterTests"/> stays whole as the in-memory bench pinning what the protocol
/// does, and this suite pins that the wire does not change it.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaVersionedRegisterSocketTests
{
    private const int AttemptsPerRecorder = 2;
    private const int HostCount = 3;
    private const int MaxWriteRounds = 10;

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);
    private static ReplicaId Fourth { get; } = Replica(4);

    /// <summary>
    /// The membership every record in this suite carries, minted from the agreed order the hosts run under.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third));

    private static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(40);

    private static int[] LanesZeroToTwo { get; } = [0, 1, 2];


    [TestMethod]
    public async Task TheFastPathSurvivesThreeConsecutiveVersionsAsTheLeaderRotatesToEachWriterOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First);

            for(int version = 1; version <= 3; version++)
            {
                QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync($"v{version}", TestContext.CancellationToken).ConfigureAwait(false);

                Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
                Assert.AreEqual(new RegisterVersion((ulong)version), outcome.Version);
                Assert.IsTrue(outcome.TookFastPath, $"Version {version} did not take the fast path, so the rotation lost the leader its reserved claim.");

                //Without dissemination the hosts stay on the version just decided and decline the next one.
                await cluster.LearnAllAsync(new VersionedValue<string>(outcome.Version, outcome.Writer!.Value, Configuration, outcome.Value!), TestContext.CancellationToken).ConfigureAwait(false);
            }

            Assert.AreEqual(new RegisterVersion(3UL), register.Committed!.Version);

            await cluster.DrainAsync(TestContext.CancellationToken).ConfigureAwait(false);

            //This is the one place the socket suite witnesses the serve-side counter counting, so the zero
            //assertions elsewhere rest on a counter proven live on this transport. The floor is the
            //membership's quorum and not every host: a proposer acts on the first quorum to answer and
            //abandons the rest of the step, so a host whose frame was still in flight when the decision was
            //taken has legitimately served nothing, and requiring its answer would fail on a slow machine
            //for a reason the protocol never promised.
            int answered = cluster.Served.Count(served => served > 0);

            Assert.IsGreaterThanOrEqualTo(cluster.Genesis.Quorum, answered, $"Only {answered} of {cluster.HostCount} hosts served anything across three committed versions, which is below the quorum those versions committed on.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// A disseminated record crosses the wire into each host's own receive leg and opens the next version.
    /// </summary>
    /// <remarks>
    /// The publish delegate here is the cluster's wire push, so the record crosses the wire into each host's
    /// own learn rather than being handed to the runners in process: the leg every socketed deployment wires
    /// is the subject, not a shortcut past it.
    /// </remarks>
    [TestMethod]
    public async Task ADisseminatedRecordCrossesTheWireAndOpensTheNextVersion()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: cluster.PublishAsync);

            QuePaxaWriteOutcome<string> first = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, first.Status);

            //The offers were awaited before the write returned, so the count is settled without a drain: each
            //increment happens before its answer leaves the host, and every answer was received before the
            //publish completed. An ordinary decide's audience is the deciding membership, so all three were
            //owed an offer.
            Assert.AreEqual(HostCount, cluster.Disseminated.Sum(), "The first decide was not offered to every member over the wire.");

            //A quorum can serve the next version only where the previous record arrived, so this commit is
            //the proof the wire-borne offers were adopted and not merely answered.
            QuePaxaWriteOutcome<string> second = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, second.Status);
            Assert.AreEqual(new RegisterVersion(2UL), second.Version);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Concurrent writers on real operating-system threads stay linearizable over the wire.
    /// </summary>
    /// <remarks>
    /// Concurrent clients drive the real transport while every operation is recorded into a history whose
    /// indeterminate outcomes stay possibilities rather than failures, and the history is checked against
    /// the append register's formal model afterwards. Safety is asserted under any interleaving; that contention
    /// actually occurred on a given run is scheduled by the operating system, so the deterministic pin for
    /// contention stays with the in-memory linearizability suite and this row pins that the wire and real
    /// threads change nothing the model can see.
    /// </remarks>
    [TestMethod]
    public async Task ConcurrentWritersLinearizeOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            Task<RegisterOperation[]>[] writers =
            [
                RunSocketWriterAsync(cluster, First, 'A', 'B', null, null, TestContext.CancellationToken),
                RunSocketWriterAsync(cluster, Second, 'C', 'D', null, null, TestContext.CancellationToken),
                RunSocketWriterAsync(cluster, Third, 'E', 'F', null, null, TestContext.CancellationToken)
            ];

            RegisterOperation[][] completed = await Task.WhenAll(writers).ConfigureAwait(false);
            RegisterOperation[] history = [.. completed.SelectMany(operations => operations)];
            string witness = await WitnessOverSocketsAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            Assert.HasCount(6, history, "A label was retired unlanded on a healthy cluster, which the rounds budget exists to prevent.");
            Assert.AreEqual(6, witness.Length, $"The witness '{witness}' does not carry exactly the six labels.");
            AppendRegisterChecker.AssertLinearizable(history, witness);

            TestContext.WriteLine($"Witness '{witness}'.");

            await cluster.DrainAsync(TestContext.CancellationToken).ConfigureAwait(false);

            //A quorum floor rather than a census, for the reason the fast-path row carries: an abandoned
            //recorder's frame may never cross, so no single host's answer is owed by the protocol.
            int answered = cluster.Served.Count(served => served > 0);

            Assert.IsGreaterThanOrEqualTo(cluster.Genesis.Quorum, answered, $"Only {answered} of {cluster.HostCount} hosts served anything under three concurrent writers.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// The concurrent workload stays linearizable across a minority partition, and the healed host converges.
    /// </summary>
    /// <remarks>
    /// The fault is injected mid-workload: the partition is installed between the first and second appends
    /// by barrier rather than by clock, so the cut lands mid-history on every run. Writes keep committing
    /// through the surviving majority, the history stays linearizable across the fault, and the healed host
    /// converges on the final record through the same wire-borne receive leg an operator's re-dissemination
    /// would use.
    /// </remarks>
    [TestMethod]
    public async Task WritersLinearizeAcrossAMinorityPartitionOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            using CountdownEvent firstWave = new(3);
            TaskCompletionSource cutInstalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task nemesis = Task.Run(() =>
            {
                firstWave.Wait(TestContext.CancellationToken);
                cluster.Partition(2);
                cutInstalled.SetResult();
            }, TestContext.CancellationToken);

            Task<RegisterOperation[]>[] writers =
            [
                RunSocketWriterAsync(cluster, First, 'A', 'B', cutInstalled.Task, () => firstWave.Signal(), TestContext.CancellationToken),
                RunSocketWriterAsync(cluster, Second, 'C', 'D', cutInstalled.Task, () => firstWave.Signal(), TestContext.CancellationToken),
                RunSocketWriterAsync(cluster, Third, 'E', 'F', cutInstalled.Task, () => firstWave.Signal(), TestContext.CancellationToken)
            ];

            RegisterOperation[][] completed = await Task.WhenAll(writers).ConfigureAwait(false);
            await nemesis.ConfigureAwait(false);
            cluster.Heal(2);

            RegisterOperation[] history = [.. completed.SelectMany(operations => operations)];
            string witness = await WitnessOverSocketsAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);

            Assert.HasCount(6, history, "A label was retired unlanded although a majority served throughout the cut.");
            AppendRegisterChecker.AssertLinearizable(history, witness);

            //The healed host converges through the receive leg: the final record is offered over the wire
            //and the host then reports the final version through its own connection.
            VersionedValue<string>? final = await HighestHeldAsync(cluster, TestContext.CancellationToken).ConfigureAwait(false);
            await cluster.PublishAsync(final!, [.. cluster.Genesis.Members.Select(configured => configured.Replica)], TestContext.CancellationToken).ConfigureAwait(false);

            VersionedValue<string>? healed = await cluster.ResolveReader(cluster.Genesis.Members[2].Replica)(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(final!.Version, healed!.Version, "The healed host did not converge on the final record it was offered.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// The change function records its input because a retry re-proposing the original value would still pass.
    /// </summary>
    [TestMethod]
    public async Task ASupersededWriteAdoptsTheWinnerAndRecomputesFromItOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> other = Register(cluster, Second);

            //The retry has nowhere to go until the version that beat it has reached the hosts.
            QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: async (committed, _, token) =>
                await cluster.LearnAllAsync(committed, token).ConfigureAwait(false));

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
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    [TestMethod]
    public async Task ASupersededWriteReportsTheWinnersRecordRatherThanFailingOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
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
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// An undecided proposal may be decided later, so treating it as a conflict would abandon a live write.
    /// </summary>
    /// <remarks>
    /// Two of three hosts are partitioned so the quorum is missed while the third still records what it was
    /// asked, which is what lets the lanes be read rather than inferred from the attempt count.
    /// </remarks>
    [TestMethod]
    public async Task AnUndecidedWriteRetriesAtTheSameVersionOnAFreshLaneOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
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

            await cluster.DrainAsync(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreSequenceEqual(
                LanesZeroToTwo,
                LanesAt(cluster, RegisterVersion.First, First),
                "The three attempts did not run on three distinct lanes, so two proposals shared a key at one version.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    [TestMethod]
    public async Task ALaterWriterWaitsItsHedgingDelayAndTheLeaderDoesNotOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            FakeTimeProvider clock = new();
            QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock);

            Assert.AreEqual(2 * BaseDelay, later.Delay);

            Task<QuePaxaWriteOutcome<string>> pending = later.TryWriteAsync("y", TestContext.CancellationToken);

            //Sent is written on the write's own flow before the wire, so a write that skipped its delay has
            //already incremented it by the time the pending task is handed back. The serve-side counter lags
            //the wire and cannot pin this.
            Assert.AreEqual(0, cluster.Sent[0]);

            clock.Advance(2 * BaseDelay);
            QuePaxaWriteOutcome<string> outcome = await pending.ConfigureAwait(false);

            Assert.IsTrue(outcome.Activated);
            Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);

            QuePaxaVersionedRegister<string> leader = Register(cluster, First, clock);

            Assert.AreEqual(TimeSpan.Zero, leader.Delay);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    [TestMethod]
    public async Task ADelayedWriterStandsDownWhenTheVersionIsAlreadyCommittedOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            FakeTimeProvider clock = new();
            QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock, _ => new ValueTask<RegisterVersion>(RegisterVersion.First));

            Task<QuePaxaWriteOutcome<string>> pending = later.TryWriteAsync("y", TestContext.CancellationToken);
            clock.Advance(2 * BaseDelay);
            QuePaxaWriteOutcome<string> outcome = await pending.ConfigureAwait(false);

            Assert.IsFalse(outcome.Activated);
            Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);

            //The sent count is settled on the caller's flow, so it pins the stand-down without a drain.
            Assert.AreEqual(0, cluster.Sent[0]);

            await cluster.DrainAsync(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(0, cluster.Served[0], "A writer that stood down still sent a request.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// This is the only vector that fails if the stand-down is hoisted out of the delay guard.
    /// </summary>
    [TestMethod]
    public async Task AZeroBaseDelayActivatesEveryWriterEvenWhenTheVersionIsReportedCommittedOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule(TimeSpan.Zero)).ConfigureAwait(false);
        try
        {
            FakeTimeProvider clock = new();
            QuePaxaVersionedRegister<string> later = Register(cluster, Third, clock, _ => new ValueTask<RegisterVersion>(RegisterVersion.First), TimeSpan.Zero);

            QuePaxaWriteOutcome<string> outcome = await later.TryWriteAsync("y", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsTrue(outcome.Activated);
            Assert.AreEqual(QuePaxaWriteStatus.Committed, outcome.Status);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// This pins the liveness cost that the single-live-instance rule pays for its safety.
    /// </summary>
    [TestMethod]
    public async Task AWriteReachesAQuorumOnlyWhereThePreviousVersionHasBeenDisseminatedOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First);

            QuePaxaWriteOutcome<string> first = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, first.Status);

            VersionedValue<string> committed = new(first.Version, first.Writer!.Value, Configuration, first.Value!);

            //One host learns, which leaves the quorum one short: two of three must serve.
            _ = await cluster.LearnAtAsync(0, committed, TestContext.CancellationToken).ConfigureAwait(false);

            QuePaxaWriteOutcome<string> starved = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Undecided, starved.Status);

            //The starved outcome is what the decline fault frames produced, so every entry behind it was
            //added server side before the write returned and this observation needs no drain.
            Assert.IsNotEmpty(cluster.Declined);

            _ = await cluster.LearnAtAsync(1, committed, TestContext.CancellationToken).ConfigureAwait(false);

            QuePaxaWriteOutcome<string> served = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, served.Status);
            Assert.AreEqual(new RegisterVersion(2UL), served.Version);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Counting a reply from another instance would decide on a set that is a minority of the instance it
    /// names.
    /// </summary>
    [TestMethod]
    public async Task AVersionSwappingTransportYieldsNoDecisionRatherThanAWrongOneOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule(), SwapReplyVersion).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First);

            QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Undecided, outcome.Status);

            //A rewrite the codec refuses would also yield Undecided, so the decode count is what separates
            //the guard this vector pins from a codec failure it must not hide behind.
            Assert.IsGreaterThan(0, cluster.DecodedReplies, "No tampered reply survived the codec, so the register's guard was never exercised.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// One honest host settles it because a committed record is a decided fact under the crash-fault model.
    /// </summary>
    [TestMethod]
    public async Task AReadAdoptsTheHighestRecordAnyHostReportsOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            VersionedValue<string> committed = new(new RegisterVersion(7UL), Second, Configuration, "learned");
            _ = await cluster.LearnAtAsync(2, committed, TestContext.CancellationToken).ConfigureAwait(false);

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

            VersionedValue<string>? read = await register.ReadAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(committed, read);
            Assert.AreEqual(new RegisterVersion(8UL), register.NextVersion);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// A readiness report assembled over real connections. Every member is asked over its own connection and
    /// every one answers, so the report names each member of the membership and the version that member holds,
    /// and the member the dissemination has not reached answers unwritten rather than falling silent.
    /// </summary>
    /// <remarks>
    /// A readiness read owes a census where a write owes a quorum. A write commits on a majority and may
    /// abandon the lanes it no longer needs, so which members answered it is the run's timing; this read
    /// addresses every member of the membership in turn and takes every answer, so each member's entry is
    /// required by name and a report that dropped one or answered for one it never asked is a report that
    /// differs from this one.
    /// </remarks>
    [TestMethod]
    public async Task AReadinessReportOverSocketsNamesEveryMemberAndTheVersionEachOneHolds()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: cluster.ObserveMemberVersionAsync);

            QuePaxaWriteOutcome<string> committed = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, committed.Status);

            //Two of the three learn, which leaves a member that answers and holds nothing beside two that
            //answer and hold the record: the reading below is what separates behind from unreachable.
            VersionedValue<string> record = register.Committed!;
            _ = await cluster.LearnAtAsync(0, record, TestContext.CancellationToken).ConfigureAwait(false);
            _ = await cluster.LearnAtAsync(1, record, TestContext.CancellationToken).ConfigureAwait(false);

            RegisterReadiness behind = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness while one member is behind: {Describe(behind)}"));
            Assert.AreEqual(Configuration, behind.Configuration, "The report was measured over a membership other than the one this register runs under.");
            Assert.AreSequenceEqual(new[] { First, Second, Third }, behind.Members.Select(member => member.Member), "The report does not name every member of the membership it was measured over, in that membership's order.");
            Assert.AreEqual(3, behind.Reachable, "A member that answered over its connection was reported unreachable.");
            Assert.AreEqual(committed.Version, behind.Members.Single(member => member.Member.Equals(First)).Version);
            Assert.AreEqual(committed.Version, behind.Members.Single(member => member.Member.Equals(Second)).Version);
            Assert.AreEqual(RegisterVersion.Unwritten, behind.Members.Single(member => member.Member.Equals(Third)).Version, "The member the dissemination has not reached reported a version no host could have given it.");
            Assert.IsFalse(behind.Members.Single(member => member.Member.Equals(Third)).HasLearned(committed.Version), "A member holding nothing was read as having learned the record.");
            Assert.IsTrue(behind.QuorumHasLearned(committed.Version), "Two of the three reported the record and the gate reads no quorum at it.");

            _ = await cluster.LearnAtAsync(2, record, TestContext.CancellationToken).ConfigureAwait(false);

            RegisterReadiness caughtUp = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the straggler caught up: {Describe(caughtUp)}"));
            Assert.AreEqual(3, caughtUp.Reachable);
            Assert.AreEqual(committed.Version, caughtUp.Members.Single(member => member.Member.Equals(Third)).Version, "The member that took the record still reports the version it held before it, so the report is a memory rather than a question.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// A member whose endpoints fault is reported unreachable while the rest of the membership answers. The
    /// route to one host is cut after every host has learned the record, so nothing about what that host holds
    /// changed and the only thing that did is whether it can be asked; the report drops it from the reachable
    /// count, still names it, and still reads a quorum at the record the two reachable members hold.
    /// </summary>
    /// <remarks>
    /// This is the complementary reading to a member that answers and has not learned. There the versions
    /// separate the members and the reachable count does not; here the reachable count separates them and the
    /// versions cannot, because a host that cannot be asked reports no version at all. An operator waiting out
    /// the first would wait forever on the second, which is why the two are counted apart. Healing the route
    /// is what shows which of the two this was: the same host answers again with the version it held all
    /// along, so the drop was the endpoints and never the host.
    /// </remarks>
    [TestMethod]
    public async Task AMemberWhoseEndpointsFaultIsReportedUnreachableWhileTheOtherMembersAnswerOverSockets()
    {
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(Schedule()).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: cluster.ObserveMemberVersionAsync);

            QuePaxaWriteOutcome<string> committed = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, committed.Status);

            await cluster.LearnAllAsync(register.Committed!, TestContext.CancellationToken).ConfigureAwait(false);

            RegisterReadiness reached = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness while every route is up: {Describe(reached)}"));
            Assert.AreEqual(3, reached.Reachable, "A member answered nothing while every route was up, so the reading below would not be the route's doing.");
            Assert.AreEqual(committed.Version, reached.Members.Single(member => member.Member.Equals(Third)).Version, "The member whose route is cut below does not hold the record beforehand, so a later silence would say nothing about reachability.");

            //The host keeps running and keeps the record it learned; what it loses is the route to it.
            cluster.Partition(2);

            RegisterReadiness faulted = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness while one member's endpoints fault: {Describe(faulted)}"));
            Assert.AreEqual(2, faulted.Reachable, "A member whose calls fault was counted among those that answered.");
            Assert.AreSequenceEqual(new[] { First, Second, Third }, faulted.Members.Select(member => member.Member), "The member that could not be reached was left out of the report rather than reported unreachable.");

            MemberReadiness unreachable = faulted.Members.Single(member => member.Member.Equals(Third));

            Assert.IsFalse(unreachable.Reachable);
            Assert.IsNull(unreachable.Version, "A member that answered nothing was reported as one that has learned nothing, which is the collapse the report exists to prevent.");
            Assert.IsFalse(unreachable.HasLearned(committed.Version), "A member that could not be asked was read as having learned the record it holds, which is a fact the report cannot have.");
            Assert.AreEqual(committed.Version, faulted.Members.Single(member => member.Member.Equals(First)).Version, "The first member stopped answering when another member's route was cut.");
            Assert.AreEqual(committed.Version, faulted.Members.Single(member => member.Member.Equals(Second)).Version, "The second member stopped answering when another member's route was cut.");
            Assert.IsTrue(faulted.QuorumHasLearned(committed.Version), "Two members of three reported the record and the gate reads no quorum at it, so an unreachable member is being counted against what the reachable ones said.");

            cluster.Heal(2);

            RegisterReadiness healed = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the route is restored: {Describe(healed)}"));
            Assert.AreEqual(3, healed.Reachable, "The host behind the restored route did not answer again, so the run cannot say the cut was the endpoints rather than the host.");
            Assert.AreEqual(committed.Version, healed.Members.Single(member => member.Member.Equals(Third)).Version, "The host behind the restored route lost the record it held while nothing could ask it for one.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// An ordinary record pushed to a host whose runner keeps a store is durable at that host before the push
    /// completes. The membership the record names is the one the host already runs under, so the durability
    /// the sender required is the only thing that can have written it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is this scenario's alone. Every reply a host with a store hands back waits on a write, so a
    /// cluster given one does per-request work no other scenario does, and only a scenario that reads what
    /// was written earns that.
    /// </para>
    /// <para>
    /// A record that installs a membership is made durable whatever the sender named, because it may be the
    /// only copy of that membership inside the membership it installs. That leaves the ordinary record as the
    /// one shape on which the two namings differ at all, which is why the reading below is taken over a
    /// record carrying the membership forward and the arm after it is taken over one that does not.
    /// </para>
    /// <para>
    /// The drain is the barrier the count needs. A proposer that abandoned a slow recorder can leave a
    /// request the host has yet to answer, and every answer this host serves costs it a write, so a count
    /// taken while one is in flight would be counting the round rather than the push.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AnOrdinaryPushedRecordIsDurableAtTheReceivingHostBeforeTheLearnCompletes()
    {
        RecordingNodeStore[] stores = [new(), new(), new()];
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(
            Schedule(),
            persistNodes: [.. stores.Select(store => (PersistVersionedNodeDelegate<string>)store.PersistAsync)]).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First);

            QuePaxaWriteOutcome<string> committed = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, committed.Status);

            await cluster.DrainAsync(TestContext.CancellationToken).ConfigureAwait(false);

            VersionedValue<string> record = register.Committed!;
            RecordingNodeStore receiving = stores[2];
            int writtenBeforeThePush = receiving.States.Count;

            //Reading the third host by name is safe here only because nothing has advanced that host before
            //the drain: its abandoned round-one request is still servable, so the drain settles it as a
            //served write rather than a decline. A scenario that disseminated or wrote again before this
            //point would turn the read into a census over an answer the protocol does not owe.
            Assert.IsGreaterThan(0, writtenBeforeThePush, "The host wrote nothing while it served the round, so this store is not the one its loop was given.");
            Assert.IsNull(receiving.States[^1].Committed, "The host had the record before it was pushed one, so a state carrying it afterwards would say nothing about the push.");
            Assert.AreEqual(cluster.Genesis, record.NextConfiguration, "The pushed record moves the membership, and a record that installs one is made durable whatever the sender named.");

            bool advanced = await cluster.LearnAtAsync(2, record, TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsTrue(advanced);
            Assert.HasCount(writtenBeforeThePush + 1, receiving.States, "The pushed learn completed without a write, so the durability its sender named is a word rather than an obligation the receiver met.");
            Assert.AreEqual(record, receiving.States[^1].Committed, "The state made durable by the push does not carry the record that was pushed.");

            //The other half of the asymmetry, and the reason the reading above is taken over an ordinary
            //record: a record that installs a membership is written even where the sender required nothing.
            VersionedValue<string> installing = new(record.Version.Next(), First, cluster.Genesis.With(Membership.Member(Fourth)), "b");
            int writtenBeforeTheInstall = receiving.States.Count;

            Assert.IsTrue(await cluster.LearnInMemoryAtAsync(2, installing, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.HasCount(writtenBeforeTheInstall + 1, receiving.States, "A learn that installed a membership completed without a write, so the record carrying that membership is one a crash takes with it.");
            Assert.AreEqual(installing.NextConfiguration, receiving.States[^1].ActiveConfiguration, "The state made durable by the installing learn runs under the membership the record replaced.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// A chain founded on a member list that is not in ascending replica order keeps that order across the
    /// wire. The replica the genesis lists first leads, and the membership that comes back inside the decided
    /// record lists the members in the order they were encoded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other cluster in this suite is founded on an ascending list, where a decoder that sorted what it
    /// read and one that read it faithfully produce the same list and no vector can separate them. A
    /// deployment's genesis order is its own, and it is load-bearing: the chain identity is the digest of that
    /// ordered list, and the first member is the bootstrap leader.
    /// </para>
    /// <para>
    /// Protocol progress cannot pin the order. Every host and the register decode the same bytes the same way,
    /// so a transform applied alike everywhere leaves them agreeing with each other and the round proceeds;
    /// what sees it is a comparison of what came back against what went in. Two are read here: the write's own
    /// status, which compares the whole decided record against the one proposed, and a record read back off a
    /// host's connection.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AGenesisWhoseMemberOrderIsNotAscendingCrossesTheWireInTheOrderItWasEncoded()
    {
        ImmutableArray<ReplicaId> order = [Third, First, Second];
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, BaseDelay))).ConfigureAwait(false);
        try
        {
            Assert.AreSequenceEqual(order, cluster.Genesis.Members.Select(configured => configured.Replica), "The chain was founded on a different list than the one this scenario rests on.");

            QuePaxaVersionedRegister<string> register = Register(cluster, Third);

            Assert.AreEqual(TimeSpan.Zero, register.Delay, "The replica the genesis lists first is not the one that writes without waiting, so the order read here is not the membership's own.");

            QuePaxaWriteOutcome<string> committed = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            //The decided record came back off the wire, and the status is a comparison of that whole record
            //against the one that went onto it, so a decoder that reordered the members reports this
            //register's own write as somebody else's.
            Assert.AreEqual(QuePaxaWriteStatus.Committed, committed.Status, "The register's own record did not come back equal to the one it proposed, so something between the two rewrote it.");
            Assert.IsTrue(committed.TookFastPath, "The bootstrap leader of a genesis in this order lost its reserved claim, so the hosts derive the leader from a list other than the one they were given.");
            Assert.AreSequenceEqual(order, register.ActiveConfiguration.Members.Select(configured => configured.Replica), "The membership that crossed the wire does not list the members in the order they were encoded.");
            Assert.AreEqual(cluster.Genesis.Cluster, register.ActiveConfiguration.Cluster, "The membership that crossed the wire names another chain than the genesis it was minted on.");

            //And once more through a host: the record is handed to every host and read back over that host's
            //own connection, so what this compares is a membership encoded here, held there and decoded again.
            await cluster.LearnAllAsync(register.Committed!, TestContext.CancellationToken).ConfigureAwait(false);

            VersionedValue<string>? readBack = await cluster.ResolveReader(First)(TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotNull(readBack, "The host reported no record after it was handed one.");
            Assert.AreEqual(register.Committed, readBack, "The record read back off a host's connection is not the one that was handed to it.");
            Assert.AreSequenceEqual(order, readBack.NextConfiguration.Members.Select(configured => configured.Replica), "The membership read back off a host's connection lists the members in another order than the one encoded.");

            //Writing continues over the order the deployment named, which is what says the order that survived
            //is also the one the schedule and the quorum are taken over.
            QuePaxaWriteOutcome<string> next = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, next.Status);
            Assert.AreEqual(new RegisterVersion(2UL), next.Version);
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>
    /// A membership grown over sockets, every member of it caught up, and then one member's route cut: the
    /// write after the change is gathered from the three members that remain reachable, each of them named,
    /// while the fourth holds the installing record the whole time and could have answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An answering set on a write owes a quorum and not a census, so a reading that names members is only
    /// legitimate where the quorum cannot be met without each of them. Here it cannot: the membership has
    /// four members and a quorum of three, one route is cut, and the three that remain are exactly a quorum,
    /// so every name in the reading is required rather than observed.
    /// </para>
    /// <para>
    /// What separates this from a member that was never disseminated to is the fourth member's state. It
    /// holds the record that installed the membership and its host is up, so it is an answerer the write
    /// could have been gathered from, and the reading afterwards says which three it actually was. The
    /// readiness read taken once the route is healed is what shows the member was able all along.
    /// </para>
    /// <para>
    /// The versions before the boundary are read as a subset of the membership plus the quorum floor, because
    /// there several members could serve and which of them answered is the run's timing.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AWriteAfterAGrowIsGatheredFromThreeNamedMembersWhileAFourthCouldHaveAnswered()
    {
        //Four hosts and three members: the fourth runs on the chain's genesis like the rest, because it
        //belongs to the chain from the moment its deployment starts it and only the membership is behind.
        ImmutableArray<ReplicaId> hosts = [First, Second, Third, Fourth];
        QuePaxaConfiguration genesis = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third));
        SocketVersionedQuePaxaCluster<string> cluster = await ConnectAsync(
            new QuePaxaLeaderSchedule(HedgingSchedule.Create(hosts, BaseDelay)),
            hostCount: hosts.Length,
            genesis: genesis).ConfigureAwait(false);
        try
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, First, observeMember: cluster.ObserveMemberVersionAsync);

            QuePaxaWriteOutcome<string> bootstrap = await register.TryWriteAsync("a", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);

            //Dissemination is explicit here, as in a deployment: only the members of the membership that
            //decided the first record are told about it.
            foreach(ReplicaId member in genesis.Members.Select(configured => configured.Replica))
            {
                Assert.IsTrue(await cluster.LearnAtAsync(hosts.IndexOf(member), register.Committed!, TestContext.CancellationToken).ConfigureAwait(false));
            }

            QuePaxaWriteOutcome<string> grown = await register.ReconfigureAsync(current => current.With(Membership.Member(Fourth)), maxAttempts: 2, TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, grown.Status);
            Assert.AreSequenceEqual(new[] { First, Second, Third, Fourth }, register.ActiveConfiguration.Members.Select(configured => configured.Replica), "The membership that crossed the wire does not list the members that were encoded, in that order.");
            Assert.AreEqual(3, register.ActiveConfiguration.Quorum, "A membership of four does not count a quorum of three, so the arithmetic the reading below rests on is not the one measured here.");

            //EVERY MEMBER CATCHES UP, THE JOINER INCLUDED. That is what makes the reading at the end a claim
            //about which members answered rather than about which of them was able to.
            await cluster.LearnAllAsync(register.Committed!, TestContext.CancellationToken).ConfigureAwait(false);

            RegisterReadiness ready = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the whole installed membership caught up: {Describe(ready)}"));
            Assert.AreEqual(4, ready.Reachable, "A member that answers over its connection was reported unreachable.");
            Assert.AreEqual(grown.Version, ready.Members.Single(member => member.Member.Equals(Fourth)).Version, "The joiner does not hold the record that admitted it, so it is not an answerer the write below could have been gathered from.");

            //The route to the third member is cut, which leaves exactly a quorum able to answer.
            cluster.Partition(hosts.IndexOf(Third));

            QuePaxaWriteOutcome<string> across = await register.TryWriteAsync("b", TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(QuePaxaWriteStatus.Committed, across.Status);
            Assert.AreEqual(new RegisterVersion(3UL), across.Version);

            //Healing before the reading is what shows the fourth answerer was a route and never a host: the
            //member that could not be asked answers again with the record it held all along.
            cluster.Heal(hosts.IndexOf(Third));

            RegisterReadiness healed = await register.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the cut route is restored: {Describe(healed)}"));
            Assert.AreEqual(grown.Version, healed.Members.Single(member => member.Member.Equals(Third)).Version, "The member whose route was cut does not hold the installing record, so it could not have served the write that ran without it.");

            await cluster.DrainAsync(TestContext.CancellationToken).ConfigureAwait(false);

            ImmutableArray<ReplicaId> firstVersion = AnsweredAt(cluster, hosts, bootstrap.Version);

            Assert.IsTrue(firstVersion.All(genesis.Contains), "The first version was answered outside the membership it ran under.");
            Assert.IsGreaterThanOrEqualTo(genesis.Quorum, firstVersion.Length, "The first version was answered by fewer members than the quorum it committed on.");

            //EVERY ANSWER IS REQUIRED BY NAME. The membership has four members and a quorum of three, and one
            //route is cut, so the write could not have committed without each of the three named here.
            Assert.AreSequenceEqual(new[] { First, Second, Fourth }, AnsweredAt(cluster, hosts, across.Version), "The write after the change was not gathered from the joiner and the two reachable incumbents, so the quorum it committed on is not the one the installed membership names.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    /// <summary>The members of <paramref name="hosts"/> that answered at <paramref name="version"/>.</summary>
    /// <param name="cluster">The cluster whose serve loops recorded the answers, drained before this is read.</param>
    /// <param name="hosts">The hosts to read, in the order the reading reports them.</param>
    /// <param name="version">The instance to read.</param>
    /// <returns>Those members, each named once.</returns>
    /// <remarks>
    /// The arrival order across hosts is the run's timing rather than a property of the protocol, so the
    /// reading is ordered by the host list, and a member that answered several steps of one instance is named
    /// once because what is read is which recorders a quorum could have been counted over.
    /// </remarks>
    private static ImmutableArray<ReplicaId> AnsweredAt(
        SocketVersionedQuePaxaCluster<string> cluster,
        ImmutableArray<ReplicaId> hosts,
        RegisterVersion version)
    {
        IReadOnlyList<(ReplicaId Member, RegisterVersion Version)> answered = cluster.Answered;

        return [.. hosts.Where(host => answered.Any(entry => entry.Version == version && entry.Member.Equals(host)))];
    }


    /// <summary>One readiness report as a line, per member, for the run's own record.</summary>
    /// <param name="readiness">The report.</param>
    /// <returns>The line.</returns>
    private static string Describe(RegisterReadiness readiness)
    {
        return string.Join(", ", readiness.Members.Select(member => member.Version is { } version
            ? string.Create(CultureInfo.InvariantCulture, $"{Name(member.Member)}@{version.Value}")
            : $"{Name(member.Member)}@unreachable"));
    }


    /// <summary>A replica's leading bytes, which is enough to tell this suite's three apart.</summary>
    /// <param name="replica">The replica.</param>
    /// <returns>Its leading bytes in hexadecimal.</returns>
    private static string Name(ReplicaId replica) => Convert.ToHexStringLower(replica.AsSpan())[..4];


    /// <summary>
    /// The reply is rewritten as bytes rather than as a deserialized object, so the swap crosses the codec
    /// that a deployment would meet it at.
    /// </summary>
    private static byte[] SwapReplyVersion(int host, byte[] payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        ulong version = root.GetProperty("version").GetUInt64();

        var buffer = new ArrayBufferWriter<byte>();
        using(var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", version + 1UL);

            //Only the version is swapped: a payload short of its recorder would be refused by the codec and
            //the register's instance check would never be reached.
            writer.WritePropertyName("recorder");
            root.GetProperty("recorder").WriteTo(writer);
            writer.WritePropertyName("reply");
            root.GetProperty("reply").WriteTo(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }


    private Task<SocketVersionedQuePaxaCluster<string>> ConnectAsync(
        QuePaxaLeaderSchedule schedule,
        TamperReplyPayloadDelegate? tamperReplyPayload = null,
        int? hostCount = null,
        QuePaxaConfiguration? genesis = null,
        PersistVersionedNodeDelegate<string>[]? persistNodes = null)
    {
        return SocketVersionedQuePaxaCluster<string>.ConnectAsync(
            schedule,
            hostCount ?? HostCount,
            static (writer, value) => writer.WriteStringValue(value),
            static element => element.GetString()!,
            TestContext.CancellationToken,
            tamperReplyPayload: tamperReplyPayload,
            genesis: genesis,
            persistNodes: persistNodes);
    }


    private static QuePaxaVersionedRegister<string> Register(
        SocketVersionedQuePaxaCluster<string> cluster,
        ReplicaId self,
        TimeProvider? clock = null,
        ObserveCommittedVersionDelegate? observe = null,
        TimeSpan? baseDelay = null,
        PublishCommittedRecordDelegate<string>? publish = null,
        ObserveMemberVersionDelegate? observeMember = null,
        ResolveCommittedRecordReaderDelegate<string>? reader = null)
    {
        return new QuePaxaVersionedRegister<string>(
            cluster.Genesis,
            self,
            baseDelay ?? BaseDelay,
            cluster.Resolve,
            ProposalPriority.Cryptographic,
            AttemptsPerRecorder,
            clock ?? TimeProvider.System,
            observe,
            resolveCommittedRecordReader: reader,
            publishCommittedRecord: publish,
            observeMemberVersion: observeMember);
    }


    /// <summary>
    /// Appends one label over the wire, retrying until it lands or until its instance is known to have
    /// decided without it. This is the in-memory linearizability bench's append ported onto real transport:
    /// an undecided attempt catches up before retrying, and an effect that landed after its own attempt gave
    /// up is read out of the witness rather than proposed again, because counting it lost is what makes one
    /// effect land twice.
    /// </summary>
    /// <returns>The completed operation, or <see langword="null"/> when the label was retired unlanded.</returns>
    private static async Task<RegisterOperation?> AppendOverSocketsAsync(
        QuePaxaVersionedRegister<string> register,
        char label,
        CancellationToken cancellationToken)
    {
        long invoked = Stopwatch.GetTimestamp();

        for(int round = 1; round <= MaxWriteRounds; round++)
        {
            //The value is computed here rather than inside a round, which is what makes it a local belief.
            string observed = register.Committed?.Value ?? string.Empty;

            QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync(observed + label, cancellationToken).ConfigureAwait(false);

            if(outcome.Status == QuePaxaWriteStatus.Committed)
            {
                return new RegisterOperation(label, invoked, Stopwatch.GetTimestamp(), observed, outcome.Value!);
            }

            //An undecided attempt learned nothing and a stood-down one did not even send, so both need the
            //catch-up or the next round proposes at the same closed version and stands down again. A
            //superseded attempt has already adopted the winner and needs none.
            if(outcome.Status == QuePaxaWriteStatus.Undecided)
            {
                _ = await register.ReadAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            int at = register.Committed?.Value.IndexOf(label, StringComparison.Ordinal) ?? -1;
            if(at >= 0)
            {
                string chain = register.Committed!.Value;

                return new RegisterOperation(label, invoked, Stopwatch.GetTimestamp(), chain[..at], chain[..(at + 1)]);
            }
        }

        return null;
    }


    /// <summary>
    /// Runs one writer on its own thread: its own register over the shared connections, two appends, and an
    /// optional barrier between them so a nemesis can land mid-history without a clock.
    /// </summary>
    private static Task<RegisterOperation[]> RunSocketWriterAsync(
        SocketVersionedQuePaxaCluster<string> cluster,
        ReplicaId writer,
        char firstLabel,
        char secondLabel,
        Task? betweenAppends,
        Action? firstDone,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            QuePaxaVersionedRegister<string> register = Register(cluster, writer, publish: cluster.PublishAsync, reader: cluster.ResolveReader);

            RegisterOperation? first = await AppendOverSocketsAsync(register, firstLabel, cancellationToken).ConfigureAwait(false);
            firstDone?.Invoke();
            if(betweenAppends is not null)
            {
                await betweenAppends.ConfigureAwait(false);
            }

            RegisterOperation? second = await AppendOverSocketsAsync(register, secondLabel, cancellationToken).ConfigureAwait(false);

            return new[] { first, second }.OfType<RegisterOperation>().ToArray();
        }, cancellationToken);
    }


    /// <summary>
    /// The final value as the hosts hold it, read over each member's own connection: the highest committed
    /// record anywhere is the chosen chain every operation must sit on.
    /// </summary>
    private static async Task<string> WitnessOverSocketsAsync(SocketVersionedQuePaxaCluster<string> cluster, CancellationToken cancellationToken)
    {
        VersionedValue<string>? highest = await HighestHeldAsync(cluster, cancellationToken).ConfigureAwait(false);

        return highest?.Value ?? string.Empty;
    }


    private static async Task<VersionedValue<string>?> HighestHeldAsync(SocketVersionedQuePaxaCluster<string> cluster, CancellationToken cancellationToken)
    {
        VersionedValue<string>? highest = null;
        foreach(ReplicaId member in cluster.Genesis.Members.Select(configured => configured.Replica))
        {
            VersionedValue<string>? held = await cluster.ResolveReader(member)(cancellationToken).ConfigureAwait(false);
            if(held is not null && (highest is null || held.Version > highest.Version))
            {
                highest = held;
            }
        }

        return highest;
    }


    /// <summary>
    /// The distinct lanes one replica proposed on at one version, in the order they first arrived.
    /// </summary>
    /// <remarks>
    /// A proposal key repeats across the steps of one attempt, so the distinct lanes are what count the
    /// attempts.
    /// </remarks>
    private static int[] LanesAt(SocketVersionedQuePaxaCluster<string> cluster, RegisterVersion version, ReplicaId replica)
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


    /// <summary>A store that records every state it is asked to write and completes at once.</summary>
    private sealed class RecordingNodeStore
    {
        /// <summary>Every state written, in the order the host's loop wrote them.</summary>
        public List<QuePaxaVersionedNodeState<string>> States { get; } = [];


        /// <summary>Writes <paramref name="state"/>, which is this store's persist delegate.</summary>
        /// <param name="state">The state to make durable.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A completed task, because nothing here can be slow.</returns>
        public ValueTask PersistAsync(QuePaxaVersionedNodeState<string> state, CancellationToken cancellationToken)
        {
            States.Add(state);

            return ValueTask.CompletedTask;
        }
    }
}
