using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// How received elements enter the local replica: the session awaits this when it gets an elements message —
/// for the initiator the answer to its fetch, for the responder the initiator's pushed entries. The uniform
/// apply rule serves both roles: for each entry whose dot the LOCAL context (before this fold) already covers,
/// the entry is a local tombstone — it is not added and its dot is returned as a push-drop; every other entry's
/// element is added to the local observed-remove set. The peer's context is then folded into the local one so
/// the merged context dominates every retained and dropped dot.
/// </summary>
/// <typeparam name="TElement">The application element type carried by each entry.</typeparam>
/// <param name="entries">The item-to-element resolutions to apply to the local replica.</param>
/// <param name="peerContext">
/// The peer's causal context, its <see cref="VectorClockState"/>, folded into the local context once the
/// genuine adds are applied; the empty clock's state in an add-only session, where the fold is a no-op and no
/// push-drops are returned.
/// </param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>
/// A task whose result is the dots the local context already covered — the local tombstones the session sends
/// back as a drop; <see cref="ImmutableArray{T}.Empty"/> when none, including the whole add-only path.
/// </returns>
/// <remarks>
/// The session awaits this only from its single consumer loop, so an implementation needs no synchronization of
/// its own. Throwing fails closed: the exception propagates out of
/// <see cref="AntiEntropySession{TElement}.RunAsync"/> and ends the loop.
/// </remarks>
public delegate ValueTask<ImmutableArray<DotState>> ApplyReconciliationElementsDelegate<TElement>(IReadOnlyList<ReconciliationElementEntry<TElement>> entries, VectorClockState peerContext, CancellationToken cancellationToken);
