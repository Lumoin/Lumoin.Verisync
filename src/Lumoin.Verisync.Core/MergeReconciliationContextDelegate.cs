using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The terminal causal-context fold for a remove-aware session, awaited once before the session completes on a
/// path where no apply ran to fold the peer's context for it — a quiescent reconciliation where the snapshots
/// were already equal, or a side that received no elements and no drops. The host folds the peer's context into
/// the local one, an element-wise maximum, so both sides end at the merged context regardless of which entries
/// changed. The fold is non-generic because a context carries no element type, and idempotent because folding a
/// context already merged leaves it unchanged.
/// </summary>
/// <param name="peerContext">The peer's causal context, its <see cref="VectorClockState"/>, to fold into the local one.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the peer's context has been folded.</returns>
/// <remarks>
/// The session awaits this only from its single consumer loop, so an implementation needs no synchronization of
/// its own, and the session invokes it at most once per run. Throwing fails closed: the exception propagates out
/// of the session's run loop and ends it.
/// </remarks>
public delegate ValueTask MergeReconciliationContextDelegate(VectorClockState peerContext, CancellationToken cancellationToken);
