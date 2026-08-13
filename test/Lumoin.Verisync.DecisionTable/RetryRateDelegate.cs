namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The measured read-modify-write retry rate of one configuration, which is the input gate B reads.
/// </summary>
/// <param name="row">The configuration's measured row.</param>
/// <returns>
/// The fraction of writes that had to re-propose because committed state moved under them, or
/// <see langword="null"/> where that configuration has no measured rate.
/// </returns>
/// <remarks>
/// The rate cannot come from a cell. It comes from the read-modify-write rider, which runs a versioned workload
/// at one uniform hop cost and therefore reports a CONTENTION rate at a stated hop cost rather than a latency,
/// and it enters the verdict as a gate rather than as a measured millisecond. A configuration the rider has no
/// figure for is left in the cell: gate B removes on a measured excess and never on an absence.
/// </remarks>
internal delegate double? RetryRateDelegate(MeasuredRow row);
