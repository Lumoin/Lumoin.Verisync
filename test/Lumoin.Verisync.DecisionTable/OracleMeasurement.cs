namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The aggregate of one configuration of the oracle arrival model.
/// </summary>
/// <param name="Trials">How many trials the aggregate covers.</param>
/// <param name="WriterCount">How many writers contended in each trial.</param>
/// <param name="TrialFastCommitRate">The fraction of trials in which ANY writer reached its fast quorum, which is the round-survival reading.</param>
/// <param name="WriterFastCommitRate">The fraction of writes that committed fast, which is what an individual writer experiences.</param>
/// <param name="MeanRoundTripsPerWrite">The assumed cost per write under the published cost model.</param>
/// <param name="MeanAddedWaitMicroseconds">The mean stagger a writer paid before sending.</param>
/// <remarks>
/// THE TWO RATES MUST NEVER SHARE A NAME. They diverge by a factor of the writer count exactly where
/// staggering works best, which is the distinction the published rows turn on.
/// </remarks>
internal sealed record OracleMeasurement(
    int Trials,
    int WriterCount,
    double TrialFastCommitRate,
    double WriterFastCommitRate,
    double MeanRoundTripsPerWrite,
    double MeanAddedWaitMicroseconds);
