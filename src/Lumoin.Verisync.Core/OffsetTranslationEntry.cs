namespace Lumoin.Verisync.Core;

/// <summary>
/// One dot-translation entry of a serialized <see cref="OffsetAnchoredSequence{TValue}"/>: a dot that
/// compaction dropped from the vertex set paired with the current-generation anchor that now serves
/// anchors expressed against it. See <see cref="OffsetAnchoredSequence{TValue}.TranslateAnchor"/>.
/// </summary>
/// <param name="Dropped">The serialized identity of the dropped dot.</param>
/// <param name="Target">The serialized current-generation anchor serving the dropped dot.</param>
/// <remarks>
/// The <see cref="Dropped"/> dot need not be absent from the vertices: a laggard merge can resurrect a
/// dropped tombstone while the entry remains, which is harmless because anchor lookups consult the
/// vertices first.
/// </remarks>
public sealed record OffsetTranslationEntry(DotState Dropped, OffsetAnchorState Target);
