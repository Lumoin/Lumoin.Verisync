using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The run-length-encoded serializable state of a compactable <see cref="Rga{TValue}"/>: its causal
/// context, vertices grouped into maximal predecessor-chained runs, tombstones coalesced into per-replica
/// counter spans, and the translation entries that serve dots dropped by compaction. Obtain it with
/// <see cref="Rga{TValue}.ToRunState"/> and reconstruct with <see cref="Rga{TValue}.FromRunState"/>.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Context">The serialized causal context.</param>
/// <param name="Runs">The serialized vertex runs, visible and tombstoned alike, in deterministic order.</param>
/// <param name="TombstoneSpans">The serialized tombstone counter spans, one set of maximal spans per replica.</param>
/// <param name="Translations">The serialized translation entries: each dropped dot paired with the retained vertex serving it.</param>
/// <remarks>
/// This state shape carries the translation map that the flat <see cref="RgaState{TValue}"/> cannot, so a
/// compacted array round-trips through it without losing anchor servability. The two state shapes belong to
/// the two distinct replication contracts and never mix.
/// </remarks>
public sealed record RgaRunState<TValue>(VectorClockState Context, ImmutableArray<RgaRunEntry<TValue>> Runs, ImmutableArray<RgaTombstoneSpan> TombstoneSpans, ImmutableArray<RgaTranslationEntry> Translations);
