namespace Lumoin.Verisync.Core;

/// <summary>
/// One translation entry of a serialized compactable <see cref="Rga{TValue}"/>: a dot that compaction dropped
/// paired with the retained vertex that now serves anchors expressed against it. See
/// <see cref="Rga{TValue}.TranslateAnchor"/>.
/// </summary>
/// <param name="Dropped">The serialized identity of the dropped dot.</param>
/// <param name="Target">The serialized identity of the retained vertex serving the dropped dot.</param>
/// <remarks>
/// The <see cref="Target"/> is always a vertex of the state — a dangling target would break servability. The
/// <see cref="Dropped"/> dot need not be absent from the vertices: a laggard merge can resurrect a dropped
/// tombstone while the entry remains, which is harmless because anchor lookups consult the vertices first.
/// </remarks>
public sealed record RgaTranslationEntry(DotState Dropped, DotState Target);
