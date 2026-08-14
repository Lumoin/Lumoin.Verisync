namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One configuration a verdict's gates removed from its cell, together with why.
/// </summary>
/// <param name="Row">The removed configuration's measured row.</param>
/// <param name="Reason">Why the gates removed it, stated as the report prints it.</param>
/// <remarks>
/// A removal is reported rather than dropped. A cell whose fastest configuration was taken out for failing
/// agreement reads as a cell the slower protocol simply won, unless the removal is on the page beside it.
/// </remarks>
internal sealed record RemovedConfiguration(MeasuredRow Row, string Reason);
