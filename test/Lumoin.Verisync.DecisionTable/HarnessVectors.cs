using System.Collections.Immutable;
using System.Globalization;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The harness's own verification suite: what has to hold before any number this harness produces is worth
/// reading.
/// </summary>
/// <remarks>
/// <para>
/// Three families of vector, and each answers a different way for the harness to be wrong. DETERMINISM says a
/// run is a function of its seed, and the seed is shown to be load-bearing beside it, because a harness that
/// ignored its seed would pass a same-seed-twice check trivially. MATRIX LOADING says a placement reaches the
/// measurement, checked against the published quorum-distance table and against a twin at a different matrix.
/// COMPUTED AGAINST SIMULATED says the pump and the arithmetic agree on the cells where both can speak: an
/// uncontended write under a jitterless model must cost exactly the quorum radius the arithmetic prices it
/// at, on both protocols and at every replica count.
/// </para>
/// <para>
/// The third family is also where the shipped gather's cost stops being an argument and becomes a
/// measurement: the fast write's shipped instant is pinned to the FARTHEST replica's round trip while its
/// quorum instant is pinned to the fast-quorum radius, and the two are different numbers on every spread
/// placement.
/// </para>
/// </remarks>
internal static class HarnessVectors
{
    /// <summary>Runs every vector and prints one line per vector.</summary>
    /// <returns>Whether every vector was clean.</returns>
    public static bool Run()
    {
        (string Name, HarnessVectorDelegate Vector)[] vectors =
        [
            ("quorum-table-from-shipped-registers", QuorumTable),
            ("matrix-loading-published-distances", PublishedQuorumDistances),
            ("matrix-parameter-is-load-bearing", MatrixIsLoadBearing),
            ("topology-library-shape", TopologyLibraryShape),
            ("clustered-majority-inverts-the-ordering", ClusteredMajorityInverts),
            ("jitter-grid-settings", JitterGridSettings),
            ("determinism-quepaxa-arm", DeterminismQuePaxa),
            ("determinism-fastcaspaxos-arm", DeterminismFastCasPaxos),
            ("determinism-oracle-arm", DeterminismOracle),
            ("computed-equals-simulated-quepaxa-leader", ComputedEqualsSimulatedQuePaxaLeader),
            ("computed-equals-simulated-quepaxa-nonleader", ComputedEqualsSimulatedQuePaxaNonLeader),
            ("computed-equals-simulated-fastcaspaxos", ComputedEqualsSimulatedFastCasPaxos),
            ("hedged-writer-runs-on-the-pump-clock", HedgedWriterRunsOnThePumpClock),
            ("fast-latency-is-measured-from-activation", FastLatencyIsMeasuredFromActivation),
            ("censored-percentile-ranks-above-survivors", CensoredPercentileRanksAboveSurvivors),
            ("censored-percentile-is-unbounded-past-the-censoring-point", CensoredPercentileIsUnboundedPastTheCensoringPoint),
            ("fast-agreement-requires-a-decision", FastAgreementRequiresADecision),
            ("ladder-units-are-per-arm", LadderUnitsArePerArm),
            ("arrival-spread-is-per-writer", ArrivalSpreadIsPerWriter),
            ("cell-seed-allocation-is-injective", CellSeedAllocationIsInjective),
            ("cross-arm-plateaus-agree", CrossArmPlateausAgree),
            ("stand-down-seam-is-reachable", StandDownSeamIsReachable),
            ("absent-leader-lane-is-above-every-writer", AbsentLeaderLaneIsAboveEveryWriter),
            ("pump-reports-a-faulted-client", PumpReportsAFaultedClient),
            ("pump-fails-closed", PumpFailsClosed),
            ("w5-tolerances-are-the-published-record", W5TolerancesAreThePublishedRecord),
            ("verdict-gate-a-removes-and-defaults-to-the-other-protocol", VerdictGateARemovesAndDefaultsToTheOtherProtocol),
            ("verdict-void-cell-has-no-winner", VerdictVoidCellHasNoWinner),
            ("verdict-unbounded-tail-cannot-win", VerdictUnboundedTailCannotWin),
            ("verdict-a-configuration-with-no-sample-is-removed", VerdictNoSampleIsRemoved),
            ("verdict-argmin-reads-the-representative-writer", VerdictArgminReadsTheRepresentativeWriter),
            ("verdict-either-band-and-its-exact-boundary", VerdictEitherBandAndItsExactBoundary),
            ("verdict-tie-break-orders-the-simpler-mode-first", VerdictTieBreakOrdersTheSimplerModeFirst),
            ("verdict-winning-rung-is-expressed-in-majority-radius", VerdictWinningRungIsExpressedInMajorityRadius),
            ("verdict-gate-b-removes-a-retried-quepaxa-configuration", VerdictGateBRemovesARetriedQuePaxaConfiguration),
            ("rmw-change-function-is-read-modify-write", RmwChangeFunctionIsReadModifyWrite),
            ("rmw-oracle-rejects-a-duplicated-change", RmwOracleRejectsADuplicatedChange),
            ("rmw-oracle-rejects-a-lost-change", RmwOracleRejectsALostChange),
            ("rmw-oracle-rejects-a-foreign-token", RmwOracleRejectsAForeignToken),
            ("rmw-oracle-accepts-the-sequential-fold", RmwOracleAcceptsTheSequentialFold),
            ("rmw-uncontended-write-costs-one-round-on-both-arms", RmwUncontendedWriteCostsOneRound),
            ("rmw-quepaxa-recomputes-against-the-winner", RmwQuePaxaRecomputesAgainstTheWinner),
            ("rmw-fastcaspaxos-composes-inside-the-round", RmwFastComposesInsideTheRound),
            ("rmw-apply-once-token-separates-the-two-arms", RmwApplyOnceTokenSeparatesTheTwoArms),
            ("rmw-determinism-quepaxa-arm", RmwDeterminismQuePaxa),
            ("rmw-determinism-fastcaspaxos-arm", RmwDeterminismFast),
            ("rmw-writer-count-cannot-exceed-the-replica-count", RmwWriterCountCannotExceedTheReplicaCount),
            ("rmw-seed-allocation-is-injective-across-both-workloads", RmwSeedAllocationIsInjective),
            ("rmw-gate-input-is-keyed-by-protocol-rung-and-spread", RmwGateInputIsKeyedByProtocolRungAndSpread),
            ("rmw-verdict-is-read-at-the-plain-cell-representative-writer", RmwVerdictIsReadAtThePlainCellRepresentativeWriter),
            ("rmw-cell-feeds-the-gate-its-own-measured-rates", RmwCellFeedsTheGateItsOwnMeasuredRates)
        ];

        Report.Text("HARNESS VECTORS");
        Report.Blank();

        bool clean = true;
        foreach((string name, HarnessVectorDelegate vector) in vectors)
        {
            var failures = new VectorFailures();
            vector(failures);
            clean &= failures.IsClean;
            Report.Line($"{(failures.IsClean ? "PASS" : "FAIL")} {name}");
            foreach(string message in failures.Messages)
            {
                Report.Line($"     {message}");
            }
        }

        Report.Blank();
        Report.Line($"VECTOR VERDICT: {(clean ? "PASS" : "FAIL")} over {vectors.Length} vectors");

        return clean;
    }


    /// <summary>
    /// The quorum arithmetic the whole grid turns on, read from the shipped registers rather than restated:
    /// three is unanimous, four is where the contrast vanishes, seven is where it is widest.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void QuorumTable(VectorFailures failures)
    {
        (int Replicas, int Fast, int Majority)[] expected = [(3, 3, 2), (4, 3, 3), (5, 4, 3), (7, 6, 4)];
        foreach((int replicas, int fast, int majority) in expected)
        {
            failures.Require(QuorumDistance.FastQuorum(replicas) == fast, $"The fast quorum at {replicas} replicas is {QuorumDistance.FastQuorum(replicas)} rather than {fast}.");
            failures.Require(QuorumDistance.ClassicQuorum(replicas) == majority, $"The classic quorum at {replicas} replicas is {QuorumDistance.ClassicQuorum(replicas)} rather than {majority}.");
            failures.Require(QuorumDistance.QuePaxaQuorum(replicas) == majority, $"The QuePaxa quorum at {replicas} replicas is {QuorumDistance.QuePaxaQuorum(replicas)} rather than {majority}.");
        }

        //At four replicas both quorums are three of four, so the two fast paths pay the same radius at every
        //site. This is the reason four is in the grid at all, and it is exact rather than measured.
        Topology four = Topologies.Global(4);
        foreach(ComputedSiteCost cost in QuorumDistance.For(four))
        {
            failures.Require(cost.FastQuorumRoundTrip == cost.MajorityRoundTrip,
                $"At four replicas the fast radius and the majority radius must coincide; site {cost.Site} pays {cost.FastQuorumRoundTrip}us against {cost.MajorityRoundTrip}us.");
        }
    }


    /// <summary>
    /// The published quorum-distance table, in milliseconds, reproduced from the loaded matrices.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void PublishedQuorumDistances(VectorFailures failures)
    {
        (Topology Placement, long[][] Published)[] cases =
        [
            (Topologies.ProbeSpread(), [[180, 240, 120], [150, 240, 120], [260, 360, 180], [190, 260, 130], [220, 300, 150]]),
            (Topologies.ProbeClustered(), [[180, 60, 30], [200, 60, 30], [184, 40, 20], [240, 460, 230], [200, 368, 184]])
        ];

        foreach((Topology placement, long[][] published) in cases)
        {
            ImmutableArray<ComputedSiteCost> costs = QuorumDistance.For(placement);
            failures.Require(costs.Length == published.Length, $"The {placement.Name} placement has {costs.Length} sites against {published.Length} published rows.");
            for(int site = 0; site < Math.Min(costs.Length, published.Length); site++)
            {
                ComputedSiteCost cost = costs[site];
                failures.Require(VirtualTimePump.ToMilliseconds(cost.FastQuorumRoundTrip) == published[site][0], $"{placement.Name} site {site} fast radius is {VirtualTimePump.ToMilliseconds(cost.FastQuorumRoundTrip)}ms against the published {published[site][0]}ms.");
                failures.Require(VirtualTimePump.ToMilliseconds(cost.ClassicRoundTrip) == published[site][1], $"{placement.Name} site {site} classic round is {VirtualTimePump.ToMilliseconds(cost.ClassicRoundTrip)}ms against the published {published[site][1]}ms.");
                failures.Require(VirtualTimePump.ToMilliseconds(cost.QuePaxaLeaderRoundTrip) == published[site][2], $"{placement.Name} site {site} QuePaxa leader round trip is {VirtualTimePump.ToMilliseconds(cost.QuePaxaLeaderRoundTrip)}ms against the published {published[site][2]}ms.");
            }
        }

        //The shipped gather is paced by the farthest replica rather than by the fast quorum, which the note's
        //own matrix prices at 220ms against 180ms at spread site zero.
        ComputedSiteCost spreadOrigin = QuorumDistance.For(Topologies.ProbeSpread())[0];
        failures.Require(VirtualTimePump.ToMilliseconds(spreadOrigin.FastShippedRoundTrip) == 220, $"The shipped gather at spread site 0 costs {VirtualTimePump.ToMilliseconds(spreadOrigin.FastShippedRoundTrip)}ms rather than the farthest replica's 220ms.");
    }


    /// <summary>
    /// A harness that silently ignored its matrix would return the same numbers for two placements, and every
    /// topology-keyed conclusion would be vacuous.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void MatrixIsLoadBearing(VectorFailures failures)
    {
        ComputedSiteCost spread = QuorumDistance.For(Topologies.ProbeSpread())[0];
        ComputedSiteCost clustered = QuorumDistance.For(Topologies.ProbeClustered())[0];

        failures.Require(spread.QuePaxaLeaderRoundTrip != clustered.QuePaxaLeaderRoundTrip,
            $"Two different placements produced one majority radius of {spread.QuePaxaLeaderRoundTrip}us, so the matrix reached no measurement.");

        JitterModel jitter = JitterModel.None;
        ImmutableArray<long> single = StaggerSchedule.Delays(1, 0);
        long spreadDecision = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(Topologies.ProbeSpread(), 1, LeadershipMode.WriterZeroLeads, single, single, SeedMixer.TrialSeed(1, 0), jitter, QuePaxaArm.DefaultEventBudget))[0].DecisionMicroseconds;
        long clusteredDecision = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(Topologies.ProbeClustered(), 1, LeadershipMode.WriterZeroLeads, single, single, SeedMixer.TrialSeed(1, 0), jitter, QuePaxaArm.DefaultEventBudget))[0].DecisionMicroseconds;

        failures.Require(spreadDecision != clusteredDecision,
            $"The pump returned {spreadDecision}us on both placements, so its matrix parameter is decorative.");
    }


    private static void TopologyLibraryShape(VectorFailures failures)
    {
        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(Topology placement in Topologies.Grid(replicaCount))
            {
                failures.Require(placement.SiteCount == replicaCount, $"The {placement.Name} placement at {replicaCount} replicas has {placement.SiteCount} sites.");
                failures.Require(!string.IsNullOrWhiteSpace(placement.Provenance), $"The {placement.Name} placement at {replicaCount} replicas carries no provenance.");
                for(int site = 0; site < placement.SiteCount; site++)
                {
                    failures.Require(placement.OneWay(site, site) > 0, $"The {placement.Name} placement at {replicaCount} replicas takes site {site}'s intra-region pair as zero, which no pair ever is.");
                }
            }

            Topology clustered = Topologies.ClusteredMajority(replicaCount);
            int majority = (replicaCount / 2) + 1;
            int inRegion = clustered.SiteRegions.Count(region => region == clustered.SiteRegions[0]);
            failures.Require(inRegion == majority, $"The clustered placement at {replicaCount} replicas puts {inRegion} replicas in the majority region rather than {majority}.");
        }

        //The five tiers must separate, or the topology axis of the grid measures one thing under five names.
        long[] majorityRadii = [.. Topologies.Grid(5).Select(placement => QuorumDistance.For(placement)[0].MajorityRoundTrip)];
        failures.Require(majorityRadii[0] < majorityRadii[1], $"The co-located tier's majority radius {majorityRadii[0]}us is not below the multi-availability-zone tier's {majorityRadii[1]}us.");
        failures.Require(majorityRadii[1] < majorityRadii[2], $"The multi-availability-zone tier's majority radius {majorityRadii[1]}us is not below the multi-region tier's {majorityRadii[2]}us.");
        failures.Require(majorityRadii[2] < majorityRadii[3], $"The multi-region tier's majority radius {majorityRadii[2]}us is not below the global tier's {majorityRadii[3]}us.");
        failures.Require(majorityRadii[4] < majorityRadii[3], $"The clustered tier's majority radius {majorityRadii[4]}us is not below the global tier's {majorityRadii[3]}us, which is the whole point of a co-located majority.");
    }


    /// <summary>
    /// OUTSIDE THE MAJORITY THE DIRECTION IS NOT A RULE, and the seven-replica arm is why. The settled reading
    /// was written at five replicas, where a remote writer's majority already leaves the region and the fast
    /// path stays the better mode for it. At seven the fast quorum is six of seven, so a remote replica that
    /// happens to sit NEAR the majority region gets a cheap majority and a supermajority that must still reach
    /// the second-farthest replica in the placement - and its ordering inverts too. That is a cardinality
    /// effect rather than a placement one, it is exact arithmetic, and a table that generalized the
    /// five-replica reading would misdecide it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    /// <remarks>
    /// The placement the settled rules turn on: inside the co-located majority a simple majority stays in the
    /// region while the supermajority cannot, so the leaderless fast path is the SLOWER mode there.
    /// </remarks>
    private static void ClusteredMajorityInverts(VectorFailures failures)
    {
        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            Topology clustered = Topologies.ClusteredMajority(replicaCount);
            string majorityRegion = clustered.SiteRegions[0];
            foreach(ComputedSiteCost cost in QuorumDistance.For(clustered))
            {
                //The fast quorum is never smaller than a majority, so its radius can never be the nearer one.
                //An inequality here means the two radii were read off the wrong ranks.
                failures.Require(cost.FastQuorumRoundTrip >= cost.MajorityRoundTrip,
                    $"At {replicaCount} replicas site {cost.Site} prices the fast radius at {cost.FastQuorumRoundTrip}us below the majority radius of {cost.MajorityRoundTrip}us, which no quorum arithmetic admits.");

                //At four replicas the two quorums coincide, so there is no ordering to invert.
                if(replicaCount != 4 && cost.Region == majorityRegion)
                {
                    failures.Require(cost.FastQuorumRoundTrip > cost.ClassicRoundTrip,
                        $"Inside the co-located majority at {replicaCount} replicas the fast path was expected to be the slower mode; site {cost.Site} pays {cost.FastQuorumRoundTrip}us against a classic round of {cost.ClassicRoundTrip}us.");
                }
            }
        }

        //Non-vacuity for the reading above: at five replicas a writer outside the majority keeps the fast
        //path as the better mode, so "the clustered placement inverts everything" is excluded.
        Topology five = Topologies.ClusteredMajority(5);
        string fiveMajorityRegion = five.SiteRegions[0];
        int remoteKeepingFast = QuorumDistance.For(five).Count(cost => cost.Region != fiveMajorityRegion && cost.FastQuorumRoundTrip < cost.ClassicRoundTrip);
        failures.Require(remoteKeepingFast > 0,
            $"At five replicas {remoteKeepingFast} sites outside the majority keep the fast path as the better mode, so the inversion above pins nothing about placement.");

        //And the seven-replica finding itself, pinned so it cannot be lost: the remote replica nearest the
        //majority region inverts as well, because a six-of-seven supermajority must reach the SECOND-FARTHEST
        //replica while its four-of-seven majority stays inside the near cluster. A quorum of six of seven
        //omits exactly one replica and omits the most distant one, so the farthest replica is never what the
        //supermajority waits for; the farthest is what the shipped gather waits for, and FastShippedRoundTrip
        //prices that separately in the same row.
        Topology seven = Topologies.ClusteredMajority(7);
        string sevenMajorityRegion = seven.SiteRegions[0];
        int remoteInverted = QuorumDistance.For(seven).Count(cost => cost.Region != sevenMajorityRegion && cost.FastQuorumRoundTrip > cost.ClassicRoundTrip);
        failures.Require(remoteInverted == 1,
            $"At seven replicas {remoteInverted} sites outside the majority were expected to invert, not this many; the widest supermajority in the grid is what makes the count nonzero at all.");
    }


    private static void JitterGridSettings(VectorFailures failures)
    {
        JitterModel published = JitterModel.PublishedMillisecondGrid;
        failures.Require(published.GrainMicroseconds == 1000, $"The published grid is {published.GrainMicroseconds}us rather than a whole millisecond.");
        failures.Require(published.SpanUnitsFor(90_000) == 30, $"The published span is {published.SpanUnitsFor(90_000)} units rather than thirty.");

        var drawn = new HashSet<long>();
        for(int key = 0; key < 200; key++)
        {
            long draw = published.Draw(SeedMixer.TrialSeed(7, key), writer: key % 5, peer: key % 3, step: 1, leg: key % 2, oneWayMicroseconds: 90_000);
            _ = drawn.Add(draw);
            failures.Require(draw % 1000 == 0, $"A published-grid draw of {draw}us is not on the whole-millisecond grid.");
            failures.Require(draw is >= 0 and < 30_000, $"A published-grid draw of {draw}us is outside zero to twenty-nine milliseconds.");
        }

        failures.Require(drawn.Count > 1, $"Every published-grid draw returned one of {drawn.Count} values, so the model is a constant rather than a distribution.");

        failures.Require(JitterModel.None.Draw(1234, 0, 0, 0, 0, 90_000) == 0, $"The jitterless model drew {JitterModel.None.Draw(1234, 0, 0, 0, 0, 90_000)}us rather than nothing.");
        failures.Require(JitterModel.None.SpanUnitsFor(90_000) == 0, $"The jitterless model reports a span of {JitterModel.None.SpanUnitsFor(90_000)} units rather than none.");

        JitterModel proportional = JitterModel.ProportionalFifteenPercent;
        failures.Require(proportional.SpanUnitsFor(100_000) == 15_000, $"The proportional span over a 100ms link is {proportional.SpanUnitsFor(100_000)}us rather than fifteen percent of it.");
        failures.Require(proportional.SpanUnitsFor(500) == 75, $"The proportional span over a co-located link is {proportional.SpanUnitsFor(500)}us rather than fifteen percent of it.");
    }


    private static void DeterminismQuePaxa(VectorFailures failures)
    {
        Topology placement = Topologies.Global(5);
        ImmutableArray<long> delays = StaggerSchedule.Delays(3, 0);
        QuePaxaTrialRequest request = new(placement, 3, LeadershipMode.WriterZeroLeads, delays, delays, SeedMixer.TrialSeed(31, 0), JitterModel.PublishedMillisecondGrid, QuePaxaArm.DefaultEventBudget);

        string first = Fingerprint(QuePaxaArm.RunTrial(request));
        string second = Fingerprint(QuePaxaArm.RunTrial(request));
        failures.Require(first == second, $"The QuePaxa arm returned two different runs at one seed: '{first}' against '{second}'.");

        string other = Fingerprint(QuePaxaArm.RunTrial(request with { TrialSeed = SeedMixer.TrialSeed(31, 1) }));
        failures.Require(first != other, $"The QuePaxa arm returned '{first}' at two different seeds, so the seed reaches no measurement and the same-seed check above pins nothing.");
    }


    private static void DeterminismFastCasPaxos(VectorFailures failures)
    {
        Topology placement = Topologies.Global(5);
        ImmutableArray<long> arrivals = [0, 0, 0];
        FastTrialRequest request = new(placement, 3, arrivals, TimeSpan.Zero, SeedMixer.TrialSeed(41, 0), JitterModel.PublishedMillisecondGrid, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget);

        string first = Fingerprint(FastCasPaxosArm.RunTrial(request));
        string second = Fingerprint(FastCasPaxosArm.RunTrial(request));
        failures.Require(first == second, $"The Fast CASPaxos arm returned two different runs at one seed: '{first}' against '{second}'.");

        string other = Fingerprint(FastCasPaxosArm.RunTrial(request with { TrialSeed = SeedMixer.TrialSeed(41, 1) }));
        failures.Require(first != other, $"The Fast CASPaxos arm returned '{first}' at two different seeds, so the seed reaches no measurement and the same-seed check above pins nothing.");
    }


    private static void DeterminismOracle(VectorFailures failures)
    {
        Topology placement = Topologies.ProbeSpread();
        JitterModel jitter = JitterModel.PublishedMillisecondGrid;

        OracleMeasurement first = OracleArrivalArm.Measure(placement, 3, 90_000, 0, jitter, seed: 3, trials: 200);
        OracleMeasurement second = OracleArrivalArm.Measure(placement, 3, 90_000, 0, jitter, seed: 3, trials: 200);
        failures.Require(first == second, $"The oracle arm returned two different aggregates at one seed: {first.TrialFastCommitRate:F3} against {second.TrialFastCommitRate:F3}.");

        OracleMeasurement other = OracleArrivalArm.Measure(placement, 3, 90_000, 0, jitter, seed: 4, trials: 200);
        failures.Require(first != other, $"The oracle arm returned a trial rate of {other.TrialFastCommitRate:F3} at two different seeds, so the seed reaches no measurement.");
    }


    /// <summary>
    /// An uncontended believed leader decides in one step, and one step is one round trip at the majority
    /// radius. Under a jitterless model that is an equality rather than a band, and a proposer that widened its
    /// quorum would still report one step while paying a radius this vector prices exactly.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void ComputedEqualsSimulatedQuePaxaLeader(VectorFailures failures)
    {
        foreach((int replicaCount, Topology placement) in EveryPlacement())
        {
            ImmutableArray<long> single = StaggerSchedule.Delays(1, 0);
            QuePaxaWriterMeasurement measurement = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
                placement, 1, LeadershipMode.WriterZeroLeads, single, single, SeedMixer.TrialSeed(51, replicaCount), JitterModel.None, QuePaxaArm.DefaultEventBudget))[0];

            long computed = QuorumDistance.For(placement)[0].QuePaxaLeaderRoundTrip;
            failures.Require(measurement.Outcome.IsDecided, $"{placement.Name} at {replicaCount}: the uncontended leader did not decide.");
            failures.Require(measurement.Outcome.Steps == 1, $"{placement.Name} at {replicaCount}: the uncontended leader took {measurement.Outcome.Steps} steps rather than one.");
            failures.Require(measurement.Outcome.DecidedAt == RecorderStep.RoundOnePhaseZero, $"{placement.Name} at {replicaCount}: the uncontended leader decided at step {measurement.Outcome.DecidedAt.Value} rather than the round's first.");
            failures.Require(measurement.PriorityDraws == 0, $"{placement.Name} at {replicaCount}: a proposer that believes it leads drew {measurement.PriorityDraws} priorities rather than none.");
            failures.Require(measurement.DecisionMicroseconds == computed, $"{placement.Name} at {replicaCount}: the simulated leader commit took {measurement.DecisionMicroseconds}us against the computed majority radius of {computed}us.");
        }
    }


    /// <summary>
    /// An uncontended non-leader decides at round one phase two, three steps, each one round trip at the same
    /// radius. It is the other half of the agreement between the pump and the arithmetic.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void ComputedEqualsSimulatedQuePaxaNonLeader(VectorFailures failures)
    {
        foreach((int replicaCount, Topology placement) in EveryPlacement())
        {
            ImmutableArray<long> single = StaggerSchedule.Delays(1, 0);
            QuePaxaWriterMeasurement measurement = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
                placement, 1, LeadershipMode.AbsentLeader, single, single, SeedMixer.TrialSeed(52, replicaCount), JitterModel.None, QuePaxaArm.DefaultEventBudget))[0];

            long computed = QuorumDistance.For(placement)[0].QuePaxaNonLeaderRoundTrip;
            failures.Require(measurement.Outcome.Steps == 3, $"{placement.Name} at {replicaCount}: the uncontended non-leader took {measurement.Outcome.Steps} steps rather than three.");
            failures.Require(measurement.PriorityDraws == replicaCount, $"{placement.Name} at {replicaCount}: a non-claimant drew {measurement.PriorityDraws} priorities rather than one per recorder at phase zero.");
            failures.Require(measurement.DecisionMicroseconds == computed, $"{placement.Name} at {replicaCount}: the simulated non-leader commit took {measurement.DecisionMicroseconds}us against the computed three steps at the majority radius of {computed}us.");
        }
    }


    /// <summary>
    /// The shipped proposer gathers all acceptors, so an uncontended fast write returns at the FARTHEST
    /// replica's round trip while its fast quorum was complete at the fast radius. Both are pinned, and on
    /// every spread placement they are different numbers.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void ComputedEqualsSimulatedFastCasPaxos(VectorFailures failures)
    {
        foreach((int replicaCount, Topology placement) in EveryPlacement())
        {
            FastWriterMeasurement measurement = FastCasPaxosArm.RunTrial(new FastTrialRequest(
                placement, 1, [0], TimeSpan.Zero, SeedMixer.TrialSeed(53, replicaCount), JitterModel.None, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget))[0];

            ComputedSiteCost computed = QuorumDistance.For(placement)[0];
            failures.Require(measurement.IsCommitted, $"{placement.Name} at {replicaCount}: the uncontended fast write did not commit.");
            failures.Require(measurement.ReachedFastQuorum, $"{placement.Name} at {replicaCount}: a lone writer has nobody to split the round with and must reach its fast quorum.");
            failures.Require(measurement.PhasesExecuted == 1, $"{placement.Name} at {replicaCount}: the uncontended fast write executed {measurement.PhasesExecuted} phases rather than one.");
            failures.Require(!measurement.RecoveryEntered, $"{placement.Name} at {replicaCount}: the uncontended fast write entered recovery.");
            failures.Require(measurement.FastWriteReturnedMicroseconds == computed.FastShippedRoundTrip, $"{placement.Name} at {replicaCount}: the shipped fast write returned at {measurement.FastWriteReturnedMicroseconds}us against the computed farthest round trip of {computed.FastShippedRoundTrip}us.");
            failures.Require(measurement.FastQuorumReachedMicroseconds == computed.FastQuorumRoundTrip, $"{placement.Name} at {replicaCount}: the fast quorum completed at {measurement.FastQuorumReachedMicroseconds}us against the computed fast radius of {computed.FastQuorumRoundTrip}us.");
        }

        //The gather's cost over a first-quorum proposer is a real number rather than a rounding difference on
        //a placement whose farthest replica is beyond its fast quorum.
        ComputedSiteCost spread = QuorumDistance.For(Topologies.ProbeSpread())[0];
        failures.Require(spread.FastShippedRoundTrip > spread.FastQuorumRoundTrip,
            $"The shipped gather and the fast quorum cost the same {spread.FastShippedRoundTrip}us at spread site 0, so this placement cannot price the gather at all.");
    }


    /// <summary>
    /// BOTH SIDES OF THE DELAY ARE PINNED HERE. The delay reached the clock, which is a statement about the
    /// INSTANT the write returned at, and the delay is outside the measurement, which is a statement about the
    /// reading that instant is reported as. Pinning only the first cements the arrival origin the two arms must
    /// not disagree on; pinning only the second would pass a harness that never waited at all.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    /// <remarks>
    /// The stagger under measurement is the shipped policy's, awaited against this pump's clock. A harness that
    /// failed to serve the TimeProvider would show a writer sending at once and the delay would be a restated
    /// number again.
    /// </remarks>
    private static void HedgedWriterRunsOnThePumpClock(VectorFailures failures)
    {
        Topology placement = Topologies.ProbeSpread();
        const long baseDelay = 50_000;
        ImmutableArray<FastWriterMeasurement> measurements = FastCasPaxosArm.RunTrial(new FastTrialRequest(
            placement, 2, [0, 0], VirtualTimePump.ToTimeSpan(baseDelay), SeedMixer.TrialSeed(61, 0), JitterModel.None, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

        ImmutableArray<ComputedSiteCost> costs = QuorumDistance.For(placement);
        failures.Require(measurements[0].AddedWaitMicroseconds == 0, $"The first writer in the schedule waited {measurements[0].AddedWaitMicroseconds}us rather than sending at once.");
        failures.Require(measurements[1].AddedWaitMicroseconds == baseDelay, $"The second writer in the schedule waited {measurements[1].AddedWaitMicroseconds}us rather than the schedule's {baseDelay}us.");
        failures.Require(measurements[0].FastWriteReturnedMicroseconds == costs[0].FastShippedRoundTrip, $"The unstaggered writer's fast write returned at {measurements[0].FastWriteReturnedMicroseconds}us against the computed {costs[0].FastShippedRoundTrip}us.");

        long staggeredInstant = measurements[1].ArrivalMicroseconds + measurements[1].AddedWaitMicroseconds + measurements[1].FastWriteReturnedMicroseconds;
        failures.Require(staggeredInstant == baseDelay + costs[1].FastShippedRoundTrip, $"The staggered writer's fast write returned at the instant {staggeredInstant}us against its delay plus the computed {costs[1].FastShippedRoundTrip}us, so the delay never reached the clock.");
        failures.Require(measurements[1].FastWriteReturnedMicroseconds == costs[1].FastShippedRoundTrip, $"The staggered writer's fast write reads {measurements[1].FastWriteReturnedMicroseconds}us against the computed {costs[1].FastShippedRoundTrip}us, so its own delay is inside a reading measured from its activation.");

        //A zero base delay reproduces the unhedged behaviour exactly, which is the shipped schedule's own
        //documented contract and the degenerate case every unhedged row of the grid rests on.
        ImmutableArray<FastWriterMeasurement> unhedged = FastCasPaxosArm.RunTrial(new FastTrialRequest(
            placement, 2, [0, 0], TimeSpan.Zero, SeedMixer.TrialSeed(61, 0), JitterModel.None, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

        failures.Require(unhedged[1].AddedWaitMicroseconds == 0, $"A zero base delay staggered the second writer by {unhedged[1].AddedWaitMicroseconds}us.");

        //A SUB-MILLISECOND STAGGER MUST REACH THE CLOCK. The co-located tier's whole ladder is under a
        //millisecond, so a delay that the platform rounded or dropped would silently turn every co-located
        //hedged row into its own unhedged row and the tier's verdict would be measured against a stagger
        //nobody waited.
        Topology coLocated = Topologies.CoLocated(4);
        ImmutableArray<ComputedSiteCost> coLocatedCosts = QuorumDistance.For(coLocated);
        foreach(long subMillisecond in (long[])[1, 37, 250, 500])
        {
            ImmutableArray<FastWriterMeasurement> fine = FastCasPaxosArm.RunTrial(new FastTrialRequest(
                coLocated, 2, [0, 0], VirtualTimePump.ToTimeSpan(subMillisecond), SeedMixer.TrialSeed(62, 0), JitterModel.None, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

            long fineInstant = fine[1].ArrivalMicroseconds + fine[1].AddedWaitMicroseconds + fine[1].FastWriteReturnedMicroseconds;
            failures.Require(fine[1].AddedWaitMicroseconds == subMillisecond, $"A {subMillisecond}us stagger was reported as {fine[1].AddedWaitMicroseconds}us of added wait.");
            failures.Require(fineInstant == subMillisecond + coLocatedCosts[1].FastShippedRoundTrip,
                $"A {subMillisecond}us stagger left the second writer's fast write returning at the instant {fineInstant}us against its delay plus the computed {coLocatedCosts[1].FastShippedRoundTrip}us, so the delay never reached the clock.");
            failures.Require(fine[1].FastWriteReturnedMicroseconds == coLocatedCosts[1].FastShippedRoundTrip,
                $"A {subMillisecond}us stagger left the second writer's reading at {fine[1].FastWriteReturnedMicroseconds}us against the computed {coLocatedCosts[1].FastShippedRoundTrip}us, so its own delay is inside a reading measured from its activation.");
        }
    }


    /// <summary>
    /// Plan section 5.4 item 1 measures a commit from that writer's own activation on BOTH protocols, and
    /// section 5.5 argmins the two arms' p95 against each other. A hedging delay is a shift of the activation
    /// and never of the cost the write paid once it started, so moving the delay must move the instant and
    /// leave the reading alone. The two runs below are the same absolute schedule reached two ways.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void FastLatencyIsMeasuredFromActivation(VectorFailures failures)
    {
        Topology placement = Topologies.ProbeSpread();
        const long baseDelay = 300_000;
        const long lateArrival = 5_000_000;

        ImmutableArray<FastWriterMeasurement> hedged = FastCasPaxosArm.RunTrial(new FastTrialRequest(
            placement, 2, [lateArrival, 0], VirtualTimePump.ToTimeSpan(baseDelay), SeedMixer.TrialSeed(63, 0), JitterModel.None, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

        ImmutableArray<FastWriterMeasurement> unhedged = FastCasPaxosArm.RunTrial(new FastTrialRequest(
            placement, 2, [lateArrival, 0], TimeSpan.Zero, SeedMixer.TrialSeed(63, 0), JitterModel.None, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

        //The hedged writer must really have waited, or the equality below would hold because nothing happened.
        failures.Require(hedged[1].AddedWaitMicroseconds == baseDelay, $"The hedged writer waited {hedged[1].AddedWaitMicroseconds}us rather than the schedule's {baseDelay}us, so the comparison below is between two unhedged runs.");
        failures.Require(unhedged[1].AddedWaitMicroseconds == 0, $"The unhedged writer waited {unhedged[1].AddedWaitMicroseconds}us.");

        //And it must have run alone, so the only difference between the two runs is where the reading starts.
        failures.Require(hedged[1].IsCommitted && unhedged[1].IsCommitted, $"A writer that ran alone committed {hedged[1].IsCommitted} hedged and {unhedged[1].IsCommitted} unhedged, so the two runs are not the same uncontended write.");
        failures.Require(hedged[1].PhasesExecuted == 1 && unhedged[1].PhasesExecuted == 1, $"The writer executed {hedged[1].PhasesExecuted} and {unhedged[1].PhasesExecuted} phases rather than the one an uncontended fast write takes.");

        failures.Require(hedged[1].CommitMicroseconds == unhedged[1].CommitMicroseconds,
            $"A hedged writer's commit reads {hedged[1].CommitMicroseconds}us against the same write's unhedged {unhedged[1].CommitMicroseconds}us, so its delay is inside the measurement rather than only inside the instant.");
        failures.Require(hedged[1].FastQuorumReachedMicroseconds == unhedged[1].FastQuorumReachedMicroseconds,
            $"A hedged writer's quorum instant reads {hedged[1].FastQuorumReachedMicroseconds}us against the same write's unhedged {unhedged[1].FastQuorumReachedMicroseconds}us.");
        failures.Require(hedged[1].FastWriteReturnedMicroseconds == unhedged[1].FastWriteReturnedMicroseconds,
            $"A hedged writer's shipped instant reads {hedged[1].FastWriteReturnedMicroseconds}us against the same write's unhedged {unhedged[1].FastWriteReturnedMicroseconds}us.");

        //The client-visible currency stays reconstructable, which is what makes one origin a choice of column
        //rather than a loss of information.
        failures.Require(hedged[1].ArrivalMicroseconds + hedged[1].AddedWaitMicroseconds + hedged[1].CommitMicroseconds == baseDelay + unhedged[1].CommitMicroseconds,
            $"The hedged writer's arrival, added wait and commit reading do not reconstruct its client-visible instant.");
    }


    /// <summary>
    /// A tail ranked over the writes that finished is biased in favour of whichever configuration fails most,
    /// and the tail is the whole reason the verdict is read at the p95. A write that never finished therefore
    /// ranks above every write that did.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void CensoredPercentileRanksAboveSurvivors(VectorFailures failures)
    {
        //Ninety finished writes at one millisecond apart and ten that never finished: the median of the
        //hundred is the fiftieth finished write, not the forty-fifth.
        double[] finished = [.. Enumerable.Range(1, 90).Select(value => (double)value)];

        PercentileReading median = PercentileReading.Of(finished, censored: 10, 0.50);
        failures.Require(median.IsBounded && median.Value == 50.0, $"The median of ninety finished writes and ten censored ones reads {median} rather than 50.000.");

        PercentileReading survivorsOnly = PercentileReading.Of(finished, censored: 0, 0.50);
        failures.Require(survivorsOnly.IsBounded && survivorsOnly.Value == 45.0, $"The median of the same ninety writes alone reads {survivorsOnly} rather than 45.000, so the censoring above changed nothing and the check pins nothing.");

        //A percentile still inside the finished mass is a number, and it is the number the whole population
        //supports rather than the one the survivors alone would give.
        PercentileReading ninetieth = PercentileReading.Of(finished, censored: 4, 0.90);
        failures.Require(ninetieth.IsBounded && ninetieth.Value == 85.0, $"The p90 of ninety finished writes and four censored ones reads {ninetieth} rather than 85.000.");

        //An uncensored population reads exactly as it always did.
        PercentileReading uncensored = PercentileReading.Of(finished, censored: 0, 0.95);
        failures.Require(uncensored.IsBounded && uncensored.Value == 86.0, $"The p95 of ninety finished writes alone reads {uncensored} rather than 86.000.");
    }


    /// <summary>
    /// Above the censoring point the population's own tail does not exist, and a row that printed a number
    /// there would hand a reducer a finite tail measured over the writes that survived it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void CensoredPercentileIsUnboundedPastTheCensoringPoint(VectorFailures failures)
    {
        double[] finished = [.. Enumerable.Range(1, 90).Select(value => (double)value)];

        PercentileReading tail = PercentileReading.Of(finished, censored: 10, 0.95);
        failures.Require(!tail.IsBounded, $"The p95 of ninety finished writes and ten censored ones reads {tail} rather than a marker, so a tail beyond the censoring point is published as a number.");
        failures.Require(tail.HasSample, $"The p95 above reports an empty population rather than an unbounded one.");
        failures.Require(double.IsPositiveInfinity(tail.Value), $"An unbounded reading compares at {tail.Value} rather than as positive infinity, so a row whose tail does not exist could win an argmin.");
        failures.Require(tail.ToString() == "unbounded", $"An unbounded reading prints as '{tail}'.");

        //The boundary is exactly where the rank crosses into the censored mass and nowhere else.
        PercentileReading justInside = PercentileReading.Of(finished, censored: 4, 0.95);
        failures.Require(justInside.IsBounded && justInside.Value == 90.0, $"The p95 of ninety finished writes and four censored ones reads {justInside} rather than 90.000, so the marker fires below the censoring point.");

        //Max is the same rule, so a censored row cannot report a finite worst case either.
        PercentileReading max = PercentileReading.Of(finished, censored: 1, 1.00);
        failures.Require(!max.IsBounded, $"The maximum of ninety finished writes and one censored one reads {max} rather than a marker.");

        //A write that never committed leaves nothing to rank, and the reading says so rather than printing a
        //number or a NaN.
        PercentileReading everythingCensored = PercentileReading.Of([], censored: 7, 0.50);
        failures.Require(!everythingCensored.IsBounded, $"A population in which nothing finished reads {everythingCensored}.");
        failures.Require(!double.IsNaN(everythingCensored.Value), $"A population in which nothing finished reads {everythingCensored.Value}, which is a NaN.");

        PercentileReading empty = PercentileReading.Of([], censored: 0, 0.50);
        failures.Require(!empty.HasSample && empty.ToString() == "none", $"A population with no write at all reads '{empty}' rather than a marker for no sample.");
        failures.Require(!double.IsNaN(empty.Value), $"A population with no write at all reads {empty.Value}, which is a NaN.");
    }


    /// <summary>
    /// Plan section 5.4 item 5 states the gate as one instance deciding one value in every trial, ON BOTH
    /// PROTOCOLS. The QuePaxa arm carries the decide half in its own predicate; the Fast arm must carry it too,
    /// or a trial in which nobody committed passes a gate that exists to void it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void FastAgreementRequiresADecision(VectorFailures failures)
    {
        ImmutableArray<FastWriterMeasurement> nobodyCommitted = [Censored(0), Censored(1)];
        failures.Require(!TrialAgreement.Fast(nobodyCommitted), $"A Fast CASPaxos trial in which none of {nobodyCommitted.Length} writers committed passed the agreement gate, so a configuration that decided nothing is reported as agreed rather than void.");

        //A single writer's exhaustion beside another writer's commit is a censored write and not a broken
        //register, which is the bounded ladder's own reading and must stay outside the gate.
        ImmutableArray<FastWriterMeasurement> oneCensored = [Committed(0, "w0"), Censored(1)];
        failures.Require(TrialAgreement.Fast(oneCensored), $"A writer that exhausted its ladder beside a writer that committed {oneCensored[0].CommittedValue} voided the trial, so a censored write is being read as an agreement failure.");

        //And the value half is still live, so the decide half above is not the only thing the gate says.
        ImmutableArray<FastWriterMeasurement> diverged = [Committed(0, "w0"), Committed(1, "w1")];
        failures.Require(!TrialAgreement.Fast(diverged), $"Two writers committed {diverged[0].CommittedValue} and {diverged[1].CommittedValue} and the trial agreed, so the register's one-value rule reaches no gate.");

        ImmutableArray<FastWriterMeasurement> agreed = [Committed(0, "w0"), Committed(1, "w0")];
        failures.Require(TrialAgreement.Fast(agreed), $"Two writers committed the same {agreed[0].CommittedValue} and the trial did not agree.");
    }


    /// <summary>
    /// Plan section 5.2 denominates the arrival spread in a writer's own majority-radius round trip, and the
    /// note's own convention prices a Fast CASPaxos hedge in the LEADING WRITER'S FAST-QUORUM round trip, which
    /// is what the reproduction gate's ladder already uses. A hedge ladder priced in a co-located majority
    /// radius sweeps two orders of magnitude below the round it staggers.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void LadderUnitsArePerArm(VectorFailures failures)
    {
        Topology clustered = Topologies.ClusteredMajority(5);
        ImmutableArray<ComputedSiteCost> computed = QuorumDistance.For(clustered);

        long staggerUnit = CellSweep.QuePaxaStaggerUnit(computed);
        long hedgeUnit = CellSweep.FastHedgeUnit(computed);

        failures.Require(staggerUnit == computed[0].MajorityRoundTrip, $"The QuePaxa stagger unit is {staggerUnit}us rather than the leader's majority radius of {computed[0].MajorityRoundTrip}us.");
        failures.Require(hedgeUnit == computed[0].FastQuorumRoundTrip, $"The Fast CASPaxos hedge unit is {hedgeUnit}us rather than the leading writer's fast-quorum round trip of {computed[0].FastQuorumRoundTrip}us.");

        //Non-vacuity: on this tier the two units are not the same number, so the two checks above are two
        //statements rather than one written twice.
        failures.Require(hedgeUnit > 100 * staggerUnit, $"The clustered tier prices the fast round at {hedgeUnit}us against a majority radius of {staggerUnit}us, so the tier cannot separate the two units at all.");

        const double rung = 0.25;
        long hedgeBaseDelay = (long)(rung * hedgeUnit);
        failures.Require(hedgeBaseDelay == (long)(rung * computed[0].FastQuorumRoundTrip), $"A {rung:F2} hedge rung is {hedgeBaseDelay}us rather than that fraction of the leading writer's fast-quorum round trip.");
        failures.Require(hedgeBaseDelay > computed[0].MajorityRoundTrip, $"A {rung:F2} hedge rung is {hedgeBaseDelay}us, below the {computed[0].MajorityRoundTrip}us majority radius, so the whole hedging axis sits under the jitter of the round it staggers.");
    }


    /// <summary>
    /// The unit is that writer's own majority-radius round trip, so a remote writer's arrival pattern is drawn
    /// over its own radius rather than over the leader's. On a clustered tier those differ by two orders of
    /// magnitude and a single unit collapses the remote writers onto the leader's spread.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void ArrivalSpreadIsPerWriter(VectorFailures failures)
    {
        Topology clustered = Topologies.ClusteredMajority(5);
        ImmutableArray<ComputedSiteCost> computed = QuorumDistance.For(clustered);
        const int writerCount = 5;
        const double spread = 2.00;

        ImmutableArray<long> spreads = CellSweep.ArrivalSpreadMicroseconds(computed, writerCount, spread);
        for(int writer = 0; writer < writerCount; writer++)
        {
            long own = (long)(spread * computed[writer % computed.Length].MajorityRoundTrip);
            failures.Require(spreads[writer] == own, $"Writer {writer} draws its arrival over {spreads[writer]}us rather than over its own {own}us.");
        }

        failures.Require(spreads.Distinct().Count() > 1, $"Every writer draws over one spread of {spreads[0]}us on a placement whose sites do not share a majority radius, so the per-writer unit pins nothing.");

        //And the draw itself is scaled, not merely the width beside it: a remote writer must be able to arrive
        //beyond the whole width the leader's radius would have allowed.
        long widestRemote = 0;
        const int writers = writerCount;
        for(int trial = 0; trial < 50; trial++)
        {
            ImmutableArray<long> offsets = CellSweep.ArrivalOffsets(SeedMixer.TrialSeed(65, trial), writers, spreads, JitterModel.ProportionalFifteenPercent.GrainMicroseconds);
            for(int writer = 0; writer < writers; writer++)
            {
                failures.Require(offsets[writer] < spreads[writer], $"Writer {writer} drew an arrival of {offsets[writer]}us outside its own spread of {spreads[writer]}us.");
            }

            widestRemote = Math.Max(widestRemote, offsets[3]);
        }

        failures.Require(widestRemote >= spreads[0], $"The widest arrival a remote writer drew over fifty trials is {widestRemote}us, inside the leader's own spread of {spreads[0]}us, so its draws are scaled by the leader's radius.");
    }


    /// <summary>
    /// A cell CONSUMES one seed base per arm while the allocator ADVANCES by its own stride, so a stride below
    /// what a cell consumes makes one cell's Fast rows draw the trial-seed stream of a neighbouring cell's
    /// QuePaxa rows. Two rows the table presents as independent measurements would then share their noise.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void CellSeedAllocationIsInjective(VectorFailures failures)
    {
        int[] arms = [CellSweep.QuePaxaArmSeedOffset, CellSweep.FastArmSeedOffset, CellSweep.ReservedArmSeedOffset];
        var allocated = new Dictionary<int, string>();

        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(int writerCount in CellSweep.WriterCounts)
            {
                foreach(int arm in arms)
                {
                    int seedBase = CellSweep.DefaultSeedBase(replicaCount, writerCount) + arm;
                    string owner = string.Create(CultureInfo.InvariantCulture, $"{replicaCount} replicas, {writerCount} writers, arm {arm}");
                    failures.Require(!allocated.TryGetValue(seedBase, out string? held), $"The seed base {seedBase} serves both {owner} and {held}, so two rows draw one trial-seed stream.");
                    allocated[seedBase] = owner;
                }
            }
        }

        failures.Require(arms.Length <= CellSweep.SeedsPerCell, $"A cell reserves {CellSweep.SeedsPerCell} seed bases for {arms.Length} arms, so the next arm added would alias into the next cell.");
    }


    /// <summary>
    /// Every Fast CASPaxos figure of the reproduction gate comes from the oracle arm and every Fast CASPaxos
    /// cell comes from the pumped arm, so without this the two arms are never set against each other and the
    /// arm behind every cell has no contended outcome pinned anywhere. The plateaus are protocol facts rather
    /// than measurements, so they are pinned exactly; the two arms draw different jitter streams, so the knee
    /// between them is not.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void CrossArmPlateausAgree(VectorFailures failures)
    {
        Topology spread = Topologies.ProbeSpread();
        JitterModel jitter = JitterModel.PublishedMillisecondGrid;
        const int writerCount = 3;
        const int trials = 200;
        const int pumpedSeedBase = 66;

        //Zero is the split every simultaneous three-writer trial takes, and ninety milliseconds and above is
        //clear of the knee on both arms. The sixty-millisecond rung is ON the knee for the pumped arm rather
        //than above it, which is exactly why only the plateaus are pinned.
        foreach(long hedgeMicroseconds in (long[])[0, 90_000, 180_000, 270_000])
        {
            OracleMeasurement oracle = OracleArrivalArm.Measure(spread, writerCount, arrivalSpreadMicroseconds: 0, hedgeMicroseconds, jitter, seed: 1, trials);

            int trialsWithFastQuorum = 0;
            int fastWrites = 0;
            for(int trial = 0; trial < trials; trial++)
            {
                ImmutableArray<FastWriterMeasurement> measurements = FastCasPaxosArm.RunTrial(new FastTrialRequest(
                    spread,
                    writerCount,
                    [0, 0, 0],
                    VirtualTimePump.ToTimeSpan(hedgeMicroseconds),
                    SeedMixer.TrialSeed(pumpedSeedBase, trial),
                    jitter,
                    FastCasPaxosArm.DefaultMaxRecoveryAttempts,
                    FastCasPaxosArm.DefaultEventBudget));

                bool anyFast = false;
                foreach(FastWriterMeasurement measurement in measurements)
                {
                    anyFast |= measurement.ReachedFastQuorum;
                    if(measurement.ReachedFastQuorum)
                    {
                        fastWrites++;
                    }
                }

                if(anyFast)
                {
                    trialsWithFastQuorum++;
                }
            }

            //At a zero hedge three simultaneous writers split the fast ballot in every trial; above the knee
            //the leading writer holds the whole quorum alone in every trial, which is one write of three.
            bool unhedged = hedgeMicroseconds == 0;
            int expectedTrials = unhedged ? 0 : trials;
            double expectedTrialRate = unhedged ? 0.0 : 1.0;
            double expectedWriterRate = unhedged ? 0.0 : 1.0 / writerCount;

            failures.Require(trialsWithFastQuorum == expectedTrials, $"At a {VirtualTimePump.ToMilliseconds(hedgeMicroseconds):F0}ms hedge the pumped arm reached a fast quorum in {trialsWithFastQuorum} of {trials} trials rather than {expectedTrials}.");
            failures.Require(fastWrites == expectedTrials, $"At a {VirtualTimePump.ToMilliseconds(hedgeMicroseconds):F0}ms hedge {fastWrites} of {trials * writerCount} pumped writes reached a fast quorum rather than {expectedTrials}.");
            failures.Require(oracle.TrialFastCommitRate == expectedTrialRate, $"At a {VirtualTimePump.ToMilliseconds(hedgeMicroseconds):F0}ms hedge the oracle arm's trial rate is {oracle.TrialFastCommitRate:F3} rather than {expectedTrialRate:F3}, so the two arms disagree on a protocol fact.");
            failures.Require(oracle.WriterFastCommitRate == expectedWriterRate, $"At a {VirtualTimePump.ToMilliseconds(hedgeMicroseconds):F0}ms hedge the oracle arm's writer rate is {oracle.WriterFastCommitRate:F3} rather than {expectedWriterRate:F3}.");
        }
    }


    /// <summary>
    /// Plan section 2.2b puts the stand-down path in scope: the pump seam is what makes it reachable at all.
    /// The grid supplies no learn signal, so this is the only place the path is exercised, and the disposition
    /// a stood-down writer takes is pinned here rather than left to whichever row first meets one.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void StandDownSeamIsReachable(VectorFailures failures)
    {
        static ValueTask<bool> AlreadyDriven(FastBallot fastBallot, CancellationToken cancellationToken) => ValueTask.FromResult(true);

        Topology placement = Topologies.ProbeSpread();
        const long baseDelay = 50_000;

        ImmutableArray<FastWriterMeasurement> withSignal = FastCasPaxosArm.RunTrial(new FastTrialRequest(
            placement, 2, [0, 0], VirtualTimePump.ToTimeSpan(baseDelay), SeedMixer.TrialSeed(67, 0), JitterModel.None,
            FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget, _ => AlreadyDriven));

        failures.Require(withSignal[1].StoodDown, $"A writer told the round was already driven reports activated {withSignal[1].Activated}, so the stand-down path is unreachable and its handling is dead weight.");
        failures.Require(withSignal[1].AddedWaitMicroseconds == baseDelay, $"The stood-down writer reports {withSignal[1].AddedWaitMicroseconds}us of added wait rather than the delay it waited before standing down.");
        failures.Require(withSignal[1].PhasesExecuted == 0, $"The stood-down writer put {withSignal[1].PhasesExecuted} phases on the transport, so it did not stand down at all.");
        failures.Require(withSignal[1].CommitMicroseconds is null, $"The stood-down writer reports a commit at {withSignal[1].CommitMicroseconds}us.");

        //A stood-down writer is its own disposition: it owes no recovery, so it is not a censored write, and
        //it carries no latency, so it is not a sample. The host must reissue it.
        failures.Require(!withSignal[1].IsCensored, $"The stood-down writer is counted as a censored write at {withSignal[1].PhasesExecuted} phases, which would rank a write that sent nothing above every write that finished.");
        failures.Require(withSignal[1].GiveUpMicroseconds is null, $"The stood-down writer reports giving up at {withSignal[1].GiveUpMicroseconds}us, which is a reading only a spent ladder has.");

        //The writer first in the schedule waits nothing and is never asked, which is the shipped contract.
        failures.Require(!withSignal[0].StoodDown, $"The writer first in the schedule stood down after {withSignal[0].AddedWaitMicroseconds}us, though it waits no delay and is never asked.");
        failures.Require(withSignal[0].IsCommitted, $"The writer first in the schedule did not commit though every other writer stood down; it reached a fast quorum: {withSignal[0].ReachedFastQuorum}.");

        //THE GRID KEEPS NO LEARN SIGNAL, so the same run without one activates everybody and the stand-down
        //column of every cell row is zero by construction rather than by luck.
        ImmutableArray<FastWriterMeasurement> withoutSignal = FastCasPaxosArm.RunTrial(new FastTrialRequest(
            placement, 2, [0, 0], VirtualTimePump.ToTimeSpan(baseDelay), SeedMixer.TrialSeed(67, 0), JitterModel.None,
            FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

        failures.Require(withoutSignal.All(measurement => !measurement.StoodDown), $"A trial with no learn signal stood {withoutSignal.Count(measurement => measurement.StoodDown)} writers down, so the grid's rows could carry a stand-down the campaign never configured.");
    }


    /// <summary>
    /// The absent-leader configurations are led by a lane no writer holds. A lane indexed by the replica count
    /// alone is writer number replicaCount's own lane whenever there are more writers than replicas, which
    /// turns the configuration into a led one without saying so.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void AbsentLeaderLaneIsAboveEveryWriter(VectorFailures failures)
    {
        Topology placement = Topologies.MultiAvailabilityZone(3);
        const int writerCount = 5;
        ImmutableArray<long> delays = StaggerSchedule.Delays(writerCount, 0);

        ImmutableArray<QuePaxaWriterMeasurement> absent = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
            placement, writerCount, LeadershipMode.AbsentLeader, delays, delays, SeedMixer.TrialSeed(68, 0), JitterModel.None, QuePaxaArm.DefaultEventBudget));

        foreach(QuePaxaWriterMeasurement measurement in absent)
        {
            failures.Require(measurement.PriorityDraws > 0, $"Writer {measurement.Writer} of {writerCount} drew no priorities under an absent leader at {placement.SiteCount} replicas, so it holds the believed leader's lane and the configuration is a led one.");
        }

        //Non-vacuity: the signature the check above excludes is real, and this harness can see it.
        ImmutableArray<QuePaxaWriterMeasurement> led = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
            placement, writerCount, LeadershipMode.WriterZeroLeads, delays, delays, SeedMixer.TrialSeed(68, 0), JitterModel.None, QuePaxaArm.DefaultEventBudget));

        failures.Require(led[0].PriorityDraws == 0, $"A writer that believes it leads drew {led[0].PriorityDraws} priorities, so the leader signature the check above excludes never appears at all.");
    }


    /// <summary>
    /// A faulted client is completed, so a guard that reads only completion accepts it as quiescent. The Fast
    /// arm then reads a half-filled state and reports the exception as an unfinished write, in the same column
    /// a spent recovery ladder lands in.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void PumpReportsAFaultedClient(VectorFailures failures)
    {
        var cause = new InvalidOperationException("The harness's client threw while the pump was draining.");
        var pump = new VirtualTimePump(100);
        var release = new TaskCompletionSource();
        var clients = new Task[1];
        clients[0] = Task.CompletedTask;

        pump.ScheduleAt(0, () => clients[0] = ThrowWhenReleasedAsync(release.Task, cause));
        pump.ScheduleAt(1000, release.SetResult);

        InvalidOperationException? reported = null;
        try
        {
            pump.Run(clients);
        }
        catch(InvalidOperationException exception)
        {
            reported = exception;
        }

        failures.Require(reported is not null, $"A client that threw mid-run left the pump returning normally at {pump.Now}us, so an arm's exception is reported as an unfinished write rather than as the defect it is.");
        failures.Require(ReferenceEquals(reported?.InnerException, cause), $"The pump reported '{reported?.InnerException?.Message ?? "no cause at all"}' rather than the client's own exception.");
        failures.Require(reported?.Message.Contains("Client 0", StringComparison.Ordinal) == true, $"The pump's report does not name the client that faulted: '{reported?.Message}'.");
    }


    private static async Task ThrowWhenReleasedAsync(Task release, Exception cause)
    {
        await release.ConfigureAwait(false);

        throw cause;
    }


    private static FastWriterMeasurement Committed(int writer, string value) => new(
        writer, writer, true, 0, 0, 4, 200, 200, true, false, 0, 1, true, 200, null, value);


    private static FastWriterMeasurement Censored(int writer) => new(
        writer, writer, true, 0, 0, 1, 200, null, false, true, FastCasPaxosArm.DefaultMaxRecoveryAttempts, 17, false, null, 4000, null);


    private static void PumpFailsClosed(VectorFailures failures)
    {
        //A run that cannot drain inside its budget must report rather than spin.
        const long tinyBudget = 2;
        bool budgetReported = false;
        try
        {
            ImmutableArray<long> single = StaggerSchedule.Delays(1, 0);
            _ = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(Topologies.ProbeSpread(), 1, LeadershipMode.WriterZeroLeads, single, single, SeedMixer.TrialSeed(71, 0), JitterModel.None, tinyBudget));
        }
        catch(InvalidOperationException)
        {
            budgetReported = true;
        }

        failures.Require(budgetReported, $"A run past its event budget of {tinyBudget} returned instead of reporting, so a wedged trial would hang the campaign.");

        //A schedule that admits the past is not a clock, and the arrival order every measurement rests on
        //would stop being a function of the seed.
        const long future = 1000;
        bool pastReported = false;
        try
        {
            var pump = new VirtualTimePump(100);
            pump.ScheduleAt(future, () => pump.ScheduleAt(0, () => { }));
            pump.Run([]);
        }
        catch(InvalidOperationException)
        {
            pastReported = true;
        }

        failures.Require(pastReported, $"An event scheduled into the past was accepted while the clock stood at {future}us.");
    }


    /// <summary>
    /// The gate's tolerances are part of the published record it certifies: a silently widened band admits
    /// figures the record never printed while every other assertion stays green. Rates and means carry half
    /// their last published digit, a decision time carries the whole-millisecond band its double-rounded
    /// column actually holds, and a count is exact.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void W5TolerancesAreThePublishedRecord(VectorFailures failures)
    {
        failures.Require(ReproductionGate.RateTolerance == 0.0005, $"The rate tolerance is {ReproductionGate.RateTolerance} rather than half the last digit of a three-decimal published rate.");
        failures.Require(ReproductionGate.MeanTolerance == 0.005, $"The mean tolerance is {ReproductionGate.MeanTolerance} rather than half the last digit of a two-decimal published mean.");
        failures.Require(ReproductionGate.MillisecondTolerance == 1.0, $"The decision-time tolerance is {ReproductionGate.MillisecondTolerance} rather than the whole-millisecond band the published column carries.");
        failures.Require(ReproductionGate.CountTolerance == 0.0, $"The count tolerance is {ReproductionGate.CountTolerance} rather than exact.");
    }


    /// <summary>The majority-radius round trip the verdict vectors price their cells in, which is 120 milliseconds.</summary>
    private const long VerdictMajorityRadiusMicroseconds = 120_000;


    /// <summary>
    /// A row carrying only what the verdict reducer reads, so that a vector's expected verdict is computable
    /// by hand from the numbers on the page rather than from a measurement.
    /// </summary>
    /// <param name="mode">The configuration.</param>
    /// <param name="rung">The ladder rung as an operator configures it.</param>
    /// <param name="rungMicroseconds">The same rung in absolute microseconds.</param>
    /// <param name="population">The p95 over every write of the row, which the verdict must not be read at.</param>
    /// <param name="representative">The representative writer's own p95, which is the column the verdict is read at.</param>
    /// <param name="agreed">Whether every trial of the configuration agreed.</param>
    /// <returns>The row.</returns>
    private static MeasuredRow VerdictRow(ConfigurationMode mode, double rung, long rungMicroseconds, PercentileReading population, PercentileReading representative, bool agreed) => new(
        mode,
        rung,
        rungMicroseconds,
        0.00,
        PercentileReading.None,
        population,
        PercentileReading.None,
        PercentileReading.None,
        representative,
        PercentileReading.None,
        0,
        0,
        0,
        0.0,
        0.0,
        0.0,
        0.0,
        0.0,
        agreed);


    /// <summary>
    /// Agreement is a gate rather than a metric, so the cell's fastest configuration is removed when it failed
    /// agreement in any trial, and a protocol left with no surviving configuration loses the cell whatever its
    /// numbers said.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictGateARemovesAndDefaultsToTheOtherProtocol(VectorFailures failures)
    {
        MeasuredRow fastest = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.At(10.0), PercentileReading.At(10.0), false);
        MeasuredRow alsoDisagreed = VerdictRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0, PercentileReading.At(20.0), PercentileReading.At(20.0), false);
        MeasuredRow slowest = VerdictRow(ConfigurationMode.FastUnhedged, 0.00, 0, PercentileReading.At(100.0), PercentileReading.At(100.0), true);

        CellVerdict verdict = VerdictReducer.ReduceSpread([fastest, alsoDisagreed, slowest], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(ReferenceEquals(verdict.Winner, slowest), $"The cell went to {verdict.Winner?.Key ?? "nothing at all"} at a p95 of {verdict.Winner?.RepresentativeP95.ToString() ?? "none"}ms while every QuePaxa configuration in it had failed agreement, so a removed configuration decided the cell.");
        failures.Require(verdict.Outcome == VerdictOutcome.Winner, $"The sole surviving configuration produced a {verdict.OutcomeName} rather than a winner.");
        failures.Require(verdict.Removed.Length == 2, $"Gate A removed {verdict.Removed.Length} configurations rather than the two that failed agreement.");
        failures.Require(verdict.Removed.All(removal => removal.Reason.Contains("gate A", StringComparison.Ordinal)), $"A removal was reported without naming gate A: '{string.Join("; ", verdict.Removed.Select(removal => removal.Reason))}'.");
        failures.Require(verdict.Reason.Contains("unconditionally", StringComparison.Ordinal), $"The cell went to the only protocol with a survivor without saying so: '{verdict.Reason}'.");
        failures.Require(verdict.RunnerUp is null, $"A cell holding one candidate reported {verdict.RunnerUp?.Key} as a runner-up.");
    }


    /// <summary>
    /// A cell in which nothing agreed is void rather than slow, and a void cell names no winner at all.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictVoidCellHasNoWinner(VectorFailures failures)
    {
        MeasuredRow quePaxa = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.At(10.0), PercentileReading.At(10.0), false);
        MeasuredRow fast = VerdictRow(ConfigurationMode.FastUnhedged, 0.00, 0, PercentileReading.At(20.0), PercentileReading.At(20.0), false);

        CellVerdict verdict = VerdictReducer.ReduceSpread([quePaxa, fast], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(verdict.Outcome == VerdictOutcome.Void, $"A cell in which neither protocol agreed reported {verdict.OutcomeName}.");
        failures.Require(verdict.Winner is null, $"A void cell named {verdict.Winner?.Key} as its winner.");
        failures.Require(verdict.RunnerUp is null, $"A void cell named {verdict.RunnerUp?.Key} as its runner-up.");
        failures.Require(verdict.Removed.Length == 2, $"A void cell reported {verdict.Removed.Length} removals rather than the two configurations it removed.");
        failures.Require(verdict.Reason.Contains("void", StringComparison.Ordinal), $"A void cell did not say it was void: '{verdict.Reason}'.");
        failures.Require(double.IsPositiveInfinity(verdict.Margin), $"A void cell carries a margin of {verdict.Margin} rather than none at all.");
    }


    /// <summary>
    /// An unbounded tail compares as positive infinity and can never win, however simple the configuration
    /// carrying it is, and a cell of nothing but unbounded tails is void rather than won on a marker.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictUnboundedTailCannotWin(VectorFailures failures)
    {
        MeasuredRow unbounded = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.Unbounded, PercentileReading.Unbounded, true);
        MeasuredRow bounded = VerdictRow(ConfigurationMode.FastHedged, 1.50, 270_000, PercentileReading.At(100.0), PercentileReading.At(100.0), true);

        CellVerdict verdict = VerdictReducer.ReduceSpread([unbounded, bounded], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(ReferenceEquals(verdict.Winner, bounded), $"The cell went to {verdict.Winner?.Key ?? "nothing at all"}, so the unbounded tail of the simplest configuration in the cell outranked a measured one.");
        failures.Require(verdict.Removed.IsEmpty, $"An unbounded tail was removed rather than ranked: '{string.Join("; ", verdict.Removed.Select(removal => removal.Reason))}'.");
        failures.Require(ReferenceEquals(verdict.RunnerUp, unbounded), $"The runner-up is {verdict.RunnerUp?.Key ?? "absent"} rather than the configuration whose tail is unbounded.");
        failures.Require(double.IsPositiveInfinity(verdict.Margin), $"The margin against an unbounded runner-up is {verdict.Margin} rather than unbounded.");

        MeasuredRow alsoUnbounded = VerdictRow(ConfigurationMode.FastUnhedged, 0.00, 0, PercentileReading.Unbounded, PercentileReading.Unbounded, true);
        CellVerdict everythingUnbounded = VerdictReducer.ReduceSpread([unbounded, alsoUnbounded], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(everythingUnbounded.Outcome == VerdictOutcome.Void, $"A cell whose every tail is unbounded reported {everythingUnbounded.OutcomeName} with {everythingUnbounded.Winner?.Key ?? "no winner"}.");
        failures.Require(everythingUnbounded.Reason.Contains("unbounded", StringComparison.Ordinal), $"A cell voided for unbounded tails did not say so: '{everythingUnbounded.Reason}'.");
    }


    /// <summary>
    /// A configuration the cell holds no observation for cannot win and is reported: an absent population is
    /// not a slow one, and a row that measured nothing must not sit in the order beside rows that did.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictNoSampleIsRemoved(VectorFailures failures)
    {
        MeasuredRow silent = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.None, PercentileReading.None, true);
        MeasuredRow measured = VerdictRow(ConfigurationMode.FastHedged, 1.50, 270_000, PercentileReading.At(100.0), PercentileReading.At(100.0), true);

        CellVerdict verdict = VerdictReducer.ReduceSpread([silent, measured], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(ReferenceEquals(verdict.Winner, measured), $"The cell went to {verdict.Winner?.Key ?? "nothing at all"} rather than to the only configuration that produced a sample.");
        failures.Require(verdict.Removed.Length == 1 && ReferenceEquals(verdict.Removed[0].Row, silent), $"A configuration with no sample at all was not removed and reported; {verdict.Removed.Length} removals were reported.");
        failures.Require(verdict.Removed.Length == 1 && verdict.Removed[0].Reason.Contains("no sample", StringComparison.Ordinal), $"The removal did not say the representative writer produced no sample: '{string.Join("; ", verdict.Removed.Select(removal => removal.Reason))}'.");
        failures.Require(verdict.RunnerUp is null, $"A removed configuration was reported as the runner-up: {verdict.RunnerUp?.Key}.");

        MeasuredRow alsoSilent = VerdictRow(ConfigurationMode.FastUnhedged, 0.00, 0, PercentileReading.None, PercentileReading.None, true);
        CellVerdict everythingSilent = VerdictReducer.ReduceSpread([silent, alsoSilent], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(everythingSilent.Outcome == VerdictOutcome.Void, $"A cell in which nothing was observed reported {everythingSilent.OutcomeName} with {everythingSilent.Winner?.Key ?? "no winner"}.");
    }


    /// <summary>
    /// The verdict is the argmin of the REPRESENTATIVE writer's p95, which is the writer the cell speaks for,
    /// and it outranks the tie-break: the least simple configuration in the cell wins it when it is fastest by
    /// more than the band.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictArgminReadsTheRepresentativeWriter(VectorFailures failures)
    {
        MeasuredRow staggered = VerdictRow(ConfigurationMode.FastHedged, 0.50, 90_000, PercentileReading.At(500.0), PercentileReading.At(100.0), true);
        MeasuredRow leaderless = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.At(10.0), PercentileReading.At(200.0), true);
        MeasuredRow leadered = VerdictRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0, PercentileReading.At(1.0), PercentileReading.At(300.0), true);

        CellVerdict verdict = VerdictReducer.ReduceSpread([leaderless, leadered, staggered], 0.00, VerdictMajorityRadiusMicroseconds, null);

        failures.Require(ReferenceEquals(verdict.Winner, staggered), $"The cell went to {verdict.Winner?.Key ?? "nothing at all"} rather than to the configuration whose representative writer paid the least, so the verdict was read at a column the cell does not speak for.");
        failures.Require(ReferenceEquals(verdict.RunnerUp, leaderless), $"The runner-up is {verdict.RunnerUp?.Key ?? "absent"} rather than the second-lowest representative reading.");
        failures.Require(verdict.Margin == 1.0, $"The margin between 200ms and 100ms is {verdict.Margin} rather than 1.0.");
        failures.Require(verdict.Outcome == VerdictOutcome.Winner, $"A cell won by a factor of two reported {verdict.OutcomeName}.");
    }


    /// <summary>
    /// The band that publishes "either" is read as the runner-up's relative excess over the winner, and a
    /// configuration exactly at ten percent is OUTSIDE it: the band and the tie-break share one boundary, and a
    /// boundary belonging to both would decide a cell by the order the rules happen to run in.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictEitherBandAndItsExactBoundary(VectorFailures failures)
    {
        (double Reading, double Margin, VerdictOutcome Outcome)[] cases =
        [
            (109.0, 0.09, VerdictOutcome.Either),
            (110.0, 0.10, VerdictOutcome.Winner),
            (111.0, 0.11, VerdictOutcome.Winner)
        ];

        foreach((double reading, double margin, VerdictOutcome outcome) in cases)
        {
            MeasuredRow best = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.At(100.0), PercentileReading.At(100.0), true);
            MeasuredRow other = VerdictRow(ConfigurationMode.FastUnhedged, 0.00, 0, PercentileReading.At(reading), PercentileReading.At(reading), true);

            CellVerdict verdict = VerdictReducer.ReduceSpread([best, other], 0.00, VerdictMajorityRadiusMicroseconds, null);

            failures.Require(ReferenceEquals(verdict.Winner, best), $"At a runner-up reading of {reading}ms the cell went to {verdict.Winner?.Key ?? "nothing at all"} rather than to the lower reading of the two equally simple configurations.");
            failures.Require(verdict.Margin == margin, $"At a runner-up reading of {reading}ms against 100ms the margin is {verdict.Margin} rather than {margin}.");
            failures.Require(verdict.Outcome == outcome, $"At a margin of {margin} the cell is published as {verdict.OutcomeName} rather than as {(outcome == VerdictOutcome.Either ? "either" : "winner")}, so the ten-percent band does not hold at its own boundary.");
        }
    }


    /// <summary>
    /// Inside the band the simpler mode is preferred, ordering leaderless above leadered above staggered, and a
    /// rung outranks the mode it was configured on. A tie-break that fires publishes "either", because it
    /// promotes a configuration that is not the fastest.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictTieBreakOrdersTheSimplerModeFirst(VectorFailures failures)
    {
        MeasuredRow staggered = VerdictRow(ConfigurationMode.FastHedged, 0.50, 90_000, PercentileReading.At(100.0), PercentileReading.At(100.0), true);
        MeasuredRow leadered = VerdictRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0, PercentileReading.At(101.0), PercentileReading.At(101.0), true);
        MeasuredRow leaderless = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.At(102.0), PercentileReading.At(102.0), true);
        MeasuredRow staggeredLeaderless = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 1.00, 120_000, PercentileReading.At(100.0), PercentileReading.At(100.0), true);

        failures.Require(VerdictReducer.SimplicityOf(staggered) == ModeSimplicity.Staggered, $"A hedged Fast CASPaxos configuration ranks {VerdictReducer.SimplicityOf(staggered)} rather than staggered.");
        failures.Require(VerdictReducer.SimplicityOf(leadered) == ModeSimplicity.Leadered, $"An unstaggered leadered configuration ranks {VerdictReducer.SimplicityOf(leadered)} rather than leadered.");
        failures.Require(VerdictReducer.SimplicityOf(leaderless) == ModeSimplicity.Leaderless, $"An unstaggered leaderless configuration ranks {VerdictReducer.SimplicityOf(leaderless)} rather than leaderless.");
        failures.Require(VerdictReducer.SimplicityOf(staggeredLeaderless) == ModeSimplicity.Staggered, $"A leaderless configuration at a nonzero rung ranks {VerdictReducer.SimplicityOf(staggeredLeaderless)}, so the ladder's knob is not priced as the liability it is.");

        CellVerdict allThree = VerdictReducer.ReduceSpread([staggered, leadered, leaderless], 0.00, VerdictMajorityRadiusMicroseconds, null);
        failures.Require(ReferenceEquals(allThree.Winner, leaderless), $"Inside the band the cell preferred {allThree.Winner?.Key ?? "nothing at all"} over the leaderless configuration.");
        failures.Require(allThree.Outcome == VerdictOutcome.Either, $"A cell decided by the tie-break is published as {allThree.OutcomeName} rather than as either.");

        CellVerdict withoutLeaderless = VerdictReducer.ReduceSpread([staggered, leadered], 0.00, VerdictMajorityRadiusMicroseconds, null);
        failures.Require(ReferenceEquals(withoutLeaderless.Winner, leadered), $"Inside the band the cell preferred {withoutLeaderless.Winner?.Key ?? "nothing at all"} over the leadered configuration.");
        failures.Require(withoutLeaderless.Outcome == VerdictOutcome.Either, $"A cell decided by the tie-break is published as {withoutLeaderless.OutcomeName} rather than as either.");

        CellVerdict withoutLeadered = VerdictReducer.ReduceSpread([staggered, leaderless], 0.00, VerdictMajorityRadiusMicroseconds, null);
        failures.Require(ReferenceEquals(withoutLeadered.Winner, leaderless), $"Inside the band the cell preferred {withoutLeadered.Winner?.Key ?? "nothing at all"} over the leaderless configuration.");
    }


    /// <summary>
    /// The winning rung is published in units of the cell's majority-radius round trip, which is the one unit
    /// both arms can be prescribed in, beside the rung as it was configured. A Fast CASPaxos rung is a fraction
    /// of the fast-quorum round trip and converts through the absolute microseconds the row carries; a QuePaxa
    /// rung is already in the published unit and converts to itself.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictWinningRungIsExpressedInMajorityRadius(VectorFailures failures)
    {
        //Half of a 180ms fast round is 90ms, which is three quarters of the cell's 120ms majority round.
        MeasuredRow fastHedge = VerdictRow(ConfigurationMode.FastHedged, 0.50, 90_000, PercentileReading.At(100.0), PercentileReading.At(100.0), true);
        MeasuredRow quePaxaStagger = VerdictRow(ConfigurationMode.QuePaxaLeadered, 0.25, 30_000, PercentileReading.At(500.0), PercentileReading.At(500.0), true);

        CellVerdict fastWins = VerdictReducer.ReduceSpread([fastHedge, quePaxaStagger], 0.00, VerdictMajorityRadiusMicroseconds, null);
        failures.Require(fastWins.WinningRungInMajorityRadius == 0.75, $"A Fast CASPaxos rung of 0.50 fast-quorum rounds, 90ms against the cell's 120ms majority round, is published as {fastWins.WinningRungInMajorityRadius} rather than 0.75 majority rounds.");
        failures.Require(fastWins.Winner?.Rung == 0.50, $"The configured rung is published as {fastWins.Winner?.Rung}, so the operator loses the number their own knob takes.");

        MeasuredRow slowHedge = VerdictRow(ConfigurationMode.FastHedged, 0.50, 90_000, PercentileReading.At(500.0), PercentileReading.At(500.0), true);
        MeasuredRow fastStagger = VerdictRow(ConfigurationMode.QuePaxaLeadered, 0.25, 30_000, PercentileReading.At(100.0), PercentileReading.At(100.0), true);

        CellVerdict quePaxaWins = VerdictReducer.ReduceSpread([slowHedge, fastStagger], 0.00, VerdictMajorityRadiusMicroseconds, null);
        failures.Require(quePaxaWins.WinningRungInMajorityRadius == 0.25, $"A QuePaxa rung of 0.25 majority rounds is published as {quePaxaWins.WinningRungInMajorityRadius} rather than as itself.");
        failures.Require(VerdictReducer.RungInMajorityRadius(fastHedge, VerdictMajorityRadiusMicroseconds) == 0.75, $"The conversion prices 90ms at {VerdictReducer.RungInMajorityRadius(fastHedge, VerdictMajorityRadiusMicroseconds)} of a 120ms round.");
    }


    /// <summary>
    /// Gate B is inert without a measured read-modify-write retry rate, which is the interchangeable-update
    /// shape of the workload. With one it removes the QuePaxa configurations that re-propose too often and
    /// ONLY those: the settled rule is about QuePaxa's retry-on-conflict, so a Fast CASPaxos rate is not a
    /// removal, and a rate exactly at the ceiling is not above it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void VerdictGateBRemovesARetriedQuePaxaConfiguration(VectorFailures failures)
    {
        MeasuredRow quePaxa = VerdictRow(ConfigurationMode.QuePaxaLeaderless, 0.00, 0, PercentileReading.At(100.0), PercentileReading.At(100.0), true);
        MeasuredRow fast = VerdictRow(ConfigurationMode.FastUnhedged, 0.00, 0, PercentileReading.At(200.0), PercentileReading.At(200.0), true);
        MeasuredRow[] rows = [quePaxa, fast];

        CellVerdict inert = VerdictReducer.ReduceSpread(rows, 0.00, VerdictMajorityRadiusMicroseconds, null);
        failures.Require(ReferenceEquals(inert.Winner, quePaxa), $"With no measured retry rate the gate must be inert, and the cell went to {inert.Winner?.Key ?? "nothing at all"}.");
        failures.Require(inert.Removed.IsEmpty, $"An inert gate removed {inert.Removed.Length} configurations.");

        CellVerdict retried = VerdictReducer.ReduceSpread(rows, 0.00, VerdictMajorityRadiusMicroseconds, row => row.Protocol == ProtocolKind.QuePaxa ? 0.11 : 0.99);
        failures.Require(ReferenceEquals(retried.Winner, fast), $"A QuePaxa configuration retrying 11 percent of its writes under a read-modify-write workload won the cell as {retried.Winner?.Key ?? "nothing at all"}.");
        failures.Require(retried.Removed.Length == 1 && ReferenceEquals(retried.Removed[0].Row, quePaxa), $"Gate B removed {retried.Removed.Length} configurations rather than the one QuePaxa configuration above the ceiling; a Fast CASPaxos rate is not a removal.");
        failures.Require(retried.Removed.Length == 1 && retried.Removed[0].Reason.Contains("gate B", StringComparison.Ordinal), $"The removal did not name gate B: '{string.Join("; ", retried.Removed.Select(removal => removal.Reason))}'.");

        CellVerdict atTheCeiling = VerdictReducer.ReduceSpread(rows, 0.00, VerdictMajorityRadiusMicroseconds, _ => VerdictReducer.RetryRateCeiling);
        failures.Require(ReferenceEquals(atTheCeiling.Winner, quePaxa), $"A retry rate of exactly {VerdictReducer.RetryRateCeiling} is not above the ceiling, and the cell went to {atTheCeiling.Winner?.Key ?? "nothing at all"}.");

        CellVerdict unmeasured = VerdictReducer.ReduceSpread(rows, 0.00, VerdictMajorityRadiusMicroseconds, _ => null);
        failures.Require(ReferenceEquals(unmeasured.Winner, quePaxa), $"A configuration the rider holds no figure for was removed on an absence, and the cell went to {unmeasured.Winner?.Key ?? "nothing at all"}.");
    }


    /// <summary>The placement the read-modify-write vectors contend on, which is the widest quorum gap in the grid.</summary>
    private static Topology RmwPlacement { get; } = Topologies.Global(5);


    /// <summary>The writer count the read-modify-write vectors contend at.</summary>
    private const int RmwWriters = 3;


    /// <summary>How many seeds a read-modify-write vector sweeps when it needs an event to occur at all.</summary>
    private const int RmwSweepSeeds = 16;


    /// <summary>The arrivals of a trial in which every writer starts at once.</summary>
    private static ImmutableArray<long> RmwSimultaneous { get; } = [.. Enumerable.Repeat(0L, RmwWriters)];


    /// <summary>
    /// One QuePaxa read-modify-write trial at <paramref name="seed"/>, with every writer starting at once and
    /// nothing staggered.
    /// </summary>
    /// <param name="seed">The trial index the seed is derived from.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <returns>The trial's outcome.</returns>
    /// <remarks>
    /// A jitterless trial is what the semantic vectors want, because the arrival order is then the placement's
    /// alone and a failing assertion names a protocol rule rather than a draw. It is also the one setting under
    /// which the seed reaches nothing on the Fast CASPaxos arm, which has no priority stream at all, so the
    /// determinism vectors ask for a jittered model instead.
    /// </remarks>
    private static RmwTrialOutcome<RmwQuePaxaWriterMeasurement> RmwQuePaxaTrial(int seed, JitterModel jitter) => RmwQuePaxaArm.RunTrial(new RmwQuePaxaTrialRequest(
        RmwPlacement,
        RmwWriters,
        RmwSimultaneous,
        0,
        SeedMixer.TrialSeed(83, seed),
        jitter,
        RmwQuePaxaArm.DefaultMaxAttempts,
        RmwQuePaxaArm.DefaultAttemptsPerRecorder,
        RmwQuePaxaArm.DefaultEventBudget));


    /// <summary>
    /// One Fast CASPaxos read-modify-write trial at <paramref name="seed"/>, with every writer starting at once
    /// and nothing hedged.
    /// </summary>
    /// <param name="seed">The trial index the seed is derived from.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <returns>The trial's outcome.</returns>
    private static RmwTrialOutcome<RmwFastWriterMeasurement> RmwFastTrial(int seed, JitterModel jitter) => RmwFastCasPaxosArm.RunTrial(new RmwFastTrialRequest(
        RmwPlacement,
        RmwWriters,
        RmwSimultaneous,
        TimeSpan.Zero,
        SeedMixer.TrialSeed(83, seed),
        jitter,
        RmwFastCasPaxosArm.DefaultMaxRecoveryRounds,
        RmwFastCasPaxosArm.DefaultEventBudget));


    /// <summary>
    /// The workload's change is a read-modify-write and carries its own apply-once token: the value it
    /// produces is a function of the value it was given, and applying one writer's change to a value that
    /// already carries it changes nothing.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwChangeFunctionIsReadModifyWrite(VectorFailures failures)
    {
        failures.Require(RmwFold.Apply(null, 'a') == "a", $"The change applied to an unwritten register produced '{RmwFold.Apply(null, 'a')}' rather than the writer's own token alone.");
        failures.Require(RmwFold.Apply("a", 'b') == "ab", $"The change applied to 'a' produced '{RmwFold.Apply("a", 'b')}', so the value it proposes does not depend on the value it read.");
        failures.Require(RmwFold.Apply("ba", 'c') == "bac", $"The change applied to 'ba' produced '{RmwFold.Apply("ba", 'c')}', so the order the value records is not the order the changes landed in.");
        failures.Require(RmwFold.Apply("ab", 'b') == "ab", $"The change applied to a value already carrying its own token produced '{RmwFold.Apply("ab", 'b')}', so a change recovered back into its own round would be counted twice.");

        //The tokens have to be injective, or two writers' changes would be one change and every count below
        //would be measured over a workload with fewer writers than the trial ran.
        for(int writer = 0; writer < RmwFold.MaximumWriters; writer++)
        {
            failures.Require(RmwFold.WriterOf(RmwFold.Token(writer)) == writer, $"Writer {writer}'s token maps back to writer {RmwFold.WriterOf(RmwFold.Token(writer))}.");
        }

        failures.Require(RmwFold.WriterOf('A') < 0, $"The token 'A' maps to writer {RmwFold.WriterOf('A')} rather than to no writer at all.");
        failures.Require(!RmwFold.Carries(null, 'a'), $"An unwritten register was reported as carrying a token.");
        failures.Require(RmwFold.Carries("cab", 'a'), $"The value 'cab' was reported as not carrying 'a'.");
    }


    /// <summary>
    /// A value carrying one writer's token twice is a change applied twice, which is what a recovery composing
    /// a writer's own value back on top of itself produces, and the oracle names it rather than tolerating it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwOracleRejectsADuplicatedChange(VectorFailures failures)
    {
        RmwFoldVerdict verdict = RmwFold.Check("aba", ['a', 'b'], 3);

        failures.Require(!verdict.Holds, $"The oracle accepted 'aba', in which one writer's change is applied twice.");
        failures.Require(verdict.Reason.Contains("twice", StringComparison.Ordinal), $"The oracle rejected a duplicated change without saying so: '{verdict.Reason}'.");
    }


    /// <summary>
    /// A committed writer's token missing from the committed value is a lost update, which is what a proposer
    /// re-proposing a stale value or applying its change to a stale base produces.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwOracleRejectsALostChange(VectorFailures failures)
    {
        RmwFoldVerdict verdict = RmwFold.Check("b", ['a', 'b'], 3);

        failures.Require(!verdict.Holds, $"The oracle accepted 'b' while the writer holding 'a' reported its change committed.");
        failures.Require(verdict.Reason.Contains("lost", StringComparison.Ordinal), $"The oracle rejected a lost change without saying so: '{verdict.Reason}'.");
    }


    /// <summary>
    /// A token belonging to no writer of the trial is a corrupted value, and the oracle separates it from the
    /// two failures a protocol can produce.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwOracleRejectsAForeignToken(VectorFailures failures)
    {
        RmwFoldVerdict verdict = RmwFold.Check("az", ['a'], 3);

        failures.Require(!verdict.Holds, $"The oracle accepted 'az' in a trial of three writers, so a token no writer holds is not caught.");
        failures.Require(verdict.Reason.Contains("no writer", StringComparison.Ordinal), $"The oracle rejected a foreign token without saying so: '{verdict.Reason}'.");
    }


    /// <summary>
    /// The oracle accepts exactly the sequential folds: any order of distinct tokens, an unwritten register
    /// with nothing committed, and a value carrying the token of a writer that never learned its own change
    /// landed. The last of those is a client-side timeout rather than a protocol violation.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwOracleAcceptsTheSequentialFold(VectorFailures failures)
    {
        foreach(string fold in new[] { "abc", "cab", "bca" })
        {
            RmwFoldVerdict verdict = RmwFold.Check(fold, ['a', 'b', 'c'], 3);
            failures.Require(verdict.Holds, $"The oracle rejected the sequential fold '{fold}': {verdict.Reason}");
        }

        RmwFoldVerdict unwritten = RmwFold.Check(null, [], 3);
        failures.Require(unwritten.Holds, $"The oracle rejected an unwritten register with nothing committed: {unwritten.Reason}");

        RmwFoldVerdict censored = RmwFold.Check("ab", ['a'], 3);
        failures.Require(censored.Holds, $"The oracle rejected a value carrying the token of a writer that spent its budget without learning its own change had landed: {censored.Reason}");
    }


    /// <summary>
    /// A lone writer has nobody to lose to, so its change costs exactly one consensus instance on QuePaxa and
    /// exactly one blind fast round on Fast CASPaxos, and neither arm reports a conflict.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwUncontendedWriteCostsOneRound(VectorFailures failures)
    {
        foreach((int replicaCount, Topology placement) in EveryPlacement())
        {
            ComputedSiteCost computed = QuorumDistance.For(placement)[0];

            RmwTrialOutcome<RmwQuePaxaWriterMeasurement> quePaxaTrial = RmwQuePaxaArm.RunTrial(new RmwQuePaxaTrialRequest(
                placement, 1, [0], 0, SeedMixer.TrialSeed(87, replicaCount), JitterModel.None,
                RmwQuePaxaArm.DefaultMaxAttempts, RmwQuePaxaArm.DefaultAttemptsPerRecorder, RmwQuePaxaArm.DefaultEventBudget));

            RmwQuePaxaWriterMeasurement quePaxa = quePaxaTrial.Writers[0];
            failures.Require(quePaxa.IsCommitted, $"{placement.Name} at {replicaCount}: the uncontended QuePaxa read-modify-write did not commit.");
            failures.Require(quePaxa.Attempts == 1, $"{placement.Name} at {replicaCount}: the uncontended QuePaxa read-modify-write spent {quePaxa.Attempts} attempts rather than one.");
            failures.Require(quePaxa.ConflictRecomputes == 0, $"{placement.Name} at {replicaCount}: a lone QuePaxa writer reported {quePaxa.ConflictRecomputes} conflicts.");
            failures.Require(quePaxa.CommittedValue == "a", $"{placement.Name} at {replicaCount}: the lone QuePaxa writer committed '{quePaxa.CommittedValue}' rather than its own token alone.");
            failures.Require(quePaxa.CommitMicroseconds == computed.QuePaxaLeaderRoundTrip, $"{placement.Name} at {replicaCount}: the simulated QuePaxa read-modify-write took {quePaxa.CommitMicroseconds}us against the computed majority radius of {computed.QuePaxaLeaderRoundTrip}us.");

            //THE ORACLE READS THE REPLICAS, so a lone writer that committed and told nobody is a broken fold
            //rather than a clean one: dissemination is what makes the next version servable, and a cluster that
            //holds nothing is not a cluster that holds this writer's change.
            failures.Require(quePaxaTrial.FinalValue == "a", $"{placement.Name} at {replicaCount}: the replicas hold '{quePaxaTrial.FinalValue ?? "nothing"}' after the lone QuePaxa write committed 'a'.");
            failures.Require(quePaxaTrial.Fold.Holds, $"{placement.Name} at {replicaCount}: the uncontended QuePaxa fold broke: {quePaxaTrial.Fold.Reason}");

            RmwTrialOutcome<RmwFastWriterMeasurement> fast = RmwFastCasPaxosArm.RunTrial(new RmwFastTrialRequest(
                placement, 1, [0], TimeSpan.Zero, SeedMixer.TrialSeed(89, replicaCount), JitterModel.None,
                RmwFastCasPaxosArm.DefaultMaxRecoveryRounds, RmwFastCasPaxosArm.DefaultEventBudget));

            RmwFastWriterMeasurement lone = fast.Writers[0];
            failures.Require(lone.IsCommitted, $"{placement.Name} at {replicaCount}: the uncontended Fast CASPaxos read-modify-write did not commit.");
            failures.Require(lone.ReachedFastQuorum, $"{placement.Name} at {replicaCount}: a lone writer has nobody to split its round with and must reach its fast quorum.");
            failures.Require(lone.PhasesExecuted == 1, $"{placement.Name} at {replicaCount}: the uncontended Fast CASPaxos read-modify-write executed {lone.PhasesExecuted} phases rather than one.");
            failures.Require(lone.ConflictRounds == 0, $"{placement.Name} at {replicaCount}: a lone Fast CASPaxos writer reported {lone.ConflictRounds} conflict rounds.");
            failures.Require(lone.CommittedValue == "a", $"{placement.Name} at {replicaCount}: the lone Fast CASPaxos writer committed '{lone.CommittedValue}' rather than its own token alone, so its blind round carried something other than its change.");
            failures.Require(lone.CommitMicroseconds == computed.FastShippedRoundTrip, $"{placement.Name} at {replicaCount}: the simulated Fast CASPaxos read-modify-write took {lone.CommitMicroseconds}us against the computed farthest round trip of {computed.FastShippedRoundTrip}us.");
            failures.Require(fast.FinalValue == "a", $"{placement.Name} at {replicaCount}: the acceptors hold '{fast.FinalValue ?? "nothing"}' after the lone Fast CASPaxos write committed 'a'.");
            failures.Require(fast.Fold.Holds, $"{placement.Name} at {replicaCount}: the uncontended Fast CASPaxos fold broke: {fast.Fold.Reason}");
        }
    }


    /// <summary>
    /// A QUEPAXA WRITE THAT LOST ITS VERSION RECOMPUTES AGAINST THE WINNER AND DOES NOT RE-PROPOSE ITS OWN
    /// STALE VALUE. The value it finally commits is exactly the winner it last recomputed against with its own
    /// token appended, so a proposer carrying its first computation forward would be a different value rather
    /// than a slower one, and the fold would lose the winner's change.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwQuePaxaRecomputesAgainstTheWinner(VectorFailures failures)
    {
        int conflicted = 0;
        int folds = 0;
        for(int seed = 0; seed < RmwSweepSeeds; seed++)
        {
            RmwTrialOutcome<RmwQuePaxaWriterMeasurement> trial = RmwQuePaxaTrial(seed, JitterModel.None);
            folds += trial.Fold.Holds ? 1 : 0;
            failures.Require(trial.Fold.Holds, $"The QuePaxa read-modify-write fold broke at seed {seed}: {trial.Fold.Reason}");

            foreach(RmwQuePaxaWriterMeasurement measurement in trial.Writers)
            {
                if(measurement.ConflictRecomputes == 0)
                {
                    continue;
                }

                conflicted++;
                failures.Require(measurement.LastConflictBase is not null, $"Writer {measurement.Writer} at seed {seed} reported {measurement.ConflictRecomputes} conflicts and no value it recomputed against.");
                failures.Require(measurement.RecomposedAgainstAnotherWriter, $"Writer {measurement.Writer} at seed {seed} recomputed against '{measurement.LastConflictBase}', which carries no other writer's change, so nothing superseded it.");
                failures.Require(!RmwFold.Carries(measurement.LastConflictBase, measurement.Token), $"Writer {measurement.Writer} at seed {seed} recomputed against '{measurement.LastConflictBase}', which already carries its own change.");

                if(measurement.IsCommitted)
                {
                    string expected = RmwFold.Apply(measurement.LastConflictBase, measurement.Token);
                    failures.Require(measurement.CommittedValue == expected, $"Writer {measurement.Writer} at seed {seed} committed '{measurement.CommittedValue}' after last recomputing against '{measurement.LastConflictBase}', which is not that winner with its own change appended.");
                }
            }
        }

        failures.Require(conflicted > 0, $"No QuePaxa writer lost its version over {RmwSweepSeeds} simultaneous three-writer trials, so this vector pins nothing about the recompute at all.");
        failures.Require(folds == RmwSweepSeeds, $"{folds} of {RmwSweepSeeds} QuePaxa folds held.");
    }


    /// <summary>
    /// A FAST CASPAXOS WRITE APPLIES ITS CHANGE TO THE VALUE THE ROUND RECOVERED, INSIDE THAT ROUND. The value
    /// it commits is exactly what its last round recovered with its own token appended, so a proposer applying
    /// its change to the value it knew before the round would drop whatever that round recovered.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwFastComposesInsideTheRound(VectorFailures failures)
    {
        int composed = 0;
        for(int seed = 0; seed < RmwSweepSeeds; seed++)
        {
            RmwTrialOutcome<RmwFastWriterMeasurement> trial = RmwFastTrial(seed, JitterModel.None);
            failures.Require(trial.Fold.Holds, $"The Fast CASPaxos read-modify-write fold broke at seed {seed}: {trial.Fold.Reason}");

            foreach(RmwFastWriterMeasurement measurement in trial.Writers)
            {
                if(!measurement.RecomposedAgainstAnotherWriter)
                {
                    continue;
                }

                composed++;
                failures.Require(measurement.RecoveryEntered, $"Writer {measurement.Writer} at seed {seed} composed against another writer's change without entering a classic round, so the composition did not happen inside a round.");
                failures.Require(measurement.ComposeCalls > 0, $"Writer {measurement.Writer} at seed {seed} composed against another writer's change with no in-round application at all.");

                if(measurement.IsCommitted)
                {
                    string expected = RmwFold.Apply(measurement.LastRecoveredValue, measurement.Token);
                    failures.Require(measurement.CommittedValue == expected, $"Writer {measurement.Writer} at seed {seed} committed '{measurement.CommittedValue}' after its last round recovered '{measurement.LastRecoveredValue}', which is not that recovered value with its own change appended.");
                }
            }
        }

        failures.Require(composed > 0, $"No Fast CASPaxos writer composed against another writer's change over {RmwSweepSeeds} simultaneous three-writer trials, so this vector pins nothing about the in-round application at all.");
    }


    /// <summary>
    /// THE APPLY-ONCE TOKEN IS THE OBSERVABLE OF THE INTERCHANGEABILITY BOUNDARY. Fast CASPaxos recovers a
    /// writer's own partially accepted value back into that writer's own round, so the token fires there and a
    /// plain append would count one change twice. QuePaxa discards a superseded proposal whole rather than
    /// composing it, and on this placement an attempt that decides nothing records nothing either, so the token
    /// does not fire here at all. Both arms run the one change function, so the difference between the two
    /// counts is the protocols' and not the workload's.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwApplyOnceTokenSeparatesTheTwoArms(VectorFailures failures)
    {
        int fastFirings = 0;
        int quePaxaFirings = 0;
        for(int seed = 0; seed < RmwSweepSeeds; seed++)
        {
            foreach(RmwFastWriterMeasurement measurement in RmwFastTrial(seed, JitterModel.None).Writers)
            {
                fastFirings += measurement.ApplyOnceTokenFirings;
            }

            foreach(RmwQuePaxaWriterMeasurement measurement in RmwQuePaxaTrial(seed, JitterModel.None).Writers)
            {
                quePaxaFirings += measurement.ApplyOnceTokenFirings;
            }
        }

        failures.Require(fastFirings > 0, $"The apply-once token never fired on the Fast CASPaxos arm over {RmwSweepSeeds} contended trials, so nothing here shows that a recovery can tally a writer's own value back into its own round.");
        failures.Require(quePaxaFirings == 0, $"The apply-once token fired {quePaxaFirings} times on the QuePaxa arm at a placement where a superseded proposal is the only way to lose, so a losing proposal was composed with the winner rather than discarded whole.");
    }


    /// <summary>
    /// A read-modify-write run is a function of its seed, and the seed is shown to be load-bearing beside it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwDeterminismQuePaxa(VectorFailures failures)
    {
        string first = RmwFingerprint(RmwQuePaxaTrial(0, JitterModel.ProportionalFifteenPercent));
        string second = RmwFingerprint(RmwQuePaxaTrial(0, JitterModel.ProportionalFifteenPercent));
        failures.Require(first == second, $"The QuePaxa read-modify-write arm returned two different runs at one seed: '{first}' against '{second}'.");

        string other = RmwFingerprint(RmwQuePaxaTrial(1, JitterModel.ProportionalFifteenPercent));
        failures.Require(first != other, $"The QuePaxa read-modify-write arm returned '{first}' at two different seeds, so the seed reaches no measurement and the same-seed check above pins nothing.");
    }


    /// <summary>
    /// A read-modify-write run is a function of its seed, and the seed is shown to be load-bearing beside it.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwDeterminismFast(VectorFailures failures)
    {
        string first = RmwFingerprint(RmwFastTrial(0, JitterModel.ProportionalFifteenPercent));
        string second = RmwFingerprint(RmwFastTrial(0, JitterModel.ProportionalFifteenPercent));
        failures.Require(first == second, $"The Fast CASPaxos read-modify-write arm returned two different runs at one seed: '{first}' against '{second}'.");

        string other = RmwFingerprint(RmwFastTrial(2, JitterModel.ProportionalFifteenPercent));
        failures.Require(first != other, $"The Fast CASPaxos read-modify-write arm returned '{first}' at two different seeds, so the seed reaches no measurement and the same-seed check above pins nothing.");
    }


    /// <summary>
    /// A QuePaxa read-modify-write writer is a member of the chain it writes to, so a trial with more writers
    /// than replicas is refused rather than measured: a non-member proposes nothing at all, and counting its
    /// refusal as a write would report contention that never happened.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwWriterCountCannotExceedTheReplicaCount(VectorFailures failures)
    {
        //A trial that ran anyway would put two registers on one replica, which is outside what a versioned
        //register supports, so whatever it then did is caught and reported here rather than left to escape and
        //take the whole vector suite down with it.
        bool refused = false;
        string escaped = string.Empty;
        try
        {
            _ = RmwQuePaxaArm.RunTrial(new RmwQuePaxaTrialRequest(
                Topologies.Global(3), 5, [0, 0, 0, 0, 0], 0, SeedMixer.TrialSeed(91, 0), JitterModel.None,
                RmwQuePaxaArm.DefaultMaxAttempts, RmwQuePaxaArm.DefaultAttemptsPerRecorder, RmwQuePaxaArm.DefaultEventBudget));
        }
        catch(ArgumentException)
        {
            refused = true;
        }
        catch(Exception unexpected)
        {
            escaped = unexpected.GetType().Name;
        }

        failures.Require(refused, $"A read-modify-write trial ran five writers over three replicas{(escaped.Length == 0 ? " and returned a measurement" : $" and failed with {escaped}")} rather than refusing the request.");

        //And the writer counts the rider does run are inside the bound at every replica count of the grid.
        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(int writerCount in CellSweep.WriterCounts.Where(count => count <= replicaCount))
            {
                failures.Require(writerCount <= RmwFold.MaximumWriters, $"The rider's {writerCount}-writer cell at {replicaCount} replicas is outside the token alphabet.");
            }
        }
    }


    /// <summary>
    /// The rider's two arms take the seed offsets a cell reserved for them, so no read-modify-write row draws
    /// the trial-seed stream of a plain row and no cell aliases into the next one.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwSeedAllocationIsInjective(VectorFailures failures)
    {
        int[] arms = [CellSweep.QuePaxaArmSeedOffset, CellSweep.FastArmSeedOffset, RmwCellSweep.QuePaxaArmSeedOffset, RmwCellSweep.FastArmSeedOffset];
        var allocated = new Dictionary<int, string>();

        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(int writerCount in CellSweep.WriterCounts)
            {
                foreach(int arm in arms)
                {
                    int seedBase = CellSweep.DefaultSeedBase(replicaCount, writerCount) + arm;
                    string owner = string.Create(CultureInfo.InvariantCulture, $"{replicaCount} replicas, {writerCount} writers, arm {arm}");
                    failures.Require(!allocated.TryGetValue(seedBase, out string? held), $"The seed base {seedBase} serves both {owner} and {held}, so two rows draw one trial-seed stream.");
                    allocated[seedBase] = owner;
                }
            }
        }

        failures.Require(arms.Distinct().Count() == arms.Length, $"The four arms of a cell take {arms.Distinct().Count()} distinct seed offsets.");
        failures.Require(arms.Length <= CellSweep.SeedsPerCell, $"A cell reserves {CellSweep.SeedsPerCell} seed bases for {arms.Length} arms, so an arm aliases into the next cell.");
    }


    /// <summary>
    /// The workload gate reads a rate keyed by protocol, rung and arrival spread. Keying it per configuration
    /// is what lets a ladder rung that eliminated the conflicts survive the gate while an unstaggered one does
    /// not, and leaving the mode out of the key is what gives a leaderless row the rate measured at its own
    /// rung rather than leaving it silently ungated: the rider's QuePaxa arm is the versioned register, which
    /// derives its leader and has no leaderless configuration to measure.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwGateInputIsKeyedByProtocolRungAndSpread(VectorFailures failures)
    {
        ImmutableArray<RmwRow> rider =
        [
            RmwGateRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0.00, 0.80),
            RmwGateRow(ConfigurationMode.QuePaxaLeadered, 1.00, 0.00, 0.02),
            RmwGateRow(ConfigurationMode.QuePaxaLeadered, 1.00, 2.00, 0.50),
            RmwGateRow(ConfigurationMode.FastUnhedged, 0.00, 0.00, 0.05)
        ];

        RetryRateDelegate rates = RmwRetryRates.For(rider);

        failures.Require(rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0.00, 0.0).Row) == 0.80, $"An unstaggered QuePaxa row read a rate of {rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0.00, 0.0).Row)} rather than the 0.80 measured at its own rung.");
        failures.Require(rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 1.00, 0.00, 0.0).Row) == 0.02, $"A staggered QuePaxa row read a rate of {rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 1.00, 0.00, 0.0).Row)}, so the rung is not in the key and one rate would decide every rung of the cell.");
        failures.Require(rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 1.00, 2.00, 0.0).Row) == 0.50, $"A row at another arrival spread read a rate of {rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 1.00, 2.00, 0.0).Row)}, so the spread is not in the key.");
        failures.Require(rates(RmwGateRow(ConfigurationMode.QuePaxaLeaderless, 1.00, 0.00, 0.0).Row) == 0.02, $"A leaderless QuePaxa row read {rates(RmwGateRow(ConfigurationMode.QuePaxaLeaderless, 1.00, 0.00, 0.0).Row)?.ToString(CultureInfo.InvariantCulture) ?? "no rate at all"}, so the mode is in the key and the plain grid's leaderless configurations would go through the gate unmeasured.");
        failures.Require(rates(RmwGateRow(ConfigurationMode.FastUnhedged, 0.00, 0.00, 0.0).Row) == 0.05, $"A Fast CASPaxos row read the QuePaxa rate, so the protocol is not in the key.");
        failures.Require(rates(RmwGateRow(ConfigurationMode.QuePaxaLeadered, 0.25, 0.00, 0.0).Row) is null, $"A rung the rider never measured read a rate, so the gate would remove on something other than a measurement.");

        //A rate two measurements disagreed on is not a rate, and a lookup that silently kept the last one
        //would make the gate depend on the order the sweep happened to run in.
        bool refused = false;
        try
        {
            _ = RmwRetryRates.For([RmwGateRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0.00, 0.10), RmwGateRow(ConfigurationMode.QuePaxaLeadered, 0.00, 0.00, 0.90)]);
        }
        catch(ArgumentException)
        {
            refused = true;
        }

        failures.Require(refused, $"Two rider rows at one protocol, rung and spread were folded into one rate.");
    }


    /// <summary>
    /// The rider's verdict is read at the writer the plain cell's verdict is read at, which is the median site
    /// by majority radius. A rider that spoke for a different writer could not be compared with the cell it
    /// came from at all.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwVerdictIsReadAtThePlainCellRepresentativeWriter(VectorFailures failures)
    {
        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(Topology placement in Topologies.Grid(replicaCount))
            {
                ImmutableArray<ComputedSiteCost> computed = QuorumDistance.For(placement);
                foreach(int writerCount in CellSweep.WriterCounts.Where(count => count <= replicaCount))
                {
                    int representative = CellSweep.RepresentativeWriter(computed, writerCount);
                    long[] radii = [.. Enumerable.Range(0, writerCount).Select(writer => computed[writer % computed.Length].MajorityRoundTrip).Order()];
                    long median = radii[(writerCount - 1) / 2];

                    failures.Require(representative >= 0 && representative < writerCount, $"{placement.Name} at {replicaCount} replicas and {writerCount} writers speaks for writer {representative}, which is not a writer of the cell.");
                    failures.Require(computed[representative % computed.Length].MajorityRoundTrip == median, $"{placement.Name} at {replicaCount} replicas and {writerCount} writers speaks for a writer paying {computed[representative % computed.Length].MajorityRoundTrip}us against the median {median}us.");
                }
            }
        }
    }


    /// <summary>
    /// A WHOLE CELL, MEASURED AND REDUCED. The rider's rows carry the conflict rate the gate reads, the gate
    /// removes the QuePaxa configurations that are above the ceiling and only those, and the two readings of
    /// the gate are the same cell answered two ways. A lone writer conflicts with nobody, so its cell is where
    /// the gate must remove nothing at all; a contended cell is where it must remove something, and a rider
    /// whose rate never reached the gate would leave the two readings identical in both.
    /// </summary>
    /// <param name="failures">The vector's failure collector.</param>
    private static void RmwCellFeedsTheGateItsOwnMeasuredRates(VectorFailures failures)
    {
        RmwCellOutcome contended = RmwCellSweep.Measure(RmwPlacement, RmwWriters, JitterModel.None, 6, 4200);

        failures.Require(contended.Agreed, $"The contended read-modify-write cell did not agree, so the fold broke somewhere in it.");
        failures.Require(contended.Rows.Length == 27, $"The contended cell measured {contended.Rows.Length} rows rather than the nine configurations at each of three arrival spreads.");
        failures.Require(contended.Rows.All(row => row.Row.Agreed && row.FoldBreaches == 0), $"{contended.Rows.Count(row => row.FoldBreaches > 0)} rows of the contended cell carry a fold breach.");

        ImmutableArray<RmwRow> quePaxa = [.. contended.Rows.Where(row => row.Row.Protocol == ProtocolKind.QuePaxa)];
        failures.Require(quePaxa.All(row => row.ConflictRetryRate > VerdictReducer.RetryRateCeiling), $"{quePaxa.Count(row => row.ConflictRetryRate <= VerdictReducer.RetryRateCeiling)} of the cell's {quePaxa.Length} QuePaxa rows re-proposed at or below the ceiling, so the rows the gate is meant to remove are not the rows the rider measured.");
        failures.Require(contended.GatedVerdicts.All(verdict => verdict.Removed.Any(removal => removal.Row.Protocol == ProtocolKind.QuePaxa && removal.Reason.Contains("gate B", StringComparison.Ordinal))), $"A contended spread's gated verdict removed no QuePaxa configuration under gate B, so the rate the rider measured never reached the reducer.");
        failures.Require(contended.GatedVerdicts.All(verdict => verdict.Removed.All(removal => removal.Row.Protocol == ProtocolKind.QuePaxa)), $"The gate removed a Fast CASPaxos configuration, and the settled rule binds QuePaxa alone.");
        failures.Require(contended.InertVerdicts.All(verdict => verdict.Removed.IsEmpty), $"The inert reading removed {contended.InertVerdicts.Sum(verdict => verdict.Removed.Length)} configurations, so the gate is not inert without a measured rate.");

        //THE LADDER IS LOAD-BEARING. A rung an operator configures has to reach the register's own hedging
        //delay, or every rung of the QuePaxa ladder would measure the unstaggered row.
        double unstaggered = quePaxa.First(row => row.Row.Rung == 0.00 && row.Row.Spread == 0.00).Row.RepresentativeAddedWaitMicroseconds;
        double staggered = quePaxa.First(row => row.Row.Rung == 1.00 && row.Row.Spread == 0.00).Row.RepresentativeAddedWaitMicroseconds;
        failures.Require(unstaggered == 0.0, $"The unstaggered QuePaxa row reports an added wait of {unstaggered}us.");
        failures.Require(staggered > unstaggered, $"The QuePaxa row at rung 1.00 reports an added wait of {staggered}us against the unstaggered row's {unstaggered}us, so the rung reaches no delay at all.");

        //A FAST CASPAXOS FALLBACK IS NOT A CONFLICT. The blind round splitting is what the classic round exists
        //to absorb, and only a ballot another proposer pre-empted costs a further round; a conflict column that
        //counted the fallback would report the two protocols' costs in one currency they do not share.
        RmwRow settled = contended.Rows.First(row => row.Row.Protocol == ProtocolKind.FastCasPaxos && row.Row.Rung == 1.50 && row.Row.Spread == 0.00);
        failures.Require(settled.ConflictRetryRate == 0.0, $"The fully hedged Fast CASPaxos row reports a conflict rate of {settled.ConflictRetryRate:F3}, so its unduelled classic rounds are counted as re-proposals.");
        failures.Require(settled.Row.WriterFastRate < 1.0, $"Every write of the fully hedged Fast CASPaxos row settled on the blind round, so the row cannot show that a fallback is not a conflict.");

        RmwCellOutcome lone = RmwCellSweep.Measure(RmwPlacement, 1, JitterModel.None, 4, 4204);

        failures.Require(lone.Rows.Length == 6, $"The one-writer cell measured {lone.Rows.Length} rows rather than the two configurations at each of three arrival spreads.");
        failures.Require(lone.Rows.All(row => row.ConflictRetryRate == 0.0), $"A lone writer reported a conflict rate above zero, so the column counts something other than another writer getting in first.");
        failures.Require(lone.GatedVerdicts.All(verdict => verdict.Removed.IsEmpty), $"The gate removed a configuration in a cell with nobody to conflict with.");
        failures.Require(lone.Rows.All(row => row.ApplyOnceRate == 0.0), $"A lone writer's change found itself already applied, which needs a rival's round to be possible at all.");
    }


    /// <summary>
    /// A rider row carrying only what the workload gate's lookup reads.
    /// </summary>
    /// <param name="mode">The configuration.</param>
    /// <param name="rung">The ladder rung as an operator configures it.</param>
    /// <param name="spread">The arrival spread.</param>
    /// <param name="rate">The conflict rate the gate would read.</param>
    /// <returns>The row.</returns>
    private static RmwRow RmwGateRow(ConfigurationMode mode, double rung, double spread, double rate) => new(
        new MeasuredRow(
            mode,
            rung,
            0,
            spread,
            PercentileReading.None,
            PercentileReading.At(100.0),
            PercentileReading.None,
            PercentileReading.None,
            PercentileReading.At(100.0),
            PercentileReading.None,
            0,
            0,
            0,
            0.0,
            0.0,
            0.0,
            0.0,
            0.0,
            true),
        rate,
        rate,
        0.0,
        0.0,
        0,
        0,
        string.Empty);


    private static string RmwFingerprint(RmwTrialOutcome<RmwQuePaxaWriterMeasurement> outcome) => string.Join(
        "|",
        outcome.FinalValue ?? "(unwritten)",
        string.Join(
            ";",
            outcome.Writers.Select(measurement => string.Create(
                CultureInfo.InvariantCulture,
                $"{measurement.Writer}:{measurement.IsCommitted}:{measurement.Attempts}:{measurement.ConflictRecomputes}:{measurement.UndecidedRecomputes}:{measurement.ApplyOnceTokenFirings}:{measurement.CommitMicroseconds}:{measurement.CommittedValue}"))));


    private static string RmwFingerprint(RmwTrialOutcome<RmwFastWriterMeasurement> outcome) => string.Join(
        "|",
        outcome.FinalValue ?? "(unwritten)",
        string.Join(
            ";",
            outcome.Writers.Select(measurement => string.Create(
                CultureInfo.InvariantCulture,
                $"{measurement.Writer}:{measurement.IsCommitted}:{measurement.RecoveryRounds}:{measurement.ConflictRounds}:{measurement.PhasesExecuted}:{measurement.ComposeCalls}:{measurement.ApplyOnceTokenFirings}:{measurement.CommitMicroseconds}:{measurement.CommittedValue}"))));


    private static IEnumerable<(int ReplicaCount, Topology Placement)> EveryPlacement()
    {
        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(Topology placement in Topologies.Grid(replicaCount))
            {
                yield return (replicaCount, placement);
            }
        }

        yield return (5, Topologies.ProbeSpread());
        yield return (5, Topologies.ProbeClustered());
        yield return (5, Topologies.CoLocatedSensitivity(5));
    }


    private static string Fingerprint(ImmutableArray<QuePaxaWriterMeasurement> measurements) => string.Join(
        "|",
        measurements.Select(measurement => string.Create(
            CultureInfo.InvariantCulture,
            $"{measurement.Writer}:{measurement.Outcome.IsDecided}:{measurement.Outcome.Steps}:{measurement.Outcome.DecidedAt.Value}:{measurement.Outcome.Value}:{measurement.DecisionMicroseconds}:{measurement.PriorityDraws}")));


    private static string Fingerprint(ImmutableArray<FastWriterMeasurement> measurements) => string.Join(
        "|",
        measurements.Select(measurement => string.Create(
            CultureInfo.InvariantCulture,
            $"{measurement.Writer}:{measurement.Activated}:{measurement.FastAcceptedCount}:{measurement.FastWriteReturnedMicroseconds}:{measurement.FastQuorumReachedMicroseconds}:{measurement.RecoveryAttempts}:{measurement.PhasesExecuted}:{measurement.IsCommitted}:{measurement.CommitMicroseconds}:{measurement.CommittedValue}")));
}
