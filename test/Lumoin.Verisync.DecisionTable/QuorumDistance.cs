using System.Collections.Immutable;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The computed half of the campaign: the uncontended cost of a write, derived by quorum-distance arithmetic
/// over a placement rather than by simulation.
/// </summary>
/// <remarks>
/// <para>
/// Every quorum size is read from the shipped register types rather than restated, so the arithmetic cannot
/// drift from the rules the protocols actually enforce at any replica count. That is what makes this table
/// evidence rather than a second implementation of the quorum formulas.
/// </para>
/// <para>
/// A cell derivable this way is never simulated. The uncontended latency column and every structural fact
/// about the two quorums are exact here and would only lose precision under a pump.
/// </para>
/// </remarks>
internal static class QuorumDistance
{
    /// <summary>The Fast CASPaxos fast quorum at <paramref name="replicaCount"/>, read from the shipped register.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The fast quorum size.</returns>
    public static int FastQuorum(int replicaCount) => FastCasPaxosRegister<string>.WithAcceptors(replicaCount).FastQuorum;


    /// <summary>The Fast CASPaxos classic quorum at <paramref name="replicaCount"/>, read from the shipped register.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The classic quorum size.</returns>
    public static int ClassicQuorum(int replicaCount) => FastCasPaxosRegister<string>.WithAcceptors(replicaCount).ClassicQuorum;


    /// <summary>The QuePaxa quorum at <paramref name="replicaCount"/>, read from the shipped register.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The quorum size.</returns>
    public static int QuePaxaQuorum(int replicaCount) => QuePaxaRegister<string>.WithRecorders(replicaCount).Quorum;


    /// <summary>
    /// One round trip from <paramref name="site"/> to the nearest replica that completes a quorum of
    /// <paramref name="quorum"/>.
    /// </summary>
    /// <param name="topology">The placement.</param>
    /// <param name="site">The replica index the write originates at.</param>
    /// <param name="quorum">The quorum size.</param>
    /// <returns>The round trip in microseconds.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="topology"/> is <see langword="null"/>.</exception>
    public static long QuorumRoundTrip(Topology topology, int site, int quorum)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentOutOfRangeException.ThrowIfLessThan(quorum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quorum, topology.SiteCount);

        return 2 * topology.SortedRadii(site)[quorum - 1];
    }


    /// <summary>
    /// The computed cost of an uncontended write from every site of <paramref name="topology"/>.
    /// </summary>
    /// <param name="topology">The placement.</param>
    /// <returns>One entry per site, in site order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="topology"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<ComputedSiteCost> For(Topology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        int replicaCount = topology.SiteCount;
        int fastQuorum = FastQuorum(replicaCount);
        int majority = QuePaxaQuorum(replicaCount);

        ImmutableArray<ComputedSiteCost>.Builder costs = ImmutableArray.CreateBuilder<ComputedSiteCost>(replicaCount);
        for(int site = 0; site < replicaCount; site++)
        {
            long fast = QuorumRoundTrip(topology, site, fastQuorum);
            long shipped = QuorumRoundTrip(topology, site, replicaCount);
            long majorityRoundTrip = QuorumRoundTrip(topology, site, majority);

            costs.Add(new ComputedSiteCost(
                site,
                topology.SiteRegions[site],
                fast,
                shipped,
                majorityRoundTrip,
                2 * majorityRoundTrip,
                majorityRoundTrip,
                3 * majorityRoundTrip));
        }

        return costs.ToImmutable();
    }
}
