using Lumoin.Verisync.Core;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The message-driven slice's real gate: agreement over delivery interleavings the synchronous drivers cannot
/// produce. The synchronous harness records at its chosen recorders ATOMICALLY, so one proposer's per-recorder
/// records can never straddle another proposer's step on an overlapping recorder; this bench interleaves at
/// the level of a single request and reaches exactly those schedules.
/// </summary>
/// <remarks>
/// <para>
/// EVERY LAW HERE CARRIES A NAMED DETERMINISTIC REACH PIN, and every law asserts its own reach over a FIXED
/// seed range so the evidence cannot be seed luck. A generated law whose reach pin is absent is not evidence:
/// agreement over a sweep in which nobody ever decided, or in which two proposers never met at one recorder,
/// is a green run that says nothing.
/// </para>
/// <para>
/// EVERY TEST PRINTS ITS SEED, because a failure that cannot be replayed gets marked flaky, and every await of
/// a proposal is bounded by an explicit timeout with its completion asserted, because a proposer that waits
/// for every endpoint rather than for the first quorum hangs instead of failing.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaInterleavingLawTests
{
    /// <summary>
    /// The per-step attempt budget every bench proposer runs with.
    /// </summary>
    /// <remarks>
    /// It must be above one, because a budget of one makes the re-send path unreachable and therefore
    /// untested, and it is what bounds a run against a partitioned recorder.
    /// </remarks>
    private const int AttemptsPerRecorder = 2;

    /// <summary>
    /// Identities from fixed bytes so that A sorts below B by lexicographic byte order.
    /// </summary>
    /// <remarks>
    /// That is load-bearing for the scripted negative and not tidiness: the divergence there holds only while
    /// the losing proposer's lane sorts ABOVE the winner's.
    /// </remarks>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    private static ProposerLane LaneB { get; } = ProposerLane.For(Replica(2));
    private static ProposerLane SecondLaneOfReplicaA { get; } = new(Replica(1), 1);

    private static int[] Seeds { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    private static TimeSpan BenchTimeout { get; } = TimeSpan.FromSeconds(60);


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// B1 AGREEMENT ACROSS SEEDED INTERLEAVINGS. Two proposers, three recorders, one configured leader: no two
    /// decided outcomes carry different values, whatever order the requests and replies are delivered in.
    /// </summary>
    /// <remarks>
    /// The reach half is asserted in the same test over the same fixed seeds — at least one seed in which two
    /// proposers' requests met at one recorder AT THE SAME STEP, and at least one decision overall — because
    /// agreement over a sweep that never contended is indistinguishable from agreement over nothing.
    /// </remarks>
    [TestMethod]
    public async Task NoTwoDecisionsCarryDifferentValuesAcrossSeededInterleavings()
    {
        int decisions = 0;
        int seedsWithContention = 0;
        foreach(int seed in Seeds)
        {
            InterleavedQuePaxaCluster<string> cluster = new(LedRecorders(3, LaneA), seed);
            QuePaxaOutcome<string>[] outcomes = await RunTwoProposersAsync(cluster).ConfigureAwait(false);

            AssertAtMostOneDecidedValue(outcomes, seed);

            bool contended = RequestsMetAtOneRecorderAtOneStep(cluster);
            decisions += outcomes.Count(outcome => outcome.IsDecided);
            seedsWithContention += contended ? 1 : 0;

            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"seed={seed}, decided={outcomes.Count(outcome => outcome.IsDecided)}, requestsDelivered={cluster.DeliveredRequests.Count}, contended={contended}"));
        }

        Assert.IsGreaterThan(0, decisions, "No seed decided anything, so the agreement law held vacuously.");
        Assert.IsGreaterThan(0, seedsWithContention, "No seed put two proposers' requests at one recorder at one step, so nothing was contended.");
    }


    /// <summary>
    /// B1 REACH, DETERMINISTIC AND SCRIPTED. The sweep above asserts its own reach statistically over fixed
    /// seeds; this pin constructs the region with no randomness at all, so the claim that the bench CAN put two
    /// proposers at one recorder in a chosen order does not rest on any seed.
    /// </summary>
    /// <remarks>
    /// The rival's request is delivered to recorder zero first, so the configured leader's own claim arrives
    /// at a recorder that has already served someone else at that step.
    /// </remarks>
    [TestMethod]
    public async Task TheBenchReachesTwoProposersAtOneRecorderInAChosenOrderByConstruction()
    {
        InterleavedQuePaxaCluster<string> cluster = new(LedRecorders(3, LaneA), seed: 0);

        QuePaxaProposer<string> leader = cluster.CreateProposer(LaneA, Source(0, 1), AttemptsPerRecorder);
        QuePaxaProposer<string> rival = cluster.CreateProposer(LaneB, Source(0, 2), AttemptsPerRecorder);

        Task<QuePaxaOutcome<string>> leaderProposal = leader.ProposeAsync(LaneA, "a", TestContext.CancellationToken);
        Task<QuePaxaOutcome<string>> rivalProposal = rival.ProposeAsync(LaneB, "b", TestContext.CancellationToken);

        Assert.IsTrue(cluster.DeliverFirstMatching(message => message.IsRequest && message.Recorder == 0 && message.Proposer == LaneB), "The rival's request to recorder zero was not in flight.");
        Assert.IsTrue(cluster.DeliverFirstMatching(message => message.IsRequest && message.Recorder == 0 && message.Proposer == LaneA), "The leader's request to recorder zero was not in flight.");

        Assert.IsTrue(RequestsMetAtOneRecorderAtOneStep(cluster), "The scripted delivery must reach the contended region or the sweep's reach assertion means nothing.");

        //The rival's reserved claim was declined and the leader's honoured, at the SAME recorder and the same
        //step, which is the state the whole downgrade rule exists to produce.
        PrioritizedProposal<string> firstAtZero = cluster.Node(0).Recorder.Register.First!;

        Assert.AreEqual(ProposalPriority.Lowest, firstAtZero.Key.Priority);
        Assert.AreEqual(LaneB, firstAtZero.Key.Owner);
        Assert.AreEqual(ProposalPriority.Reserved, cluster.Node(0).Recorder.Register.CurrentAggregate!.Key.Priority);

        QuePaxaOutcome<string>[] outcomes = await DrainAsync(cluster, [leaderProposal, rivalProposal]).ConfigureAwait(false);

        AssertAtMostOneDecidedValue(outcomes, cluster.Seed);
        Assert.IsGreaterThan(0, outcomes.Count(outcome => outcome.IsDecided), "The pin must reach a decision or it proves only that nothing happened.");

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"seed={cluster.Seed}, scripted prefix, requestsDelivered={cluster.DeliveredRequests.Count}"));
    }


    /// <summary>
    /// B2 AGREEMENT WITH THREE LANES, two of them lanes of ONE replica.
    /// </summary>
    /// <remarks>
    /// That is the contention width the lane exists to make reachable and that no checked configuration
    /// explored: the checked runs had two proposers, and lanes make three or more concurrent proposer
    /// identities reachable on a three-replica deployment. It is also why the reserved priority is granted to
    /// a lane rather than to a replica — two lanes of the leader's own replica each claiming it would
    /// reproduce the divergence hazard from inside the leader.
    /// </remarks>
    [TestMethod]
    public async Task ThreeConcurrentLanesIncludingTwoOfOneReplicaAgree()
    {
        int decisions = 0;
        foreach(int seed in Seeds)
        {
            InterleavedQuePaxaCluster<string> cluster = new(LedRecorders(3, LaneA), seed);

            QuePaxaProposer<string> first = cluster.CreateProposer(LaneA, Source(seed, 1), AttemptsPerRecorder);
            QuePaxaProposer<string> second = cluster.CreateProposer(SecondLaneOfReplicaA, Source(seed, 2), AttemptsPerRecorder);
            QuePaxaProposer<string> third = cluster.CreateProposer(LaneB, Source(seed, 3), AttemptsPerRecorder);

            QuePaxaOutcome<string>[] outcomes = await DrainAsync(
                cluster,
                [
                    first.ProposeAsync(LaneA, "a", TestContext.CancellationToken),
                    second.ProposeAsync(SecondLaneOfReplicaA, "a2", TestContext.CancellationToken),
                    third.ProposeAsync(LaneB, "b", TestContext.CancellationToken)
                ]).ConfigureAwait(false);

            AssertAtMostOneDecidedValue(outcomes, seed);

            //REACH: all three lanes were observed sending. A law over three proposers of which only two ever
            //reached a recorder is a law over two proposers wearing the wrong name.
            int lanesObserved = cluster.DeliveredRequests.Select(delivered => delivered.Proposer).Distinct().Count();

            Assert.AreEqual(3, lanesObserved, "Fewer than three lanes reached a recorder, so the three-lane width was not exercised.");

            decisions += outcomes.Count(outcome => outcome.IsDecided);

            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"seed={seed}, lanes={lanesObserved}, decided={outcomes.Count(outcome => outcome.IsDecided)}, requestsDelivered={cluster.DeliveredRequests.Count}"));
        }

        Assert.IsGreaterThan(0, decisions, "No seed decided anything, so the three-lane agreement law held vacuously.");
    }


    /// <summary>
    /// B3 AGREEMENT UNDER DUPLICATION. A re-delivered request is permitted exactly because a second IDENTICAL
    /// record is the identity on the recorder: at the recorder's own step the aggregate already dominates the
    /// duplicate's key, so no field would change and the register returns itself.
    /// </summary>
    /// <remarks>
    /// The counter is what makes this evidence for that rule rather than for the stale branch the core already
    /// covered — a duplicate landing BELOW the recorder's step exercises staleness, which is a different rule
    /// with its own tests.
    /// </remarks>
    [TestMethod]
    public async Task AgreementHoldsWhenRequestsAreDuplicated()
    {
        int sameStepDuplicates = 0;
        int idempotentDuplicates = 0;
        int decisions = 0;
        foreach(int seed in Seeds)
        {
            InterleavedQuePaxaCluster<string> cluster = new(LedRecorders(3, LaneA), seed)
            {
                RequestDuplicationPercent = 100
            };

            QuePaxaOutcome<string>[] outcomes = await RunTwoProposersAsync(cluster).ConfigureAwait(false);

            AssertAtMostOneDecidedValue(outcomes, seed);

            //Every same-step duplicate left its recorder reference-identical, which is the identity property
            //stated as an equality rather than as an inequality: one duplicate that changed something would
            //break it.
            Assert.AreEqual(cluster.SameStepDuplicatesDelivered, cluster.IdempotentDuplicatesDelivered, "A same-step duplicate changed the recorder, so a re-delivery is not the identity.");

            sameStepDuplicates += cluster.SameStepDuplicatesDelivered;
            idempotentDuplicates += cluster.IdempotentDuplicatesDelivered;
            decisions += outcomes.Count(outcome => outcome.IsDecided);

            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"seed={seed}, sameStepDuplicates={cluster.SameStepDuplicatesDelivered}, idempotentDuplicates={cluster.IdempotentDuplicatesDelivered}, decided={outcomes.Count(outcome => outcome.IsDecided)}"));
        }

        Assert.IsGreaterThan(0, decisions, "No seed decided anything, so the duplication law held vacuously.");
        Assert.IsGreaterThan(0, sameStepDuplicates, "No duplicate landed at its recorder's own step, so the law only exercised the stale branch.");
        Assert.IsGreaterThan(0, idempotentDuplicates, "No duplicate was shown to have left its recorder unchanged.");
    }


    /// <summary>
    /// B3 REACH, DETERMINISTIC AND SCRIPTED. The sweep's counters depend on where the pump happened to place a
    /// duplicate; this pin places one by hand, immediately after its original, so the same-step region is
    /// reached by construction and the recorder instance is compared directly rather than through a counter.
    /// </summary>
    [TestMethod]
    public async Task TheDuplicationBenchReachesTheSameStepIdempotentDuplicateByConstruction()
    {
        InterleavedQuePaxaCluster<string> cluster = new(LeaderlessRecorders(3), seed: 0)
        {
            RequestDuplicationPercent = 100
        };

        QuePaxaProposer<string> proposer = cluster.CreateProposer(LaneA, Source(0, 1), attemptsPerRecorder: 1);
        Task<QuePaxaOutcome<string>> proposal = proposer.ProposeAsync(null, "a", TestContext.CancellationToken);

        Assert.IsTrue(cluster.DeliverFirstMatching(message => message.IsRequest && message.Recorder == 0 && !message.IsDuplicate), "The original request to recorder zero was not in flight.");

        QuePaxaRecorder<string> afterOriginal = cluster.Node(0).Recorder;

        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, afterOriginal.Step);
        Assert.IsTrue(cluster.DeliverFirstMatching(message => message.IsRequest && message.Recorder == 0 && message.IsDuplicate), "The duplicate of the original request was not enqueued.");

        //THE IDENTITY, OBSERVED DIRECTLY. The duplicate reached a recorder standing at the duplicate's own
        //step and the recorder came out reference-identical, so a retransmission on a lossy link costs no
        //durable write at all.
        Assert.AreSame(afterOriginal, cluster.Node(0).Recorder);
        Assert.AreEqual(1, cluster.SameStepDuplicatesDelivered);
        Assert.AreEqual(1, cluster.IdempotentDuplicatesDelivered);

        QuePaxaOutcome<string>[] outcomes = await DrainAsync(cluster, [proposal]).ConfigureAwait(false);

        Assert.IsTrue(outcomes[0].IsDecided, "The pin must reach a decision or it proves only that a duplicate was inert.");

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"seed={cluster.Seed}, scripted duplicate, sameStepDuplicates={cluster.SameStepDuplicatesDelivered}, idempotentDuplicates={cluster.IdempotentDuplicatesDelivered}"));
    }


    /// <summary>
    /// B4 AGREEMENT AND PROGRESS UNDER A PARTITIONED MINORITY. A quorum of two out of three still decides while
    /// the third recorder loses every message in both directions.
    /// </summary>
    /// <remarks>
    /// THE PROGRESS HALF IS THE REACH PIN and is asserted per seed rather than in aggregate: without it, the
    /// agreement half passes vacuously exactly when the partition stopped everyone, which is the failure this
    /// law is meant to detect. Termination rests on the bounded attempt budget — the partitioned recorder's
    /// endpoint faults, the proposer re-sends, and the budget is what stops that being an infinite regress.
    /// </remarks>
    [TestMethod]
    public async Task AQuorumDecidesWhileAMinorityIsPartitionedThroughout()
    {
        foreach(int seed in Seeds)
        {
            InterleavedQuePaxaCluster<string> cluster = new(LedRecorders(3, LaneA), seed);
            cluster.Partition(2);

            QuePaxaOutcome<string>[] outcomes = await RunTwoProposersAsync(cluster).ConfigureAwait(false);

            AssertAtMostOneDecidedValue(outcomes, seed);

            int decided = outcomes.Count(outcome => outcome.IsDecided);
            int servedByThePartitioned = cluster.DeliveredRequests.Count(delivered => delivered.Recorder == 2);

            Assert.IsGreaterThan(0, decided, string.Create(CultureInfo.InvariantCulture, $"Nothing decided at seed {seed}, so the agreement half of the partition law is vacuous."));
            Assert.AreEqual(0, servedByThePartitioned, "The partitioned recorder served a request, so the partition was not in force.");

            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"seed={seed}, partitioned=2, decided={decided}, requestsDelivered={cluster.DeliveredRequests.Count}"));
        }
    }


    /// <summary>
    /// B5 THE MISCONFIGURED CLUSTER IS THE NEGATIVE, AND IT ASSERTS THAT A DIVERGENCE IS REACHABLE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A reader finding a test that asserts two decisions carrying different values will otherwise assume it
    /// is broken: it pins the HAZARD rather than the fix, in the idiom of the synchronous suite's own
    /// disagreeing-recorders negative, and what it costs a deployment is exactly what the downgrade rule buys
    /// — the recorders must agree on the configured leader.
    /// </para>
    /// <para>
    /// IT IS SCRIPTED AND USES NO SEED, so it cannot be flaky. A &lt; B is load-bearing: when the fast path is
    /// refused, the fall-through takes the GREATEST first proposal, two reserved-priority proposals tie on
    /// priority, and the proposer identifier settles it. With A below B the fall-through hands B its OWN
    /// proposal and B decides its own value against the value A already decided; with the order reversed the
    /// same schedule converges.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task RecordersConfiguredWithDifferentLeadersDivergeUnderAScriptedDelivery()
    {
        Assert.IsTrue(LaneA < LaneB, "The divergence this test pins exists only while A sorts below B.");

        //Recorders zero and two are configured with A and recorder one with B, which is the deployment
        //failure an agreed configuration exists to prevent.
        QuePaxaRecorder<string>[] recorders =
        [
            QuePaxaRecorder<string>.LedBy(LaneA),
            QuePaxaRecorder<string>.LedBy(LaneB),
            QuePaxaRecorder<string>.LedBy(LaneA)
        ];
        InterleavedQuePaxaCluster<string> cluster = new(recorders, seed: 0);

        //Every send below is a phase-zero step with a leadership claim, or a phase-one or phase-two step, so
        //no priority is ever drawn; a source that throws proves the whole scenario is constructed.
        ProposalPrioritySourceDelegate never = static () => throw new InvalidOperationException("The scripted hazard draws no priority.");

        QuePaxaProposer<string> believingA = cluster.CreateProposer(LaneA, never, attemptsPerRecorder: 1);
        Task<QuePaxaOutcome<string>> proposalA = believingA.ProposeAsync(LaneA, "a", TestContext.CancellationToken);

        //A's quorum is recorders zero and two, both configured with A, so both honour its reserved claim and
        //the gather is uniform.
        DeliverExchange(cluster, LaneA, 0, 2);

        QuePaxaOutcome<string> outcomeA = await AwaitProposalAsync(proposalA).ConfigureAwait(false);

        Assert.IsTrue(outcomeA.IsDecided);
        Assert.AreEqual("a", outcomeA.Value);
        Assert.AreEqual(LaneA, outcomeA.DecidedBy);
        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, outcomeA.DecidedAt);

        QuePaxaProposer<string> believingB = cluster.CreateProposer(LaneB, never, attemptsPerRecorder: 1);
        Task<QuePaxaOutcome<string>> proposalB = believingB.ProposeAsync(LaneB, "b", TestContext.CancellationToken);

        //B's quorum is recorders zero and one, delivered in the OPPOSITE order to A's overlap: recorder zero
        //declined B's claim and still holds A's reserved first, and recorder one honoured B's.
        DeliverExchange(cluster, LaneB, 0, 1);

        PrioritizedProposal<string> declinedAtZero = cluster.Node(0).Recorder.Register.First!;
        PrioritizedProposal<string> honouredAtOne = cluster.Node(1).Recorder.Register.First!;

        Assert.AreEqual(ProposalPriority.Reserved, declinedAtZero.Key.Priority);
        Assert.AreEqual(LaneA, declinedAtZero.Key.Owner);
        Assert.AreEqual(ProposalPriority.Reserved, honouredAtOne.Key.Priority);
        Assert.AreEqual(LaneB, honouredAtOne.Key.Owner);
        Assert.AreNotEqual(declinedAtZero, honouredAtOne);

        //Two reserved-priority proposals alive in one instance at one step: the state the downgrade rule
        //exists to make unreachable, and the state the agreement hazard is built from.
        DeliverExchange(cluster, LaneB, 0, 1);
        DeliverExchange(cluster, LaneB, 0, 1);

        QuePaxaOutcome<string> outcomeB = await AwaitProposalAsync(proposalB).ConfigureAwait(false);

        Assert.IsTrue(outcomeB.IsDecided);
        Assert.AreEqual("b", outcomeB.Value);
        Assert.AreEqual(LaneB, outcomeB.DecidedBy);
        Assert.AreEqual(RecorderStep.FromRoundAndPhase(1, 2), outcomeB.DecidedAt);

        //THE DIVERGENCE. Two decisions, two values, one instance.
        Assert.AreNotEqual(outcomeA.Value, outcomeB.Value);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"seed={cluster.Seed} (scripted, unused), decidedA={outcomeA.Value}, decidedAtA={outcomeA.DecidedAt.Value}, decidedB={outcomeB.Value}, decidedAtB={outcomeB.DecidedAt.Value}"));
    }


    /// <summary>
    /// B6 REPLAY DETERMINISM. One seed run twice produces identical delivery traces, which is what makes a
    /// printed seed worth printing: a failing interleaving replays anywhere rather than being reported as
    /// flaky.
    /// </summary>
    /// <remarks>
    /// The generator is xorshift rather than the platform's, for the same reason.
    /// </remarks>
    [TestMethod]
    public async Task OneSeedRunTwiceProducesTheIdenticalDeliveryTrace()
    {
        const int seed = 20_260_807;

        InterleavedQuePaxaCluster<string> first = new(LedRecorders(3, LaneA), seed);
        QuePaxaOutcome<string>[] firstOutcomes = await RunTwoProposersAsync(first).ConfigureAwait(false);

        InterleavedQuePaxaCluster<string> second = new(LedRecorders(3, LaneA), seed);
        QuePaxaOutcome<string>[] secondOutcomes = await RunTwoProposersAsync(second).ConfigureAwait(false);

        Assert.AreSequenceEqual(first.DeliveryTrace.ToList(), second.DeliveryTrace.ToList());
        Assert.AreSequenceEqual(
            firstOutcomes.Select(outcome => $"{outcome.IsDecided}:{outcome.Value}:{outcome.DecidedAt.Value}:{outcome.Steps}").ToList(),
            secondOutcomes.Select(outcome => $"{outcome.IsDecided}:{outcome.Value}:{outcome.DecidedAt.Value}:{outcome.Steps}").ToList());

        //A trace of one message would replay trivially, so the length is asserted rather than assumed.
        Assert.IsGreaterThan(1, first.DeliveryTrace.Count, "A trace this short replays by accident rather than by determinism.");

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"seed={seed}, traceLength={first.DeliveryTrace.Count}, decided={firstOutcomes.Count(outcome => outcome.IsDecided)}"));
    }


    /// <summary>
    /// Delivers one proposer's exchange with the named recorders: every request first, then every reply.
    /// </summary>
    /// <remarks>
    /// The filter names the PROPOSER as well as the recorder, because a script that asked only for "the next
    /// message at recorder one" would pick up whatever another proposer left in flight there and would quietly
    /// stop being the scenario it is named for.
    /// </remarks>
    private static void DeliverExchange(InterleavedQuePaxaCluster<string> cluster, ProposerLane proposer, params int[] recorders)
    {
        foreach(int recorder in recorders)
        {
            Assert.IsTrue(
                cluster.DeliverFirstMatching(message => message.IsRequest && message.Recorder == recorder && message.Proposer == proposer),
                "The script expected a request in flight and found none.");
        }

        foreach(int recorder in recorders)
        {
            Assert.IsTrue(
                cluster.DeliverFirstMatching(message => !message.IsRequest && message.Recorder == recorder && message.Proposer == proposer),
                "The script expected a reply in flight and found none.");
        }
    }


    /// <summary>
    /// Two proposers that each believe they lead, against recorders that agree only one of them does.
    /// </summary>
    /// <remarks>
    /// That is the model's two-believed-leaders-against-one-configured-leader shape and the state the green
    /// configurations reach constantly.
    /// </remarks>
    private async Task<QuePaxaOutcome<string>[]> RunTwoProposersAsync(InterleavedQuePaxaCluster<string> cluster)
    {
        QuePaxaProposer<string> first = cluster.CreateProposer(LaneA, Source(cluster.Seed, 1), AttemptsPerRecorder);
        QuePaxaProposer<string> second = cluster.CreateProposer(LaneB, Source(cluster.Seed, 2), AttemptsPerRecorder);

        return await DrainAsync(
            cluster,
            [
                first.ProposeAsync(LaneA, "a", TestContext.CancellationToken),
                second.ProposeAsync(LaneB, "b", TestContext.CancellationToken)
            ]).ConfigureAwait(false);
    }


    private async Task<QuePaxaOutcome<string>[]> DrainAsync(InterleavedQuePaxaCluster<string> cluster, Task<QuePaxaOutcome<string>>[] proposals)
    {
        cluster.RunToQuiescence();

        QuePaxaOutcome<string>[] outcomes = await Task.WhenAll(proposals).WaitAsync(BenchTimeout, TestContext.CancellationToken).ConfigureAwait(false);

        foreach(Task<QuePaxaOutcome<string>> proposal in proposals)
        {
            Assert.IsTrue(proposal.IsCompletedSuccessfully, "A proposal did not complete under its timeout, so the run left a proposer waiting on a quorum it already had.");
        }

        return outcomes;
    }


    private async Task<QuePaxaOutcome<string>> AwaitProposalAsync(Task<QuePaxaOutcome<string>> proposal)
    {
        QuePaxaOutcome<string> outcome = await proposal.WaitAsync(BenchTimeout, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(proposal.IsCompletedSuccessfully, "The proposal did not complete under its timeout.");

        return outcome;
    }


    private static void AssertAtMostOneDecidedValue(IReadOnlyList<QuePaxaOutcome<string>> outcomes, int seed)
    {
        string? decided = null;
        foreach(QuePaxaOutcome<string> outcome in outcomes)
        {
            if(!outcome.IsDecided)
            {
                continue;
            }

            if(decided is null)
            {
                decided = outcome.Value;

                continue;
            }

            Assert.AreEqual(decided, outcome.Value, string.Create(CultureInfo.InvariantCulture, $"Two decisions carried different values at seed {seed}."));
        }
    }


    /// <summary>
    /// Two proposers' requests met at one recorder AT ONE STEP when that recorder served them back to back.
    /// </summary>
    /// <remarks>
    /// A recorder that served every request of one proposer and only then the other's is a sequential run, not
    /// a contended one, and a law swept over sequential runs says nothing about interleaving.
    /// </remarks>
    private static bool RequestsMetAtOneRecorderAtOneStep(InterleavedQuePaxaCluster<string> cluster)
    {
        Dictionary<int, InterleavedQuePaxaCluster<string>.DeliveredRequest> previousAtRecorder = new();
        foreach(InterleavedQuePaxaCluster<string>.DeliveredRequest delivered in cluster.DeliveredRequests)
        {
            if(previousAtRecorder.TryGetValue(delivered.Recorder, out InterleavedQuePaxaCluster<string>.DeliveredRequest? previous)
                && previous.Proposer != delivered.Proposer
                && previous.Step == delivered.Step)
            {
                return true;
            }

            previousAtRecorder[delivered.Recorder] = delivered;
        }

        return false;
    }


    /// <summary>
    /// A distinct priority stream per proposer per seed, so a run is reproducible and two proposers never draw
    /// the identical sequence.
    /// </summary>
    private static ProposalPrioritySourceDelegate Source(int seed, int proposer)
    {
        SeededPrioritySource source = new(((ulong)(uint)seed * 1_000UL) + (ulong)proposer);

        return source.Next;
    }


    private static QuePaxaRecorder<string>[] LedRecorders(int count, ProposerLane leader)
    {
        var recorders = new QuePaxaRecorder<string>[count];
        for(int i = 0; i < count; i++)
        {
            recorders[i] = QuePaxaRecorder<string>.LedBy(leader);
        }

        return recorders;
    }


    private static QuePaxaRecorder<string>[] LeaderlessRecorders(int count)
    {
        var recorders = new QuePaxaRecorder<string>[count];
        for(int i = 0; i < count; i++)
        {
            recorders[i] = QuePaxaRecorder<string>.Leaderless;
        }

        return recorders;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// Xorshift64 rather than the cryptographic source: every priority in a run is reproducible from its seed,
    /// so a failing interleaving replays the identical draws on any runtime.
    /// </summary>
    private sealed class SeededPrioritySource
    {
        private ulong state;

        public SeededPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        public ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;

            //The two reserved endpoints are excluded, so the source honours the delegate's contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }
}
