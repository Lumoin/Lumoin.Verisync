using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one read-modify-write cell's sweep produced: every configuration's row and the cell's verdict read
/// both with the workload gate live and with it inert.
/// </summary>
/// <param name="Agreed">Whether the fold oracle held in every trial of every configuration, which is a gate rather than a metric.</param>
/// <param name="Rows">Every configuration's row, in the order the sweep ran them.</param>
/// <param name="GatedVerdicts">One verdict per arrival spread with the workload gate reading this rider's own measured retry rates.</param>
/// <param name="InertVerdicts">One verdict per arrival spread with the workload gate inert, which is the reading an interchangeable update shape gets.</param>
/// <remarks>
/// BOTH VERDICTS ARE CARRIED AND BOTH ARE PRINTED. Whether the target workload is a genuine read-modify-write
/// or an idempotent, monotone or abort-on-lose update is a fact about the deployment rather than about either
/// protocol, and a cell that published only one reading would answer a question the measurement was never
/// given.
/// </remarks>
internal sealed record RmwCellOutcome(
    bool Agreed,
    ImmutableArray<RmwRow> Rows,
    ImmutableArray<CellVerdict> GatedVerdicts,
    ImmutableArray<CellVerdict> InertVerdicts);
