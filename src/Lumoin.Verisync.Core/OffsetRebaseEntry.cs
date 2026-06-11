namespace Lumoin.Verisync.Core;

/// <summary>
/// One base-offset-translation entry of a serialized <see cref="OffsetAnchoredSequence{TValue}"/>: a
/// previous-generation base offset paired with the offset it maps to in the current base. See
/// <see cref="OffsetAnchoredSequence{TValue}.TranslateAnchor"/>.
/// </summary>
/// <param name="PreviousOffset">The previous-generation base offset.</param>
/// <param name="CurrentOffset">The offset it maps to in the current base.</param>
/// <remarks>
/// Each compaction REPLACES this map with the generation's own offset shift, because a
/// previous-generation base anchor can no longer arrive once the stability line passed the previous
/// checkpoint, so only the latest shift is ever consulted.
/// </remarks>
public sealed record OffsetRebaseEntry(int PreviousOffset, int CurrentOffset);
