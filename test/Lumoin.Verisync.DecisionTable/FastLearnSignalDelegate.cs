using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// Supplies the learn signal writer <paramref name="writer"/> consults before it activates, or
/// <see langword="null"/> when that writer has none.
/// </summary>
/// <param name="writer">The writer index.</param>
/// <returns>The signal, or <see langword="null"/> when the writer activates on its delay unconditionally.</returns>
/// <remarks>
/// <para>
/// THE GRID RUNS WITHOUT A LEARN SIGNAL. Plan section 5.3's mode table carries no stand-down arm, so every
/// cell supplies none and every cell row reports a stood-down count of zero: a hedged writer in the grid
/// spends its fast round even when an earlier-scheduled writer already drove the round, which is the
/// configuration available to a host with no learn path and is the pessimistic side of the ladder. The seam
/// exists so that path is reachable and certified rather than dead, and so a configuration that carries a
/// learn path can be measured beside one that does not.
/// </para>
/// <para>
/// The signal is per writer because <see cref="FastRoundProgressDelegate"/> carries no writer identity: a
/// host closes over its own. A writer first in the hedging schedule waits no delay and is never asked, which
/// is the shipped writer's own contract rather than a property of this harness.
/// </para>
/// <para>
/// A stood-down writer is neither an unfinished write nor a latency sample. The shipped contract makes a
/// skipped write carry no outcome that the host must reissue, and the harness has no reissue path, so it
/// reports the disposition honestly in a column of its own rather than inventing a policy.
/// </para>
/// </remarks>
internal delegate FastRoundProgressDelegate? FastLearnSignalDelegate(int writer);
