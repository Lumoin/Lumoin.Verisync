using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One serialized tombstone of a <see cref="Rga{TValue}"/>: the removed element's identity and the
/// dotted remove events that hide it.
/// </summary>
/// <param name="Target">The serialized identity of the removed element.</param>
/// <param name="RemoveDots">The serialized remove events, one per replica that concurrently removed the target. Empty for a tombstone loaded from pre-dotted state, which is retained forever because no remove event exists to certify.</param>
public sealed record RgaTombstoneEntry(DotState Target, ImmutableArray<DotState> RemoveDots);
