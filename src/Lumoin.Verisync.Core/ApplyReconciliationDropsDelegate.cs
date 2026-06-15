using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// How a remove propagates into the local replica: the session awaits this both for a received drop message —
/// the initiator's tombstones the responder must honour — and for the initiator's own local drops, the entries
/// it holds that the peer's context proves were observed and removed. For each dot the host removes the present
/// entry that dot names, keeping the context, then folds the peer's context so the merged context dominates the
/// dropped dot and the entry never resurrects on a later reconcile.
/// </summary>
/// <typeparam name="TElement">The application element type the dropped entries carried, matching the session's.</typeparam>
/// <param name="dots">The dots whose present entries the local replica must drop.</param>
/// <param name="peerContext">
/// The peer's causal context, its <see cref="VectorClockState"/>, folded into the local context after the drops
/// so the merged context dominates every dropped dot.
/// </param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the dots have been dropped and the context folded.</returns>
/// <remarks>
/// The session awaits this only from its single consumer loop, so an implementation needs no synchronization of
/// its own. Throwing fails closed: the exception propagates out of
/// <see cref="AntiEntropySession{TElement}.RunAsync"/> and ends the loop.
/// </remarks>
public delegate ValueTask ApplyReconciliationDropsDelegate<TElement>(IReadOnlyList<DotState> dots, VectorClockState peerContext, CancellationToken cancellationToken);
