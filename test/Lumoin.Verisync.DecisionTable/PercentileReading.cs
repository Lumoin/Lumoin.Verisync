using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One percentile of a latency population that may be censored, together with whether that percentile has a
/// finite value at all.
/// </summary>
/// <remarks>
/// <para>
/// A WRITE THAT NEVER FINISHED RANKS ABOVE EVERY WRITE THAT DID. Ranking a tail over the survivors alone
/// understates it in favour of whichever configuration fails most, and the tail is the whole reason a verdict
/// is read at the p95 rather than at the p50. The rank is therefore taken over every write of the row and a
/// censored write occupies the top of the order.
/// </para>
/// <para>
/// A percentile whose rank lands inside the censored mass has no value the population supports, and this type
/// reports that rather than inventing one. It compares as positive infinity, so a row whose tail is unbounded
/// can never win an argmin, and it prints as a marker, so no column of the report can carry a
/// <see cref="double.NaN"/>.
/// </para>
/// </remarks>
internal sealed record PercentileReading
{
    private PercentileReading(double value, bool hasSample)
    {
        Value = value;
        HasSample = hasSample;
    }


    /// <summary>The reading of a population that holds no observation at all.</summary>
    public static PercentileReading None { get; } = new(double.PositiveInfinity, false);

    /// <summary>The reading of a percentile whose rank landed inside the censored mass.</summary>
    public static PercentileReading Unbounded { get; } = new(double.PositiveInfinity, true);


    /// <summary>The value in the unit its population was given in, which is positive infinity when the reading carries no number.</summary>
    public double Value { get; }

    /// <summary>Whether the population held any observation at all.</summary>
    public bool HasSample { get; }

    /// <summary>Whether the reading is a number rather than a marker.</summary>
    public bool IsBounded => !double.IsPositiveInfinity(Value);


    /// <summary>The reading at <paramref name="value"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The reading.</returns>
    public static PercentileReading At(double value) => new(value, true);


    /// <summary>
    /// The <paramref name="percentile"/> of a population of <paramref name="finished"/> observations and
    /// <paramref name="censored"/> writes that produced none.
    /// </summary>
    /// <param name="finished">The observations, in whatever unit the column carries, in any order.</param>
    /// <param name="censored">How many writes of the same population produced no observation.</param>
    /// <param name="percentile">The percentile, from zero to one.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="finished"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="censored"/> is negative or <paramref name="percentile"/> is outside zero to one.</exception>
    public static PercentileReading Of(IReadOnlyList<double> finished, int censored, double percentile)
    {
        ArgumentNullException.ThrowIfNull(finished);
        ArgumentOutOfRangeException.ThrowIfNegative(censored);
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1.0);

        //The population is every write the row covers, not only the writes that produced a number.
        int population = finished.Count + censored;
        if(population == 0)
        {
            return None;
        }

        int rank = (int)Math.Ceiling(percentile * population);
        int index = Math.Clamp(rank - 1, 0, population - 1);
        if(index >= finished.Count)
        {
            return Unbounded;
        }

        double[] sorted = [.. finished.Order()];

        return At(sorted[index]);
    }


    /// <summary>The reading as a report column, which is a marker rather than a number when it is not one.</summary>
    /// <returns>The column.</returns>
    public override string ToString() => !HasSample
        ? "none"
        : IsBounded ? Value.ToString("F3", CultureInfo.InvariantCulture) : "unbounded";
}
