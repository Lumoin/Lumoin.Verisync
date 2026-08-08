using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Measures how often a Fast CASPaxos fast round splits when several replicas write the same register over a
/// wide-area network, and how much a hedging schedule changes that.
///
/// The question this answers is the escalation trigger for the metadata plane: a fast round that splits costs
/// a classic recovery, so the fraction of contended transitions that fall back is what decides whether the
/// register tier is adequate or whether a protocol whose concurrent proposers cooperate rather than collide
/// is worth its machinery.
///
/// The model is a discrete-event simulation of one contended transition. Each writer sends its accept to
/// every acceptor; a message arrives at its acceptor after the one-way delay for that site pair plus jitter;
/// arrivals are then applied in time order through the production <see cref="FastCasPaxosRegister{TValue}"/>,
/// so the accept rules, the tie-breaking, and the fast-quorum predicate exercised here are the shipped ones
/// and not a restatement. An acceptor keeps the first value it sees for the fast ballot and rejects any
/// other, so a writer commits on the fast path only when it reaches a fast quorum of acceptors first.
///
/// Two things are modelled and one is assumed. Modelled: arrival order under a wide-area latency matrix with
/// jitter, and the staggering a <see cref="HedgingSchedule"/> imposes. Assumed: the cost of falling back,
/// taken as one wasted fast round trip plus two for the classic recovery, against one round trip for a fast
/// commit. The simulation does not model recoveries contending with each other, which can only make the
/// unhedged column worse than reported here.
/// </summary>
[TestClass]
internal sealed class WanFastRoundContentionProbe
{
    public TestContext TestContext { get; set; } = null!;

    //One-way delays in milliseconds between five sites, symmetric with a zero diagonal, at the scale of
    //inter-region wide-area links: Ireland, N. Virginia, Sao Paulo, Mumbai, Tokyo. Illustrative figures at
    //realistic magnitudes, not measurements of any particular deployment.
    private static int[][] SpreadDelays { get; } =
    [
        [0, 38, 90, 60, 110],
        [38, 0, 60, 95, 75],
        [90, 60, 0, 150, 130],
        [60, 95, 150, 0, 65],
        [110, 75, 130, 65, 0]
    ];

    //The same five acceptors placed as a co-located majority plus two remote sites: three European sites a
    //few milliseconds apart, Tokyo and Sao Paulo far from everything. This is how a deployment that wants a
    //cheap majority actually looks, and it is the placement where the fast path's supermajority has to leave
    //the region while the classic quorum does not.
    private static int[][] ClusteredDelays { get; } =
    [
        [0, 15, 5, 110, 90],
        [15, 0, 10, 120, 100],
        [5, 10, 0, 115, 92],
        [110, 120, 115, 0, 130],
        [90, 100, 92, 130, 0]
    ];

    //The three sites forming the co-located majority in the clustered placement.
    private static ImmutableHashSet<int> ClusteredMajority { get; } = [0, 1, 2];

    private const int SiteCount = 5;
    private const int WriterCount = 3;
    private const int TrialsPerConfiguration = 400;
    private const int DefaultJitter = 30;

    //The cost model, in round trips: a fast commit is one, a writer that must recover pays its wasted fast
    //round plus the classic prepare and accept.
    private const double FastCommitRoundTrips = 1.0;
    private const double FallbackRoundTrips = 3.0;


    [TestMethod]
    public void HedgingTheFastRoundCutsContendedFallbacks()
    {
        int leaderRoundTrip = FastQuorumRoundTrip(SpreadDelays, 0);
        TestContext.WriteLine($"acceptors={SiteCount}, writers={WriterCount}, fastQuorum={FastQuorum}, classicQuorum={ClassicQuorum}, trials={TrialsPerConfiguration}, jitter=0..{DefaultJitter}ms");
        TestContext.WriteLine($"fast-quorum round trip by writer site (ms): {string.Join(", ", Enumerable.Range(0, WriterCount).Select(site => FastQuorumRoundTrip(SpreadDelays, site)))}");
        TestContext.WriteLine($"hedging unit = the leading writer's fast-quorum round trip = {leaderRoundTrip}ms");

        Measurement control = Measure(writerCount: 1, arrivalSpread: 0, hedgeDelay: 0, jitter: DefaultJitter, seed: 1);
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"control (single writer): fastCommitRate={control.FastCommitRate:F3}"));

        //A lone writer has no one to split the round with, so a model that ever fails here is not modelling
        //the protocol.
        Assert.AreEqual(1.0, control.FastCommitRate, "A single writer must always reach its fast quorum.");

        double[] hedgeFractions = [0.0, 0.25, 0.5, 1.0, 1.5];
        var byHedge = new List<(double Fraction, Measurement Result)>();
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"simultaneous arrivals, hedge delay sweep over {hedgeFractions.Length} settings");
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"columns: hedgeDelay/RTT, hedgeDelayMs, fastCommitRate, meanRoundTripsPerWrite (fallback costs {FallbackRoundTrips:F0}), meanAddedWaitMs"));
        foreach(double fraction in hedgeFractions)
        {
            int hedgeDelay = (int)(fraction * leaderRoundTrip);
            Measurement result = Measure(WriterCount, arrivalSpread: 0, hedgeDelay, DefaultJitter, seed: 1);
            byHedge.Add((fraction, result));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fraction:F2}, {hedgeDelay}, {result.FastCommitRate:F3}, {result.MeanRoundTripsPerWrite:F2}, {result.MeanAddedWaitMs:F1}"));
        }

        double[] spreadFractions = [0.0, 0.5, 1.0, 2.0];
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"arrival-spread sweep, unhedged against hedging at {leaderRoundTrip}ms");
        TestContext.WriteLine($"columns: spread/RTT, spreadMs, unhedgedFastCommitRate, hedgedFastCommitRate, unhedgedRoundTrips, hedgedRoundTrips ({TrialsPerConfiguration} trials per row)");
        var bySpread = new List<(double Fraction, Measurement Unhedged, Measurement Hedged)>();
        foreach(double fraction in spreadFractions)
        {
            int spread = (int)(fraction * leaderRoundTrip);
            Measurement unhedged = Measure(WriterCount, spread, hedgeDelay: 0, DefaultJitter, seed: 2);
            Measurement hedged = Measure(WriterCount, spread, leaderRoundTrip, DefaultJitter, seed: 2);
            bySpread.Add((fraction, unhedged, hedged));
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{fraction:F2}, {spread}, {unhedged.FastCommitRate:F3}, {hedged.FastCommitRate:F3}, {unhedged.MeanRoundTripsPerWrite:F2}, {hedged.MeanRoundTripsPerWrite:F2}"));
        }

        int[] writerCounts = [2, 3, 5];
        int halfRoundTrip = leaderRoundTrip / 2;
        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"writer-count sweep at an arrival spread of {halfRoundTrip}ms");
        TestContext.WriteLine($"columns: writers, unhedgedFastCommitRate, hedgedFastCommitRate, unhedgedRoundTrips, hedgedRoundTrips ({TrialsPerConfiguration} trials per row)");
        foreach(int writers in writerCounts)
        {
            Measurement unhedged = Measure(writers, halfRoundTrip, hedgeDelay: 0, DefaultJitter, seed: 3);
            Measurement hedged = Measure(writers, halfRoundTrip, leaderRoundTrip, DefaultJitter, seed: 3);
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{writers}, {unhedged.FastCommitRate:F3}, {hedged.FastCommitRate:F3}, {unhedged.MeanRoundTripsPerWrite:F2}, {hedged.MeanRoundTripsPerWrite:F2}"));
        }

        TestContext.WriteLine(string.Empty);
        TestContext.WriteLine($"quorum distance: one round trip at fast-quorum distance against two at classic-quorum distance");
        TestContext.WriteLine($"columns: placement, site, fastCommitMs, classicRoundMs, fastOverClassic (quorums {FastQuorum} and {ClassicQuorum} of {SiteCount})");
        var quorumDistance = new List<(string Placement, int Site, int FastMs, int ClassicMs)>();
        foreach((string placement, int[][] delays) in new[] { ("spread", SpreadDelays), ("clustered", ClusteredDelays) })
        {
            for(int site = 0; site < SiteCount; site++)
            {
                int fastMs = FastQuorumRoundTrip(delays, site);
                int classicMs = 2 * ClassicQuorumRoundTrip(delays, site);
                quorumDistance.Add((placement, site, fastMs, classicMs));
                TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{placement}, {site}, {fastMs}, {classicMs}, {(double)fastMs / classicMs:F2}"));
            }
        }

        //On a spread placement the fast path keeps an edge everywhere, but never the 2x that counting round
        //trips suggests: a fast commit is one round trip to the FOURTH-nearest acceptor while a classic round
        //is two to the THIRD-nearest, so the supermajority eats most of the saving.
        foreach((string _, int site, int fastMs, int classicMs) in quorumDistance.Where(entry => entry.Placement == "spread"))
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
        foreach((string _, int site, int fastMs, int classicMs) in quorumDistance.Where(entry => entry.Placement == "clustered"))
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
        Measurement fullyHedged = byHedge[^1].Result;

        //Simultaneous wide-area writers split the round: this is the cost the escalation question is about,
        //and it has to be present before any remedy is worth measuring.
        Assert.IsLessThan(0.25, unhedgedSimultaneous.FastCommitRate,
            $"Unhedged simultaneous writers were expected to split the fast round; the fast-commit rate was {unhedgedSimultaneous.FastCommitRate:F3}.");

        //A delay at the scale of the round trip hands the round to the leading writer almost every time.
        Assert.IsGreaterThan(0.95, fullyHedged.FastCommitRate,
            $"Hedging by one round trip was expected to restore the fast path; the fast-commit rate was {fullyHedged.FastCommitRate:F3}.");

        //More stagger never means more splits: the effect has to be monotone, or the mechanism is not the
        //one being claimed.
        for(int i = 1; i < byHedge.Count; i++)
        {
            Assert.IsGreaterThanOrEqualTo(byHedge[i - 1].Result.FastCommitRate, byHedge[i].Result.FastCommitRate,
                $"Fast-commit rate fell when the hedging delay grew from {byHedge[i - 1].Fraction:F2} to {byHedge[i].Fraction:F2} round trips.");
        }

        //Writes that arrive naturally spread out contend less on their own, and what hedging has left to buy
        //shrinks with them. That is the honest limit of the mechanism, and the reason the arrival pattern
        //rather than the link length decides whether a protocol whose proposers cooperate is worth its
        //machinery: spreading is not a cure either, since a fast quorum is a supermajority and even writes a
        //couple of round trips apart still split the round some of the time.
        (double Fraction, Measurement Unhedged, Measurement Hedged) simultaneous = bySpread[0];
        (double Fraction, Measurement Unhedged, Measurement Hedged) wideSpread = bySpread[^1];

        Assert.IsGreaterThan(simultaneous.Unhedged.FastCommitRate, wideSpread.Unhedged.FastCommitRate,
            $"Spreading arrivals by {wideSpread.Fraction:F2} round trips was expected to reduce contention without any hedging.");

        Assert.IsLessThan(simultaneous.Hedged.FastCommitRate - simultaneous.Unhedged.FastCommitRate,
            wideSpread.Hedged.FastCommitRate - wideSpread.Unhedged.FastCommitRate,
            "Hedging's remaining gain was expected to shrink as arrivals spread out on their own.");
    }


    //Runs one configuration over independent seeded trials and reports the aggregate.
    private static Measurement Measure(int writerCount, int arrivalSpread, int hedgeDelay, int jitter, int seed)
    {
        uint randomState = seed == 0 ? 2463534242u : (uint)seed;
        int trialsWithFastCommit = 0;
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

                    arrivals.Add((activation[writer] + SpreadDelays[writer][acceptor] + delay, writer, acceptor));
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
            roundTripTotal / writes,
            addedWaitTotal / writes);
    }


    //Both quorum sizes are read from the production register so the probe cannot drift from the shipped rules.
    private static int FastQuorum { get; } = FastCasPaxosRegister<string>.WithAcceptors(SiteCount).FastQuorum;

    private static int ClassicQuorum { get; } = FastCasPaxosRegister<string>.WithAcceptors(SiteCount).ClassicQuorum;


    //The round trip a writer at this site needs for a fast commit: twice the delay to the nearest acceptor
    //that completes its fast quorum.
    private static int FastQuorumRoundTrip(int[][] delays, int site)
    {
        int[] sorted = [.. delays[site].Order()];

        return 2 * sorted[FastQuorum - 1];
    }


    //The same measure for the classic quorum, which is a simple majority and therefore a nearer acceptor.
    private static int ClassicQuorumRoundTrip(int[][] delays, int site)
    {
        int[] sorted = [.. delays[site].Order()];

        return 2 * sorted[ClassicQuorum - 1];
    }


    private static string Value(int writer) => $"w{writer}";


    private static uint NextBelow(ref uint state, uint exclusiveUpperBound)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;

        return state % exclusiveUpperBound;
    }


    private sealed record Measurement(double FastCommitRate, double MeanRoundTripsPerWrite, double MeanAddedWaitMs);
}
