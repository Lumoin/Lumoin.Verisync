using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The measurement harness for the per-setup QuePaxa against Fast CASPaxos decision table.
/// </summary>
/// <remarks>
/// <para>
/// The harness is a console tool rather than a test class, and deliberately so. It lives outside the gating
/// suite because the grid it feeds costs minutes where the suite costs seconds, because most of its output is
/// print-only by nature, and because the test application runs with no filter, so a test class here would
/// gate whether or not that was intended. The plateaus that are protocol facts rather than measurements are
/// mirrored back into the gating suite as assertions in their own slice.
/// </para>
/// <para>
/// No benchmark framework and no wall clock. This is discrete-event virtual time; a wall-clock
/// micro-benchmark would measure the machine that ran it rather than the protocols.
/// </para>
/// </remarks>
internal static class Program
{
    private const int TrialsDefault = 400;


    /// <summary>Runs one command and reports whether it passed.</summary>
    /// <param name="args">The command and its arguments.</param>
    /// <returns>Zero when the command passed and one when it did not.</returns>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        //Every figure this harness prints is a measurement, and a measurement whose decimal separator depends
        //on the machine that produced it is not comparable with the one beside it. The culture is pinned once,
        //here, so no formatting site anywhere can leak the host's.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        //Commands are matched case-insensitively by folding to upper case, which is the direction that round
        //trips for every culture.
        string command = args.Length == 0 ? "HELP" : args[0].ToUpperInvariant();

        return command switch
        {
            "VERIFY" => HarnessVectors.Run() ? 0 : 1,
            "W5" => ReproductionGate.Run(TrialsAt(args, 1)) ? 0 : 1,
            "COMPUTED" => Computed(),
            "CELL" => Cell(args),
            "RMW" => Rmw(args),
            "GATES" => Gates(TrialsAt(args, 1)),
            _ => Help()
        };
    }


    private static int Gates(int trials)
    {
        bool vectors = HarnessVectors.Run();
        Report.Blank();
        bool reproduced = ReproductionGate.Run(trials);
        Report.Blank();
        Report.Line($"HARNESS GATES: vectors {(vectors ? "PASS" : "FAIL")}, W5 {(reproduced ? "PASS" : "FAIL")}");

        return vectors && reproduced ? 0 : 1;
    }


    private static int Computed()
    {
        Report.Text("COMPUTED CELLS - uncontended cost by quorum-distance arithmetic, no simulation");
        Report.Text("Every figure below is exact. Nothing here is measured and nothing here is contention-shaped.");
        Report.Blank();
        Report.Text("columns: replicas, topology, site, region, quePaxaLeaderMs, quePaxaOtherMs, fastQuorumMs, fastShippedMs, classicMs, fast/classic, shipped/quorum");

        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            foreach(Topology placement in Topologies.Grid(replicaCount))
            {
                foreach(ComputedSiteCost cost in QuorumDistance.For(placement))
                {
                    Report.Line($"{replicaCount}, {placement.Name}, {cost.Site}, {cost.Region}, {VirtualTimePump.ToMilliseconds(cost.QuePaxaLeaderRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.QuePaxaNonLeaderRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.FastQuorumRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.FastShippedRoundTrip):F3}, {VirtualTimePump.ToMilliseconds(cost.ClassicRoundTrip):F3}, {cost.FastOverClassic:F2}, {cost.ShippedOverQuorum:F2}");
                }
            }
        }

        Report.Blank();
        Report.Text("quorum table, read from the shipped registers");
        Report.Text("columns: replicas, fastQuorum, majority, gap");
        foreach(int replicaCount in Topologies.ReplicaCounts)
        {
            int fast = QuorumDistance.FastQuorum(replicaCount);
            int majority = QuorumDistance.QuePaxaQuorum(replicaCount);
            Report.Line($"{replicaCount}, {fast}, {majority}, {fast - majority}");
        }

        return 0;
    }


    private static int Cell(string[] args)
    {
        if(args.Length < 4)
        {
            Report.Text("usage: cell <replicas> <topology> <writers> [trials] [seedBase]");
            Report.Line($"topologies: {string.Join(", ", TopologyNames)}");

            return 1;
        }

        int replicaCount = int.Parse(args[1], CultureInfo.InvariantCulture);
        string topologyName = args[2].ToUpperInvariant();
        int writerCount = int.Parse(args[3], CultureInfo.InvariantCulture);
        int trials = TrialsAt(args, 4);

        //Every published row carries its seed base and every exact plateau is re-run at a second one, so the
        //base has to be something an operator can set rather than a number the allocator alone decides.
        int seedBase = args.Length > 5
            ? int.Parse(args[5], CultureInfo.InvariantCulture)
            : CellSweep.DefaultSeedBase(replicaCount, writerCount);

        Topology? placement = Resolve(topologyName, replicaCount);
        if(placement is null)
        {
            Report.Line($"unknown topology '{topologyName}'; known: {string.Join(", ", TopologyNames)}");

            return 1;
        }

        JitterModel jitter = placement.Name.StartsWith("probe", StringComparison.Ordinal)
            ? JitterModel.PublishedMillisecondGrid
            : JitterModel.ProportionalFifteenPercent;

        return CellSweep.Run(placement, writerCount, jitter, trials, seedBase).Agreed ? 0 : 1;
    }


    private static int Rmw(string[] args)
    {
        if(args.Length < 4)
        {
            Report.Text("usage: rmw <replicas> <topology> <writers> [trials] [seedBase]");
            Report.Line($"topologies: {string.Join(", ", TopologyNames)}");

            return 1;
        }

        int replicaCount = int.Parse(args[1], CultureInfo.InvariantCulture);
        string topologyName = args[2].ToUpperInvariant();
        int writerCount = int.Parse(args[3], CultureInfo.InvariantCulture);
        int trials = TrialsAt(args, 4);
        int seedBase = args.Length > 5
            ? int.Parse(args[5], CultureInfo.InvariantCulture)
            : CellSweep.DefaultSeedBase(replicaCount, writerCount);

        Topology? placement = Resolve(topologyName, replicaCount);
        if(placement is null)
        {
            Report.Line($"unknown topology '{topologyName}'; known: {string.Join(", ", TopologyNames)}");

            return 1;
        }

        //A read-modify-write writer is a member of the chain it writes to, so a cell asking for more writers
        //than replicas describes a workload no deployment can run and is refused rather than clamped.
        if(writerCount > replicaCount)
        {
            Report.Line($"a read-modify-write cell runs at most one writer per replica; {writerCount} writers were asked for over {replicaCount} replicas");

            return 1;
        }

        JitterModel jitter = placement.Name.StartsWith("probe", StringComparison.Ordinal)
            ? JitterModel.PublishedMillisecondGrid
            : JitterModel.ProportionalFifteenPercent;

        return RmwCellSweep.Run(placement, writerCount, jitter, trials, seedBase).Agreed ? 0 : 1;
    }


    private static int Help()
    {
        Report.Text("Lumoin.Verisync.DecisionTable - the measurement harness for the QuePaxa against Fast CASPaxos decision table.");
        Report.Blank();
        Report.Text("  verify                                  the harness's own vectors");
        Report.Text("  w5 [trials]                             the reproduction gate over the seven published five-replica rows");
        Report.Text("  gates [trials]                          verify then w5, which is what a slice is adjudicated on");
        Report.Text("  computed                                the computed cells and the quorum table, no simulation");
        Report.Text("  cell <replicas> <topology> <writers> [trials] [seedBase]");
        Report.Text("  rmw <replicas> <topology> <writers> [trials] [seedBase]");
        Report.Text("                                          the read-modify-write rider, at most one writer per replica");
        Report.Blank();
        Report.Line($"topologies: {string.Join(", ", TopologyNames)}");

        return 1;
    }


    private static ImmutableArray<string> TopologyNames { get; } =
    [
        "co-located",
        "co-located-sensitivity",
        "multi-az",
        "multi-region",
        "global",
        "clustered-majority",
        "probe-spread",
        "probe-clustered"
    ];


    private static Topology? Resolve(string upperCaseName, int replicaCount) => upperCaseName switch
    {
        "CO-LOCATED" => Topologies.CoLocated(replicaCount),
        "CO-LOCATED-SENSITIVITY" => Topologies.CoLocatedSensitivity(replicaCount),
        "MULTI-AZ" => Topologies.MultiAvailabilityZone(replicaCount),
        "MULTI-REGION" => Topologies.MultiRegion(replicaCount),
        "GLOBAL" => Topologies.Global(replicaCount),
        "CLUSTERED-MAJORITY" => Topologies.ClusteredMajority(replicaCount),
        "PROBE-SPREAD" => Topologies.ProbeSpread(),
        "PROBE-CLUSTERED" => Topologies.ProbeClustered(),
        _ => null
    };


    private static int TrialsAt(string[] args, int index) => args.Length > index ? int.Parse(args[index], CultureInfo.InvariantCulture) : TrialsDefault;
}
