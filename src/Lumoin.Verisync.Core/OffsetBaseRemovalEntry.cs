using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One serialized base-offset removal of an <see cref="OffsetAnchoredSequence{TValue}"/>: the removed
/// base element's offset and the dotted remove events that hide it.
/// </summary>
/// <param name="Offset">The removed base element's offset in the agreed base snapshot.</param>
/// <param name="RemoveDots">The serialized remove events, one per replica that concurrently removed the offset. Empty for a base removal loaded from pre-dotted state, which is retained forever because no remove event exists to certify.</param>
public sealed record OffsetBaseRemovalEntry(int Offset, ImmutableArray<DotState> RemoveDots);
