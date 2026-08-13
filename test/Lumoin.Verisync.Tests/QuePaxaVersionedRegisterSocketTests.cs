using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;
using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned register's integration scenarios over real loopback TCP, one runner-backed recorder host
/// per connection, per the standing rule that a wire-codec protocol is exercised over a real transport and
/// not only in process.
/// </summary>
/// <remarks>
/// <para>
/// Ported here are the ten scenarios below, which are the fast path across three consecutive versions, the
/// two superseded outcomes, the undecided retry on a fresh lane, the two hedging vectors and the zero-delay
/// activation, the dissemination-bound quorum, the version-swapping transport, and the catch-up read. With
/// them goes the bare family's re-delivery after a failed persist, which lives in
/// <see cref="QuePaxaSocketClusterTests"/> beside the other bare-family rounds.
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
            QuePaxaVersionedRegister<string> register = Register(cluster, First, publish: async (committed, token) =>
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
                cluster.Endpoints(),
                First,
                Schedule(),
                ProposalPriority.Cryptographic,
                AttemptsPerRecorder,
                TimeProvider.System,
                readCommittedRecords: cluster.Readers());

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
