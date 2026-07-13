namespace Lumoin.Verisync.Core;

/// <summary>
/// The pluggable sequence-strategy seam: a bundle of delegates realising one sequence CRDT design —
/// its addressing model, its merge, and its ordering — behind which containers such as
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/> operate without knowing the strategy.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type elements are referred to by.</typeparam>
/// <remarks>
/// <para>
/// A strategy is part of a document's <em>replication contract</em>, not a per-replica preference: two
/// replicas running different strategies over the same document do not degrade, they silently diverge.
/// <see cref="StrategyId"/> names the strategy so the contract can be pinned — record it in the
/// document's genesis entry or first seal, and fail closed on mismatch. Containers enforce the local
/// half of this: merging states carried under different strategy identifiers throws.
/// </para>
/// <para>
/// The laws are not pluggable. Whatever the strategy, <see cref="Merge"/> must be a join-semilattice
/// merge (commutative, associative, idempotent) and <see cref="InsertAfter"/> must preserve local
/// insertion intention; every registered strategy is expected to pass the shared law tests. What
/// <em>is</em> selectable per strategy is everything else: anchor representation, tie-breaking,
/// tombstone handling, and — with the waterline-compaction work — how state below a sealed checkpoint
/// is reclaimed. The compaction and anchor-translation delegates have landed on this context as
/// <see cref="Compact"/> and <see cref="TranslateAnchor"/>; a strategy that does not compact leaves
/// both null.
/// </para>
/// <para>
/// Registered strategies live in <see cref="WellKnownSequenceStrategies"/>.
/// </para>
/// </remarks>
public sealed class SequenceCrdtContext<TSequence, TValue, TAnchor>
{
    /// <summary>
    /// The stable identifier of this strategy, pinned in the document's replication contract. Two
    /// contexts with the same identifier must be behaviourally identical.
    /// </summary>
    public required string StrategyId { get; init; }

    /// <summary>The empty sequence this strategy starts from.</summary>
    public required TSequence Empty { get; init; }

    /// <summary>Inserts a value at the head of the sequence.</summary>
    public required SequenceInsertAtHeadDelegate<TSequence, TValue, TAnchor> InsertAtHead { get; init; }

    /// <summary>Inserts a value immediately after an anchored element.</summary>
    public required SequenceInsertAfterDelegate<TSequence, TValue, TAnchor> InsertAfter { get; init; }

    /// <summary>Removes an anchored element.</summary>
    public required SequenceRemoveDelegate<TSequence, TAnchor> Remove { get; init; }

    /// <summary>Merges two sequence states; must satisfy the join-semilattice laws.</summary>
    public required SequenceMergeDelegate<TSequence> Merge { get; init; }

    /// <summary>Materializes the visible values in sequence order.</summary>
    public required SequenceValuesDelegate<TSequence, TValue> Values { get; init; }

    /// <summary>
    /// Compacts state below the waterline, or <see langword="null"/> when this strategy does not
    /// compact. Subject to the compaction laws of the shared harness.
    /// </summary>
    public CompactSequenceDelegate<TSequence, TValue>? Compact { get; init; }

    /// <summary>
    /// Translates anchors that may refer to compacted state, or <see langword="null"/> when this
    /// strategy's anchors survive compaction unchanged.
    /// </summary>
    public TranslateAnchorDelegate<TSequence, TAnchor>? TranslateAnchor { get; init; }

    /// <summary>
    /// Reads the sequence's causal context for gossip digests and stability frontiers, or
    /// <see langword="null"/> when the strategy does not expose a remove-aware causal context.
    /// </summary>
    public SequenceCausalContextDelegate<TSequence>? CausalContext { get; init; }

    /// <summary>
    /// Produces the certified dotted projection at a frontier — the checkpoint a container seals — or
    /// <see langword="null"/> when the strategy cannot certify a projection and therefore cannot be sealed.
    /// </summary>
    public CertifySequenceProjectionDelegate<TSequence, TValue>? CertifyProjection { get; init; }

    /// <summary>
    /// Enumerates the vertex insert-dots a frontier does not cover — the strategy's insert-quiescence
    /// probe — or <see langword="null"/> when the strategy's compaction imposes no insert-quiescence
    /// precondition. A null slot is the honest statement that sealing this strategy is not group-quiescent;
    /// hosts branch on its presence to learn whether a seal must be driven to quiescence at all.
    /// </summary>
    public SequenceUnstableInsertsDelegate<TSequence>? UnstableInserts { get; init; }
}
