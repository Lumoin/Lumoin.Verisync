using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one configuration cost at one arrival spread, carried whole so that the printed row and the verdict
/// read the same record and every column of it comes from the same trial loop.
/// </summary>
/// <param name="Mode">The configuration, which names the protocol and the mode inside it.</param>
/// <param name="Rung">The ladder rung as an operator configures it, in units of that arm's own unit.</param>
/// <param name="RungMicroseconds">The same rung in absolute microseconds, which is what makes it convertible between the two arms' currencies.</param>
/// <param name="Spread">The arrival spread, in units of a writer's own majority-radius round trip.</param>
/// <param name="P50">The median commit latency in milliseconds over every write of the row.</param>
/// <param name="P95">The p95 commit latency in milliseconds over every write of the row.</param>
/// <param name="Max">The worst commit latency in milliseconds over every write of the row.</param>
/// <param name="P95RoundTrips">The same p95, each observation already divided by that writer's own majority-radius round trip.</param>
/// <param name="RepresentativeP95">The representative writer's own p95 commit latency in milliseconds, which is the column the verdict is read at.</param>
/// <param name="RepresentativeP95RoundTrips">The representative writer's own p95 in its own round trips.</param>
/// <param name="Unfinished">How many writes spent their recovery ladder without committing.</param>
/// <param name="RepresentativeUnfinished">How many of those were the representative writer's own, which is the denominator the verdict column is read over.</param>
/// <param name="StoodDown">How many writes stood down on a learn signal and sent nothing, which is zero throughout the grid.</param>
/// <param name="TrialFastRate">The fraction of trials in which any writer took the fast path.</param>
/// <param name="WriterFastRate">The fraction of writes that took it.</param>
/// <param name="MeanSteps">The mean steps or phases a write executed.</param>
/// <param name="MeanAddedWaitMicroseconds">The mean stagger a writer paid before sending.</param>
/// <param name="RepresentativeAddedWaitMicroseconds">The stagger the representative writer itself paid, which is what reconstructs the client currency at the column the verdict is read from.</param>
/// <param name="Agreed">Whether every trial of this configuration agreed.</param>
/// <remarks>
/// THE PERCENTILES ARE PART OF THE RECORD RATHER THAN OF THE PRINTER. A verdict derived from a differently
/// ranked tail than the one the table prints would be a second opinion wearing the first one's numbers, so the
/// readings are taken once, where the trial loop ends, and the row is what both the report and the reducer
/// consume afterwards.
/// </remarks>
internal sealed record MeasuredRow(
    ConfigurationMode Mode,
    double Rung,
    long RungMicroseconds,
    double Spread,
    PercentileReading P50,
    PercentileReading P95,
    PercentileReading Max,
    PercentileReading P95RoundTrips,
    PercentileReading RepresentativeP95,
    PercentileReading RepresentativeP95RoundTrips,
    int Unfinished,
    int RepresentativeUnfinished,
    int StoodDown,
    double TrialFastRate,
    double WriterFastRate,
    double MeanSteps,
    double MeanAddedWaitMicroseconds,
    double RepresentativeAddedWaitMicroseconds,
    bool Agreed)
{
    /// <summary>Which protocol the configuration belongs to.</summary>
    public ProtocolKind Protocol => Mode is ConfigurationMode.QuePaxaLeadered or ConfigurationMode.QuePaxaLeaderless
        ? ProtocolKind.QuePaxa
        : ProtocolKind.FastCasPaxos;


    /// <summary>The protocol's name as every report column prints it.</summary>
    public string ProtocolName => Protocol == ProtocolKind.QuePaxa ? "QuePaxa" : "FastCASPaxos";


    /// <summary>The mode's name as every report column prints it.</summary>
    public string ModeName => Mode switch
    {
        ConfigurationMode.QuePaxaLeadered => "leadered",
        ConfigurationMode.QuePaxaLeaderless => "leaderless",
        ConfigurationMode.FastUnhedged => "unhedged",
        _ => "hedged"
    };


    /// <summary>The configuration as one token a verdict line can be grepped by: protocol, mode and rung.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{ProtocolName}/{ModeName}/{Rung:F2}");


    /// <summary>
    /// The row of a configuration whose trial loop has just ended, with every percentile taken here.
    /// </summary>
    /// <param name="mode">The configuration.</param>
    /// <param name="rung">The ladder rung as an operator configures it.</param>
    /// <param name="rungMicroseconds">The same rung in absolute microseconds.</param>
    /// <param name="spread">The arrival spread.</param>
    /// <param name="latencies">Every write's commit latency in milliseconds, measured from that writer's own activation.</param>
    /// <param name="roundTrips">The same latencies, each already divided by that writer's own majority-radius round trip.</param>
    /// <param name="representativeLatencies">The representative writer's own commit latencies in milliseconds.</param>
    /// <param name="representativeRoundTrips">The representative writer's own latencies in its own round trips.</param>
    /// <param name="unfinished">How many writes spent their recovery ladder without committing.</param>
    /// <param name="representativeUnfinished">How many of those were the representative writer's own.</param>
    /// <param name="stoodDown">How many writes stood down on a learn signal and sent nothing.</param>
    /// <param name="trialFastRate">The fraction of trials in which any writer took the fast path.</param>
    /// <param name="writerFastRate">The fraction of writes that took it.</param>
    /// <param name="meanSteps">The mean steps or phases a write executed.</param>
    /// <param name="meanAddedWaitMicroseconds">The mean stagger a writer paid before sending.</param>
    /// <param name="representativeAddedWaitMicroseconds">The stagger the representative writer itself paid.</param>
    /// <param name="agreed">Whether every trial of this configuration agreed.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ArgumentNullException">Thrown if any of the four populations is <see langword="null"/>.</exception>
    public static MeasuredRow Of(
        ConfigurationMode mode,
        double rung,
        long rungMicroseconds,
        double spread,
        IReadOnlyList<double> latencies,
        IReadOnlyList<double> roundTrips,
        IReadOnlyList<double> representativeLatencies,
        IReadOnlyList<double> representativeRoundTrips,
        int unfinished,
        int representativeUnfinished,
        int stoodDown,
        double trialFastRate,
        double writerFastRate,
        double meanSteps,
        double meanAddedWaitMicroseconds,
        double representativeAddedWaitMicroseconds,
        bool agreed)
    {
        ArgumentNullException.ThrowIfNull(latencies);
        ArgumentNullException.ThrowIfNull(roundTrips);
        ArgumentNullException.ThrowIfNull(representativeLatencies);
        ArgumentNullException.ThrowIfNull(representativeRoundTrips);

        return new MeasuredRow(
            mode,
            rung,
            rungMicroseconds,
            spread,
            PercentileReading.Of(latencies, unfinished, 0.50),
            PercentileReading.Of(latencies, unfinished, 0.95),
            PercentileReading.Of(latencies, unfinished, 1.00),
            PercentileReading.Of(roundTrips, unfinished, 0.95),
            PercentileReading.Of(representativeLatencies, representativeUnfinished, 0.95),
            PercentileReading.Of(representativeRoundTrips, representativeUnfinished, 0.95),
            unfinished,
            representativeUnfinished,
            stoodDown,
            trialFastRate,
            writerFastRate,
            meanSteps,
            meanAddedWaitMicroseconds,
            representativeAddedWaitMicroseconds,
            agreed);
    }
}
