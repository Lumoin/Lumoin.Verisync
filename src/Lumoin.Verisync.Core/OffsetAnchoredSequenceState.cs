using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of an <see cref="OffsetAnchoredSequence{TValue}"/>: its agreed base snapshot
/// with the generation identity it was materialized at, its dotted base removals, its causal context,
/// its vertices and dotted tombstones, and the two translation maps a compacted generation carries.
/// Obtain it with <see cref="OffsetAnchoredSequence{TValue}.ToState"/> and reconstruct with
/// <see cref="OffsetAnchoredSequence{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Base">The agreed base snapshot this generation edits over.</param>
/// <param name="BaseFrontier">The serialized generation identity: the consensus-agreed stability frontier the base was last materialized at, empty for a generation that has never base-changed.</param>
/// <param name="BaseGeneration">The base-generation ordinal counting the base-changing compactions this generation descends from: zero exactly at the empty frontier, stamped together with the frontier so honest members at one frontier always agree on it.</param>
/// <param name="RemovedBaseOffsets">The removed base entries, each an offset retained for ordering paired with the dotted remove events that hide it.</param>
/// <param name="Context">The serialized causal context.</param>
/// <param name="Vertices">The serialized live vertices, visible and tombstoned alike.</param>
/// <param name="Tombstones">The serialized tombstones, each a removed live element's identity paired with the dotted remove events that hide it.</param>
/// <param name="CompactedDotAnchors">The dot-translation entries serving dots that compaction dropped.</param>
/// <param name="CompactedBaseOffsets">The base-offset-translation entries serving previous-generation base anchors, anchor-typed so a reclaimed offset CAN translate to a gap anchor (possibly the head) rather than only a shifted position — the wire shape a consensus-carried reclamation follow-on emits; this compaction defers reclamation and only ever shifts.</param>
/// <remarks>
/// The two translation maps are empty for an uncompacted generation and carry the latest compaction's
/// shifts otherwise, so a compacted sequence round-trips through this shape without losing anchor
/// servability across generations.
/// </remarks>
public sealed record OffsetAnchoredSequenceState<TValue>(
    ImmutableArray<TValue> Base,
    VectorClockState BaseFrontier,
    int BaseGeneration,
    ImmutableArray<OffsetBaseRemovalEntry> RemovedBaseOffsets,
    VectorClockState Context,
    ImmutableArray<OffsetVertexEntry<TValue>> Vertices,
    ImmutableArray<OffsetTombstoneEntry> Tombstones,
    ImmutableArray<OffsetTranslationEntry> CompactedDotAnchors,
    ImmutableArray<OffsetBaseAnchorEntry> CompactedBaseOffsets);
