using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one cell's sweep produced: every configuration's row, the verdict read off those rows at each arrival
/// spread, and whether the cell agreed at all.
/// </summary>
/// <param name="Agreed">Whether every configuration agreed in every trial, which is a gate rather than a metric.</param>
/// <param name="Rows">Every configuration's row, in the order the sweep ran them.</param>
/// <param name="Verdicts">One verdict per arrival spread, in the order the sweep ran them.</param>
/// <remarks>
/// The sweep hands back what it printed rather than only whether it passed, because the grid is one caller
/// above the cell and the table it writes is derived from these rows rather than transcribed from this
/// report.
/// </remarks>
internal sealed record CellOutcome(bool Agreed, ImmutableArray<MeasuredRow> Rows, ImmutableArray<CellVerdict> Verdicts);
