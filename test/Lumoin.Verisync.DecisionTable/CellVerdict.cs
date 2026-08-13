using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one cell decided at one arrival spread, and everything the decision rests on.
/// </summary>
/// <param name="Spread">The arrival spread the verdict speaks at, in units of a writer's own majority-radius round trip.</param>
/// <param name="Outcome">Whether the cell has a winner, is too close to call, or is void.</param>
/// <param name="Winner">The winning configuration, which is absent only on a void cell.</param>
/// <param name="RunnerUp">The best surviving configuration that is not the winner, which is absent when the cell held only one candidate.</param>
/// <param name="Margin">How far the runner-up's representative p95 lies above the winner's, as a fraction of the winner's, and positive infinity where there is nothing to compare against.</param>
/// <param name="WinningRungInMajorityRadius">The winning rung in units of the cell's majority-radius round trip, which is the form an operator can configure across both arms.</param>
/// <param name="Removed">Every configuration the gates removed, with its reason, in the order the gates removed them.</param>
/// <param name="Reason">How the verdict arose, which is what makes it re-derivable rather than judged.</param>
/// <remarks>
/// THE MARGIN AND THE RUNNER-UP ARE CARRIED IN EVERY CELL, including the cells that have a winner and the cells
/// that are too close to have one. A table that recorded only its winners could not be re-read when a number
/// moves, and a close call it did not disclose would read as a decision.
/// </remarks>
internal sealed record CellVerdict(
    double Spread,
    VerdictOutcome Outcome,
    MeasuredRow? Winner,
    MeasuredRow? RunnerUp,
    double Margin,
    double WinningRungInMajorityRadius,
    ImmutableArray<RemovedConfiguration> Removed,
    string Reason)
{
    /// <summary>The margin as the report prints it, which is a marker where the cell held nothing to compare against.</summary>
    public string MarginText => double.IsPositiveInfinity(Margin)
        ? "unbounded"
        : Margin.ToString("F3", CultureInfo.InvariantCulture);


    /// <summary>The outcome as the report prints it.</summary>
    public string OutcomeName => Outcome switch
    {
        VerdictOutcome.Winner => "winner",
        VerdictOutcome.Either => "either",
        _ => "void"
    };
}
