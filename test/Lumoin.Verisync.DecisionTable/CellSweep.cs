using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One cell of the grid: a replica count, a placement and a writer count, measured over every mode and every
/// arrival spread the campaign runs per cell.
/// </summary>
/// <remarks>
/// <para>
/// The arrival pattern is a sweep INSIDE a cell rather than a fifth dimension of the grid, because the
/// settled reading is that the escalation trigger is the arrival pattern and not the geography. Every
/// configuration is therefore reported at each spread rather than at one chosen point.
/// </para>
/// <para>
/// The two protocols' mode axes are not symmetric and the report says so. A QuePaxa mode is a recorder
/// configuration - led or leaderless, a safety input decided before any write. A Fast CASPaxos mode is one
/// number on a hedging schedule, whose zero value the shipped type documents as reproducing unhedged
/// behaviour exactly. An operator reading the two verdicts as the same kind of instruction would be wrong.
/// </para>
/// <para>
/// A CELL CARRIES THREE UNITS AND PRINTS ALL OF THEM. A QuePaxa stagger rung is a fraction of the leader's
/// majority-radius round trip, a Fast CASPaxos hedge rung is a fraction of the leading writer's fast-quorum
/// round trip, and an arrival spread and the round-trip column are denominated in each writer's OWN
/// majority-radius round trip. Every row also carries its rung in absolute milliseconds, so a reducer can
/// convert a rung between the two currencies without knowing which unit produced it.
/// </para>
/// </remarks>
internal static class CellSweep
{
    /// <summary>The stagger rungs a QuePaxa leadered configuration is swept over, in units of the leader's majority-radius round trip.</summary>
    private static ImmutableArray<double> QuePaxaLeaderedRungs { get; } = [0.00, 0.25, 0.50, 1.00];

    /// <summary>The stagger rungs a QuePaxa leaderless configuration is swept over.</summary>
    private static ImmutableArray<double> QuePaxaLeaderlessRungs { get; } = [0.00, 1.00];

    /// <summary>The hedging rungs a Fast CASPaxos configuration is swept over, in units of the leading writer's fast-quorum round trip, the first of which is the unhedged one.</summary>
    private static ImmutableArray<double> FastRungs { get; } = [0.00, 0.25, 0.50, 1.00, 1.50];

    /// <summary>The arrival spreads every configuration is reported at, in units of a writer's own majority-radius round trip.</summary>
    private static ImmutableArray<double> ArrivalSpreads { get; } = [0.00, 0.50, 2.00];


    /// <summary>The writer counts the grid's writer axis runs over.</summary>
    public static ImmutableArray<int> WriterCounts { get; } = [1, 2, 3, 5];

    /// <summary>How many consecutive seed bases one cell reserves, which is one per arm plus headroom.</summary>
    public const int SeedsPerCell = 4;

    /// <summary>The QuePaxa arm's offset inside its cell's seed range.</summary>
    public const int QuePaxaArmSeedOffset = 0;

    /// <summary>The Fast CASPaxos arm's offset inside its cell's seed range.</summary>
    public const int FastArmSeedOffset = 1;

    /// <summary>The offset a third arm would take inside its cell's seed range.</summary>
    public const int ReservedArmSeedOffset = 2;


    /// <summary>
    /// The seed base a cell runs under when the operator supplies none.
    /// </summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <returns>The base of the cell's seed range, which its arms take their offsets inside.</returns>
    /// <remarks>
    /// A cell CONSUMES one seed base per arm, so the allocator's stride has to exceed what a cell consumes:
    /// a stride below it makes one cell's Fast rows draw the trial-seed stream of a neighbouring cell's
    /// QuePaxa rows, and two rows the table presents as independent measurements then share their noise. The
    /// topology deliberately does not enter the base. Sharing one stream across the tiers at one cell key is
    /// common random numbers, which is the harness's declared and documented method and makes a cross-tier
    /// contrast less noisy rather than more misleading.
    /// </remarks>
    public static int DefaultSeedBase(int replicaCount, int writerCount) =>
        1000 + (replicaCount * SeedsPerCell * 10) + (writerCount * SeedsPerCell);


    /// <summary>The unit a QuePaxa stagger rung is denominated in: the leader's own majority-radius round trip.</summary>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <returns>The unit in microseconds.</returns>
    /// <remarks>
    /// This is the note's published convention and the unit the reproduction gate reproduces its stagger
    /// ladder in, so it is also the unit an operator reading a QuePaxa rung is being prescribed in.
    /// </remarks>
    public static long QuePaxaStaggerUnit(ImmutableArray<ComputedSiteCost> computed) => computed[0].MajorityRoundTrip;


    /// <summary>The unit a Fast CASPaxos hedge rung is denominated in: the leading writer's fast-quorum round trip.</summary>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <returns>The unit in microseconds.</returns>
    /// <remarks>
    /// A hedge exists to keep a fast round from splitting, so its rung has to be a fraction of the round it
    /// staggers, which is the convention the reproduction gate's own hedge ladder and the published rows
    /// already use. On a placement whose leading writer sits inside a co-located majority the majority radius
    /// is two orders of magnitude below the fast round, and a ladder priced in it would sweep a range the
    /// shipped policy never enters.
    /// </remarks>
    public static long FastHedgeUnit(ImmutableArray<ComputedSiteCost> computed) => computed[0].FastQuorumRoundTrip;


    /// <summary>
    /// The arrival-spread width each writer draws over at <paramref name="spread"/> units.
    /// </summary>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <param name="spread">The spread in units of a writer's own majority-radius round trip.</param>
    /// <returns>The widths in writer order, in microseconds.</returns>
    /// <remarks>
    /// The unit is that writer's own radius rather than the leader's, so an arrival pattern on a placement
    /// whose sites are decades apart stays a pattern at every site instead of collapsing the remote writers
    /// onto the width the nearest one would have drawn over.
    /// </remarks>
    public static ImmutableArray<long> ArrivalSpreadMicroseconds(ImmutableArray<ComputedSiteCost> computed, int writerCount, double spread) =>
        [.. Enumerable.Range(0, writerCount).Select(writer => (long)(spread * computed[writer % computed.Length].MajorityRoundTrip))];


    /// <summary>
    /// Runs one cell and prints every configuration.
    /// </summary>
    /// <param name="placement">The placement, whose site count is the replica count.</param>
    /// <param name="writerCount">How many writers contend for the instance.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <param name="trials">How many trials each row is measured over.</param>
    /// <param name="seedBase">The base of the cell's seed range, which separates this cell's trials from another cell's.</param>
    /// <returns>The cell's rows, its verdict at each arrival spread, and whether every configuration agreed in every trial.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="placement"/> or <paramref name="jitter"/> is <see langword="null"/>.</exception>
    public static CellOutcome Run(Topology placement, int writerCount, JitterModel jitter, int trials, int seedBase)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(writerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);

        int replicaCount = placement.SiteCount;
        ImmutableArray<ComputedSiteCost> computed = QuorumDistance.For(placement);
        long staggerUnit = QuePaxaStaggerUnit(computed);
        long hedgeUnit = FastHedgeUnit(computed);
        int representative = RepresentativeWriter(computed, writerCount);

        Report.Line($"CELL replicas={replicaCount}, topology={placement.Name}, writers={writerCount}, trials={trials}, jitter={jitter.Description}");
        Report.Line($"provenance: {placement.Provenance}");
        Report.Line($"regions: {string.Join(", ", placement.SiteRegions)}");
        Report.Line($"quorums: fast {QuorumDistance.FastQuorum(replicaCount)} of {replicaCount}, majority {QuorumDistance.QuePaxaQuorum(replicaCount)} of {replicaCount}");
        Report.Line($"QuePaxa stagger unit = the leader's majority-radius round trip = {VirtualTimePump.ToMilliseconds(staggerUnit):F3}ms");
        Report.Line($"Fast CASPaxos hedging unit = the leading writer's fast-quorum round trip = {VirtualTimePump.ToMilliseconds(hedgeUnit):F3}ms");
        Report.Line($"arrival spread and the round-trip column are in each writer's own majority-radius round trip; representative writer = {representative} (median site)");
        Report.Line($"seed base = {seedBase}: QuePaxa arm {seedBase + QuePaxaArmSeedOffset}, Fast CASPaxos arm {seedBase + FastArmSeedOffset}");
        Report.Blank();

        Report.Text("computed, uncontended, no simulation");
        Report.Text("columns: site, region, quePaxaLeaderMs, quePaxaOtherMs, fastQuorumMs, fastShippedMs, classicMs, fast/classic, shipped/quorum");
        foreach(ComputedSiteCost cost in computed)
        {
            Report.Line($"{cost.Site}, {cost.Region}, {VirtualTimePump.ToMilliseconds(cost.QuePaxaLeaderRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.QuePaxaNonLeaderRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.FastQuorumRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.FastShippedRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.ClassicRoundTrip):F3}, {cost.FastOverClassic:F2}, {cost.ShippedOverQuorum:F2}");
        }

        Report.Blank();
        Report.Text("measured, one row per configuration and arrival spread");
        Report.Text("a latency column reads 'unbounded' where the percentile falls inside the writes that never finished, and 'none' where nothing was observed at all");
        Report.Text("columns: protocol, mode, rung, rungMs, spread, p50Ms, p95Ms, maxMs, p95Rtt, p95RepresentativeMs, p95RepresentativeRtt, trialFastRate, writerFastRate, meanSteps, meanAddedWaitMs, representativeAddedWaitMs, unfinishedWrites, representativeUnfinished, stoodDownWrites, agreed");

        ImmutableArray<MeasuredRow>.Builder rows = ImmutableArray.CreateBuilder<MeasuredRow>();
        foreach(double spread in ArrivalSpreads)
        {
            ImmutableArray<long> spreadMicroseconds = ArrivalSpreadMicroseconds(computed, writerCount, spread);

            foreach(double rung in Rungs(QuePaxaLeaderedRungs, writerCount))
            {
                rows.Add(QuePaxaRow(placement, computed, writerCount, LeadershipMode.WriterZeroLeads, ConfigurationMode.QuePaxaLeadered, rung, spread, spreadMicroseconds, staggerUnit, jitter, trials, seedBase, representative));
            }

            foreach(double rung in Rungs(QuePaxaLeaderlessRungs, writerCount))
            {
                rows.Add(QuePaxaRow(placement, computed, writerCount, LeadershipMode.Leaderless, ConfigurationMode.QuePaxaLeaderless, rung, spread, spreadMicroseconds, staggerUnit, jitter, trials, seedBase, representative));
            }

            foreach(double rung in Rungs(FastRungs, writerCount))
            {
                rows.Add(FastRow(placement, computed, writerCount, rung, spread, spreadMicroseconds, hedgeUnit, jitter, trials, seedBase, representative));
            }
        }

        ImmutableArray<MeasuredRow> measured = rows.ToImmutable();
        bool agreed = measured.All(row => row.Agreed);

        Report.Blank();
        Report.Line($"AGREEMENT GATE: {(agreed ? "PASS" : "FAIL - the cell is void, not slow")}");

        //The rung is republished in the majority radius because that is the one unit both arms can be
        //prescribed in, and the cell's is the leader's own, which is the unit a QuePaxa rung was configured
        //in and therefore converts to itself.
        ImmutableArray<CellVerdict> verdicts = VerdictReducer.Reduce(measured, staggerUnit, null);

        Report.Blank();
        Report.Text("verdict, derived from the rows above by the campaign's rule rather than judged");
        Report.Text("an outcome of 'either' is a margin under ten percent, which is published rather than resolved, and the winner beside it is the preference the tie-break recorded");
        Report.Text("gate B is inert here: it reads a measured read-modify-write retry rate, which the rider supplies and a cell cannot");
        foreach(CellVerdict verdict in verdicts)
        {
            PrintVerdict(verdict);
        }

        return new CellOutcome(agreed, measured, verdicts);
    }


    /// <summary>The rungs a configuration is swept over, which is the first rung alone where there is nobody to stagger against.</summary>
    /// <param name="rungs">The ladder the configuration would sweep at more than one writer.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <returns>The rungs to sweep.</returns>
    private static ImmutableArray<double> Rungs(ImmutableArray<double> rungs, int writerCount) => writerCount == 1 ? [rungs[0]] : rungs;


    /// <summary>
    /// Measures one QuePaxa configuration at one arrival spread, prints its row and returns it.
    /// </summary>
    /// <param name="placement">The placement.</param>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <param name="leadership">How the recorders are led.</param>
    /// <param name="mode">The configuration the row is reported under.</param>
    /// <param name="rung">The stagger rung, in units of the leader's majority-radius round trip.</param>
    /// <param name="spread">The arrival spread, in units of a writer's own majority-radius round trip.</param>
    /// <param name="spreadMicroseconds">Each writer's own spread width, in writer order.</param>
    /// <param name="staggerUnit">The unit the rung is denominated in.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <param name="trials">How many trials the row is measured over.</param>
    /// <param name="seedBase">The base of the cell's seed range.</param>
    /// <param name="representative">The writer the verdict speaks for.</param>
    /// <returns>The row.</returns>
    private static MeasuredRow QuePaxaRow(
        Topology placement,
        ImmutableArray<ComputedSiteCost> computed,
        int writerCount,
        LeadershipMode leadership,
        ConfigurationMode mode,
        double rung,
        double spread,
        ImmutableArray<long> spreadMicroseconds,
        long staggerUnit,
        JitterModel jitter,
        int trials,
        int seedBase,
        int representative)
    {
        long stagger = (long)(rung * staggerUnit);
        ImmutableArray<long> staggerDelays = StaggerSchedule.Delays(writerCount, stagger);

        var latencies = new List<double>(trials * writerCount);
        var roundTrips = new List<double>(trials * writerCount);
        var representativeLatencies = new List<double>(trials);
        var representativeRoundTrips = new List<double>(trials);
        int trialsWithFastPath = 0;
        int fastWrites = 0;
        int undecided = 0;
        int representativeUndecided = 0;
        long stepTotal = 0;
        long addedWaitTotal = 0;
        long representativeAddedWaitTotal = 0;
        bool agreed = true;

        for(int trial = 0; trial < trials; trial++)
        {
            ulong trialSeed = SeedMixer.TrialSeed(seedBase + QuePaxaArmSeedOffset, trial);
            ImmutableArray<long> offsets = ArrivalOffsets(trialSeed, writerCount, spreadMicroseconds, jitter.GrainMicroseconds);
            ImmutableArray<long> activations = [.. offsets.Select((offset, writer) => offset + staggerDelays[writer])];

            ImmutableArray<QuePaxaWriterMeasurement> measurements = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
                placement, writerCount, leadership, activations, staggerDelays, trialSeed, jitter, QuePaxaArm.DefaultEventBudget));

            bool anyFast = false;
            foreach(QuePaxaWriterMeasurement measurement in measurements)
            {
                if(measurement.Outcome.IsDecided)
                {
                    latencies.Add(VirtualTimePump.ToMilliseconds(measurement.DecisionMicroseconds));
                    roundTrips.Add(measurement.DecisionMicroseconds / (double)computed[measurement.Site].MajorityRoundTrip);
                }
                else
                {
                    undecided++;
                }

                stepTotal += measurement.Outcome.Steps;
                addedWaitTotal += measurement.AddedWaitMicroseconds;
                anyFast |= measurement.IsFastPath;
                if(measurement.IsFastPath)
                {
                    fastWrites++;
                }
            }

            agreed &= TrialAgreement.QuePaxa(measurements);

            QuePaxaWriterMeasurement speaker = measurements[representative];
            representativeAddedWaitTotal += speaker.AddedWaitMicroseconds;
            if(speaker.Outcome.IsDecided)
            {
                representativeLatencies.Add(VirtualTimePump.ToMilliseconds(speaker.DecisionMicroseconds));
                representativeRoundTrips.Add(speaker.DecisionMicroseconds / (double)computed[speaker.Site].MajorityRoundTrip);
            }
            else
            {
                representativeUndecided++;
            }

            if(anyFast)
            {
                trialsWithFastPath++;
            }
        }

        double writes = (double)trials * writerCount;
        MeasuredRow row = MeasuredRow.Of(
            mode,
            rung,
            stagger,
            spread,
            latencies,
            roundTrips,
            representativeLatencies,
            representativeRoundTrips,
            undecided,
            representativeUndecided,
            0,
            trialsWithFastPath / (double)trials,
            fastWrites / writes,
            stepTotal / writes,
            addedWaitTotal / writes,
            representativeAddedWaitTotal / (double)trials,
            agreed);

        PrintRow(row);

        return row;
    }


    /// <summary>
    /// Measures one Fast CASPaxos configuration at one arrival spread, prints its row and returns it.
    /// </summary>
    /// <param name="placement">The placement.</param>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <param name="rung">The hedging rung, in units of the leading writer's fast-quorum round trip.</param>
    /// <param name="spread">The arrival spread, in units of a writer's own majority-radius round trip.</param>
    /// <param name="spreadMicroseconds">Each writer's own spread width, in writer order.</param>
    /// <param name="hedgeUnit">The unit the rung is denominated in.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <param name="trials">How many trials the row is measured over.</param>
    /// <param name="seedBase">The base of the cell's seed range.</param>
    /// <param name="representative">The writer the verdict speaks for.</param>
    /// <returns>The row.</returns>
    private static MeasuredRow FastRow(
        Topology placement,
        ImmutableArray<ComputedSiteCost> computed,
        int writerCount,
        double rung,
        double spread,
        ImmutableArray<long> spreadMicroseconds,
        long hedgeUnit,
        JitterModel jitter,
        int trials,
        int seedBase,
        int representative)
    {
        long baseDelay = (long)(rung * hedgeUnit);

        var latencies = new List<double>(trials * writerCount);
        var roundTrips = new List<double>(trials * writerCount);
        var quorumLatencies = new List<double>(trials * writerCount);
        var representativeLatencies = new List<double>(trials);
        var representativeRoundTrips = new List<double>(trials);
        int trialsWithFastQuorum = 0;
        int fastWrites = 0;
        long phaseTotal = 0;
        long addedWaitTotal = 0;
        long representativeAddedWaitTotal = 0;
        int uncommitted = 0;
        int representativeUncommitted = 0;
        int stoodDown = 0;
        bool agreed = true;

        for(int trial = 0; trial < trials; trial++)
        {
            ulong trialSeed = SeedMixer.TrialSeed(seedBase + FastArmSeedOffset, trial);
            ImmutableArray<long> arrivals = ArrivalOffsets(trialSeed, writerCount, spreadMicroseconds, jitter.GrainMicroseconds);

            ImmutableArray<FastWriterMeasurement> measurements = FastCasPaxosArm.RunTrial(new FastTrialRequest(
                placement, writerCount, arrivals, VirtualTimePump.ToTimeSpan(baseDelay), trialSeed, jitter, FastCasPaxosArm.DefaultMaxRecoveryAttempts, FastCasPaxosArm.DefaultEventBudget));

            bool anyFast = false;
            foreach(FastWriterMeasurement measurement in measurements)
            {
                phaseTotal += measurement.PhasesExecuted;
                addedWaitTotal += measurement.AddedWaitMicroseconds;
                anyFast |= measurement.ReachedFastQuorum;
                if(measurement.ReachedFastQuorum)
                {
                    fastWrites++;
                }

                if(measurement.FastQuorumReachedMicroseconds is { } quorumInstant)
                {
                    quorumLatencies.Add(VirtualTimePump.ToMilliseconds(quorumInstant));
                }

                //THREE DISPOSITIONS, NEVER TWO. A write that finished is a latency sample. A write that spent
                //its recovery ladder is censored: it contributes no sample and the percentiles rank it above
                //every write that finished. A write that stood down on a learn signal sent nothing at all,
                //owes no recovery and must be reissued by the host, so it is neither.
                if(measurement.StoodDown)
                {
                    stoodDown++;
                }
                else if(measurement.CommitMicroseconds is { } commitInstant)
                {
                    latencies.Add(VirtualTimePump.ToMilliseconds(commitInstant));
                    roundTrips.Add(commitInstant / (double)computed[measurement.Site].MajorityRoundTrip);
                }
                else
                {
                    uncommitted++;
                }
            }

            agreed &= TrialAgreement.Fast(measurements);

            //The same three dispositions at the writer the verdict speaks for: a writer that sent nothing is
            //not a sample and not a denominator either.
            FastWriterMeasurement speaker = measurements[representative];
            representativeAddedWaitTotal += speaker.AddedWaitMicroseconds;
            if(!speaker.StoodDown)
            {
                if(speaker.CommitMicroseconds is { } representativeCommit)
                {
                    representativeLatencies.Add(VirtualTimePump.ToMilliseconds(representativeCommit));
                    representativeRoundTrips.Add(representativeCommit / (double)computed[speaker.Site].MajorityRoundTrip);
                }
                else
                {
                    representativeUncommitted++;
                }
            }

            if(anyFast)
            {
                trialsWithFastQuorum++;
            }
        }

        double writes = (double)trials * writerCount;
        MeasuredRow row = MeasuredRow.Of(
            rung == 0.0 ? ConfigurationMode.FastUnhedged : ConfigurationMode.FastHedged,
            rung,
            baseDelay,
            spread,
            latencies,
            roundTrips,
            representativeLatencies,
            representativeRoundTrips,
            uncommitted,
            representativeUncommitted,
            stoodDown,
            trialsWithFastQuorum / (double)trials,
            fastWrites / writes,
            phaseTotal / writes,
            addedWaitTotal / writes,
            representativeAddedWaitTotal / (double)trials,
            agreed);

        PrintRow(row);

        //The quorum instant is reported beside the shipped one, always. Reporting only the shipped instant
        //makes the distance arithmetic look wrong; reporting only the quorum instant measures a proposer that
        //does not exist. Its population is the writes that reached a fast quorum, which the line states, so
        //the writes that never did are a disclosed denominator here rather than a censored rank.
        Report.Line($"     quorum-instant reading in ms: p50={PercentileReading.Of(quorumLatencies, 0, 0.50)}, p95={PercentileReading.Of(quorumLatencies, 0, 0.95)} over {quorumLatencies.Count} of {writes:F0} writes");

        return row;
    }


    /// <summary>Prints one measured row, in the column order the header announced.</summary>
    /// <param name="row">The row.</param>
    private static void PrintRow(MeasuredRow row) =>
        Report.Line($"{row.ProtocolName}, {row.ModeName}, {row.Rung:F2}, {VirtualTimePump.ToMilliseconds(row.RungMicroseconds):F3}, {row.Spread:F2}, {row.P50}, {row.P95}, {row.Max}, {row.P95RoundTrips}, {row.RepresentativeP95}, {row.RepresentativeP95RoundTrips}, {row.TrialFastRate:F3}, {row.WriterFastRate:F3}, {row.MeanSteps:F2}, {VirtualTimePump.ToMilliseconds((long)row.MeanAddedWaitMicroseconds):F3}, {VirtualTimePump.ToMilliseconds((long)row.RepresentativeAddedWaitMicroseconds):F3}, {row.Unfinished}, {row.RepresentativeUnfinished}, {row.StoodDown}, {(row.Agreed ? "yes" : "NO")}");


    /// <summary>
    /// Prints one arrival spread's verdict, and one line for each configuration its gates removed.
    /// </summary>
    /// <param name="verdict">The verdict.</param>
    /// <remarks>
    /// The removals are printed above the verdict they shaped, because a cell whose fastest configuration was
    /// taken out reads as a cell the slower protocol simply won unless the removal is on the page beside it.
    /// </remarks>
    private static void PrintVerdict(CellVerdict verdict)
    {
        foreach(RemovedConfiguration removal in verdict.Removed)
        {
            Report.Line($"REMOVED spread={verdict.Spread:F2} configuration={removal.Row.Key} reason={removal.Reason}");
        }

        if(verdict.Winner is null)
        {
            Report.Line($"VERDICT spread={verdict.Spread:F2} outcome={verdict.OutcomeName} reason={verdict.Reason}");

            return;
        }

        Report.Line($"VERDICT spread={verdict.Spread:F2} outcome={verdict.OutcomeName} configuration={verdict.Winner.Key} rungMajorityRadius={verdict.WinningRungInMajorityRadius:F3} rungMs={VirtualTimePump.ToMilliseconds(verdict.Winner.RungMicroseconds):F3} p95Ms={verdict.Winner.RepresentativeP95} margin={verdict.MarginText} runnerUp={verdict.RunnerUp?.Key ?? "none"} runnerUpP95Ms={(verdict.RunnerUp is null ? "none" : verdict.RunnerUp.RepresentativeP95.ToString())} reason={verdict.Reason}");
    }


    /// <summary>
    /// The arrival offset each writer draws at <paramref name="trialSeed"/> over its own spread.
    /// </summary>
    /// <param name="trialSeed">The trial's seed.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <param name="spreadMicroseconds">Each writer's own spread width, in writer order.</param>
    /// <param name="grain">The grid the offsets land on.</param>
    /// <returns>The offsets in writer order, in microseconds.</returns>
    /// <remarks>
    /// Offsets are drawn from the trial seed rather than from a running stream, so an unstaggered and a
    /// staggered row at one seed see exactly the same arrival pattern and the ladder's effect is the only
    /// difference between them.
    /// </remarks>
    public static ImmutableArray<long> ArrivalOffsets(ulong trialSeed, int writerCount, ImmutableArray<long> spreadMicroseconds, long grain)
    {
        ImmutableArray<long>.Builder offsets = ImmutableArray.CreateBuilder<long>(writerCount);
        for(int writer = 0; writer < writerCount; writer++)
        {
            if(spreadMicroseconds[writer] <= 0)
            {
                offsets.Add(0);

                continue;
            }

            long spreadUnits = Math.Max(spreadMicroseconds[writer] / grain, 1);
            ulong key = trialSeed ^ ((ulong)(uint)writer << 48) ^ 0xA5A5_A5A5UL;
            offsets.Add((long)(SeedMixer.Mix(key) % (ulong)spreadUnits) * grain);
        }

        return offsets.ToImmutable();
    }


    /// <summary>The writer a cell's verdict speaks for.</summary>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <returns>The writer index.</returns>
    /// <remarks>
    /// The verdict speaks for the median site, and the sites are ordered by the radius a write from them pays.
    /// A deployment picks a topology before it knows which member writes, so the median site is the honest
    /// single answer and the flip by placement is reported beside it rather than averaged away.
    /// </remarks>
    public static int RepresentativeWriter(ImmutableArray<ComputedSiteCost> computed, int writerCount)
    {
        int[] writers = [.. Enumerable.Range(0, writerCount).OrderBy(writer => computed[writer % computed.Length].MajorityRoundTrip).ThenBy(writer => writer)];

        return writers[(writerCount - 1) / 2];
    }
}
