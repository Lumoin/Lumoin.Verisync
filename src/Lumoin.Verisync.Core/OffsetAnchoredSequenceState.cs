using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The serializable state of an <see cref="OffsetAnchoredSequence{TValue}"/>: its agreed base snapshot
/// and removed base offsets, its causal context, its vertices and tombstones, and the two translation
/// maps a compacted generation carries. Obtain it with <see cref="OffsetAnchoredSequence{TValue}.ToState"/>
/// and reconstruct with <see cref="OffsetAnchoredSequence{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="Base">The agreed base snapshot this generation edits over.</param>
/// <param name="RemovedBaseOffsets">The offsets of removed base elements, retained for ordering.</param>
/// <param name="Context">The serialized causal context.</param>
/// <param name="Vertices">The serialized live vertices, visible and tombstoned alike.</param>
/// <param name="Tombstones">The serialized identities of the tombstoned live elements.</param>
/// <param name="CompactedDotAnchors">The dot-translation entries serving dots that compaction dropped.</param>
/// <param name="CompactedBaseOffsets">The base-offset-translation entries serving previous-generation base anchors.</param>
/// <remarks>
/// The two translation maps are empty for an uncompacted generation and carry the latest compaction's
/// shifts otherwise, so a compacted sequence round-trips through this shape without losing anchor
/// servability across generations.
/// </remarks>
public sealed record OffsetAnchoredSequenceState<TValue>(
    ImmutableArray<TValue> Base,
    ImmutableArray<int> RemovedBaseOffsets,
    VectorClockState Context,
    ImmutableArray<OffsetVertexEntry<TValue>> Vertices,
    ImmutableArray<DotState> Tombstones,
    ImmutableArray<OffsetTranslationEntry> CompactedDotAnchors,
    ImmutableArray<OffsetRebaseEntry> CompactedBaseOffsets);
