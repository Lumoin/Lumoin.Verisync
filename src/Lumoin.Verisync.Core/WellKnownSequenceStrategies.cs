namespace Lumoin.Verisync.Core;

/// <summary>
/// The registry of sequence strategies this library ships. A strategy identifier is part of a
/// document's replication contract — see <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}"/> —
/// so identifiers here are stable, versioned strings that never change meaning once published.
/// </summary>
public static class WellKnownSequenceStrategies
{
    /// <summary>
    /// The identifier of the <see cref="Rga{TValue}"/>-backed strategy: dot-identity anchors,
    /// Lamport-assigned insert identities, tombstoned removal, no compaction.
    /// </summary>
    public const string RgaV1 = "verisync.sequence.rga.v1";

    /// <summary>
    /// The identifier of the checkpoint-offset strategy: edits over an immutable consensus-agreed base
    /// snapshot, anchors as base offsets or live dots, generation-aligned merging. The compaction and
    /// anchor-translation delegates have landed on this strategy with the waterline-compaction work.
    /// </summary>
    public const string OffsetV1 = "verisync.sequence.offset.v1";

    /// <summary>
    /// The identifier of the compactable RGA strategy: the same dot-identity anchors, Lamport-assigned
    /// insert identities, and tombstoned removal as <see cref="RgaV1"/>, plus ghost-based waterline
    /// compaction and a run-length serialized state. A stable tombstone with any retained descendant
    /// persists as a ghost; only recursively childless stable tombstones drop, and the dots they leave are
    /// served from a translation map. This is a distinct replication contract from <see cref="RgaV1"/>; the
    /// two identifiers never mix.
    /// </summary>
    public const string RgaRleV1 = "verisync.sequence.rga-rle.v1";


    /// <summary>
    /// Creates the <see cref="Rga{TValue}"/>-backed strategy context.
    /// </summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <returns>A context carrying <see cref="RgaV1"/> and the RGA operations.</returns>
    public static SequenceCrdtContext<Rga<TValue>, TValue, Dot> CreateRga<TValue>()
    {
        return new SequenceCrdtContext<Rga<TValue>, TValue, Dot>
        {
            StrategyId = RgaV1,
            Empty = Rga<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor) => sequence.Remove(anchor),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values
        };
    }


    /// <summary>
    /// Creates the compactable RGA strategy context.
    /// </summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <returns>A context carrying <see cref="RgaRleV1"/>, the RGA operations, and the compaction and anchor-translation delegates.</returns>
    /// <remarks>
    /// The sequence type and the RGA operations are identical to <see cref="CreateRga{TValue}"/>; only the
    /// strategy identifier differs and the compaction seams are wired. Because the two identifiers name
    /// distinct replication contracts, a document pinned to one never merges state carried under the other.
    /// </remarks>
    public static SequenceCrdtContext<Rga<TValue>, TValue, Dot> CreateRgaRle<TValue>()
    {
        return new SequenceCrdtContext<Rga<TValue>, TValue, Dot>
        {
            StrategyId = RgaRleV1,
            Empty = Rga<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor) => sequence.Remove(anchor),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values,
            Compact = static (sequence, frontier, checkpoint) => sequence.Compact(frontier, checkpoint),
            TranslateAnchor = static (sequence, anchor) => sequence.TranslateAnchor(anchor)
        };
    }


    /// <summary>
    /// Creates the checkpoint-offset strategy context.
    /// </summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <returns>A context carrying <see cref="OffsetV1"/> and the offset-anchored operations.</returns>
    public static SequenceCrdtContext<OffsetAnchoredSequence<TValue>, TValue, OffsetAnchor> CreateOffset<TValue>()
    {
        return new SequenceCrdtContext<OffsetAnchoredSequence<TValue>, TValue, OffsetAnchor>
        {
            StrategyId = OffsetV1,
            Empty = OffsetAnchoredSequence<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor) => sequence.Remove(anchor),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values,
            Compact = static (sequence, frontier, checkpoint) => sequence.Compact(frontier, checkpoint),
            TranslateAnchor = static (sequence, anchor) => sequence.TranslateAnchor(anchor)
        };
    }
}
