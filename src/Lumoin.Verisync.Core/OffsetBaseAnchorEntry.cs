namespace Lumoin.Verisync.Core;

/// <summary>
/// One base-offset-translation entry of a serialized <see cref="OffsetAnchoredSequence{TValue}"/>: a
/// previous-generation base offset paired with the current-generation anchor that serves it — the
/// shifted base position when the entry survived, the gap anchor (possibly the head) when reclamation
/// dropped it. See <see cref="OffsetAnchoredSequence{TValue}.TranslateAnchor"/>.
/// </summary>
/// <param name="PreviousOffset">The previous-generation base offset.</param>
/// <param name="Target">The serialized current-generation anchor serving the previous offset.</param>
/// <remarks>
/// Each compaction REPLACES this map with the generation's own translation, because a
/// previous-generation base anchor can no longer arrive once the stability line passed the previous
/// checkpoint, so only the latest translation is ever consulted.
/// </remarks>
public sealed record OffsetBaseAnchorEntry(int PreviousOffset, OffsetAnchorState Target);
