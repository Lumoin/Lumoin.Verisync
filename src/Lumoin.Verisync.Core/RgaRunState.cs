using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The run-length-encoded serializable state of a compactable <see cref="Rga{TValue}"/>: its causal context,
/// vertices grouped into maximal predecessor-chained runs, dotted tombstones coalesced into two-range spans with
/// an irregular fallback, and the translation entries — coalesced into spans where they permit — that serve dots
/// dropped by compaction. Obtain it with <see cref="Rga{TValue}.ToRunState"/> and reconstruct with
/// <see cref="Rga{TValue}.FromRunState"/>.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Context">The serialized causal context.</param>
/// <param name="Runs">The serialized vertex runs, visible and tombstoned alike, in deterministic order.</param>
/// <param name="TombstoneSpans">The serialized dotted tombstones a two-range span can express, in deterministic order.</param>
/// <param name="IrregularTombstones">The serialized tombstones a span cannot express — concurrent removes, legacy empties, or non-aligned dot arithmetic — in deterministic order.</param>
/// <param name="Translations">The serialized singleton translation entries: each dropped dot paired with the retained vertex serving it.</param>
/// <param name="TranslationSpans">The serialized translation spans: maximal contiguous runs of dropped dots sharing one retained target.</param>
/// <remarks>
/// This state shape carries the translation map that the flat <see cref="RgaState{TValue}"/> cannot, so a
/// compacted array round-trips through it without losing anchor servability. The two state shapes belong to the
/// two distinct replication contracts and never mix. Deterministic order is pinned: spans by (target replica,
/// target from), irregulars by target, translations by dropped, translation spans by (dropped replica, from).
/// </remarks>
public sealed record RgaRunState<TValue>(
    VectorClockState Context,
    ImmutableArray<RgaRunEntry<TValue>> Runs,
    ImmutableArray<RgaTombstoneSpan> TombstoneSpans,
    ImmutableArray<RgaConcurrentTombstone> IrregularTombstones,
    ImmutableArray<RgaTranslationEntry> Translations,
    ImmutableArray<RgaTranslationSpan> TranslationSpans);
