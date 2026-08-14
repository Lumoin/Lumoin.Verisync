using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The distribution a per-leg latency jitter is drawn from: a uniform draw over a span of whole grid units,
/// where a unit is a fixed number of microseconds.
/// </summary>
/// <remarks>
/// <para>
/// THE GRID IS A SETTING RATHER THAN THE CLOCK'S RESOLUTION, and the reproduction gate is why. The published
/// rows were drawn on a whole-millisecond grid, so a model that drew natively in microseconds could not
/// reproduce a single one of them; a model that drew only in milliseconds could not express a co-located
/// placement, whose whole one-way delay is half a grid unit. Both are therefore settings of one model: the
/// reproduction runs at a thousand-microsecond grid over thirty units, and the co-located tiers run at a
/// microsecond grain over a span proportional to the link.
/// </para>
/// <para>
/// A fixed span is the same span on every link. A proportional span is a fraction of the one-way delay of the
/// link it jitters, which preserves the median round trip and is the sibling simulation's cross-region
/// default. A span of zero units draws nothing at all, which is the jitterless model the arithmetic vectors
/// compare against.
/// </para>
/// </remarks>
internal sealed class JitterModel
{
    private JitterModel(long grainMicroseconds, int fixedSpanUnits, double proportionalFraction, string description)
    {
        GrainMicroseconds = grainMicroseconds;
        FixedSpanUnits = fixedSpanUnits;
        ProportionalFraction = proportionalFraction;
        Description = description;
    }


    /// <summary>The jitterless model, under which every leg costs exactly its matrix delay.</summary>
    /// <remarks>
    /// This is what makes a simulated uncontended write comparable with the quorum-distance arithmetic to the
    /// microsecond rather than within a band, which is the harness's own sanity gate.
    /// </remarks>
    public static JitterModel None { get; } = new(1, 0, 0.0, "none");

    /// <summary>
    /// The model the published rows were drawn under: uniform over zero to twenty-nine whole milliseconds on
    /// every leg, whatever the link.
    /// </summary>
    public static JitterModel PublishedMillisecondGrid { get; } = FixedUnits(30, 1000);

    /// <summary>
    /// The campaign's model for the topology matrices: uniform over zero to fifteen percent of the link's own
    /// one-way delay, drawn at microsecond grain.
    /// </summary>
    /// <remarks>
    /// The fraction is the sibling simulation's cross-region jitter ratio, and a proportional span preserves
    /// the median round trip instead of inflating the near links the way a fixed span does.
    /// </remarks>
    public static JitterModel ProportionalFifteenPercent { get; } = Proportional(0.15, 1);


    /// <summary>How many microseconds one grid unit is worth.</summary>
    public long GrainMicroseconds { get; }

    /// <summary>The span in grid units when it is the same on every link, or zero when the span is proportional or absent.</summary>
    public int FixedSpanUnits { get; }

    /// <summary>The span as a fraction of the link's one-way delay, or zero when the span is fixed or absent.</summary>
    public double ProportionalFraction { get; }

    /// <summary>A short name for the model, printed beside every measurement it produced.</summary>
    public string Description { get; }


    /// <summary>Creates a model whose span is the same on every link.</summary>
    /// <param name="spanUnits">The span in grid units. Zero draws nothing.</param>
    /// <param name="grainMicroseconds">How many microseconds one grid unit is worth. Must be positive.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="spanUnits"/> is negative or <paramref name="grainMicroseconds"/> is not positive.</exception>
    public static JitterModel FixedUnits(int spanUnits, long grainMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spanUnits);
        ArgumentOutOfRangeException.ThrowIfLessThan(grainMicroseconds, 1);

        string description = string.Create(CultureInfo.InvariantCulture, $"uniform 0..{spanUnits - 1} units of {grainMicroseconds}us");

        return new JitterModel(grainMicroseconds, spanUnits, 0.0, spanUnits == 0 ? "none" : description);
    }


    /// <summary>Creates a model whose span is a fraction of the one-way delay of the link it jitters.</summary>
    /// <param name="fraction">The fraction of the one-way delay. Must not be negative.</param>
    /// <param name="grainMicroseconds">How many microseconds one grid unit is worth. Must be positive.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="fraction"/> is negative or <paramref name="grainMicroseconds"/> is not positive.</exception>
    public static JitterModel Proportional(double fraction, long grainMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fraction);
        ArgumentOutOfRangeException.ThrowIfLessThan(grainMicroseconds, 1);

        string description = string.Create(CultureInfo.InvariantCulture, $"uniform 0..{fraction:P0} of the one-way delay at {grainMicroseconds}us grain");

        return new JitterModel(grainMicroseconds, 0, fraction, fraction == 0.0 ? "none" : description);
    }


    /// <summary>
    /// The span, in whole grid units, this model draws over for a link of <paramref name="oneWayMicroseconds"/>.
    /// </summary>
    /// <param name="oneWayMicroseconds">The link's one-way delay in microseconds.</param>
    /// <returns>The span in grid units, which is zero when the model draws nothing on this link.</returns>
    public int SpanUnitsFor(long oneWayMicroseconds)
    {
        if(FixedSpanUnits > 0)
        {
            return FixedSpanUnits;
        }

        if(ProportionalFraction <= 0.0)
        {
            return 0;
        }

        //A link short enough that its whole proportional span falls inside one grid unit gets no jitter at
        //all rather than a whole unit of it, which would be jitter larger than the effect it models.
        return (int)(ProportionalFraction * oneWayMicroseconds / GrainMicroseconds);
    }


    /// <summary>
    /// Draws the jitter, in microseconds, one message leg pays.
    /// </summary>
    /// <param name="trialSeed">The seed of the trial the leg belongs to.</param>
    /// <param name="writer">The writer index the leg belongs to.</param>
    /// <param name="peer">The replica index at the far end of the link.</param>
    /// <param name="step">The protocol step the leg carries.</param>
    /// <param name="leg">Zero for the request leg and one for the reply leg.</param>
    /// <param name="oneWayMicroseconds">The link's one-way delay in microseconds.</param>
    /// <returns>The jitter in microseconds.</returns>
    /// <remarks>
    /// The key layout is the probe's, so a run at <see cref="PublishedMillisecondGrid"/> draws the published
    /// rows' patterns exactly. The draw is stateless, so an edit elsewhere in the harness cannot silently
    /// re-roll a measured number and two rows of one ladder share their jitter patterns.
    /// </remarks>
    public long Draw(ulong trialSeed, int writer, int peer, int step, int leg, long oneWayMicroseconds)
    {
        int span = SpanUnitsFor(oneWayMicroseconds);
        if(span <= 0)
        {
            return 0;
        }

        ulong key = trialSeed
            ^ ((ulong)(uint)writer << 40)
            ^ ((ulong)(uint)peer << 32)
            ^ ((ulong)(uint)step << 8)
            ^ (uint)leg;

        return (long)(SeedMixer.Mix(key) % (uint)span) * GrainMicroseconds;
    }
}
