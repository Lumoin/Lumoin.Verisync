using System.Collections.Immutable;

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
    /// Lamport-assigned insert identities, and dotted removal — each remove is an event on the shared
    /// counter axis that the stability frontier certifies — with no compaction.
    /// </summary>
    public const string RgaV2 = "verisync.sequence.rga.v2";

    /// <summary>
    /// The identifier of the checkpoint-offset strategy: edits over an immutable consensus-agreed base
    /// snapshot, anchors as base offsets or live dots, generation-aligned merging. The compaction and
    /// anchor-translation delegates have landed on this strategy with the waterline-compaction work.
    /// </summary>
    /// <remarks>
    /// The offset strategy's removes leave no causal footprint, so no frontier can certify them and
    /// waterline compaction can resurrect a remove a member never observed — a known defect of this
    /// strategy, fixed by a follow-on design with its own certification for base-offset removal. Until
    /// then prefer <see cref="RgaRleV2"/> where certified removal matters.
    /// </remarks>
    public const string OffsetV1 = "verisync.sequence.offset.v1";

    /// <summary>
    /// The identifier of the compactable RGA strategy: the same dot-identity anchors, Lamport-assigned
    /// insert identities, and dotted removal as <see cref="RgaV2"/>, plus ghost-based waterline
    /// compaction and a run-length serialized state. A tombstone drops only when its insert and a
    /// remove-dot are both certified at the frontier; a stable tombstone with any retained descendant, or
    /// whose remove is not yet certified, persists as a ghost, and the dots a drop leaves behind are
    /// served from a translation map. This is a distinct replication contract from <see cref="RgaV2"/>; the
    /// two identifiers never mix.
    /// </summary>
    public const string RgaRleV2 = "verisync.sequence.rga-rle.v2";


    /// <summary>
    /// Creates the <see cref="Rga{TValue}"/>-backed strategy context.
    /// </summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <returns>A context carrying <see cref="RgaV2"/> and the RGA operations.</returns>
    public static SequenceCrdtContext<Rga<TValue>, TValue, Dot> CreateRga<TValue>()
    {
        return new SequenceCrdtContext<Rga<TValue>, TValue, Dot>
        {
            StrategyId = RgaV2,
            Empty = Rga<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor, replica) => sequence.Remove(anchor, replica),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values,
            CausalContext = static sequence => sequence.CausalContext
        };
    }


    /// <summary>
    /// Creates the compactable RGA strategy context.
    /// </summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <returns>A context carrying <see cref="RgaRleV2"/>, the RGA operations, and the compaction and anchor-translation delegates.</returns>
    /// <remarks>
    /// The sequence type and the RGA operations are identical to <see cref="CreateRga{TValue}"/>; only the
    /// strategy identifier differs and the compaction seams are wired. Because the two identifiers name
    /// distinct replication contracts, a document pinned to one never merges state carried under the other.
    /// </remarks>
    public static SequenceCrdtContext<Rga<TValue>, TValue, Dot> CreateRgaRle<TValue>()
    {
        return new SequenceCrdtContext<Rga<TValue>, TValue, Dot>
        {
            StrategyId = RgaRleV2,
            Empty = Rga<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor, replica) => sequence.Remove(anchor, replica),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values,
            Compact = static (sequence, frontier, checkpoint) => sequence.Compact(frontier, checkpoint),
            TranslateAnchor = static (sequence, anchor) => sequence.TranslateAnchor(anchor),
            CausalContext = static sequence => sequence.CausalContext,
            CertifyProjection = static (sequence, frontier) => sequence.CertifiedProjection(frontier)
        };
    }


    /// <summary>
    /// Creates the checkpoint-offset strategy context.
    /// </summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <returns>A context carrying <see cref="OffsetV1"/> and the offset-anchored operations.</returns>
    /// <remarks>
    /// The offset strategy does not certify a projection — its removes leave no causal footprint — so its
    /// <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}.CertifyProjection"/> stays null and an
    /// offset container cannot be sealed. Its compaction wiring projects the dotted checkpoint down to values
    /// for the byte-identical <see cref="OffsetAnchoredSequence{TValue}.Compact"/>, deliberately discarding the
    /// dots: real offset dot certification is the slice-3 parity work.
    /// </remarks>
    public static SequenceCrdtContext<OffsetAnchoredSequence<TValue>, TValue, OffsetAnchor> CreateOffset<TValue>()
    {
        return new SequenceCrdtContext<OffsetAnchoredSequence<TValue>, TValue, OffsetAnchor>
        {
            StrategyId = OffsetV1,
            Empty = OffsetAnchoredSequence<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor, replica) => sequence.Remove(anchor),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values,
            Compact = static (sequence, frontier, checkpoint) => sequence.Compact(frontier, ProjectValues(checkpoint)),
            TranslateAnchor = static (sequence, anchor) => sequence.TranslateAnchor(anchor)
        };
    }


    private static ImmutableArray<TValue> ProjectValues<TValue>(ImmutableArray<SequenceCheckpointEntry<TValue>> checkpoint)
    {
        //The offset strategy's integrity check is value-typed, so the dotted checkpoint's dots are dropped here;
        //the slice-3 parity work owns offset dot certification.
        var values = new TValue[checkpoint.Length];
        for(int i = 0; i < checkpoint.Length; i++)
        {
            values[i] = checkpoint[i].Value;
        }

        return ImmutableArray.Create(values);
    }
}
