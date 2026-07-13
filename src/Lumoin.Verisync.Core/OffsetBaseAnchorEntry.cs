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
/// Each compaction REPLACES this map with the generation's own translation, so a key serves exactly one
/// generation, the immediately preceding one, and a target is always a current-generation bare anchor. An
/// incoming base address carries the generation it was read at, so the translation seam serves it through
/// this map only at the one generation these keys number, and fails closed otherwise.
/// </remarks>
public sealed record OffsetBaseAnchorEntry(int PreviousOffset, OffsetAnchorState Target);
