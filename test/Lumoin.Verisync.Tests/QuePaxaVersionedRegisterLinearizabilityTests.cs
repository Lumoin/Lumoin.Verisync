using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned register's linearizability suite. Concurrent writers append single characters to one
/// register over a scheduled transport, and the final committed value is the witness their history is checked
/// against.
/// </summary>
/// <remarks>
/// <para>
/// THE INSTRUMENT IS AN APPEND-LOG WITNESS CHECKER AND NOT A GENERAL ONE. Because the value is an append log,
/// the final value is a complete witness of the order operations took effect in, so no permutation search is
/// needed. What the suite certifies is therefore linearizability of append operations under a supplied
/// witness, which is narrower than linearizability of the register for arbitrary values.
/// </para>
/// <para>
/// THE WITNESS IS NOT A READ. <see cref="QuePaxaVersionedRegister{TValue}.ReadAsync"/> takes no consensus step
/// and is explicitly not linearizable, so the witness is the highest record the hosts hold rather than
/// anything a register reports. <c>ReadAsync</c> appears here only where a writer catches up, never where the
/// history is judged.
/// </para>
/// <para>
/// AN OBSERVED VALUE IS A LOCAL BELIEF. QuePaxa decides among proposed values rather than composing an update
/// inside the round, so a writer computes what to propose from what its own register believes committed. The
/// <see cref="RegisterOperation.Observed"/> a history carries is that belief and not a quorum recovery, which
/// is a weaker fact than the CasPaxos benches record under the same field name.
/// </para>
/// <para>
/// AN EFFECT CAN LAND AFTER ITS OWN ATTEMPT GAVE UP. An undecided attempt's proposal may be carried by
/// another proposer and decided later, so counting it lost and writing the label again is what would make one
/// effect land twice. The append log makes the landing observable, so a writer reads the effect out of the
/// witness instead of re-proposing blind. What it does NOT need is a rule against reusing the label: the
/// register proposes at the version its own committed state derives, so a retry lands in the instance the
/// undecided attempt used or in an older one every host declines, and one instance decides one value.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaVersionedRegisterLinearizabilityTests
{
    private const int AttemptsPerRecorder = 3;
    private const int HostCount = 3;
    private const int MaxWriteRounds = 24;
    private const int SeedCount = 20;
    private const int EventBound = 20_000;

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);

    /// <summary>
    /// The membership every record in this suite carries, minted from the agreed order the hosts run under.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third));

    private static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(40);


    [TestMethod]
    public async Task ConcurrentWritersLinearizeAcrossSeeds()
    {
        int hedgedRuns = 0;
        for(int seed = 1; seed <= SeedCount; seed++)
        {
            InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence(writers);
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string witness = Witness(cluster);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];

            //The count is asserted before the checker runs, because a short history satisfies the checker's
            //real-time clause vacuously and would report a green for a run that lost operations.
            Assert.HasCount(6, history, $"seed={seed}: an operation did not land, so the history is short and the checker would pass on less than it was asked to certify.");
            Assert.AreEqual(history.Count, witness.Length, $"seed={seed}: the witness '{witness}' carries a different number of effects than the history.");

            AppendRegisterChecker.AssertLinearizable(history, witness);

            if(cluster.TimersFired > 0)
            {
                hedgedRuns++;
            }

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed={seed}, witness={witness}, events={cluster.DeliveryTrace.Count}, timersFired={cluster.TimersFired}, learned={cluster.DisseminationsLearned}"));
        }

        //A suite where no writer ever waited would certify the unhedged protocol only, whatever its name says.
        Assert.IsGreaterThan(0, hedgedRuns, "No run fired a hedging timer, so the delayed-writer path was never exercised.");
    }


    [TestMethod]
    public async Task WritersLinearizeWhenAMinorityIsPartitioned()
    {
        //A quorum is two of three, so one unreachable host must cost liveness nothing. It does cost
        //dissemination, which is what makes the remaining hosts the only witnesses.
        for(int seed = 1; seed <= SeedCount; seed++)
        {
            InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);
            cluster.Partition(2);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence(writers);
            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string witness = Witness(cluster);

            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];

            Assert.HasCount(4, history, $"seed={seed}: a write failed against a reachable majority, which is a liveness defect rather than a safety one.");
            Assert.AreEqual(history.Count, witness.Length, $"seed={seed}: the witness '{witness}' carries a different number of effects than the history.");

            //Dissemination is a scheduled delivery and not an inline call, so an unreachable host learns
            //nothing. A bench that published inline would leave this host current and hide the cost the
            //single-live-instance rule charges.
            Assert.IsNull(cluster.Host(2).Committed, $"seed={seed}: the partitioned host learned a record, so dissemination did not travel over the transport.");

            AppendRegisterChecker.AssertLinearizable(history, witness);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed={seed}, witness={witness}, events={cluster.DeliveryTrace.Count}"));
        }
    }


    [TestMethod]
    public async Task AnEmptyQueueIsNotQuiescenceWhileAWriterIsParked()
    {
        //This is the law the quiescence rule exists for. A writer waiting out its hedging delay has put
        //nothing on the transport, so a pump that stopped at an empty queue would return with that writer
        //still parked and its operations missing from the history.
        InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed: 7);

        Task<RegisterOperation[]>[] writers =
        [
            RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
            RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
            RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
        ];

        bool reachedAnEmptyQueueWithAnArmedTimer = false;
        int events = 0;
        while(events < EventBound && cluster.Step())
        {
            events++;
            if(cluster.PendingCount == 0 && cluster.ArmedTimerCount > 0)
            {
                reachedAnEmptyQueueWithAnArmedTimer = true;
            }
        }

        Assert.IsLessThan(EventBound, events, "The run did not quiesce inside its bound, which reports a livelock rather than hanging the suite.");
        Assert.IsTrue(reachedAnEmptyQueueWithAnArmedTimer, "The run never emptied its queue with a timer still armed, so it does not distinguish a pump that stops at an empty queue from one that does not.");

        //REACHING THAT STATE IS NOT THE LAW; SURVIVING IT IS. A pump that stopped at an empty queue would set
        //the flag above too, on the step before it stopped, and would then leave these writers parked forever.
        //Awaiting them without this check would hang the suite instead of failing it.
        Assert.IsTrue(writers.All(writer => writer.IsCompleted), "The schedule emptied with a writer still parked, so an empty queue was treated as quiescence and the history would be missing that writer's operations.");

        RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
        List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];

        Assert.HasCount(6, history);
        AppendRegisterChecker.AssertLinearizable(history, Witness(cluster));

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed=7, events={events}, timersFired={cluster.TimersFired}"));
    }


    [TestMethod]
    public async Task TwoWritersRequestsInterleaveAtOneHost()
    {
        //A reach pin rather than a law about the protocol: without it every linearizability green above could
        //come from writers that ran one after the other, which certifies nothing about contention.
        bool interleaved = false;
        for(int seed = 1; seed <= SeedCount && !interleaved; seed++)
        {
            InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence(writers);
            _ = await Task.WhenAll(writers).ConfigureAwait(false);

            for(int host = 0; host < cluster.HostCount && !interleaved; host++)
            {
                ReplicaId[] senders = [.. cluster.DeliveredCalls.Where(call => call.Host == host).Select(call => call.Sender)];
                interleaved = ReturnsToAnEarlierSender(senders);
                if(interleaved)
                {
                    TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed={seed}, host={host}, senders={senders.Length}, distinct={senders.Distinct().Count()}"));
                }
            }
        }

        Assert.IsTrue(interleaved, "No host saw one writer's requests resume after another writer's, so the bench never produced genuine contention at a host.");
    }


    [TestMethod]
    public async Task ARunReplaysFromItsSeed()
    {
        (IReadOnlyList<string> firstTrace, string firstWitness) = await RunAndDescribeAsync(seed: 11).ConfigureAwait(false);
        (IReadOnlyList<string> secondTrace, string secondWitness) = await RunAndDescribeAsync(seed: 11).ConfigureAwait(false);

        Assert.AreSequenceEqual(firstTrace.ToList(), secondTrace.ToList());
        Assert.AreEqual(firstWitness, secondWitness);

        //A trace of one event would replay by accident, so its length is asserted rather than assumed.
        Assert.IsGreaterThan(1, firstTrace.Count, "A trace this short replays by accident rather than by determinism.");

        (IReadOnlyList<string> otherTrace, _) = await RunAndDescribeAsync(seed: 12).ConfigureAwait(false);

        //Determinism is worth nothing if the seed does not change anything, which is what a bench whose
        //scheduler had stopped sampling would look like.
        Assert.AreNotEqual(string.Join("|", firstTrace), string.Join("|", otherTrace), "Two seeds produced the identical delivery order, so the scheduler is not exploring interleavings.");

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed=11 replayed {firstTrace.Count} events to witness {firstWitness}"));
    }


    [TestMethod]
    public async Task ATimerDueBeforeTheNextDeliveryRunsBeforeIt()
    {
        //Ordering a due timer against the next delivery is what keeps a hedging delay the length the schedule
        //asked for. A pump that drained its queue first would fire every timer at an instant already past its
        //deadline, which does not break agreement and does make the hedge longer than it was configured to be,
        //so the bench would measure a schedule nobody chose.
        int firedUnderTraffic = 0;
        int firedLate = 0;
        for(int seed = 1; seed <= SeedCount; seed++)
        {
            InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence(writers);
            _ = await Task.WhenAll(writers).ConfigureAwait(false);

            firedUnderTraffic += cluster.TimersFiredUnderTraffic;
            firedLate += cluster.TimersFiredLate;
        }

        //Without this the law passes on runs whose queue happened to empty before any deadline, which is the
        //one shape that pins nothing about the ordering.
        Assert.IsGreaterThan(0, firedUnderTraffic, "No timer fired while messages were in flight, so no run reached the situation the ordering governs.");
        Assert.AreEqual(0, firedLate, $"{firedLate} timers fired at an instant already past their deadline, so a delivery was ordered ahead of a timer that was due before it.");

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"timersUnderTraffic={firedUnderTraffic}, timersLate={firedLate}"));
    }


    [TestMethod]
    public async Task TheSenderIsCapturedAtTheEndpointRatherThanReadOffTheProposalKey()
    {
        //A proposer that adopted another lane's template carries that lane's owner, and the key is never
        //restamped, so the replica that sent a request and the replica that owns the key it carries are two
        //different facts. A bench that read the sender off the key would attribute a carried proposal to the
        //replica whose template it carries and under-report who was actually talking to a host.
        int carried = 0;
        for(int seed = 1; seed <= SeedCount; seed++)
        {
            InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
            ];

            cluster.RunToQuiescence(writers);
            _ = await Task.WhenAll(writers).ConfigureAwait(false);

            carried += cluster.DeliveredCalls.Count(call => call.Sender != call.KeyOwner);
        }

        Assert.IsGreaterThan(0, carried, "No request carried a proposal key owned by a replica other than its sender, so this bench cannot tell a captured sender from a derived one.");

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"requests whose key owner is not the sender: {carried}"));
    }


    [TestMethod]
    public async Task WritersLinearizeAcrossAMidRunPartitionAndHeal()
    {
        //A partition that appears mid-attempt is what leaves an attempt undecided with its instance still
        //open, which is the one situation where a label may not be re-proposed: the proposal it made can still
        //be carried and decided once the partition heals.
        for(int seed = 1; seed <= SeedCount; seed++)
        {
            InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);

            Task<RegisterOperation[]>[] writers =
            [
                RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
                RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
                RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
            ];

            int events = 0;
            while(events < EventBound && cluster.Step())
            {
                events++;
                if(events == 12)
                {
                    cluster.Partition(1);
                }

                if(events == 48)
                {
                    cluster.Heal(1);
                }
            }

            Assert.IsLessThan(EventBound, events, $"seed={seed}: the run did not quiesce inside its bound.");
            Assert.IsTrue(writers.All(writer => writer.IsCompleted), $"seed={seed}: the schedule emptied with a writer still parked.");

            RegisterOperation[][] results = await Task.WhenAll(writers).ConfigureAwait(false);
            string witness = Witness(cluster);
            List<RegisterOperation> history = [.. results.SelectMany(operations => operations)];

            Assert.AreEqual(history.Count, witness.Length, $"seed={seed}: the witness '{witness}' carries a different number of effects than the history, so an effect landed twice or outside it.");
            AppendRegisterChecker.AssertLinearizable(history, witness);

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed={seed}, witness={witness}, operations={history.Count}, events={events}"));
        }
    }


    [TestMethod]
    public void AQuiescentScheduleWithAnIncompleteClientIsReportedRatherThanReturned()
    {
        //The one rule this law fires and nothing else: a client parked on a seam this cluster does not drive
        //leaves the schedule empty while its operations are still missing, and returning there would hand the
        //checker a short history that passes its real-time clause vacuously.
        InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed: 3);
        TaskCompletionSource neverCompletes = new();

        InvalidOperationException reported = Assert.ThrowsExactly<InvalidOperationException>(() => cluster.RunToQuiescence([neverCompletes.Task]));

        Assert.Contains("1 of 1 clients incomplete", reported.Message);
        Assert.AreEqual(0, cluster.PendingCount);
        Assert.AreEqual(0, cluster.ArmedTimerCount);
    }


    [TestMethod]
    public void AHostThatCanServeNoVersionFaultsTheRunRatherThanLookingUnreachable()
    {
        //A host whose committed record leaves no version to follow is a defect and not a decline, so it must
        //not be folded into the transport's fault path. A bench that caught every exception from a host would
        //report this as a missed quorum, and the run would come back merely undecided.
        VersionedValue<string> exhausted = new(RegisterVersion.MaxValue, First, Configuration, "x");
        InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed: 5, exhausted);

        QuePaxaVersionedRegister<string> register = cluster.CreateRegister(First, AttemptsPerRecorder);
        Task<QuePaxaWriteOutcome<string>> write = register.TryWriteAsync("a", TestContext.CancellationToken);

        ConsensusRefusedException reported = Assert.ThrowsExactly<ConsensusRefusedException>(() => cluster.RunToQuiescence([write]));

        Assert.AreEqual(ConsensusRefusal.VersionRangeSpent, reported.Refusal);
    }


    private async Task<(IReadOnlyList<string> Trace, string Witness)> RunAndDescribeAsync(int seed)
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(Schedule(), HostCount, seed);

        Task<RegisterOperation[]>[] writers =
        [
            RunWriterAsync(cluster, First, 'A', 'B', TestContext.CancellationToken),
            RunWriterAsync(cluster, Second, 'C', 'D', TestContext.CancellationToken),
            RunWriterAsync(cluster, Third, 'E', 'F', TestContext.CancellationToken)
        ];

        cluster.RunToQuiescence(writers);
        _ = await Task.WhenAll(writers).ConfigureAwait(false);

        return (cluster.DeliveryTrace, Witness(cluster));
    }


    /// <summary>
    /// Whether one host saw a writer's request after another writer's had come between two of them, which is
    /// interleaving rather than two runs placed end to end.
    /// </summary>
    private static bool ReturnsToAnEarlierSender(ReplicaId[] senders)
    {
        for(int index = 2; index < senders.Length; index++)
        {
            for(int earlier = 0; earlier < index - 1; earlier++)
            {
                if(senders[earlier] == senders[index] && senders[earlier] != senders[index - 1])
                {
                    return true;
                }
            }
        }

        return false;
    }


    private static string Witness(InterleavedVersionedQuePaxaCluster<string> cluster) => cluster.HighestCommitted?.Value ?? string.Empty;


    private static async Task<RegisterOperation[]> RunWriterAsync(
        InterleavedVersionedQuePaxaCluster<string> cluster,
        ReplicaId writer,
        char firstLabel,
        char secondLabel,
        CancellationToken cancellationToken)
    {
        QuePaxaVersionedRegister<string> register = cluster.CreateRegister(writer, AttemptsPerRecorder);

        RegisterOperation? first = await AppendAsync(cluster, register, firstLabel, cancellationToken).ConfigureAwait(false);
        RegisterOperation? second = await AppendAsync(cluster, register, secondLabel, cancellationToken).ConfigureAwait(false);

        return [.. new[] { first, second }.OfType<RegisterOperation>()];
    }


    /// <summary>
    /// Appends one label, retrying until it lands or until its instance is known to have decided without it.
    /// </summary>
    /// <returns>The completed operation, or <see langword="null"/> when the label was retired unlanded.</returns>
    private static async Task<RegisterOperation?> AppendAsync(
        InterleavedVersionedQuePaxaCluster<string> cluster,
        QuePaxaVersionedRegister<string> register,
        char label,
        CancellationToken cancellationToken)
    {
        long invoked = cluster.Tick();

        for(int round = 1; round <= MaxWriteRounds; round++)
        {
            //The value is computed here rather than inside a round, which is what makes it a local belief.
            string observed = register.Committed?.Value ?? string.Empty;

            QuePaxaWriteOutcome<string> outcome = await register.TryWriteAsync(observed + label, cancellationToken).ConfigureAwait(false);

            if(outcome.Status == QuePaxaWriteStatus.Committed)
            {
                return new RegisterOperation(label, invoked, cluster.Tick(), observed, outcome.Value!);
            }

            //An undecided attempt learned nothing, and a stood-down one did not even send. Both need the
            //catch-up or the next round proposes at the same closed version and stands down again. A
            //superseded attempt has already adopted the winner and needs none.
            if(outcome.Status == QuePaxaWriteStatus.Undecided)
            {
                _ = await register.ReadAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            //AN EFFECT CAN LAND AFTER ITS OWN ATTEMPT GAVE UP, carried by another proposer and decided in the
            //instance that attempt proposed in. Counting it lost and proposing the label again is what makes
            //one effect land twice, so the operation is read out of the witness instead. The value it wrote
            //is taken from the chain rather than from what this round proposed, because an earlier round's
            //proposal may be the one that landed.
            int at = register.Committed?.Value.IndexOf(label, StringComparison.Ordinal) ?? -1;
            if(at >= 0)
            {
                string chain = register.Committed!.Value;

                return new RegisterOperation(label, invoked, cluster.Tick(), chain[..at], chain[..(at + 1)]);
            }

            //RE-PROPOSING THE LABEL IS SAFE HERE, and the reason is the register's own version derivation
            //rather than anything this bench does. The next attempt proposes at NextVersion, which is derived
            //from what this register has committed, so it lands in the instance the undecided attempt used or
            //in an older one that every host declines. One instance decides one value, so a label cannot land
            //twice however many lanes carry it. A guard against reuse would therefore never fire, and a guard
            //no run can fire is not evidence.
        }

        return null;
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
