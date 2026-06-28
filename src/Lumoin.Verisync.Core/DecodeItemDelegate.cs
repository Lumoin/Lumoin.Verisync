using System;
using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Decodes exactly one item from the cursor over an item-stream frame, advancing the cursor past the item's
/// bytes. This is the per-item seam of <see cref="ItemStreamChannelReader{TItem}"/>: a length-prefixed flow of
/// one structured type is consumed item by item, with no collection ever materialised.
/// </summary>
/// <typeparam name="TItem">The item type. May be a plain value (no backing) or a value that views pooled bytes (with a backing returned through <paramref name="lease"/>).</typeparam>
/// <param name="reader">
/// The cursor over the frame's payload, positioned at the start of the next item. The implementation advances
/// it past exactly one item and bounds every field length against <see cref="System.Buffers.SequenceReader{T}.Remaining"/>
/// before it copies — the item count and field widths are attacker-influenced and are never trusted past the
/// bytes actually present.
/// </param>
/// <param name="pool">The pool any owned item backing is rented from. Required and non-null; provenance for the item's memory is explicit, as everywhere else in the engine.</param>
/// <param name="lease">
/// The pooled backing the returned item views, or <see langword="null"/> when the item owns nothing (a pure
/// value type). The reader disposes it once the item has been handled, so an item's bytes live exactly as long
/// as the item is in play. An implementation that rents and then throws must return the rental before it
/// throws — a rejected item leaks nothing.
/// </param>
/// <returns>The decoded item, valid only until the reader disposes <paramref name="lease"/>.</returns>
/// <exception cref="InvalidOperationException">Thrown when the item is truncated or a field length reaches past the frame bounds.</exception>
public delegate TItem DecodeItemDelegate<TItem>(ref SequenceReader<byte> reader, MemoryPool<byte> pool, out IDisposable? lease);
