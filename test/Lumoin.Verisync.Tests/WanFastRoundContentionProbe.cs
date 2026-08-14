using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Measures how often a Fast CASPaxos fast round splits when several replicas write the same register over a
/// wide-area network, how much a staggered send schedule changes that, and what the same contention costs a
/// QuePaxa register driven through its shipped proposer and recorders.
///
/// The question this answers is the escalation trigger for the metadata plane: a Fast CASPaxos round that
/// splits costs a classic recovery, so the fraction of contended transitions that fall back is what decides
/// whether the register tier is adequate or whether a protocol whose concurrent proposers cooperate rather
/// than collide is worth its machinery. The QuePaxa half measures the cooperating side of that comparison
/// under the same arrival model: a contended round costs steps rather than colliding recoveries, and the
/// stagger that rescues the Fast CASPaxos round is the same one that restores the QuePaxa leader's one-step
/// commit.
///
/// The Fast CASPaxos model is a discrete-event simulation of one contended transition. Each writer sends its
/// accept to every acceptor; a message arrives at its acceptor after the one-way delay for that site pair
/// plus jitter; arrivals are then applied in time order through the production
/// <see cref="FastCasPaxosRegister{TValue}"/>, so the accept rules, the tie-breaking, and the fast-quorum
/// predicate exercised here are the shipped ones and not a restatement. The staggering, by contrast, is a
/// restatement: each writer's activation is delayed by its position times the configured delay, which is the
/// policy a <see cref="HedgingSchedule"/> carries, and neither <see cref="HedgingSchedule"/> nor
/// <see cref="HedgedFastWriter{TValue}"/> is exercised here. An acceptor keeps the first value it sees for
/// the fast ballot and rejects any other, so a writer commits on the fast path only when it reaches a fast
/// quorum of acceptors first. The trial fast-commit rate counts trials in which any writer reached its fast
/// quorum; the per-writer rate beside it counts the writes that did, and the two diverge exactly where
/// hedging hands the round to one writer while the rest still fall back.
///
/// The QuePaxa half runs the shipped stack end to end over a virtual-time transport: per-message delivery
/// through <see cref="RecorderEndpointDelegate{TValue}"/> endpoints into a <see cref="QuePaxaNode{TValue}"/>
/// per site, driven by <see cref="QuePaxaProposer{TValue}"/>, so the fold, the downgrade, the
/// request-to-reply mapping and the act-on-the-first-quorum rule are the shipped ones. The transport is
/// lossless, so the proposer's fault machinery - the attempt budget, the quorum-unreachable exit and the
/// below-step reply filter - is deliberately unexercised here; the interleaving law suite covers it. The
/// leaderless downgrade rule is likewise pinned by the recorder unit suite rather than here, because every
/// configuration below gives all recorders one leader or none.
///
/// The cost of falling back is assumed for Fast CASPaxos, taken as one wasted fast round trip plus two for
/// the classic recovery against one round trip for a fast commit, and it is measured for QuePaxa as protocol
/// steps, each one round trip to a majority-radius quorum. Steps are the only cross-protocol currency in any
/// assertion: the Fast CASPaxos rate is an oracle count over every acceptor while the QuePaxa rate is the
/// shipped proposer acting on the first quorum of replies, which is the stricter test, so the comparison is
/// conservative toward Fast CASPaxos. Virtual milliseconds from the pump include jitter on both legs and are
/// never compared against the jitterless distance columns. The simulation does not model recoveries
/// contending with each other, which can only make the unhedged Fast CASPaxos column worse than reported
/// here.
/// </summary>
[TestClass]
internal sealed class WanFastRoundContentionProbe
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// One-way delays in milliseconds between five sites, symmetric with a zero diagonal, at the scale of
    /// inter-region wide-area links: Ireland, N. Virginia, Sao Paulo, Mumbai, Tokyo.
    /// </summary>
    /// <remarks>
    /// Illustrative figures at realistic magnitudes, not measurements of any particular deployment.
    /// </remarks>
    private static int[][] SpreadDelays { get; } =
    [
        [0, 38, 90, 60, 110],
        [38, 0, 60, 95, 75],
        [90, 60, 0, 150, 130],
        [60, 95, 150, 0, 65],
        [110, 75, 130, 65, 0]
    ];

    /// <summary>
    /// The same five acceptors placed as a co-located majority plus two remote sites: three European sites a
    /// few milliseconds apart, Tokyo and Sao Paulo far from everything.
    /// </summary>
    /// <remarks>
    /// This is how a deployment that wants a cheap majority actually looks, and it is the placement where the
    /// fast path's supermajority has to leave the region while the classic quorum does not.
    /// </remarks>
    private static int[][] ClusteredDelays { get; } =
    [
        [0, 15, 5, 110, 90],
        [15, 0, 10, 120, 100],
        [5, 10, 0, 115, 92],
        [110, 120, 115, 0, 130],
        [90, 100, 92, 130, 0]
    ];

    /// <summary>
    /// The three sites forming the co-located majority in the clustered placement.
    /// </summary>
    private static ImmutableHashSet<int> ClusteredMajority { get; } = [0, 1, 2];

    private const int SiteCount = 5;
    private const int WriterCount = 3;
    private const int TrialsPerConfiguration = 400;
    private const int DefaultJitter = 30;

    /// <summary>
    /// The cost model, in round trips: a fast commit is one, a writer that must recover pays its wasted fast
    /// round plus the classic prepare and accept.
    /// </summary>
    private const double FastCommitRoundTrips = 1.0;
    private const double FallbackRoundTrips = 3.0;


    [TestMethod]
    public void HedgingTheFastRoundCutsContendedFallbacks()
    {
        int leaderRoundTrip = FastQuorumRoundTrip(SpreadDelays, 0);
        TestContext.WriteLine($"acceptors={SiteCount}, writers={WriterCount}, fastQuorum={FastQuorum}, classicQuorum={ClassicQuorum}, trials={TrialsPerConfiguration}, jitter=0..{DefaultJitter}ms");
        TestContext.WriteLine($"fast-quorum round trip by writer site (ms): {string.Join(", ", Enumerable.Range(0, WriterCount).Select(site => FastQuorumRoundTrip(SpreadDelays, site)))}");
        TestContext.WriteLine($"hedging unit = the leading writer's fast-quorum round trip = {leaderRoundTrip}ms");

        Measurement control = Measure(SpreadDelays, writerCount: 1, arrivalSpread: 0, hedgeDelay: 0, jitter: DefaultJitter, seed: 1);
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"control (single writer): trialFastCommitRate={control.TrialFastCommitRate:F3}"));

        //A lone writer has no one to split the round with, so a model that ever fails here is not modelling
        //the protocol.
        Assert.AreEqual(1.0, control.TrialFastCommitRate, "A single writer must always reach its fast quorum.");

        double[] hedgeFractions = [0.0, 0.25, 0.5, 1.0, 1.5];
        var byHedge = new List<(double Fraction, Measurement Result)>();
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"simultaneous arrivals, hedge delay sweep over {hedgeFractions.Length} settings");
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"columns: hedgeDelay/RTT, hedgeDelayMs, trialFastCommitRate, writerFastCommitRate, meanRoundTripsPerWrite (fallback costs {FallbackRoundTrips:F0}), meanAddedWaitMs"));
        foreach(double fraction in hedgeFractions)
        {
            int hedgeDelay = (int)(fraction * leaderRoundTrip);
            Measurement result = Measure(SpreadDelays, WriterCount, arrivalSpread: 0, hedgeDelay, DefaultJitter, seed: 1);
            byHedge.Add((fraction, result));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fraction:F2}, {hedgeDelay}, {result.TrialFastCommitRate:F3}, {result.WriterFastCommitRate:F3}, {result.MeanRoundTripsPerWrite:F2}, {result.MeanAddedWaitMs:F1}"));
        }

        double[] spreadFractions = [0.0, 0.5, 1.0, 2.0];
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"arrival-spread sweep, unhedged against hedging at {leaderRoundTrip}ms");
        TestContext.WriteLine($"columns: spread/RTT, spreadMs, unhedgedTrialRate, hedgedTrialRate, unhedgedRoundTrips, hedgedRoundTrips ({TrialsPerConfiguration} trials per row)");
        var bySpread = new List<(double Fraction, Measurement Unhedged, Measurement Hedged)>();
        foreach(double fraction in spreadFractions)
        {
            int spread = (int)(fraction * leaderRoundTrip);
            Measurement unhedged = Measure(SpreadDelays, WriterCount, spread, hedgeDelay: 0, DefaultJitter, seed: 2);
            Measurement hedged = Measure(SpreadDelays, WriterCount, spread, leaderRoundTrip, DefaultJitter, seed: 2);
            bySpread.Add((fraction, unhedged, hedged));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fraction:F2}, {spread}, {unhedged.TrialFastCommitRate:F3}, {hedged.TrialFastCommitRate:F3}, {unhedged.MeanRoundTripsPerWrite:F2}, {hedged.MeanRoundTripsPerWrite:F2}"));
        }

        int[] writerCounts = [2, 3, 5];
        int halfRoundTrip = leaderRoundTrip / 2;
        Measurement? hedgedAtFiveWriters = null;
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"writer-count sweep at an arrival spread of {halfRoundTrip}ms");
        TestContext.WriteLine($"columns: writers, unhedgedTrialRate, hedgedTrialRate, hedgedWriterRate, unhedgedRoundTrips, hedgedRoundTrips ({TrialsPerConfiguration} trials per row)");
        foreach(int writers in writerCounts)
        {
            Measurement unhedged = Measure(SpreadDelays, writers, halfRoundTrip, hedgeDelay: 0, DefaultJitter, seed: 3);
            Measurement hedged = Measure(SpreadDelays, writers, halfRoundTrip, leaderRoundTrip, DefaultJitter, seed: 3);
            if(writers == 5)
            {
                hedgedAtFiveWriters = hedged;
            }

            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{writers}, {unhedged.TrialFastCommitRate:F3}, {hedged.TrialFastCommitRate:F3}, {hedged.WriterFastCommitRate:F3}, {unhedged.MeanRoundTripsPerWrite:F2}, {hedged.MeanRoundTripsPerWrite:F2}"));
        }

        //The trial rate and the per-writer rate must be tellable apart exactly where they diverge most: full
        //hedging at five writers hands the round to some writer in nearly every trial while most of the five
        //writes still fall back, so a rate column that showed 1.000 there as a per-writer experience would
        //be claiming what no writer gets.
        Assert.IsGreaterThan(0.95, hedgedAtFiveWriters!.TrialFastCommitRate,
            $"Full hedging at five writers was expected to save the round in nearly every trial; the trial rate was {hedgedAtFiveWriters.TrialFastCommitRate:F3}.");

        Assert.IsLessThan(0.25, hedgedAtFiveWriters.WriterFastCommitRate,
            $"Most of five hedged writers were still expected to fall back; the per-writer rate was {hedgedAtFiveWriters.WriterFastCommitRate:F3}.");

        //Contention measured on the clustered placement, beside a twin run whose every argument except the
        //delay matrix is identical. The twin is what makes the matrix parameter load-bearing: a Measure that
        //ignored its matrix would return two identical records here, and the separation assert below fails
        //on identity before any threshold is consulted. The spread at intra-cluster scale is what separates
        //the placements - a first mover sweeps a co-located majority plus the nearest remote site before its
        //rivals wake, while the same head start buys much less when every acceptor is far away.
        int intraClusterSpread = 45;
        Measurement clusteredContention = Measure(ClusteredDelays, WriterCount, intraClusterSpread, hedgeDelay: 0, DefaultJitter, seed: 4);
        Measurement spreadTwin = Measure(SpreadDelays, WriterCount, intraClusterSpread, hedgeDelay: 0, DefaultJitter, seed: 4);
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"clustered contention at an arrival spread of {intraClusterSpread}ms, against the spread-placement twin");
        TestContext.WriteLine($"columns: placement, trialFastCommitRate, writerFastCommitRate, meanRoundTripsPerWrite ({TrialsPerConfiguration} trials per row)");
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"clustered, {clusteredContention.TrialFastCommitRate:F3}, {clusteredContention.WriterFastCommitRate:F3}, {clusteredContention.MeanRoundTripsPerWrite:F2}"));
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"spread, {spreadTwin.TrialFastCommitRate:F3}, {spreadTwin.WriterFastCommitRate:F3}, {spreadTwin.MeanRoundTripsPerWrite:F2}"));

        Assert.IsGreaterThan(spreadTwin.TrialFastCommitRate + 0.10, clusteredContention.TrialFastCommitRate,
            $"A head start at intra-cluster scale was expected to save materially more rounds on the clustered placement than on the spread one; measured {clusteredContention.TrialFastCommitRate:F3} against {spreadTwin.TrialFastCommitRate:F3}.");

        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"quorum distance: one round trip at fast-quorum distance against two at classic-quorum distance");
        TestContext.WriteLine($"columns: placement, site, fastCommitMs, classicRoundMs, quePaxaLeaderMs, quePaxaOtherMs, fastOverClassic (quorums {FastQuorum}, {ClassicQuorum} and {QuePaxaQuorum} of {SiteCount})");
        var quorumDistance = new List<(string Placement, int Site, int FastMs, int ClassicMs, int QuePaxaLeaderMs)>();
        foreach((string placement, int[][] delays) in new[] { ("spread", SpreadDelays), ("clustered", ClusteredDelays) })
        {
            for(int site = 0; site < SiteCount; site++)
            {
                int fastMs = FastQuorumRoundTrip(delays, site);
                int classicMs = 2 * ClassicQuorumRoundTrip(delays, site);
                int quePaxaLeaderMs = QuePaxaLeaderRoundTrip(delays, site);
                quorumDistance.Add((placement, site, fastMs, classicMs, quePaxaLeaderMs));
                TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{placement}, {site}, {fastMs}, {classicMs}, {quePaxaLeaderMs}, {3 * quePaxaLeaderMs}, {(double)fastMs / classicMs:F2}"));
            }
        }

        //Both quorums are strict majorities read from their production types, which is what licenses reading
        //the classic radius as QuePaxa's radius in the columns above.
        Assert.AreEqual(ClassicQuorum, QuePaxaQuorum,
            "The QuePaxa quorum and the classic quorum are both strict majorities and must agree at five replicas.");

        //The QuePaxa believed leader decides in ONE round trip at the majority radius, so it beats the
        //leaderless fast path's supermajority radius at every site of both placements - including the
        //clustered remote sites, where the margin is thin (230 against 240 at site 3) and load-bearing
        //against future matrix edits. The win is conditional: the non-leader column is three steps and the
        //worst number at every spread site, while the Fast CASPaxos fast path is any-writer.
        foreach((string placement, int site, int fastMs, int classicMs, int quePaxaLeaderMs) in quorumDistance)
        {
            Assert.IsLessThan(fastMs, quePaxaLeaderMs,
                $"The QuePaxa believed leader at {placement} site {site} was expected to beat the leaderless fast path's supermajority radius.");

            //Harness integrity rather than a production pin: given equal quorums this is the same sorted
            //radius read twice, and an inequality here means the columns drifted apart in the harness.
            Assert.AreEqual(classicMs, 2 * quePaxaLeaderMs,
                $"The classic round at {placement} site {site} is two round trips at the radius the QuePaxa leader pays once.");
        }

        //On a spread placement the fast path keeps an edge everywhere, but never the 2x that counting round
        //trips suggests: a fast commit is one round trip to the FOURTH-nearest acceptor while a classic round
        //is two to the THIRD-nearest, so the supermajority eats most of the saving.
        foreach((string _, int site, int fastMs, int classicMs, int _) in quorumDistance.Where(entry => entry.Placement == "spread"))
        {
            Assert.IsLessThan(classicMs, fastMs,
                $"On the spread placement the fast path was expected to beat a classic round at site {site}.");

            Assert.IsGreaterThan(0.5, (double)fastMs / classicMs,
                $"The fast path at site {site} was expected to save less than half a classic round, not more.");
        }

        //On a clustered placement the ordering INVERTS for the co-located majority: a simple majority is
        //inside the region while the supermajority is not, so the leaderless fast path has to leave the
        //cluster and the leadered classic round does not. A writer inside the majority should prefer classic;
        //a writer outside it should prefer fast. Mode selection is therefore per writer, by placement.
        foreach((string _, int site, int fastMs, int classicMs, int _) in quorumDistance.Where(entry => entry.Placement == "clustered"))
        {
            bool coLocated = ClusteredMajority.Contains(site);
            if(coLocated)
            {
                Assert.IsGreaterThan(classicMs, fastMs,
                    $"Inside the co-located majority the fast path was expected to be the SLOWER mode at site {site}.");
            }
            else
            {
                Assert.IsLessThan(classicMs, fastMs,
                    $"Outside the co-located majority the fast path was expected to remain the faster mode at site {site}.");
            }
        }

        Measurement unhedgedSimultaneous = byHedge[0].Result;
        Measurement fullyHedged = byHedge.Single(entry => entry.Fraction == 1.0).Result;

        //Simultaneous wide-area writers split the round: this is the cost the escalation question is about,
        //and it has to be present before any remedy is worth measuring.
        Assert.IsLessThan(0.25, unhedgedSimultaneous.TrialFastCommitRate,
            $"Unhedged simultaneous writers were expected to split the fast round; the trial fast-commit rate was {unhedgedSimultaneous.TrialFastCommitRate:F3}.");

        //A delay at the scale of the round trip hands the round to the leading writer almost every time.
        Assert.IsGreaterThan(0.95, fullyHedged.TrialFastCommitRate,
            $"Hedging by one round trip was expected to restore the fast path; the trial fast-commit rate was {fullyHedged.TrialFastCommitRate:F3}.");

        //More stagger never means more splits: the effect has to be monotone, or the mechanism is not the
        //one being claimed.
        for(int i = 1; i < byHedge.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(byHedge[i - 1].Result.TrialFastCommitRate, byHedge[i].Result.TrialFastCommitRate,
                $"Trial fast-commit rate fell when the hedging delay grew from {byHedge[i - 1].Fraction:F2} to {byHedge[i].Fraction:F2} round trips.");
        }

        //Writes that arrive naturally spread out contend less on their own, and what hedging has left to buy
        //shrinks with them. That is the honest limit of the mechanism, and the reason the arrival pattern
        //rather than the link length decides whether a protocol whose proposers cooperate is worth its
        //machinery: spreading is not a cure either, since a fast quorum is a supermajority and even writes a
        //couple of round trips apart still split the round some of the time.
        (double Fraction, Measurement Unhedged, Measurement Hedged) simultaneous = bySpread[0];
        (double Fraction, Measurement Unhedged, Measurement Hedged) wideSpread = bySpread[^1];

        Assert.IsGreaterThan(simultaneous.Unhedged.TrialFastCommitRate, wideSpread.Unhedged.TrialFastCommitRate,
            $"Spreading arrivals by {wideSpread.Fraction:F2} round trips was expected to reduce contention without any hedging.");

        Assert.IsLessThan(simultaneous.Hedged.TrialFastCommitRate - simultaneous.Unhedged.TrialFastCommitRate,
            wideSpread.Hedged.TrialFastCommitRate - wideSpread.Unhedged.TrialFastCommitRate,
            "Hedging's remaining gain was expected to shrink as arrivals spread out on their own.");
    }


    [TestMethod]
    public void QuePaxaContentionCostsBoundedStepsAndStaggeringRestoresTheFastPath()
    {
        //The pump's two fail-closed guards convert a wedge into a red rather than a hang, and each is pinned
        //by firing it once: a writer whose messages are withheld is reported parked when the queue drains,
        //and a run that cannot drain within its event budget stops rather than spins.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = RunWanTrial(
            SpreadDelays, writerCount: 1, WanLeadership.WriterZeroLeads, [0L], trialSeed: 1, eventBudget: WanEventBudget, withholdDelivery: true));

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = RunWanTrial(
            SpreadDelays, writerCount: 1, WanLeadership.WriterZeroLeads, [0L], trialSeed: 1, eventBudget: 2, withholdDelivery: false));

        int ladderUnit = QuePaxaLeaderRoundTrip(SpreadDelays, 0);
        TestContext.WriteLine($"recorders={SiteCount}, quorum={QuePaxaQuorum} of {SiteCount}, writers at their site indices, trials={TrialsPerConfiguration}, jitter=0..{DefaultJitter - 1}ms per leg");
        TestContext.WriteLine($"stagger unit = the leader's majority-radius round trip = {ladderUnit}ms");

        //The believed leader's uncontended commit is one protocol step, and the virtual-time ceiling kills
        //the demotion the step count cannot see: a proposer that widened its quorum would still decide in
        //one step but pay the fourth-nearest radius, 180ms and up, where the third-nearest reply is in by
        //178ms under every jitter pattern. The leader also draws no phase-zero priority at all, because the
        //reserved template is the claim, so a wiring that stopped believing costs a draw this loop sees.
        for(int trial = 0; trial < TrialsPerConfiguration; trial++)
        {
            WanWriterResult leader = RunWanTrial(SpreadDelays, 1, WanLeadership.WriterZeroLeads, [0L], WanTrialSeed(1, trial), WanEventBudget, withholdDelivery: false)[0];
            Assert.AreEqual(RecorderStep.RoundOnePhaseZero, leader.Outcome.DecidedAt, $"The uncontended believed leader must decide at the first step; trial {trial}.");
            Assert.AreEqual(1, leader.Outcome.Steps, $"The uncontended believed leader must decide in one step; trial {trial}.");
            Assert.AreEqual(Value(0), leader.Outcome.Value, $"The uncontended leader must decide its own value; trial {trial}.");
            Assert.AreEqual(0, leader.PriorityDraws, $"A proposer that believes it leads draws no phase-zero priority; trial {trial}.");
            Assert.IsLessThan(180L, leader.DecisionTime, $"The uncontended leader must decide within its majority-radius round trip; trial {trial} took {leader.DecisionTime}ms.");
        }

        //The clustered twin of the same control is the arm's own matrix-parameter pin: at the clustered
        //placement the majority is in-region and the third reply is home by 88ms under every jitter
        //pattern, while a pump that silently fell back to the spread matrix cannot answer before 120ms.
        for(int trial = 0; trial < TrialsPerConfiguration; trial++)
        {
            WanWriterResult leader = RunWanTrial(ClusteredDelays, 1, WanLeadership.WriterZeroLeads, [0L], WanTrialSeed(2, trial), WanEventBudget, withholdDelivery: false)[0];
            Assert.AreEqual(1, leader.Outcome.Steps, $"The clustered uncontended leader must decide in one step; trial {trial}.");
            Assert.IsLessThan(90L, leader.DecisionTime, $"The clustered leader's decision must stay inside its region; trial {trial} took {leader.DecisionTime}ms.");
        }

        //The ordinary path: a writer under a configured leader that never writes decides its own value at
        //round one phase two, three steps, drawing one ordinary priority per recorder at phase zero and
        //none afterwards.
        for(int trial = 0; trial < TrialsPerConfiguration; trial++)
        {
            WanWriterResult writer = RunWanTrial(SpreadDelays, 1, WanLeadership.AbsentLeader, [0L], WanTrialSeed(3, trial), WanEventBudget, withholdDelivery: false)[0];
            Assert.AreEqual(RoundOnePhaseTwo, writer.Outcome.DecidedAt, $"An uncontended non-leader must decide at round one phase two; trial {trial}.");
            Assert.AreEqual(3, writer.Outcome.Steps, $"An uncontended non-leader must decide in three steps; trial {trial}.");
            Assert.AreEqual(Value(0), writer.Outcome.Value, $"An uncontended non-leader must decide its own value; trial {trial}.");
            Assert.AreEqual(SiteCount, writer.PriorityDraws, $"A non-claimant draws one ordinary priority per recorder at phase zero; trial {trial}.");
        }

        //Under simultaneous activation NEITHER protocol's fast path survives: the fast decision reads each
        //recorder's FIRST proposal at the round's first step, and a contender's ordinary proposal that
        //arrives first at its co-located recorder occupies that slot for good, because the reserved
        //priority wins the aggregate and never the first slot. What the leader buys instead is the
        //coordinated fallback: every writer adopts the reserved template and decides the leader's value at
        //round one phase two, three steps, with no recovery collisions, in every trial. The stagger ladder
        //is then the same mechanism the Fast CASPaxos half sweeps, and one unit of stagger puts the
        //reserved claim first everywhere, restoring the one-step commit for every writer at once. The rows
        //share their jitter patterns, so the monotone assertion below is exact rather than statistical.
        double[] staggerFractions = [0.0, 0.25, 0.5, 1.0];
        var byStagger = new List<(double Fraction, double LeaderFastRate)>();
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"led contention, {WriterCount} writers, stagger ladder writer x delay over {staggerFractions.Length} settings");
        TestContext.WriteLine($"columns: stagger/RTT, staggerMs, leaderFastRate, writerFastRate, meanSteps, meanDecisionMs (jitter-inclusive; {TrialsPerConfiguration} trials per row)");
        foreach(double fraction in staggerFractions)
        {
            int stagger = (int)(fraction * ladderUnit);
            int leaderFast = 0;
            int fastObservations = 0;
            long stepTotal = 0;
            long decisionTotal = 0;
            for(int trial = 0; trial < TrialsPerConfiguration; trial++)
            {
                WanWriterResult[] writers = RunWanTrial(SpreadDelays, WriterCount, WanLeadership.WriterZeroLeads, [0L, stagger, 2L * stagger], WanTrialSeed(10, trial), WanEventBudget, withholdDelivery: false);
                for(int w = 0; w < writers.Length; w++)
                {
                    WanWriterResult writer = writers[w];
                    Assert.IsTrue(writer.Outcome.IsDecided, $"Every led writer's attempt must decide; stagger {stagger}, writer {w}, trial {trial}.");
                    stepTotal += writer.Outcome.Steps;
                    decisionTotal += writer.DecisionTime;
                    if(writer.Outcome.DecidedAt == RecorderStep.RoundOnePhaseZero)
                    {
                        fastObservations++;
                    }

                    if(stagger == 0)
                    {
                        Assert.AreEqual(RoundOnePhaseTwo, writer.Outcome.DecidedAt, $"Simultaneous led writers all decide at round one phase two; writer {w}, trial {trial}.");
                        Assert.AreEqual(3, writer.Outcome.Steps, $"Simultaneous led contention costs exactly three steps; writer {w}, trial {trial}.");
                        Assert.AreEqual(Value(0), writer.Outcome.Value, $"Simultaneous led contention decides the leader's value; writer {w}, trial {trial}.");
                        Assert.AreEqual(WanLanes[0], writer.Outcome.DecidedBy, $"Simultaneous led contention decides the leader's proposal; writer {w}, trial {trial}.");
                        Assert.AreEqual(w == 0 ? 0 : SiteCount, writer.PriorityDraws, $"Only non-claimants draw, once per recorder at phase zero; writer {w}, trial {trial}.");
                    }

                    if(fraction == 1.0)
                    {
                        Assert.AreEqual(RecorderStep.RoundOnePhaseZero, writer.Outcome.DecidedAt, $"A full stagger unit restores the one-step commit for every writer; writer {w}, trial {trial}.");
                        Assert.AreEqual(1, writer.Outcome.Steps, $"A full stagger unit restores the one-step commit; writer {w}, trial {trial}.");
                        Assert.AreEqual(Value(0), writer.Outcome.Value, $"The restored fast path decides the leader's value; writer {w}, trial {trial}.");
                        Assert.AreEqual(WanLanes[0], writer.Outcome.DecidedBy, $"The restored fast path decides the leader's proposal; writer {w}, trial {trial}.");

                        //The restored one-step commit must also cost one-step time measured from the
                        //writer's OWN activation: the third-nearest reply is home by 178ms at sites 0 and 1
                        //and by 238ms at site 2 under every jitter pattern, so 240ms bounds every writer
                        //the way 178 against 180 bounds the uncontended control.
                        Assert.IsLessThan(240L, writer.DecisionTime,
                            $"The restored fast path must cost one-step time from the writer's own activation; writer {w}, trial {trial} took {writer.DecisionTime}ms.");
                    }
                }

                if(writers[0].Outcome.DecidedAt == RecorderStep.RoundOnePhaseZero)
                {
                    leaderFast++;
                }
            }

            double leaderRate = (double)leaderFast / TrialsPerConfiguration;
            double writes = (double)TrialsPerConfiguration * WriterCount;
            byStagger.Add((fraction, leaderRate));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fraction:F2}, {stagger}, {leaderRate:F3}, {fastObservations / writes:F3}, {stepTotal / writes:F2}, {decisionTotal / writes:F1}"));
        }

        Assert.AreEqual(0.0, byStagger[0].LeaderFastRate,
            "Simultaneous contention kills the leader's fast path in every trial; a nonzero rate here means the fast gate widened.");

        Assert.AreEqual(1.0, byStagger[^1].LeaderFastRate,
            "A full stagger unit restores the leader's fast path in every trial; a lower rate here is the demotion the campaign rule guards.");

        for(int i = 1; i < byStagger.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(byStagger[i - 1].LeaderFastRate, byStagger[i].LeaderFastRate,
                $"The leader's fast rate fell when the stagger grew from {byStagger[i - 1].Fraction:F2} to {byStagger[i].Fraction:F2} units.");
        }

        //Five writers are the count at which the Fast CASPaxos fast path collapses, 0.005 by trial even
        //with arrivals spread over 90ms and a fortiori when simultaneous; here the simultaneous round
        //stays bounded and agreed, and the writer whose quorum misses the leader's nearest recorder pays
        //extra steps, which is the reach pin that keeps the agreement net non-vacuous.
        int trialsBeyondThreeSteps = 0;
        long fiveWriterStepTotal = 0;
        for(int trial = 0; trial < TrialsPerConfiguration; trial++)
        {
            WanWriterResult[] writers = RunWanTrial(SpreadDelays, 5, WanLeadership.WriterZeroLeads, [0L, 0L, 0L, 0L, 0L], WanTrialSeed(20, trial), WanEventBudget, withholdDelivery: false);
            bool beyondThree = false;
            for(int w = 0; w < writers.Length; w++)
            {
                Assert.IsTrue(writers[w].Outcome.IsDecided, $"Every writer's attempt must decide; writer {w}, trial {trial}.");
                Assert.AreEqual(writers[0].Outcome.Value, writers[w].Outcome.Value, $"One instance decides one value; writer {w}, trial {trial}.");
                Assert.AreEqual(writers[0].Outcome.DecidedBy, writers[w].Outcome.DecidedBy, $"One instance decides one owner; writer {w}, trial {trial}.");
                fiveWriterStepTotal += writers[w].Outcome.Steps;
                beyondThree |= writers[w].Outcome.Steps > 3;
            }

            if(beyondThree)
            {
                trialsBeyondThreeSteps++;
            }
        }

        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"led, five simultaneous writers: meanSteps={fiveWriterStepTotal / (TrialsPerConfiguration * 5.0):F2}, trialsWithAWriterBeyondThreeSteps={trialsBeyondThreeSteps}"));

        Assert.IsGreaterThan(0, trialsBeyondThreeSteps,
            "Five writers were expected to reach a schedule where somebody pays more than three steps; without one the agreement net above pins nothing.");

        //Leaderless writers cooperate through redrawn priorities rather than a reserved template, so the
        //round still agrees but pays for what leadership was buying. The relational gate against the led
        //mean of exactly three is what makes 'leadership buys steps' a measured claim.
        int leaderlessBeyondThree = 0;
        long leaderlessStepTotal = 0;
        for(int trial = 0; trial < TrialsPerConfiguration; trial++)
        {
            WanWriterResult[] writers = RunWanTrial(SpreadDelays, WriterCount, WanLeadership.Leaderless, [0L, 0L, 0L], WanTrialSeed(21, trial), WanEventBudget, withholdDelivery: false);
            for(int w = 0; w < writers.Length; w++)
            {
                Assert.IsTrue(writers[w].Outcome.IsDecided, $"Every leaderless attempt must decide; writer {w}, trial {trial}.");
                Assert.AreEqual(writers[0].Outcome.Value, writers[w].Outcome.Value, $"One leaderless instance decides one value; writer {w}, trial {trial}.");
                Assert.AreEqual(writers[0].Outcome.DecidedBy, writers[w].Outcome.DecidedBy, $"One leaderless instance decides one owner; writer {w}, trial {trial}.");
                leaderlessStepTotal += writers[w].Outcome.Steps;
                if(writers[w].Outcome.Steps > 3)
                {
                    leaderlessBeyondThree++;
                }
            }
        }

        double leaderlessMeanSteps = leaderlessStepTotal / ((double)TrialsPerConfiguration * WriterCount);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"leaderless, three simultaneous writers: meanSteps={leaderlessMeanSteps:F2}, writesBeyondThreeSteps={leaderlessBeyondThree}"));

        Assert.IsGreaterThan(0, leaderlessBeyondThree,
            "Leaderless contention was expected to reach a schedule where somebody pays more than three steps; without one the mean below is vacuous.");

        //The bound sits halfway between the led constant of three and the worst leaderless mean a
        //twenty-base development sweep produced, 4.007, so a legal jitter pattern has ample room. A
        //harness that wholesale re-acquired a leader dies at the reach pin above with zero writes beyond
        //three steps; this gate holds the margin against a partial drift the reach pin cannot price.
        Assert.IsGreaterThan(3.5, leaderlessMeanSteps,
            $"Leaderless contention was expected to cost materially more than the led constant of three steps per write; the mean was {leaderlessMeanSteps:F2}.");

        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"cross-protocol cost at spread site 0, each step one round trip at its quorum's radius: QuePaxa led contended 3 x {ladderUnit}ms and restored fast path 1 x {ladderUnit}ms, against the Fast CASPaxos assumption fallen back of 1 x {FastQuorumRoundTrip(SpreadDelays, 0)}ms wasted fast plus 2 x {ClassicQuorumRoundTrip(SpreadDelays, 0)}ms classic, and 1 x {FastQuorumRoundTrip(SpreadDelays, 0)}ms fast");
    }


    /// <summary>
    /// Runs one configuration over independent seeded trials and reports the aggregate.
    /// </summary>
    /// <remarks>
    /// The delay matrix is a parameter so a clustered row measures clustered contention rather than
    /// inheriting the spread matrix.
    /// </remarks>
    private static Measurement Measure(int[][] delays, int writerCount, int arrivalSpread, int hedgeDelay, int jitter, int seed)
    {
        uint randomState = seed == 0 ? 2463534242u : (uint)seed;
        int trialsWithFastCommit = 0;
        int fastCommits = 0;
        double roundTripTotal = 0;
        double addedWaitTotal = 0;
        for(int trial = 0; trial < TrialsPerConfiguration; trial++)
        {
            //Every writer's activation is its own arrival offset plus the stagger its schedule position
            //imposes. The offsets are drawn before the stagger is added, so a hedged and an unhedged run at
            //the same seed see exactly the same arrival pattern.
            var activation = new int[writerCount];
            for(int writer = 0; writer < writerCount; writer++)
            {
                int offset = arrivalSpread == 0 ? 0 : (int)NextBelow(ref randomState, (uint)arrivalSpread);
                activation[writer] = offset + (writer * hedgeDelay);
                addedWaitTotal += writer * hedgeDelay;
            }

            var arrivals = new List<(int Time, int Writer, int Acceptor)>(writerCount * SiteCount);
            for(int writer = 0; writer < writerCount; writer++)
            {
                for(int acceptor = 0; acceptor < SiteCount; acceptor++)
                {
                    int delay = jitter == 0 ? 0 : (int)NextBelow(ref randomState, (uint)jitter);

                    arrivals.Add((activation[writer] + delays[writer][acceptor] + delay, writer, acceptor));
                }
            }

            //A simultaneous arrival is broken by writer then acceptor index so a trial replays identically.
            arrivals.Sort(static (left, right) =>
            {
                int byTime = left.Time.CompareTo(right.Time);
                if(byTime != 0)
                {
                    return byTime;
                }

                int byWriter = left.Writer.CompareTo(right.Writer);

                return byWriter != 0 ? byWriter : left.Acceptor.CompareTo(right.Acceptor);
            });

            FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(SiteCount);
            var accepts = new int[writerCount];
            foreach((int _, int writer, int acceptor) in arrivals)
            {
                ImmutableHashSet<int> target = [acceptor];
                (register, int accepted) = register.ProposeFastReaching(FastBallot.Fast(1), Value(writer), target);
                accepts[writer] += accepted;
            }

            bool anyCommitted = false;
            for(int writer = 0; writer < writerCount; writer++)
            {
                bool committed = register.IsFastQuorum(accepts[writer]);
                anyCommitted |= committed;
                if(committed)
                {
                    fastCommits++;
                }

                roundTripTotal += committed ? FastCommitRoundTrips : FallbackRoundTrips;
            }

            if(anyCommitted)
            {
                trialsWithFastCommit++;
            }
        }

        double writes = (double)TrialsPerConfiguration * writerCount;

        return new Measurement(
            (double)trialsWithFastCommit / TrialsPerConfiguration,
            fastCommits / writes,
            roundTripTotal / writes,
            addedWaitTotal / writes);
    }


    /// <summary>
    /// Both quorum sizes are read from the production register so the probe cannot drift from the shipped rules.
    /// </summary>
    private static int FastQuorum { get; } = FastCasPaxosRegister<string>.WithAcceptors(SiteCount).FastQuorum;

    private static int ClassicQuorum { get; } = FastCasPaxosRegister<string>.WithAcceptors(SiteCount).ClassicQuorum;


    /// <summary>
    /// The round trip a writer at this site needs for a fast commit: twice the delay to the nearest acceptor
    /// that completes its fast quorum.
    /// </summary>
    private static int FastQuorumRoundTrip(int[][] delays, int site)
    {
        int[] sorted = [.. delays[site].Order()];

        return 2 * sorted[FastQuorum - 1];
    }


    /// <summary>
    /// The same measure for the classic quorum, which is a simple majority and therefore a nearer acceptor.
    /// </summary>
    private static int ClassicQuorumRoundTrip(int[][] delays, int site)
    {
        int[] sorted = [.. delays[site].Order()];

        return 2 * sorted[ClassicQuorum - 1];
    }


    /// <summary>
    /// The QuePaxa quorum is read from the production register so the columns cannot drift from the shipped
    /// rules.
    /// </summary>
    private static int QuePaxaQuorum { get; } = QuePaxaRegister<string>.WithRecorders(SiteCount).Quorum;


    /// <summary>
    /// The round trip the believed leader needs for its one-step commit: twice the delay to the nearest
    /// recorder that completes its majority quorum.
    /// </summary>
    private static int QuePaxaLeaderRoundTrip(int[][] delays, int site)
    {
        int[] sorted = [.. delays[site].Order()];

        return 2 * sorted[QuePaxaQuorum - 1];
    }


    private static string Value(int writer) => $"w{writer}";


    private static uint NextBelow(ref uint state, uint exclusiveUpperBound)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;

        return state % exclusiveUpperBound;
    }


    private const long WanEventBudget = 200_000;

    private static RecorderStep RoundOnePhaseTwo { get; } = RecorderStep.FromRoundAndPhase(1, 2);

    /// <summary>
    /// Identities from fixed bytes: no assertion above may depend on which way generated identities sort.
    /// </summary>
    /// <remarks>
    /// The lane beyond the site count leads the absent-leader configurations and never writes.
    /// </remarks>
    private static ImmutableArray<ProposerLane> WanLanes { get; } = [.. Enumerable.Range(1, SiteCount + 1).Select(index => ProposerLane.For(WanReplica((byte)index)))];


    private static ReplicaId WanReplica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// A splitmix-style finalizer: it statelessly maps distinct inputs to well-spread values, so a derived
    /// seed or a jitter draw depends on what it is for and never on the order the harness happened to ask in.
    /// </summary>
    private static ulong WanMix(ulong value)
    {
        ulong mixed = value + 0x9E3779B97F4A7C15UL;
        mixed = (mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL;
        mixed = (mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL;

        return mixed ^ (mixed >> 31);
    }


    private static ulong WanTrialSeed(int configuration, int trial) => WanMix(((ulong)(uint)configuration << 32) | (uint)trial);


    private static WanWriterResult[] RunWanTrial(
        int[][] delays,
        int writerCount,
        WanLeadership leadership,
        long[] activations,
        ulong trialSeed,
        long eventBudget,
        bool withholdDelivery)
    {
        var pump = new WanQuePaxaPump(delays, writerCount, leadership, activations, trialSeed, eventBudget, withholdDelivery);

        return pump.Run();
    }


    private enum WanLeadership
    {
        /// <summary>
        /// Writer zero's lane is the configured leader of every recorder and believes it leads.
        /// </summary>
        WriterZeroLeads,

        /// <summary>
        /// Every recorder is led by a lane that never writes, so every writer runs the ordinary path.
        /// </summary>
        AbsentLeader,

        /// <summary>
        /// The recorders are leaderless and no writer claims leadership.
        /// </summary>
        Leaderless
    }


    /// <summary>
    /// One writer's result from a WAN trial: the shipped outcome, the virtual milliseconds from the writer's
    /// activation to its decision, and how many phase-zero priorities its source supplied.
    /// </summary>
    private sealed record WanWriterResult(QuePaxaOutcome<string> Outcome, long DecisionTime, int PriorityDraws);


    private sealed record WanEvent(
        WanEventKind Kind,
        int Writer,
        int Recorder,
        RecordRequest<string>? Request,
        TaskCompletionSource<RecordReply<string>>? Completion,
        RecordReply<string>? Reply);


    private enum WanEventKind
    {
        /// <summary>
        /// Calls the writer's one-shot proposal at its activation instant, so a stagger is a property of the
        /// schedule rather than of the endpoints.
        /// </summary>
        WriterStart,

        /// <summary>
        /// A request reaches its site's node and folds there, in true per-recorder arrival order.
        /// </summary>
        RequestArrival,

        /// <summary>
        /// A reply reaches its caller and completes that call's own task, which resumes the proposer inline.
        /// </summary>
        ReplyArrival
    }


    /// <summary>
    /// A single-threaded discrete-event transport for the QuePaxa half.
    /// </summary>
    /// <remarks>
    /// Every endpoint call enqueues an arrival, every arrival folds at its site's node and enqueues the reply,
    /// and every reply completes its own call's task, which resumes the proposer inline on the pump thread;
    /// the owner-thread check turns any escape to the pool into a red rather than into nondeterminism.
    /// Correlation is per call and never per recorder, which is what the endpoint delegate's contract demands.
    /// The transport is lossless, so the proposer's attempt budget and quorum-unreachable exit are
    /// deliberately never load-bearing here.
    /// </remarks>
    private sealed class WanQuePaxaPump
    {
        public WanQuePaxaPump(
            int[][] delays,
            int writerCount,
            WanLeadership leadership,
            long[] activations,
            ulong trialSeed,
            long eventBudget,
            bool withholdDelivery)
        {
            Delays = delays;
            TrialSeed = trialSeed;
            EventBudget = eventBudget;
            WithholdDelivery = withholdDelivery;
            OwnerThread = Environment.CurrentManagedThreadId;
            Activations = activations;
            ProposerLane? leader = leadership switch
            {
                WanLeadership.WriterZeroLeads => WanLanes[0],
                WanLeadership.AbsentLeader => WanLanes[SiteCount],
                _ => null
            };
            BelievedLeader = leader;
            Nodes = new QuePaxaNode<string>[SiteCount];
            for(int site = 0; site < SiteCount; site++)
            {
                Nodes[site] = new QuePaxaNode<string>(leader is null
                    ? QuePaxaRecorder<string>.Leaderless
                    : QuePaxaRecorder<string>.LedBy(leader.Value));
            }

            Sources = new WanPrioritySource[writerCount];
            Proposers = new QuePaxaProposer<string>[writerCount];
            Tasks = new Task<QuePaxaOutcome<string>>?[writerCount];
            DecisionTimes = new long[writerCount];
            Completed = new bool[writerCount];
            for(int writer = 0; writer < writerCount; writer++)
            {
                Sources[writer] = new WanPrioritySource(WanMix(trialSeed ^ ((ulong)(writer + 1) * 0xD1B54A32D192ED03UL)));
                var endpoints = new RecorderEndpointDelegate<string>[SiteCount];
                int capturedWriter = writer;
                for(int recorder = 0; recorder < SiteCount; recorder++)
                {
                    int capturedRecorder = recorder;
                    endpoints[recorder] = (request, _) => Send(capturedWriter, capturedRecorder, request);
                }

                Proposers[writer] = new QuePaxaProposer<string>(endpoints, WanLanes[writer], Sources[writer].Next, attemptsPerRecorder: 1);
                Enqueue(new WanEvent(WanEventKind.WriterStart, writer, 0, null, null, null), activations[writer]);
            }
        }


        private int[][] Delays { get; }

        private ulong TrialSeed { get; }

        private long EventBudget { get; }

        private bool WithholdDelivery { get; }

        private int OwnerThread { get; }

        private long[] Activations { get; }

        private ProposerLane? BelievedLeader { get; }

        private QuePaxaNode<string>[] Nodes { get; }

        private WanPrioritySource[] Sources { get; }

        private QuePaxaProposer<string>[] Proposers { get; }

        private Task<QuePaxaOutcome<string>>?[] Tasks { get; }

        private long[] DecisionTimes { get; }

        private bool[] Completed { get; }

        private PriorityQueue<WanEvent, (long Time, long Sequence)> Queue { get; } = new();

        private long Now { get; set; }

        private long Sequence { get; set; }


        public WanWriterResult[] Run()
        {
            long dispatched = 0;
            while(Queue.TryDequeue(out WanEvent? item, out (long Time, long Sequence) key))
            {
                Now = key.Time;
                dispatched++;
                if(dispatched > EventBudget)
                {
                    throw new InvalidOperationException($"The WAN pump exceeded its event budget of {EventBudget} at seed {TrialSeed}; a run that cannot drain is a defect rather than a slow trial.");
                }

                Dispatch(item);
                for(int writer = 0; writer < Tasks.Length; writer++)
                {
                    if(!Completed[writer] && Tasks[writer] is { IsCompleted: true })
                    {
                        Completed[writer] = true;
                        DecisionTimes[writer] = Now - Activations[writer];
                    }
                }
            }

            var results = new WanWriterResult[Tasks.Length];
            for(int writer = 0; writer < Tasks.Length; writer++)
            {
                if(Tasks[writer] is not { IsCompleted: true } task)
                {
                    throw new InvalidOperationException($"Writer {writer} is parked after the queue drained at seed {TrialSeed}; an empty queue with an incomplete writer is a lost message, not quiescence.");
                }

                results[writer] = new WanWriterResult(task.GetAwaiter().GetResult(), DecisionTimes[writer], Sources[writer].DrawCount);
            }

            return results;
        }


        private ValueTask<RecordReply<string>> Send(int writer, int recorder, RecordRequest<string> request)
        {
            AssertOwnerThread();
            var completion = new TaskCompletionSource<RecordReply<string>>();
            if(!WithholdDelivery)
            {
                Enqueue(new WanEvent(WanEventKind.RequestArrival, writer, recorder, request, completion, null),
                    Now + Delays[writer][recorder] + Jitter(writer, recorder, request.Step.Value, leg: 0));
            }

            return new ValueTask<RecordReply<string>>(completion.Task);
        }


        private void Dispatch(WanEvent item)
        {
            AssertOwnerThread();
            switch(item.Kind)
            {
                case WanEventKind.WriterStart:
                    Tasks[item.Writer] = Proposers[item.Writer].ProposeAsync(BelievedLeader, Value(item.Writer), CancellationToken.None);
                    break;
                case WanEventKind.RequestArrival:
                    RecordReply<string> reply = Nodes[item.Recorder].Handle(item.Request!);
                    Enqueue(item with { Kind = WanEventKind.ReplyArrival, Reply = reply },
                        Now + Delays[item.Recorder][item.Writer] + Jitter(item.Writer, item.Recorder, item.Request!.Step.Value, leg: 1));
                    break;
                default:
                    item.Completion!.SetResult(item.Reply!);
                    break;
            }
        }


        private void Enqueue(WanEvent item, long time)
        {
            AssertOwnerThread();
            Queue.Enqueue(item, (time, Sequence++));
        }


        /// <summary>
        /// Jitter is stateless per message leg: a draw depends on the trial seed and on which leg it jitters,
        /// never on the order the pump happened to ask in, so a harness edit cannot silently re-roll every
        /// measured number and the stagger ladder's rows share their jitter patterns exactly.
        /// </summary>
        private int Jitter(int writer, int recorder, int step, int leg)
        {
            ulong key = TrialSeed
                ^ ((ulong)(uint)writer << 40)
                ^ ((ulong)(uint)recorder << 32)
                ^ ((ulong)(uint)step << 8)
                ^ (uint)leg;

            return (int)(WanMix(key) % (uint)DefaultJitter);
        }


        private void AssertOwnerThread()
        {
            if(Environment.CurrentManagedThreadId != OwnerThread)
            {
                throw new InvalidOperationException($"The pump is single-threaded and a continuation escaped it: owner thread {OwnerThread}, current thread {Environment.CurrentManagedThreadId}.");
            }
        }
    }


    /// <summary>
    /// Xorshift64 rather than the cryptographic source: every priority is reproducible from its seed, so a
    /// failing configuration replays the identical draws.
    /// </summary>
    /// <remarks>
    /// Each writer owns a stream, because the believed leader draws nothing at its first step and a shared
    /// stream would couple the writers through dispatch order.
    /// </remarks>
    private sealed class WanPrioritySource
    {
        private ulong state;

        public WanPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        public int DrawCount { get; private set; }


        public ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            DrawCount++;

            //The two excluded endpoints, None and Reserved, are mapped away so the source honours the
            //delegate's ordinary-priority contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }


    /// <summary>
    /// The trial rate counts trials in which ANY writer reached its fast quorum, which is the round-survival
    /// reading; the writer rate counts the writes that committed fast, which is what an individual writer
    /// experiences.
    /// </summary>
    /// <remarks>
    /// The two must never share a name, because they diverge by a factor of the writer count exactly where
    /// hedging works best.
    /// </remarks>
    private sealed record Measurement(double TrialFastCommitRate, double WriterFastCommitRate, double MeanRoundTripsPerWrite, double MeanAddedWaitMs);
}
