using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One cell of the read-modify-write rider: a replica count, a placement and a writer count, measured over
/// every mode and every arrival spread, where each writer applies one change to shared state rather than
/// proposing one value for a single transition.
/// </summary>
/// <remarks>
/// <para>
/// THE RIDER IS A SECOND WORKLOAD OVER THE SAME AXES AND NOT A SECOND GRID. Its cell key, its arrival spreads,
/// its ladder rungs, its representative writer and its seed allocator are the plain cell's, so a rate it
/// measures can be read against the plain cell it came from without any conversion at all. What differs is the
/// workload: every writer holds a change that must land, so a writer whose proposal loses still owes its
/// change and the cell measures what applying it costs.
/// </para>
/// <para>
/// THE TWO ARMS PAY FOR CONTENTION IN DIFFERENT CURRENCIES AND THE ROW SAYS SO. QuePaxa decides among whole
/// proposals, so a loser discards its proposal, recomputes against the winner and runs another consensus
/// instance; Fast CASPaxos recovers the value inside the round and applies the change to it, so a loser pays
/// another round only when its ballot was pre-empted. The conflict column counts each arm's own re-proposal
/// event, which is what makes it the gate's input, and the composition and apply-once columns beside it are
/// what make the difference between the two visible rather than asserted.
/// </para>
/// <para>
/// A WRITER IS A MEMBER, so the writer count cannot exceed the replica count. QuePaxa's read-modify-write path
/// is the versioned register and it writes as one replica of its chain; a writer outside the membership
/// proposes nothing at all, and measuring a refusal as though it were contention would put the rider's
/// headline number on a workload nobody ran.
/// </para>
/// <para>
/// The QuePaxa arm has no leaderless configuration. The versioned register derives its leader from the
/// committed record rather than taking one at the recorder, so leadership is not an axis here; the mode column
/// reports every QuePaxa row as leadered because that is what a versioned register always is.
/// </para>
/// </remarks>
internal static class RmwCellSweep
{
    /// <summary>The stagger rungs a QuePaxa configuration is swept over, in units of the leader's majority-radius round trip.</summary>
    private static ImmutableArray<double> QuePaxaRungs { get; } = [0.00, 0.25, 0.50, 1.00];

    /// <summary>The hedging rungs a Fast CASPaxos configuration is swept over, the first of which is the unhedged one.</summary>
    private static ImmutableArray<double> FastRungs { get; } = [0.00, 0.25, 0.50, 1.00, 1.50];

    /// <summary>The arrival spreads every configuration is reported at, in units of a writer's own majority-radius round trip.</summary>
    private static ImmutableArray<double> ArrivalSpreads { get; } = [0.00, 0.50, 2.00];


    /// <summary>The QuePaxa read-modify-write arm's offset inside its cell's seed range.</summary>
    /// <remarks>
    /// It is the offset the plain sweep reserved for a third arm, so the rider draws a stream no plain row
    /// draws and the two workloads at one cell key are independent measurements rather than one noise pattern
    /// reported twice.
    /// </remarks>
    public const int QuePaxaArmSeedOffset = CellSweep.ReservedArmSeedOffset;

    /// <summary>The Fast CASPaxos read-modify-write arm's offset inside its cell's seed range.</summary>
    public const int FastArmSeedOffset = CellSweep.ReservedArmSeedOffset + 1;


    /// <summary>
    /// Measures one read-modify-write cell without printing anything.
    /// </summary>
    /// <param name="placement">The placement, whose site count is the replica count.</param>
    /// <param name="writerCount">How many writers each apply one change. Must not exceed the replica count.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <param name="trials">How many trials each row is measured over.</param>
    /// <param name="seedBase">The base of the cell's seed range, which separates this cell's trials from another cell's.</param>
    /// <returns>The cell's rows, its verdict at each arrival spread under both readings of the gate, and whether every fold held.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="placement"/> or <paramref name="jitter"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="writerCount"/> or <paramref name="trials"/> is not positive.</exception>
    /// <exception cref="ArgumentException">Thrown if there are more writers than replicas.</exception>
    /// <remarks>
    /// The measurement is separated from the report so that a vector can hold a whole cell to its own rules
    /// without printing one. A cell whose columns could only be read out of a printed line would be pinned by
    /// parsing its own output, which tests the printer rather than the sweep.
    /// </remarks>
    public static RmwCellOutcome Measure(Topology placement, int writerCount, JitterModel jitter, int trials, int seedBase)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(writerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);

        int replicaCount = placement.SiteCount;
        if(writerCount > replicaCount)
        {
            throw new ArgumentException($"The cell runs {writerCount} writers over {replicaCount} replicas, and a read-modify-write writer is a member of the chain it writes to.", nameof(writerCount));
        }

        ImmutableArray<ComputedSiteCost> computed = QuorumDistance.For(placement);
        long staggerUnit = CellSweep.QuePaxaStaggerUnit(computed);
        long hedgeUnit = CellSweep.FastHedgeUnit(computed);
        int representative = CellSweep.RepresentativeWriter(computed, writerCount);

        ImmutableArray<RmwRow>.Builder rows = ImmutableArray.CreateBuilder<RmwRow>();
        foreach(double spread in ArrivalSpreads)
        {
            ImmutableArray<long> spreadMicroseconds = CellSweep.ArrivalSpreadMicroseconds(computed, writerCount, spread);

            foreach(double rung in Rungs(QuePaxaRungs, writerCount))
            {
                rows.Add(QuePaxaRow(placement, computed, writerCount, rung, spread, spreadMicroseconds, staggerUnit, jitter, trials, seedBase, representative));
            }

            foreach(double rung in Rungs(FastRungs, writerCount))
            {
                rows.Add(FastRow(placement, computed, writerCount, rung, spread, spreadMicroseconds, hedgeUnit, jitter, trials, seedBase, representative));
            }
        }

        ImmutableArray<RmwRow> measured = rows.ToImmutable();
        ImmutableArray<MeasuredRow> plain = [.. measured.Select(row => row.Row)];

        return new RmwCellOutcome(
            measured.All(row => row.Row.Agreed),
            measured,
            VerdictReducer.Reduce(plain, staggerUnit, RmwRetryRates.For(measured)),
            VerdictReducer.Reduce(plain, staggerUnit, null));
    }


    /// <summary>
    /// Measures one read-modify-write cell and prints every configuration.
    /// </summary>
    /// <param name="placement">The placement, whose site count is the replica count.</param>
    /// <param name="writerCount">How many writers each apply one change. Must not exceed the replica count.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <param name="trials">How many trials each row is measured over.</param>
    /// <param name="seedBase">The base of the cell's seed range.</param>
    /// <returns>What the cell measured.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="placement"/> or <paramref name="jitter"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="writerCount"/> or <paramref name="trials"/> is not positive.</exception>
    /// <exception cref="ArgumentException">Thrown if there are more writers than replicas.</exception>
    public static RmwCellOutcome Run(Topology placement, int writerCount, JitterModel jitter, int trials, int seedBase)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(jitter);

        int replicaCount = placement.SiteCount;
        ImmutableArray<ComputedSiteCost> computed = QuorumDistance.For(placement);
        long staggerUnit = CellSweep.QuePaxaStaggerUnit(computed);
        long hedgeUnit = CellSweep.FastHedgeUnit(computed);

        Report.Line($"RMW CELL replicas={replicaCount}, topology={placement.Name}, writers={writerCount}, trials={trials}, jitter={jitter.Description}");
        Report.Line($"provenance: {placement.Provenance}");
        Report.Line($"regions: {string.Join(", ", placement.SiteRegions)}");
        Report.Line($"quorums: fast {QuorumDistance.FastQuorum(replicaCount)} of {replicaCount}, majority {QuorumDistance.QuePaxaQuorum(replicaCount)} of {replicaCount}");
        Report.Line($"QuePaxa stagger unit = the leader's majority-radius round trip = {VirtualTimePump.ToMilliseconds(staggerUnit):F3}ms");
        Report.Line($"Fast CASPaxos hedging unit = the leading writer's fast-quorum round trip = {VirtualTimePump.ToMilliseconds(hedgeUnit):F3}ms");
        Report.Line($"arrival spread and the round-trip column are in each writer's own majority-radius round trip; representative writer = {CellSweep.RepresentativeWriter(computed, writerCount)} (median site)");
        Report.Line($"seed base = {seedBase}: QuePaxa arm {seedBase + QuePaxaArmSeedOffset}, Fast CASPaxos arm {seedBase + FastArmSeedOffset}");
        Report.Blank();

        Report.Text("workload: every writer applies one change to shared state, appending its own token to the value it read, and retries until the change lands or its budget is spent");
        Report.Text("a latency column is the writer's OWN change committing, measured from its own activation");
        Report.Text("conflictRate is the fraction of writes that re-proposed because another writer got in first: for QuePaxa another consensus instance, for Fast CASPaxos another classic ballot");
        Report.Text("applyOnceRate is the fraction of writes whose change found itself already applied, which a Fast CASPaxos recovery produces whenever it tallies the writer's own blind round back in and QuePaxa reaches only through an attempt that decided nothing and was carried afterwards");
        Report.Text("columns: protocol, mode, rung, rungMs, spread, p50Ms, p95Ms, maxMs, p95Rtt, p95RepresentativeMs, p95RepresentativeRtt, trialFastRate, writerFastRate, meanRounds, meanAddedWaitMs, representativeAddedWaitMs, conflictRate, meanConflicts, recomposedRate, applyOnceRate, censoredWrites, foldBreaches, lastFold, foldHeld");

        RmwCellOutcome outcome = Measure(placement, writerCount, jitter, trials, seedBase);
        foreach(RmwRow row in outcome.Rows)
        {
            PrintRow(row);
        }

        Report.Blank();
        Report.Line($"FOLD GATE: {(outcome.Agreed ? "PASS" : "FAIL - the cell is void, not slow")}");

        Report.Blank();
        Report.Text("verdict with the workload gate INERT, which is what an idempotent, monotone or abort-on-lose update shape gets");
        foreach(CellVerdict verdict in outcome.InertVerdicts)
        {
            PrintVerdict("INERT", verdict);
        }

        Report.Blank();
        Report.Text("verdict with the workload gate LIVE on this rider's own measured conflict rates, which is what a genuine read-modify-write shape gets");
        Report.Line($"the gate removes a QuePaxa configuration whose conflict rate is above {VerdictReducer.RetryRateCeiling:F2}");
        foreach(CellVerdict verdict in outcome.GatedVerdicts)
        {
            PrintVerdict("GATED", verdict);
        }

        return outcome;
    }


    /// <summary>The rungs a configuration is swept over, which is the first rung alone where there is nobody to stagger against.</summary>
    /// <param name="rungs">The ladder the configuration would sweep at more than one writer.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <returns>The rungs to sweep.</returns>
    private static ImmutableArray<double> Rungs(ImmutableArray<double> rungs, int writerCount) => writerCount == 1 ? [rungs[0]] : rungs;


    /// <summary>
    /// Measures one QuePaxa read-modify-write configuration at one arrival spread.
    /// </summary>
    /// <param name="placement">The placement.</param>
    /// <param name="computed">The placement's computed per-site costs.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <param name="rung">The stagger rung, in units of the leader's majority-radius round trip.</param>
    /// <param name="spread">The arrival spread, in units of a writer's own majority-radius round trip.</param>
    /// <param name="spreadMicroseconds">Each writer's own spread width, in writer order.</param>
    /// <param name="staggerUnit">The unit the rung is denominated in.</param>
    /// <param name="jitter">The per-leg jitter distribution.</param>
    /// <param name="trials">How many trials the row is measured over.</param>
    /// <param name="seedBase">The base of the cell's seed range.</param>
    /// <param name="representative">The writer the verdict speaks for.</param>
    /// <returns>The row.</returns>
    private static RmwRow QuePaxaRow(
        Topology placement,
        ImmutableArray<ComputedSiteCost> computed,
        int writerCount,
        double rung,
        double spread,
        ImmutableArray<long> spreadMicroseconds,
        long staggerUnit,
        JitterModel jitter,
        int trials,
        int seedBase,
        int representative)
    {
        long baseDelay = (long)(rung * staggerUnit);
        var tally = new RowTally(trials, writerCount);

        for(int trial = 0; trial < trials; trial++)
        {
            ulong trialSeed = SeedMixer.TrialSeed(seedBase + QuePaxaArmSeedOffset, trial);
            ImmutableArray<long> arrivals = CellSweep.ArrivalOffsets(trialSeed, writerCount, spreadMicroseconds, jitter.GrainMicroseconds);

            RmwTrialOutcome<RmwQuePaxaWriterMeasurement> outcome = RmwQuePaxaArm.RunTrial(new RmwQuePaxaTrialRequest(
                placement,
                writerCount,
                arrivals,
                baseDelay,
                trialSeed,
                jitter,
                RmwQuePaxaArm.DefaultMaxAttempts,
                RmwQuePaxaArm.DefaultAttemptsPerRecorder,
                RmwQuePaxaArm.DefaultEventBudget));

            tally.OpenTrial(outcome.Fold, outcome.FinalValue);
            foreach(RmwQuePaxaWriterMeasurement measurement in outcome.Writers)
            {
                tally.Observe(
                    computed[measurement.Site].MajorityRoundTrip,
                    measurement.Writer == representative,
                    measurement.IsCommitted,
                    measurement.CommitMicroseconds,
                    measurement.AddedWaitMicroseconds,
                    measurement.IsCommitted && measurement.TookFastPath,
                    measurement.Attempts,
                    measurement.ConflictRecomputes,
                    measurement.RecomposedAgainstAnotherWriter,
                    measurement.ApplyOnceTokenFirings);
            }

            tally.CloseTrial();
        }

        return tally.ToRow(ConfigurationMode.QuePaxaLeadered, rung, baseDelay, spread);
    }


    /// <summary>
    /// Measures one Fast CASPaxos read-modify-write configuration at one arrival spread.
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
    private static RmwRow FastRow(
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
        var tally = new RowTally(trials, writerCount);

        for(int trial = 0; trial < trials; trial++)
        {
            ulong trialSeed = SeedMixer.TrialSeed(seedBase + FastArmSeedOffset, trial);
            ImmutableArray<long> arrivals = CellSweep.ArrivalOffsets(trialSeed, writerCount, spreadMicroseconds, jitter.GrainMicroseconds);

            RmwTrialOutcome<RmwFastWriterMeasurement> outcome = RmwFastCasPaxosArm.RunTrial(new RmwFastTrialRequest(
                placement,
                writerCount,
                arrivals,
                VirtualTimePump.ToTimeSpan(baseDelay),
                trialSeed,
                jitter,
                RmwFastCasPaxosArm.DefaultMaxRecoveryRounds,
                RmwFastCasPaxosArm.DefaultEventBudget));

            tally.OpenTrial(outcome.Fold, outcome.FinalValue);
            foreach(RmwFastWriterMeasurement measurement in outcome.Writers)
            {
                tally.Observe(
                    computed[measurement.Site].MajorityRoundTrip,
                    measurement.Writer == representative,
                    measurement.IsCommitted,
                    measurement.CommitMicroseconds,
                    measurement.AddedWaitMicroseconds,
                    measurement.TookFastPath,
                    measurement.PhasesExecuted,
                    measurement.ConflictRounds,
                    measurement.RecomposedAgainstAnotherWriter,
                    measurement.ApplyOnceTokenFirings);
            }

            tally.CloseTrial();
        }

        return tally.ToRow(rung == 0.0 ? ConfigurationMode.FastUnhedged : ConfigurationMode.FastHedged, rung, baseDelay, spread);
    }


    /// <summary>Prints one measured row, in the column order the header announced.</summary>
    /// <param name="row">The row.</param>
    private static void PrintRow(RmwRow row) =>
        Report.Line($"{row.Row.ProtocolName}, {row.Row.ModeName}, {row.Row.Rung:F2}, {VirtualTimePump.ToMilliseconds(row.Row.RungMicroseconds):F3}, {row.Row.Spread:F2}, {row.Row.P50}, {row.Row.P95}, {row.Row.Max}, {row.Row.P95RoundTrips}, {row.Row.RepresentativeP95}, {row.Row.RepresentativeP95RoundTrips}, {row.Row.TrialFastRate:F3}, {row.Row.WriterFastRate:F3}, {row.Row.MeanSteps:F2}, {VirtualTimePump.ToMilliseconds((long)row.Row.MeanAddedWaitMicroseconds):F3}, {VirtualTimePump.ToMilliseconds((long)row.Row.RepresentativeAddedWaitMicroseconds):F3}, {row.ConflictRetryRate:F3}, {row.MeanConflictRetries:F3}, {row.RecomposedRate:F3}, {row.ApplyOnceRate:F3}, {row.Censored}, {row.FoldBreaches}, {row.SampleFinalValue}, {(row.Row.Agreed ? "yes" : "NO")}");


    /// <summary>
    /// Prints one arrival spread's verdict under one reading of the gate, and one line for each configuration
    /// its gates removed.
    /// </summary>
    /// <param name="reading">Which reading of the workload gate produced the verdict.</param>
    /// <param name="verdict">The verdict.</param>
    private static void PrintVerdict(string reading, CellVerdict verdict)
    {
        foreach(RemovedConfiguration removal in verdict.Removed)
        {
            Report.Line($"REMOVED {reading} spread={verdict.Spread:F2} configuration={removal.Row.Key} reason={removal.Reason}");
        }

        if(verdict.Winner is null)
        {
            Report.Line($"RMW VERDICT {reading} spread={verdict.Spread:F2} outcome={verdict.OutcomeName} reason={verdict.Reason}");

            return;
        }

        Report.Line($"RMW VERDICT {reading} spread={verdict.Spread:F2} outcome={verdict.OutcomeName} configuration={verdict.Winner.Key} rungMajorityRadius={verdict.WinningRungInMajorityRadius:F3} rungMs={VirtualTimePump.ToMilliseconds(verdict.Winner.RungMicroseconds):F3} p95Ms={verdict.Winner.RepresentativeP95} margin={verdict.MarginText} runnerUp={verdict.RunnerUp?.Key ?? "none"} runnerUpP95Ms={(verdict.RunnerUp is null ? "none" : verdict.RunnerUp.RepresentativeP95.ToString())} reason={verdict.Reason}");
    }


    /// <summary>
    /// One configuration's populations while its trial loop runs.
    /// </summary>
    /// <param name="trials">How many trials the row is measured over.</param>
    /// <param name="writerCount">The writer count.</param>
    /// <remarks>
    /// The two arms report different records and the same columns, so the accumulation is stated once here and
    /// each arm hands it the fields its own measurement carries. A second copy of the arithmetic would let one
    /// arm's percentiles be taken over a different population than the other's, and the verdict argmins the two
    /// against each other.
    /// </remarks>
    private sealed class RowTally(int trials, int writerCount)
    {
        /// <summary>Every write's commit latency in milliseconds.</summary>
        private List<double> Latencies { get; } = new(trials * writerCount);

        /// <summary>The same latencies in each writer's own majority-radius round trips.</summary>
        private List<double> RoundTrips { get; } = new(trials * writerCount);

        /// <summary>The representative writer's own commit latencies in milliseconds.</summary>
        private List<double> RepresentativeLatencies { get; } = new(trials);

        /// <summary>The representative writer's own latencies in its own round trips.</summary>
        private List<double> RepresentativeRoundTrips { get; } = new(trials);

        /// <summary>How many writes spent their budget without their own change committing.</summary>
        private int Censored { get; set; }

        /// <summary>How many of those were the representative writer's own.</summary>
        private int RepresentativeCensored { get; set; }

        /// <summary>How many trials the fold oracle rejected.</summary>
        private int FoldBreaches { get; set; }

        /// <summary>Whether every trial's fold held.</summary>
        private bool FoldHolds { get; set; } = true;

        /// <summary>The value the replicas held at the end of the last trial.</summary>
        private string SampleFinalValue { get; set; } = string.Empty;

        /// <summary>How many trials had at least one write settle on the protocol's one-round-trip path.</summary>
        private int TrialsWithFastPath { get; set; }

        /// <summary>Whether the trial being observed has had such a write.</summary>
        private bool TrialHasFastPath { get; set; }

        /// <summary>How many writes settled on that path.</summary>
        private int FastWrites { get; set; }

        /// <summary>The total rounds every write executed.</summary>
        private long RoundTotal { get; set; }

        /// <summary>The total stagger every write paid.</summary>
        private long AddedWaitTotal { get; set; }

        /// <summary>The total stagger the representative writer paid.</summary>
        private long RepresentativeAddedWaitTotal { get; set; }

        /// <summary>How many writes re-proposed at least once because another writer got in first.</summary>
        private int ConflictWrites { get; set; }

        /// <summary>The total number of those re-proposals.</summary>
        private long ConflictTotal { get; set; }

        /// <summary>How many writes composed against a value another writer had already committed.</summary>
        private int RecomposedWrites { get; set; }

        /// <summary>How many writes found their own change already applied.</summary>
        private int ApplyOnceWrites { get; set; }


        /// <summary>Records what one trial's oracle found and starts observing that trial's writes.</summary>
        /// <param name="fold">The oracle's verdict.</param>
        /// <param name="finalValue">The value the replicas were left holding.</param>
        public void OpenTrial(RmwFoldVerdict fold, string? finalValue)
        {
            ArgumentNullException.ThrowIfNull(fold);

            if(!fold.Holds)
            {
                FoldBreaches++;
                FoldHolds = false;
            }

            SampleFinalValue = finalValue ?? "(unwritten)";
            TrialHasFastPath = false;
        }


        /// <summary>Records what one write of the open trial cost.</summary>
        /// <param name="majorityRoundTrip">That writer's own majority-radius round trip, which the round-trip column divides by.</param>
        /// <param name="isRepresentative">Whether the write is the representative writer's own.</param>
        /// <param name="isCommitted">Whether the writer's own change committed.</param>
        /// <param name="commitMicroseconds">When it committed, measured from that writer's own activation.</param>
        /// <param name="addedWaitMicroseconds">The stagger the writer paid before it first sent.</param>
        /// <param name="tookFastPath">Whether the change settled on the protocol's one-round-trip path.</param>
        /// <param name="rounds">The rounds the write executed.</param>
        /// <param name="conflicts">How many times it re-proposed because another writer got in first.</param>
        /// <param name="recomposed">Whether its change function ran against a value another writer had already committed.</param>
        /// <param name="applyOnceFirings">How many times its change function found its own change already applied.</param>
        public void Observe(
            long majorityRoundTrip,
            bool isRepresentative,
            bool isCommitted,
            long? commitMicroseconds,
            long addedWaitMicroseconds,
            bool tookFastPath,
            int rounds,
            int conflicts,
            bool recomposed,
            int applyOnceFirings)
        {
            RoundTotal += rounds;
            AddedWaitTotal += addedWaitMicroseconds;
            ConflictTotal += conflicts;
            if(conflicts > 0)
            {
                ConflictWrites++;
            }

            if(recomposed)
            {
                RecomposedWrites++;
            }

            if(applyOnceFirings > 0)
            {
                ApplyOnceWrites++;
            }

            if(tookFastPath)
            {
                FastWrites++;
                TrialHasFastPath = true;
            }

            //A write that never landed its own change is censored: it contributes no sample and the row's
            //percentiles rank it above every write that finished.
            if(isCommitted && commitMicroseconds is { } committed)
            {
                Latencies.Add(VirtualTimePump.ToMilliseconds(committed));
                RoundTrips.Add(committed / (double)majorityRoundTrip);
            }
            else
            {
                Censored++;
            }

            if(!isRepresentative)
            {
                return;
            }

            RepresentativeAddedWaitTotal += addedWaitMicroseconds;
            if(isCommitted && commitMicroseconds is { } representative)
            {
                RepresentativeLatencies.Add(VirtualTimePump.ToMilliseconds(representative));
                RepresentativeRoundTrips.Add(representative / (double)majorityRoundTrip);
            }
            else
            {
                RepresentativeCensored++;
            }
        }


        /// <summary>Closes the trial being observed, which is how the trial-level fast-path count is taken.</summary>
        /// <remarks>
        /// The trial-level rate counts trials rather than writes, so it can only be taken once the trial's
        /// whole writer loop has been observed. A count taken while the loop is still running would report the
        /// writes seen so far, and the two readings diverge by a factor of the writer count exactly where
        /// staggering works best.
        /// </remarks>
        public void CloseTrial()
        {
            if(TrialHasFastPath)
            {
                TrialsWithFastPath++;
            }
        }


        /// <summary>The row these populations make.</summary>
        /// <param name="mode">The configuration.</param>
        /// <param name="rung">The ladder rung as an operator configures it.</param>
        /// <param name="rungMicroseconds">The same rung in absolute microseconds.</param>
        /// <param name="spread">The arrival spread.</param>
        /// <returns>The row.</returns>
        public RmwRow ToRow(ConfigurationMode mode, double rung, long rungMicroseconds, double spread)
        {
            double writes = (double)trials * writerCount;
            MeasuredRow row = MeasuredRow.Of(
                mode,
                rung,
                rungMicroseconds,
                spread,
                Latencies,
                RoundTrips,
                RepresentativeLatencies,
                RepresentativeRoundTrips,
                Censored,
                RepresentativeCensored,
                0,
                TrialsWithFastPath / (double)trials,
                FastWrites / writes,
                RoundTotal / writes,
                AddedWaitTotal / writes,
                RepresentativeAddedWaitTotal / (double)trials,
                FoldHolds);

            return new RmwRow(
                row,
                ConflictWrites / writes,
                ConflictTotal / writes,
                RecomposedWrites / writes,
                ApplyOnceWrites / writes,
                Censored,
                FoldBreaches,
                SampleFinalValue);
        }
    }
}
