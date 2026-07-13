using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A coalesced run of translation entries for one replica in a serialized compactable <see cref="Rga{TValue}"/>:
/// the dropped dots <c>(DroppedReplica, c)</c> for every counter <c>c</c> in the inclusive range
/// <c>[FromCounter, ToCounter]</c>, all serving the identical retained <see cref="Target"/>.
/// </summary>
/// <param name="DroppedReplica">The dropped dots' replica raw identifier bytes.</param>
/// <param name="FromCounter">The first dropped counter in the span; at least one.</param>
/// <param name="ToCounter">The last dropped counter in the span; at least <see cref="FromCounter"/>.</param>
/// <param name="Target">The serialized identity of the retained vertex every dropped dot in the span translates to.</param>
/// <remarks>
/// The coalescing predicate is pinned: a span may cover exactly a maximal run of contiguous counters on one
/// replica axis where EVERY counter in the range is a dropped dot, every one maps to the IDENTICAL retained
/// target, and NONE of the covered dots is currently a vertex. A resurrected ghost-with-witness — a dropped dot
/// that is again a live vertex — serializes as a singleton <see cref="RgaTranslationEntry"/>, never inside a
/// span, because that per-dot entry is the load-bearing witness the stale-replay detector and invariant TC read;
/// over-coalescing would fabricate witnesses.
/// </remarks>
public sealed record RgaTranslationSpan(ImmutableArray<byte> DroppedReplica, int FromCounter, int ToCounter, DotState Target);
