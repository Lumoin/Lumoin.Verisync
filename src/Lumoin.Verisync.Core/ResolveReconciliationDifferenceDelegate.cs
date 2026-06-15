using System;
using System.Collections.Generic;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The initiator's classification seam, called exactly once when its decoder completes — including with an
/// empty list on a quiescent reconciliation where the two snapshots were already equal. The host partitions
/// the decoded difference by local membership against the peer's causal context: an item it lacks goes into
/// the resolution's fetch list; an item it holds the peer's context covers (the peer observed and removed it)
/// becomes a local drop; an item it holds the peer's context does not cover goes into the push list as an
/// element entry for the peer to add.
/// </summary>
/// <typeparam name="TElement">The application element type carried by a pushed entry.</typeparam>
/// <param name="decodedItems">The fixed-width items the decoder recovered as the symmetric difference.</param>
/// <param name="peerContext">
/// The peer's causal context, its <see cref="VectorClockState"/>, against which a held item's dot is tested for
/// observed-removal; the empty clock's state in an add-only session, where it is ignored and the resolution
/// carries no local drops.
/// </param>
/// <returns>The partition of the difference into items to fetch, entries to push, and dots to drop locally.</returns>
/// <remarks>
/// The session awaits no result here; resolution is synchronous, run from the single consumer loop the moment
/// the decode completes. Returning <see langword="null"/> fails closed: the session faults rather than treat a
/// missing resolution as quiescence.
/// </remarks>
public delegate ReconciliationDifferenceResolution<TElement> ResolveReconciliationDifferenceDelegate<TElement>(IReadOnlyList<ReadOnlyMemory<byte>> decodedItems, VectorClockState peerContext);
