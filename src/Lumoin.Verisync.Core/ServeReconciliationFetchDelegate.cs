using System;
using System.Collections.Generic;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The responder's lookup seam: resolve each requested item to its element entry from the session's pinned
/// snapshot. The responder calls this when it receives a fetch, then sends the entries back as an elements
/// message.
/// </summary>
/// <typeparam name="TElement">The application element type the items identify.</typeparam>
/// <param name="items">The fixed-width items the initiator decoded but does not hold locally.</param>
/// <returns>One element entry per requested item, covering exactly the requested set.</returns>
/// <remarks>
/// The returned entries must cover EXACTLY the requested items — the session verifies set equality and fails
/// closed otherwise, because a partial answer would strand the initiator waiting on resolutions that never
/// arrive. Resolution is synchronous, run from the single consumer loop.
/// </remarks>
public delegate IReadOnlyList<ReconciliationElementEntry<TElement>> ServeReconciliationFetchDelegate<TElement>(IReadOnlyList<ReadOnlyMemory<byte>> items);
