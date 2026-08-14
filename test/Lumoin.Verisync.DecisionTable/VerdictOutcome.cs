namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What a cell's verdict says at one arrival spread.
/// </summary>
/// <remarks>
/// A table that hides its close calls is worse than no table, so a margin under the band is its own outcome
/// rather than a winner with a small number beside it.
/// </remarks>
internal enum VerdictOutcome
{
    /// <summary>One configuration won by a margin at or above the band.</summary>
    Winner,

    /// <summary>The margin is inside the band, so the cell is published as "either" rather than as a winner.</summary>
    Either,

    /// <summary>The cell has no verdict at all, which is a cell that is void rather than slow.</summary>
    Void
}
