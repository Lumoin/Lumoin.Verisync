using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The bridge's acceptance test: the seven published five-replica rows, re-measured by this harness on the
/// matrix that produced them and set beside the figures the note prints.
/// </summary>
/// <remarks>
/// <para>
/// NOTHING THIS HARNESS MEASURES IS WORTH BELIEVING UNTIL THIS PASSES. The published rows are the fixed point
/// the new code has to land on; a mismatch means the new harness is not the same experiment, whatever else it
/// asserts. That is why the gate is a design constraint on the harness rather than a check bolted on
/// afterwards: reproducing a row means reproducing its jitter draws, so the jitter model draws on a
/// configurable grid with the probe's exact keying and takes the whole-millisecond grid as one setting.
/// </para>
/// <para>
/// The harness is new code rather than an extraction of the probe, and the duplication is deliberate: it is
/// what makes this an independent reproduction rather than the same bytes reporting their own numbers back.
/// </para>
/// </remarks>
internal static class ReproductionGate
{
    /// <summary>The trial count every published row was measured over.</summary>
    public const int PublishedTrials = 400;

    /// <summary>The seed base the published QuePaxa stagger ladder ran under.</summary>
    private const int StaggerLadderSeedBase = 10;

    /// <summary>The seed base the published five-writer QuePaxa row ran under.</summary>
    private const int FiveWriterSeedBase = 20;

    /// <summary>The seed base the published leaderless QuePaxa row ran under.</summary>
    private const int LeaderlessSeedBase = 21;

    /// <summary>The configuration seed the published Fast CASPaxos hedge ladder ran under.</summary>
    private const int HedgeLadderSeed = 1;

    /// <summary>Half the last digit of a published rate, which carries three decimals.</summary>
    internal static double RateTolerance => 0.0005;

    /// <summary>Half the last digit of a published mean, which carries two decimals.</summary>
    internal static double MeanTolerance => 0.005;

    /// <summary>
    /// The band a published decision time reproduces inside. The note carries the column as whole
    /// milliseconds while the probe printed it to one decimal, so the published figure is already a rounding
    /// of a rounding and a whole millisecond is the precision it actually carries. The column is a rider on
    /// the gate rather than one of its named metrics for exactly that reason.
    /// </summary>
    internal static double MillisecondTolerance => 1.0;

    /// <summary>A published count is exact, so it reproduces or it does not.</summary>
    internal static double CountTolerance => 0.0;


    /// <summary>
    /// Runs the gate and prints the seven rows side by side.
    /// </summary>
    /// <param name="trials">The trial count. The published rows were measured at <see cref="PublishedTrials"/>.</param>
    /// <returns>Whether every figure reproduced inside its published precision.</returns>
    public static bool Run(int trials)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);

        Topology spread = Topologies.ProbeSpread();
        int replicaCount = spread.SiteCount;
        JitterModel jitter = JitterModel.PublishedMillisecondGrid;
        long majorityRoundTrip = QuorumDistance.QuorumRoundTrip(spread, 0, QuorumDistance.QuePaxaQuorum(replicaCount));
        long fastRoundTrip = QuorumDistance.QuorumRoundTrip(spread, 0, QuorumDistance.FastQuorum(replicaCount));

        Report.Text("W5 REPRODUCTION GATE - the seven published five-replica rows, re-measured");
        Report.Line($"matrix={spread.Name}, replicas={replicaCount}, trials={trials}, jitter={jitter.Description}");
        Report.Line($"QuePaxa stagger unit = the leader's majority-radius round trip = {VirtualTimePump.ToMilliseconds(majorityRoundTrip):F0}ms");
        Report.Line($"Fast CASPaxos hedging unit = the leading writer's fast-quorum round trip = {VirtualTimePump.ToMilliseconds(fastRoundTrip):F0}ms");
        Report.Blank();

        var rows = ImmutableArray.CreateBuilder<ReproductionRow>();
        rows.AddRange(StaggerLadder(spread, jitter, majorityRoundTrip, trials));
        rows.AddRange(FiveSimultaneousWriters(spread, jitter, trials));
        rows.AddRange(LeaderlessWriters(spread, jitter, trials));
        rows.AddRange(HedgeLadder(spread, jitter, fastRoundTrip, trials));

        Report.Blank();
        Report.Text("side by side");
        Report.Text("columns: row, metric, published (at its own precision), reproduced, difference, tolerance, verdict");

        //The reproduced and difference columns are printed FINER than the tolerance beside them. At the
        //published precision a figure sitting exactly on its tolerance boundary rounds to a difference of one
        //whole tolerance unit and reads as a violation stamped REPRODUCED, while the reproduced column hides
        //where the measurement actually is. Nothing about the verdict changes; only what the row shows.
        bool reproduced = true;
        foreach(ReproductionRow row in rows)
        {
            reproduced &= row.IsReproduced;
            Report.Line($"{row.Row}, {row.Metric}, {row.Published:F3}, {row.Reproduced:F6}, {row.Difference:+0.0000;-0.0000;0.0000}, {row.Tolerance:F4}, {(row.IsReproduced ? "REPRODUCED" : "MISMATCH")}");
        }

        Report.Blank();
        Report.Line($"W5 VERDICT: {(reproduced ? "PASS" : "FAIL")} over {rows.Count} published figures in 7 rows");

        return reproduced;
    }


    private static ImmutableArray<ReproductionRow> StaggerLadder(Topology spread, JitterModel jitter, long ladderUnit, int trials)
    {
        const int writerCount = 3;
        double[] fractions = [0.00, 0.25, 0.50, 1.00];
        double[] publishedLeaderRate = [0.000, 0.242, 0.970, 1.000];
        double[] publishedMeanSteps = [3.00, 2.84, 1.06, 1.00];
        double[] publishedMeanDecision = [507, 483, 180, 169];

        Report.Text("rows 1-4: QuePaxa led stagger ladder, three writers");
        Report.Text("columns: stagger/RTT, staggerMs, leaderFastRate, writerFastRate, meanSteps, meanDecisionMs");

        var rows = ImmutableArray.CreateBuilder<ReproductionRow>();
        for(int rung = 0; rung < fractions.Length; rung++)
        {
            long stagger = (long)(fractions[rung] * ladderUnit);
            ImmutableArray<long> delays = StaggerSchedule.Delays(writerCount, stagger);

            int leaderFast = 0;
            int fastObservations = 0;
            long stepTotal = 0;
            long decisionTotal = 0;
            for(int trial = 0; trial < trials; trial++)
            {
                ImmutableArray<QuePaxaWriterMeasurement> measurements = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
                    spread,
                    writerCount,
                    LeadershipMode.WriterZeroLeads,
                    delays,
                    delays,
                    SeedMixer.TrialSeed(StaggerLadderSeedBase, trial),
                    jitter,
                    QuePaxaArm.DefaultEventBudget));

                foreach(QuePaxaWriterMeasurement measurement in measurements)
                {
                    stepTotal += measurement.Outcome.Steps;
                    decisionTotal += measurement.DecisionMicroseconds;
                    if(measurement.IsFastPath)
                    {
                        fastObservations++;
                    }
                }

                if(measurements[0].IsFastPath)
                {
                    leaderFast++;
                }
            }

            double writes = (double)trials * writerCount;
            double leaderRate = (double)leaderFast / trials;
            double meanSteps = stepTotal / writes;
            double meanDecision = VirtualTimePump.ToMilliseconds(decisionTotal) / writes;

            Report.Line($"{fractions[rung]:F2}, {VirtualTimePump.ToMilliseconds(stagger):F0}, {leaderRate:F3}, {fastObservations / writes:F3}, {meanSteps:F2}, {meanDecision:F1}");

            string name = string.Create(CultureInfo.InvariantCulture, $"quepaxa-stagger-{fractions[rung]:F2}");
            rows.Add(new ReproductionRow(name, "leaderFastRate", publishedLeaderRate[rung], leaderRate, RateTolerance));
            rows.Add(new ReproductionRow(name, "meanSteps", publishedMeanSteps[rung], meanSteps, MeanTolerance));
            rows.Add(new ReproductionRow(name, "meanDecisionMs", publishedMeanDecision[rung], meanDecision, MillisecondTolerance));
        }

        return rows.ToImmutable();
    }


    private static ImmutableArray<ReproductionRow> FiveSimultaneousWriters(Topology spread, JitterModel jitter, int trials)
    {
        const int writerCount = 5;
        const double publishedMeanSteps = 3.78;
        const double publishedTrialsBeyondThree = 391;

        ImmutableArray<long> delays = StaggerSchedule.Delays(writerCount, 0);
        long stepTotal = 0;
        int trialsBeyondThreeSteps = 0;
        for(int trial = 0; trial < trials; trial++)
        {
            ImmutableArray<QuePaxaWriterMeasurement> measurements = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
                spread,
                writerCount,
                LeadershipMode.WriterZeroLeads,
                delays,
                delays,
                SeedMixer.TrialSeed(FiveWriterSeedBase, trial),
                jitter,
                QuePaxaArm.DefaultEventBudget));

            bool beyondThree = false;
            foreach(QuePaxaWriterMeasurement measurement in measurements)
            {
                stepTotal += measurement.Outcome.Steps;
                beyondThree |= measurement.Outcome.Steps > 3;
            }

            if(beyondThree)
            {
                trialsBeyondThreeSteps++;
            }
        }

        double meanSteps = stepTotal / ((double)trials * writerCount);
        Report.Blank();
        Report.Text("row 5: QuePaxa led, five simultaneous writers");
        Report.Line($"meanSteps={meanSteps:F2}, trialsWithAWriterBeyondThreeSteps={trialsBeyondThreeSteps}");

        return
        [
            new ReproductionRow("quepaxa-led-5-writers", "meanSteps", publishedMeanSteps, meanSteps, MeanTolerance),
            new ReproductionRow("quepaxa-led-5-writers", "trialsBeyondThreeSteps", publishedTrialsBeyondThree, trialsBeyondThreeSteps, CountTolerance)
        ];
    }


    private static ImmutableArray<ReproductionRow> LeaderlessWriters(Topology spread, JitterModel jitter, int trials)
    {
        const int writerCount = 3;
        const double publishedMeanSteps = 4.17;
        const double publishedWritesBeyondThree = 380;

        ImmutableArray<long> delays = StaggerSchedule.Delays(writerCount, 0);
        long stepTotal = 0;
        int writesBeyondThree = 0;
        for(int trial = 0; trial < trials; trial++)
        {
            ImmutableArray<QuePaxaWriterMeasurement> measurements = QuePaxaArm.RunTrial(new QuePaxaTrialRequest(
                spread,
                writerCount,
                LeadershipMode.Leaderless,
                delays,
                delays,
                SeedMixer.TrialSeed(LeaderlessSeedBase, trial),
                jitter,
                QuePaxaArm.DefaultEventBudget));

            foreach(QuePaxaWriterMeasurement measurement in measurements)
            {
                stepTotal += measurement.Outcome.Steps;
                if(measurement.Outcome.Steps > 3)
                {
                    writesBeyondThree++;
                }
            }
        }

        double meanSteps = stepTotal / ((double)trials * writerCount);
        Report.Blank();
        Report.Text("row 6: QuePaxa leaderless, three simultaneous writers");
        Report.Line($"meanSteps={meanSteps:F2}, writesBeyondThreeSteps={writesBeyondThree}");

        return
        [
            new ReproductionRow("quepaxa-leaderless-3-writers", "meanSteps", publishedMeanSteps, meanSteps, MeanTolerance),
            new ReproductionRow("quepaxa-leaderless-3-writers", "writesBeyondThreeSteps", publishedWritesBeyondThree, writesBeyondThree, CountTolerance)
        ];
    }


    private static ImmutableArray<ReproductionRow> HedgeLadder(Topology spread, JitterModel jitter, long hedgingUnit, int trials)
    {
        const int writerCount = 3;
        double[] fractions = [0.00, 0.25, 0.50, 1.00, 1.50];
        double[] publishedTrialRate = [0.000, 0.750, 1.000, 1.000, 1.000];
        double[] publishedWriterRate = [0.000, 0.250, 0.333, 0.333, 0.333];
        double[] publishedRoundTrips = [3.00, 2.50, 2.33, 2.33, 2.33];

        Report.Blank();
        Report.Text("row 7: Fast CASPaxos oracle hedge ladder, three writers, simultaneous arrivals");
        Report.Text("columns: hedge/RTT, hedgeMs, trialFastCommitRate, writerFastCommitRate, meanRoundTripsPerWrite, meanAddedWaitMs");

        //The control the probe asserts first: a lone writer has no one to split the round with, so a model
        //that ever fails here is not modelling the protocol.
        OracleMeasurement control = OracleArrivalArm.Measure(spread, writerCount: 1, arrivalSpreadMicroseconds: 0, hedgeDelayMicroseconds: 0, jitter, HedgeLadderSeed, trials);
        Report.Line($"control (single writer): trialFastCommitRate={control.TrialFastCommitRate:F3}");

        var rows = ImmutableArray.CreateBuilder<ReproductionRow>();
        rows.Add(new ReproductionRow("fastcaspaxos-control", "trialFastCommitRate", 1.000, control.TrialFastCommitRate, RateTolerance));

        for(int rung = 0; rung < fractions.Length; rung++)
        {
            long hedgeDelay = (long)(fractions[rung] * hedgingUnit);
            OracleMeasurement measurement = OracleArrivalArm.Measure(spread, writerCount, arrivalSpreadMicroseconds: 0, hedgeDelay, jitter, HedgeLadderSeed, trials);

            Report.Line($"{fractions[rung]:F2}, {VirtualTimePump.ToMilliseconds(hedgeDelay):F0}, {measurement.TrialFastCommitRate:F3}, {measurement.WriterFastCommitRate:F3}, {measurement.MeanRoundTripsPerWrite:F2}, {VirtualTimePump.ToMilliseconds((long)measurement.MeanAddedWaitMicroseconds):F1}");

            string name = string.Create(CultureInfo.InvariantCulture, $"fastcaspaxos-hedge-{fractions[rung]:F2}");
            rows.Add(new ReproductionRow(name, "trialFastCommitRate", publishedTrialRate[rung], measurement.TrialFastCommitRate, RateTolerance));
            rows.Add(new ReproductionRow(name, "writerFastCommitRate", publishedWriterRate[rung], measurement.WriterFastCommitRate, RateTolerance));
            rows.Add(new ReproductionRow(name, "meanRoundTripsPerWrite", publishedRoundTrips[rung], measurement.MeanRoundTripsPerWrite, MeanTolerance));
        }

        return rows.ToImmutable();
    }
}
