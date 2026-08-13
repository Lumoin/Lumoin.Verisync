using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The rule that turns a cell's measurements into a verdict, applied in the order the campaign's plan states
/// it: gate A on agreement, gate B on the read-modify-write retry rate, the argmin, the tie-break, and the
/// margin the cell is published at.
/// </summary>
/// <remarks>
/// <para>
/// THE TABLE IS DERIVED RATHER THAN JUDGED. A judged table cannot be re-derived when a number moves, so every
/// step here is a pure function of the rows it is handed and of the cell's own majority-radius round trip, and
/// nothing about a cell is decided anywhere else.
/// </para>
/// <para>
/// THE TIE-BREAK'S BAND AND THE "EITHER" BAND ARE ONE BAND, read as the relative excess of a reading over the
/// lowest reading in the cell. Two bands would disagree at their shared boundary, and a boundary that resolves
/// one way for the tie-break and the other way for the publication decides a cell by the order the rules
/// happen to run in. A configuration at exactly the band is OUTSIDE it, so the boundary resolves to a winner.
/// </para>
/// <para>
/// A tie-break that fires can only produce an "either" cell. It promotes a simpler configuration over a
/// strictly faster one, which puts the runner-up's reading at or below the winner's and the margin at or below
/// zero; the winner is still named, because "either, and prefer this one" is a more useful record than a
/// refusal to say anything.
/// </para>
/// </remarks>
internal static class VerdictReducer
{
    /// <summary>The relative excess over the cell's best reading inside which two configurations are not separated.</summary>
    /// <remarks>
    /// A cell whose margin is under this is published as "either". A mode carrying a policy knob is an
    /// operational liability and must not win a cell on noise, and a table that hid its close calls would be
    /// worse than no table.
    /// </remarks>
    public const double MarginBand = 0.10;


    /// <summary>The measured read-modify-write retry rate above which gate B removes a QuePaxa configuration.</summary>
    /// <remarks>
    /// The gate binds QuePaxa alone, on the settled rule that read-modify-write on QuePaxa is
    /// retry-on-conflict. A rate exactly at the ceiling is not above it and is not removed.
    /// </remarks>
    public const double RetryRateCeiling = 0.10;


    /// <summary>
    /// The verdict at every arrival spread the rows cover.
    /// </summary>
    /// <param name="rows">Every configuration's row, at every spread, in sweep order.</param>
    /// <param name="majorityRadiusMicroseconds">The cell's majority-radius round trip, which the winning rung is expressed in.</param>
    /// <param name="retryRates">The read-modify-write retry rates gate B reads, or <see langword="null"/> where the workload shape leaves the gate inert.</param>
    /// <returns>One verdict per spread, in the order the spreads first appear.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="rows"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="majorityRadiusMicroseconds"/> is not positive.</exception>
    public static ImmutableArray<CellVerdict> Reduce(IReadOnlyList<MeasuredRow> rows, long majorityRadiusMicroseconds, RetryRateDelegate? retryRates)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(majorityRadiusMicroseconds);

        ImmutableArray<CellVerdict>.Builder verdicts = ImmutableArray.CreateBuilder<CellVerdict>();
        foreach(double spread in rows.Select(row => row.Spread).Distinct())
        {
            verdicts.Add(ReduceSpread([.. rows.Where(row => row.Spread == spread)], spread, majorityRadiusMicroseconds, retryRates));
        }

        return verdicts.ToImmutable();
    }


    /// <summary>
    /// The verdict over the configurations of one arrival spread.
    /// </summary>
    /// <param name="rows">The configurations measured at that spread, which the caller has already grouped.</param>
    /// <param name="spread">The spread the verdict speaks at.</param>
    /// <param name="majorityRadiusMicroseconds">The cell's majority-radius round trip, which the winning rung is expressed in.</param>
    /// <param name="retryRates">The read-modify-write retry rates gate B reads, or <see langword="null"/> where the workload shape leaves the gate inert.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="rows"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="majorityRadiusMicroseconds"/> is not positive.</exception>
    public static CellVerdict ReduceSpread(IReadOnlyList<MeasuredRow> rows, double spread, long majorityRadiusMicroseconds, RetryRateDelegate? retryRates)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(majorityRadiusMicroseconds);

        ImmutableArray<RemovedConfiguration>.Builder removed = ImmutableArray.CreateBuilder<RemovedConfiguration>();

        //GATE A. Agreement is a gate rather than a metric: a configuration that failed it in any trial leaves
        //the cell, and a protocol left with no surviving configuration loses the cell unconditionally.
        var surviving = new List<MeasuredRow>();
        foreach(MeasuredRow row in rows)
        {
            if(row.Agreed)
            {
                surviving.Add(row);
            }
            else
            {
                removed.Add(new RemovedConfiguration(row, "gate A, the configuration failed agreement in at least one trial"));
            }
        }

        if(surviving.Count == 0)
        {
            return Void(spread, removed.ToImmutable(), "no configuration agreed in every trial, so the cell is void rather than slow");
        }

        bool bothProtocols = surviving.Any(row => row.Protocol == ProtocolKind.QuePaxa) && surviving.Any(row => row.Protocol == ProtocolKind.FastCasPaxos);
        string reason = bothProtocols
            ? "the argmin of the representative writer's p95 over the surviving configurations"
            : string.Create(CultureInfo.InvariantCulture, $"gate A left {surviving[0].ProtocolName} as the only protocol with a surviving configuration, so the cell goes to it unconditionally");

        //GATE B. The gate is inert without measured retry rates, which is the interchangeable-update shape of
        //the workload; under a read-modify-write shape it removes the QuePaxa configurations that re-propose
        //too often, because there the retries are the cost rather than a detail of it.
        if(retryRates is not null)
        {
            var retained = new List<MeasuredRow>();
            foreach(MeasuredRow row in surviving)
            {
                double? rate = retryRates(row);
                if(row.Protocol == ProtocolKind.QuePaxa && rate > RetryRateCeiling)
                {
                    removed.Add(new RemovedConfiguration(row, string.Create(CultureInfo.InvariantCulture, $"gate B, the measured read-modify-write retry rate {rate:F3} is above {RetryRateCeiling:F2}")));
                }
                else
                {
                    retained.Add(row);
                }
            }

            surviving = retained;
        }

        if(surviving.Count == 0)
        {
            return Void(spread, removed.ToImmutable(), "gate B removed every configuration that agreed, so the cell is void rather than slow");
        }

        //A configuration the cell holds no observation for at all cannot win, and it is reported rather than
        //ranked: an absent population is not a slow one, and leaving it in the order would let a row that
        //measured nothing sit beside rows that measured something.
        var candidates = new List<MeasuredRow>();
        foreach(MeasuredRow row in surviving)
        {
            if(row.RepresentativeP95.HasSample)
            {
                candidates.Add(row);
            }
            else
            {
                removed.Add(new RemovedConfiguration(row, "the representative writer produced no sample at all, so the configuration cannot win"));
            }
        }

        if(candidates.Count == 0)
        {
            return Void(spread, removed.ToImmutable(), "no surviving configuration produced a sample at the representative writer");
        }

        //An unbounded tail compares as positive infinity, so it can never be the argmin while any bounded
        //reading is in the cell, and a cell of nothing but unbounded tails has no winner at all.
        double best = candidates.Min(row => row.RepresentativeP95.Value);
        if(double.IsPositiveInfinity(best))
        {
            return Void(spread, removed.ToImmutable(), "every surviving configuration's representative tail is unbounded, so the cell is void rather than slow");
        }

        MeasuredRow winner = candidates
            .Where(row => Excess(row.RepresentativeP95.Value, best) < MarginBand)
            .OrderBy(SimplicityOf)
            .ThenBy(row => row.RepresentativeP95.Value)
            .ThenBy(row => row.Mode)
            .ThenBy(row => row.Rung)
            .First();

        MeasuredRow? runnerUp = candidates
            .Where(row => !ReferenceEquals(row, winner))
            .OrderBy(row => row.RepresentativeP95.Value)
            .ThenBy(SimplicityOf)
            .ThenBy(row => row.Mode)
            .ThenBy(row => row.Rung)
            .FirstOrDefault();

        double margin = runnerUp is null
            ? double.PositiveInfinity
            : Excess(runnerUp.RepresentativeP95.Value, winner.RepresentativeP95.Value);

        return new CellVerdict(
            spread,
            margin < MarginBand ? VerdictOutcome.Either : VerdictOutcome.Winner,
            winner,
            runnerUp,
            margin,
            RungInMajorityRadius(winner, majorityRadiusMicroseconds),
            removed.ToImmutable(),
            reason);
    }


    /// <summary>
    /// How simple <paramref name="row"/>'s configuration is to operate.
    /// </summary>
    /// <param name="row">The configuration's row.</param>
    /// <returns>The rank the tie-break orders on.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="row"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The rung outranks the mode it was configured on. A staggered leaderless configuration carries the same
    /// policy knob a staggered leadered one does, and the knob is the liability the ordering exists to price.
    /// </remarks>
    public static ModeSimplicity SimplicityOf(MeasuredRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if(row.Rung != 0.0)
        {
            return ModeSimplicity.Staggered;
        }

        return row.Mode == ConfigurationMode.QuePaxaLeadered ? ModeSimplicity.Leadered : ModeSimplicity.Leaderless;
    }


    /// <summary>
    /// The rung of <paramref name="row"/> in units of the cell's majority-radius round trip.
    /// </summary>
    /// <param name="row">The configuration's row.</param>
    /// <param name="majorityRadiusMicroseconds">The cell's majority-radius round trip.</param>
    /// <returns>The rung in majority-radius round trips.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="row"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="majorityRadiusMicroseconds"/> is not positive.</exception>
    /// <remarks>
    /// THE TWO ARMS CONFIGURE THEIR RUNGS IN DIFFERENT UNITS and a verdict has to prescribe one. A QuePaxa
    /// rung is already a fraction of the majority radius and converts to itself; a Fast CASPaxos rung is a
    /// fraction of the fast-quorum round trip and converts through the absolute microseconds the row carries,
    /// which is what that column is on the row for.
    /// </remarks>
    public static double RungInMajorityRadius(MeasuredRow row, long majorityRadiusMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(majorityRadiusMicroseconds);

        return row.RungMicroseconds / (double)majorityRadiusMicroseconds;
    }


    /// <summary>
    /// How far <paramref name="value"/> lies above <paramref name="best"/>, as a fraction of it.
    /// </summary>
    /// <param name="value">The reading being placed.</param>
    /// <param name="best">The reading it is placed against.</param>
    /// <returns>The relative excess, which is zero for two identical readings and positive infinity for an unbounded one.</returns>
    /// <remarks>
    /// Two identical readings are never separated, whatever they are: the equality is read first so that a
    /// pair of unbounded readings does not divide one infinity by another, and so that the reading a band is
    /// measured from is always inside its own band.
    /// </remarks>
    private static double Excess(double value, double best) => value == best ? 0.0 : (value - best) / best;


    /// <summary>
    /// The verdict of a cell that has none.
    /// </summary>
    /// <param name="spread">The spread the verdict speaks at.</param>
    /// <param name="removed">Every configuration the gates removed, with its reason.</param>
    /// <param name="reason">Why the cell is void.</param>
    /// <returns>The verdict.</returns>
    private static CellVerdict Void(double spread, ImmutableArray<RemovedConfiguration> removed, string reason) =>
        new(spread, VerdictOutcome.Void, null, null, double.PositiveInfinity, 0.0, removed, reason);
}
