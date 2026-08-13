using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;
using System.Buffers;
using System.Collections.Immutable;
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
/// separation from the other side.
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

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);

    /// <summary>
    /// The membership every record in this suite carries, minted from the agreed order the hosts run under.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis([First, Second, Third]);

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
            //assertions elsewhere rest on a counter proven live on this transport.
            for(int host = 0; host < cluster.HostCount; host++)
            {
                Assert.IsGreaterThan(0, cluster.Served[host], $"Host {host} served nothing across three committed versions.");
            }
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
    }


    //The change function records its input because a retry re-proposing the original value would still pass.
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


    //An undecided proposal may be decided later, so treating it as a conflict would abandon a live write. Two
    //of three hosts are partitioned so the quorum is missed while the third still records what it was asked,
    //which is what lets the lanes be read rather than inferred from the attempt count.
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


    //This is the only vector that fails if the stand-down is hoisted out of the delay guard.
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


    //This pins the liveness cost that the single-live-instance rule pays for its safety.
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


    //Counting a reply from another instance would decide on a set that is a minority of the instance it names.
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


    //One honest host settles it because a committed record is a decided fact under the crash-fault model.
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

            VersionedValue<string>? read = await register.ReadAsync(TestContext.CancellationToken).ConfigureAwait(false);

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

            RegisterReadiness behind = await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false);

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

            RegisterReadiness caughtUp = await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false);

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

            RegisterReadiness reached = await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness while every route is up: {Describe(reached)}"));
            Assert.AreEqual(3, reached.Reachable, "A member answered nothing while every route was up, so the reading below would not be the route's doing.");
            Assert.AreEqual(committed.Version, reached.Members.Single(member => member.Member.Equals(Third)).Version, "The member whose route is cut below does not hold the record beforehand, so a later silence would say nothing about reachability.");

            //The host keeps running and keeps the record it learned; what it loses is the route to it.
            cluster.Partition(2);

            RegisterReadiness faulted = await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false);

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

            RegisterReadiness healed = await register.ReadReadinessAsync(TestContext.CancellationToken).ConfigureAwait(false);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the route is restored: {Describe(healed)}"));
            Assert.AreEqual(3, healed.Reachable, "The host behind the restored route did not answer again, so the run cannot say the cut was the endpoints rather than the host.");
            Assert.AreEqual(committed.Version, healed.Members.Single(member => member.Member.Equals(Third)).Version, "The host behind the restored route lost the record it held while nothing could ask it for one.");
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
        }
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


    //The reply is rewritten as bytes rather than as a deserialized object, so the swap crosses the codec that
    //a deployment would meet it at.
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
            writer.WriteString("recorder", root.GetProperty("recorder").GetString());
            writer.WritePropertyName("reply");
            root.GetProperty("reply").WriteTo(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }


    private Task<SocketVersionedQuePaxaCluster<string>> ConnectAsync(QuePaxaLeaderSchedule schedule, TamperReplyPayloadDelegate? tamperReplyPayload = null)
    {
        return SocketVersionedQuePaxaCluster<string>.ConnectAsync(
            schedule,
            HostCount,
            static (writer, value) => writer.WriteStringValue(value),
            static element => element.GetString()!,
            TestContext.CancellationToken,
            tamperReplyPayload: tamperReplyPayload);
    }


    private static QuePaxaVersionedRegister<string> Register(
        SocketVersionedQuePaxaCluster<string> cluster,
        ReplicaId self,
        TimeProvider? clock = null,
        ObserveCommittedVersionDelegate? observe = null,
        TimeSpan? baseDelay = null,
        PublishCommittedRecordDelegate<string>? publish = null,
        ObserveMemberVersionDelegate? observeMember = null)
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
            publishCommittedRecord: publish,
            observeMemberVersion: observeMember);
    }


    //The distinct lanes one replica proposed on at one version, in the order they first arrived. A proposal
    //key repeats across the steps of one attempt, so the distinct lanes are what count the attempts.
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
}
