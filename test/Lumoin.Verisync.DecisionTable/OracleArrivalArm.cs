using System.Collections.Immutable;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The oracle arrival model the published Fast CASPaxos rows are denominated in, generalized in the replica
/// count and denominated in microseconds.
/// </summary>
/// <remarks>
/// <para>
/// It carries the published currency and nothing more. Each writer's accept reaches every acceptor after the
/// one-way delay for that site pair plus jitter, arrivals are applied in time order through the shipped
/// <see cref="FastCasPaxosRegister{TValue}"/>, and a writer is counted as having committed fast when its
/// accepts reached a fast quorum ACROSS EVERY ACCEPTOR. No proposer in the model ever waits for or acts on
/// those replies, so the count is an oracle rather than a measurement, and the round-trip column beside it is
/// a two-valued lookup on that oracle rather than a measured latency.
/// </para>
/// <para>
/// THAT IS PRECISELY WHY IT IS HERE. Every published Fast CASPaxos row is in this currency, and the
/// reproduction gate cannot be run against a currency the published rows were never denominated in. The
/// pumped arm is what supersedes it; this arm is what proves the new harness is the same experiment.
/// </para>
/// <para>
/// The draw order and the pseudo-random stream are the probe's, because reproducing a published row means
/// reproducing its draws. Offsets are drawn before the stagger is added, so a hedged and an unhedged run at
/// one seed see exactly the same arrival pattern.
/// </para>
/// </remarks>
internal static class OracleArrivalArm
{
    /// <summary>The assumed cost of a fast commit, in round trips.</summary>
    public const double FastCommitRoundTrips = 1.0;

    /// <summary>The assumed cost of a fallback: the wasted fast round plus the classic prepare and accept.</summary>
    public const double FallbackRoundTrips = 3.0;


    /// <summary>
    /// Runs one configuration over independent seeded trials and reports the aggregate.
    /// </summary>
    /// <param name="topology">The placement, whose site count is the replica count.</param>
    /// <param name="writerCount">How many writers contend in each trial.</param>
    /// <param name="arrivalSpreadMicroseconds">The width of the uniform arrival spread. Zero makes every writer arrive together.</param>
    /// <param name="hedgeDelayMicroseconds">The stagger one schedule position imposes. Zero is the unhedged configuration.</param>
    /// <param name="jitter">The jitter distribution, whose grain is also the grid arrivals are drawn on.</param>
    /// <param name="seed">The configuration seed the whole stream derives from.</param>
    /// <param name="trials">How many trials to run.</param>
    /// <returns>The aggregate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="topology"/> or <paramref name="jitter"/> is <see langword="null"/>.</exception>
    public static OracleMeasurement Measure(
        Topology topology,
        int writerCount,
        long arrivalSpreadMicroseconds,
        long hedgeDelayMicroseconds,
        JitterModel jitter,
        int seed,
        int trials)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(writerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(arrivalSpreadMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(hedgeDelayMicroseconds);

        int replicaCount = topology.SiteCount;
        long grain = jitter.GrainMicroseconds;
        long arrivalSpread = arrivalSpreadMicroseconds / grain;
        long hedgeDelay = hedgeDelayMicroseconds / grain;

        //Xorshift32 rather than the system source: the sequence is deterministic across runtimes, so a seed
        //printed by a row replays the identical arrival pattern anywhere. State must be nonzero.
        uint randomState = seed == 0 ? 2463534242u : (uint)seed;
        int trialsWithFastCommit = 0;
        int fastCommits = 0;
        double roundTripTotal = 0;
        double addedWaitTotal = 0;

        var activation = new long[writerCount];
        var accepts = new int[writerCount];
        var arrivals = new List<(long Time, int Writer, int Acceptor)>(writerCount * replicaCount);

        for(int trial = 0; trial < trials; trial++)
        {
            for(int writer = 0; writer < writerCount; writer++)
            {
                long offset = arrivalSpread == 0 ? 0 : NextBelow(ref randomState, (uint)arrivalSpread);
                activation[writer] = offset + (writer * hedgeDelay);
                addedWaitTotal += writer * hedgeDelay;
            }

            arrivals.Clear();
            for(int writer = 0; writer < writerCount; writer++)
            {
                int site = writer % replicaCount;
                for(int acceptor = 0; acceptor < replicaCount; acceptor++)
                {
                    long oneWay = topology.OneWay(site, acceptor);
                    int span = jitter.SpanUnitsFor(oneWay);
                    long drawn = span == 0 ? 0 : NextBelow(ref randomState, (uint)span);

                    arrivals.Add((activation[writer] + (oneWay / grain) + drawn, writer, acceptor));
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

            FastCasPaxosRegister<string> register = FastCasPaxosRegister<string>.WithAcceptors(replicaCount);
            Array.Clear(accepts);
            foreach((long _, int writer, int acceptor) in arrivals)
            {
                ImmutableHashSet<int> target = [acceptor];
                (register, int accepted) = register.ProposeFastReaching(FastBallot.Fast(1), HarnessIdentity.Value(writer), target);
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

        double writes = (double)trials * writerCount;

        return new OracleMeasurement(
            trials,
            writerCount,
            (double)trialsWithFastCommit / trials,
            fastCommits / writes,
            roundTripTotal / writes,
            addedWaitTotal / writes * grain);
    }


    private static uint NextBelow(ref uint state, uint exclusiveUpperBound)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;

        return state % exclusiveUpperBound;
    }
}
