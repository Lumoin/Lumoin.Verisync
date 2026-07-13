using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A serialized tombstone of a compactable <see cref="Rga{TValue}"/> that a two-range
/// <see cref="RgaTombstoneSpan"/> cannot express: a removed element's identity paired with its remove events
/// listed one by one.
/// </summary>
/// <param name="Target">The serialized identity of the removed element.</param>
/// <param name="RemoveDots">
/// The serialized remove events hiding the target. Empty for a legacy tombstone loaded from pre-dotted state,
/// which is retained forever because no remove event exists to certify.
/// </param>
/// <remarks>
/// A tombstone lands here rather than in a span when its remove-dots cannot be packed as a single aligned
/// counter range: several concurrent removes of one target, a legacy tombstone with no remove-dot at all, or
/// dot arithmetic that does not advance in lockstep with the target counters. A single-replica contiguous
/// deletion pass still coalesces to one <see cref="RgaTombstoneSpan"/>; this is the fallback for everything
/// else.
/// </remarks>
public sealed record RgaConcurrentTombstone(DotState Target, ImmutableArray<DotState> RemoveDots);
