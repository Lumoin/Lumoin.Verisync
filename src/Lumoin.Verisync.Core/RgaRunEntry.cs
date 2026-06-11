using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One run of a serialized compactable <see cref="Rga{TValue}"/>: a maximal chain of same-replica vertices
/// whose counters increase by one and where each vertex was inserted after the one before it. The run covers
/// vertices <c>(First.Replica, First.Counter + i)</c> for <c>i</c> in <c>[0, Values.Length)</c>; the vertex
/// at <c>i = 0</c> records <see cref="Predecessor"/> (<see langword="null"/> for a head insert) and the vertex
/// at <c>i &gt; 0</c> records the vertex at <c>i - 1</c> as its predecessor.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="First">The identity of the run's first vertex.</param>
/// <param name="Predecessor">The serialized identity of the element the run's first vertex was inserted after, or <see langword="null"/> for a head insert.</param>
/// <param name="Values">The run's element values in counter order; never empty.</param>
/// <remarks>
/// Tombstone status does not break runs: a run is a contiguous chain of vertices regardless of which of them
/// are tombstoned, because tombstones are carried in a separate set. A run packs the common case of a replica
/// typing consecutive characters into a single entry.
/// </remarks>
public sealed record RgaRunEntry<TValue>(DotState First, DotState? Predecessor, ImmutableArray<TValue> Values);
