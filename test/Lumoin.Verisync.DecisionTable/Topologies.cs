using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The placements the grid is keyed on, plus the two illustrative matrices the reproduction gate needs.
/// </summary>
/// <remarks>
/// <para>
/// Every figure here is either a confirmed median taken from the sibling simulation's sourced latency
/// profiles or a stated modelling choice, and each placement carries which of the two it is. The sourced
/// profiles are cross-region Azure P50 medians; the intra-region, availability-zone and co-located figures
/// are modelling choices at the right order of magnitude and nothing here claims otherwise.
/// </para>
/// <para>
/// A placement for a replica count assigns replicas to regions and then reads every pair's delay off the
/// region matrix, so the same rule produces a placement at three, four, five and seven replicas and the grid
/// cannot drift between counts.
/// </para>
/// </remarks>
internal static class Topologies
{
    /// <summary>The one-way delay between two replicas in one region, which no pair ever takes as zero.</summary>
    public const long IntraRegionOneWay = 500;

    /// <summary>The one-way delay between two availability zones of one region.</summary>
    /// <remarks>
    /// A modelling choice at two milliseconds round trip, in the middle of the adjudicated one-to-three
    /// millisecond band for a multi-availability-zone deployment.
    /// </remarks>
    public const long AvailabilityZoneOneWay = 1000;

    /// <summary>The one-way delay of the co-located sensitivity row, at three times the co-located figure.</summary>
    public const long CoLocatedSensitivityOneWay = 1500;

    /// <summary>How many availability zones the multi-availability-zone placement spreads replicas over.</summary>
    private const int AvailabilityZoneCount = 3;


    /// <summary>
    /// One region, every pair at the intra-region delay. Absolute milliseconds here are model figures: a real
    /// deployment's processing cost is comparable with the network cost at this scale, and the decision signal
    /// is the differential in round structure rather than the absolute latency.
    /// </summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placement.</returns>
    public static Topology CoLocated(int replicaCount) => Uniform(
        "co-located",
        "Modelling choice at intra-region scale, bounded above by the sibling simulation's LAN default of a one-millisecond base delay plus one millisecond of jitter. Not a measurement of any deployment.",
        replicaCount,
        IntraRegionOneWay);


    /// <summary>The co-located sensitivity row, which shows whether the tier's conclusion turns on the exact figure.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placement.</returns>
    public static Topology CoLocatedSensitivity(int replicaCount) => Uniform(
        "co-located-sensitivity",
        "The co-located tier at three times its modelled one-way delay, carried so the tier's conclusion can be shown not to turn on the exact figure.",
        replicaCount,
        CoLocatedSensitivityOneWay);


    /// <summary>
    /// One region over three availability zones, replicas assigned round robin.
    /// </summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placement.</returns>
    /// <remarks>
    /// This tier exists because the adjudication split "regional" in two: a deployment reaches multiple
    /// availability zones before it reaches multiple regions, and a table whose regional row silently meant
    /// continental would misdecide the nearer deployment.
    /// </remarks>
    public static Topology MultiAvailabilityZone(int replicaCount)
    {
        ImmutableArray<string> regions = [.. Enumerable.Range(0, AvailabilityZoneCount).Select(zone => string.Create(CultureInfo.InvariantCulture, $"AZ{zone + 1}"))];
        long[][] regionDelays = new long[AvailabilityZoneCount][];
        for(int from = 0; from < AvailabilityZoneCount; from++)
        {
            regionDelays[from] = new long[AvailabilityZoneCount];
            for(int to = 0; to < AvailabilityZoneCount; to++)
            {
                regionDelays[from][to] = from == to ? IntraRegionOneWay : AvailabilityZoneOneWay;
            }
        }

        return Place(
            "multi-az",
            "Modelling choice: one region over three availability zones at a two-millisecond cross-zone round trip, inside the adjudicated one-to-three millisecond band, with same-zone pairs at the co-located figure.",
            replicaCount,
            regions,
            regionDelays,
            RoundRobin(replicaCount, AvailabilityZoneCount));
    }


    /// <summary>Three United States regions on the sibling simulation's sourced continental profile.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placement.</returns>
    public static Topology MultiRegion(int replicaCount) => Place(
        "multi-region",
        "The sibling simulation's three-region United States profile, whose figures its own comment records as confirmed Azure P50 median round trips of 66, 53 and 20 milliseconds; halved here to one-way delays. Intra-region pairs take the modelled intra-region figure.",
        replicaCount,
        UnitedStatesRegions,
        UnitedStatesOneWay(),
        RoundRobin(replicaCount, UnitedStatesRegions.Length));


    /// <summary>Five regions on the sibling simulation's sourced global profile.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placement.</returns>
    /// <remarks>
    /// Replicas take the regions in the profile's own order and wrap once the count exceeds five, so a
    /// seven-replica placement doubles up in the first two regions rather than inventing new ones.
    /// </remarks>
    public static Topology Global(int replicaCount) => Place(
        "global",
        "The sibling simulation's five-region global profile, whose figures its own comment records as confirmed Azure P50 medians; halved here to one-way delays. Intra-region pairs take the modelled intra-region figure.",
        replicaCount,
        GlobalRegions,
        GlobalOneWay(),
        RoundRobin(replicaCount, GlobalRegions.Length));


    /// <summary>
    /// A co-located majority plus the farthest available remote regions, which is the placement the settled
    /// rules say inverts the ordering between the two protocols.
    /// </summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placement.</returns>
    /// <remarks>
    /// A strict majority sits in the first global region at intra-region distances from itself, and the
    /// remainder go to Southeast Asia, Australia Southeast, Brazil South and France Central in that order.
    /// This is where the fast quorum must leave the region while the majority does not.
    /// </remarks>
    public static Topology ClusteredMajority(int replicaCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicaCount, 1);

        int majority = (replicaCount / 2) + 1;
        int[] remoteOrder = [2, 4, 3, 1];
        if(replicaCount - majority > remoteOrder.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaCount), replicaCount, "The clustered placement has no region left for the replicas outside the majority.");
        }

        var assignment = new int[replicaCount];
        for(int replica = 0; replica < replicaCount; replica++)
        {
            assignment[replica] = replica < majority ? 0 : remoteOrder[replica - majority];
        }

        return Place(
            "clustered-majority",
            "A strict majority in East US 2 at the modelled intra-region delay, the remainder at the farthest global-profile regions in order. The region matrix is the sourced global profile; the placement rule is the campaign's.",
            replicaCount,
            GlobalRegions,
            GlobalOneWay(),
            assignment);
    }


    /// <summary>
    /// The probe's illustrative five-site spread matrix, in microseconds, which the reproduction gate runs on.
    /// </summary>
    /// <returns>The placement.</returns>
    /// <remarks>
    /// It is kept for reproduction only. Reproducing a published row needs the matrix that produced it, and
    /// its own comment in the probe records the figures as illustrative at realistic magnitudes rather than as
    /// a measurement of any deployment.
    /// </remarks>
    public static Topology ProbeSpread() => FromMilliseconds(
        "probe-spread",
        "The probe's illustrative inter-region matrix (Ireland, N. Virginia, Sao Paulo, Mumbai, Tokyo), carried unchanged so the published rows can be reproduced. Illustrative at realistic magnitudes, not a measurement.",
        ["Ireland", "N.Virginia", "SaoPaulo", "Mumbai", "Tokyo"],
        [
            [0, 38, 90, 60, 110],
            [38, 0, 60, 95, 75],
            [90, 60, 0, 150, 130],
            [60, 95, 150, 0, 65],
            [110, 75, 130, 65, 0]
        ]);


    /// <summary>The probe's illustrative clustered matrix, in microseconds.</summary>
    /// <returns>The placement.</returns>
    public static Topology ProbeClustered() => FromMilliseconds(
        "probe-clustered",
        "The probe's illustrative clustered matrix: three European sites a few milliseconds apart plus Tokyo and Sao Paulo. Metro scale rather than rack scale, carried for reproduction only.",
        ["EU-a", "EU-b", "EU-c", "Tokyo", "SaoPaulo"],
        [
            [0, 15, 5, 110, 90],
            [15, 0, 10, 120, 100],
            [5, 10, 0, 115, 92],
            [110, 120, 115, 0, 130],
            [90, 100, 92, 130, 0]
        ]);


    /// <summary>The campaign's five topology tiers at <paramref name="replicaCount"/> replicas, in grid order.</summary>
    /// <param name="replicaCount">The replica count.</param>
    /// <returns>The placements.</returns>
    public static ImmutableArray<Topology> Grid(int replicaCount) =>
    [
        CoLocated(replicaCount),
        MultiAvailabilityZone(replicaCount),
        MultiRegion(replicaCount),
        Global(replicaCount),
        ClusteredMajority(replicaCount)
    ];


    /// <summary>The replica counts the grid is keyed on.</summary>
    /// <remarks>
    /// Four is in because the two quorums coincide there and the contrast vanishes; seven because the radius
    /// gap is widest there. Neither has ever been run in this repository.
    /// </remarks>
    public static ImmutableArray<int> ReplicaCounts { get; } = [3, 4, 5, 7];


    private static ImmutableArray<string> UnitedStatesRegions { get; } = ["EUS2", "WUS", "WUS3"];

    private static ImmutableArray<string> GlobalRegions { get; } = ["EUS2", "FRC", "SEA", "BRS", "AUSE"];


    /// <summary>Halved from the profile's confirmed median round trips of 66, 53 and 20 milliseconds.</summary>
    /// <returns>The one-way delays in microseconds, in region order.</returns>
    private static long[][] UnitedStatesOneWay() =>
    [
        [IntraRegionOneWay, 33_000, 26_000],
        [33_000, IntraRegionOneWay, 10_000],
        [26_000, 10_000, IntraRegionOneWay]
    ];


    /// <summary>
    /// Halved from the profile's ten confirmed median round trips: 85, 228, 116, 204, 148, 190, 236, 332, 88
    /// and 311 milliseconds.
    /// </summary>
    /// <returns>The one-way delays in microseconds, in region order.</returns>
    private static long[][] GlobalOneWay() =>
    [
        [IntraRegionOneWay, 42_000, 114_000, 58_000, 102_000],
        [42_000, IntraRegionOneWay, 74_000, 95_000, 118_000],
        [114_000, 74_000, IntraRegionOneWay, 166_000, 44_000],
        [58_000, 95_000, 166_000, IntraRegionOneWay, 155_000],
        [102_000, 118_000, 44_000, 155_000, IntraRegionOneWay]
    ];


    private static int[] RoundRobin(int replicaCount, int regionCount)
    {
        var assignment = new int[replicaCount];
        for(int replica = 0; replica < replicaCount; replica++)
        {
            assignment[replica] = replica % regionCount;
        }

        return assignment;
    }


    private static Topology Uniform(string name, string provenance, int replicaCount, long oneWay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicaCount, 1);

        long[][] delays = new long[replicaCount][];
        for(int from = 0; from < replicaCount; from++)
        {
            delays[from] = new long[replicaCount];
            for(int to = 0; to < replicaCount; to++)
            {
                delays[from][to] = oneWay;
            }
        }

        ImmutableArray<string> regions = [.. Enumerable.Repeat("EUS2", replicaCount)];

        return new Topology(name, provenance, regions, delays);
    }


    private static Topology Place(string name, string provenance, int replicaCount, ImmutableArray<string> regions, long[][] regionOneWay, int[] assignment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replicaCount, 1);

        long[][] delays = new long[replicaCount][];
        for(int from = 0; from < replicaCount; from++)
        {
            delays[from] = new long[replicaCount];
            for(int to = 0; to < replicaCount; to++)
            {
                delays[from][to] = regionOneWay[assignment[from]][assignment[to]];
            }
        }

        ImmutableArray<string> siteRegions = [.. assignment.Select(region => regions[region])];

        return new Topology(name, provenance, siteRegions, delays);
    }


    private static Topology FromMilliseconds(string name, string provenance, ImmutableArray<string> siteRegions, int[][] milliseconds)
    {
        long[][] delays = new long[milliseconds.Length][];
        for(int from = 0; from < milliseconds.Length; from++)
        {
            delays[from] = new long[milliseconds[from].Length];
            for(int to = 0; to < milliseconds[from].Length; to++)
            {
                delays[from][to] = milliseconds[from][to] * 1000L;
            }
        }

        return new Topology(name, provenance, siteRegions, delays);
    }
}
