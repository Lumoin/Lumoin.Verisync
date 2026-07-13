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
    /// snapshot, anchors as base offsets or live dots, generation-fenced merging, and dotted removal on
    /// both axes — a live tombstone and a base-offset removal each mint an event on the shared counter
    /// axis that the stability frontier certifies — with base-materializing waterline compaction that
    /// requires an insert-quiescent frontier.
    /// </summary>
    /// <remarks>
    /// The strategy certifies both removal kinds. At compaction the retention taxonomy is four-way: an
    /// unstable vertex is retained, a stable visible vertex converts into the new base, a stable
    /// tombstoned vertex with an uncertified remove converts as pending-removed so members that disagree
    /// on the remove still materialize the identical base, and a stable tombstoned vertex with a
    /// certified remove is retained as a ghost exactly when a child is retained. A removed base entry is
    /// NOT reclaimed by this compaction: it is kept as the hidden ordering placeholder for its subtree
    /// with its remove-dots riding forward, because a frontier-local reclamation cannot be both
    /// frontier-pure and order-preserving; a certified removal only makes the slot RECLAIMABLE by a
    /// consensus-carried follow-on. Compaction — and therefore sealing an offset container — requires an
    /// insert-quiescent frontier that covers every vertex's insert-dot, an honest restriction of the
    /// base-materializing model and enforced fail-closed. Merging is fenced by the base-generation
    /// identity — the consensus-agreed frontier the base was materialized at, stamped only by
    /// base-changing compactions. The addressing type is <see cref="OffsetAddress"/>: the structural
    /// <see cref="OffsetAnchor"/> paired with the base generation it was read at, so a base address is
    /// served exactly by its generation and a stale one fails closed rather than being guessed. The
    /// strategy identifier does not change — offset.v2 is unreleased, and this is its first shipped shape.
    /// </remarks>
    public const string OffsetV2 = "verisync.sequence.offset.v2";

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
    /// <returns>A context carrying <see cref="OffsetV2"/>, the offset-anchored operations, and the compaction, anchor-translation, and certification delegates.</returns>
    /// <remarks>
    /// The offset strategy certifies a projection — both removal kinds are dotted events the frontier
    /// certifies — so <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}.CertifyProjection"/> is
    /// wired and an offset container can be sealed, at a frontier that is insert-quiescent — the
    /// base-materializing compaction fails closed below full insert stability. Its compaction takes the
    /// dotted checkpoint directly and asserts it against the strategy's own certified projection on both
    /// dot and value, with base elements carried under deterministic sentinel identities.
    /// </remarks>
    public static SequenceCrdtContext<OffsetAnchoredSequence<TValue>, TValue, OffsetAddress> CreateOffset<TValue>()
    {
        return new SequenceCrdtContext<OffsetAnchoredSequence<TValue>, TValue, OffsetAddress>
        {
            StrategyId = OffsetV2,
            Empty = OffsetAnchoredSequence<TValue>.Empty,
            InsertAtHead = static (sequence, value, replica) => sequence.InsertAtHead(value, replica),
            InsertAfter = static (sequence, after, value, replica) => sequence.InsertAfter(after, value, replica),
            Remove = static (sequence, anchor, replica) => sequence.Remove(anchor, replica),
            Merge = static (left, right) => left.Merge(right),
            Values = static sequence => sequence.Values,
            Compact = static (sequence, frontier, checkpoint) => sequence.Compact(frontier, checkpoint),
            TranslateAnchor = static (sequence, anchor) => sequence.TranslateAnchor(anchor),
            CausalContext = static sequence => sequence.CausalContext,
            CertifyProjection = static (sequence, frontier) => sequence.CertifiedProjection(frontier),
            UnstableInserts = static (sequence, frontier) => sequence.UnstableInserts(frontier)
        };
    }
}
