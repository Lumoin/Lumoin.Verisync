using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Compacts a sequence below the waterline: state that is both captured by the agreed checkpoint (the WHAT) and
/// below the group stability frontier (the WHEN) is reclaimed, without changing the visible sequence or breaking
/// anchors above the waterline.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="sequence">The sequence to compact; immutable, never modified.</param>
/// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>. Identities at or below it can never again be referenced by any member.</param>
/// <param name="checkpoint">The agreed checkpoint the compacted region collapses into: the certified dotted projection at the frontier.</param>
/// <returns>The compacted sequence.</returns>
/// <remarks>
/// <para>
/// The checkpoint is the CERTIFIED PROJECTION at the frontier, not a client-supplied summary: the strategy
/// derives the same projection itself and fails closed if the passed checkpoint diverges from it. This is an
/// integrity assertion, not an intersection — the container seals the certified projection through consensus
/// and passes exactly that back here, so a mismatch means genuine divergence or a forged commitment, never an
/// honest edge. State below the frontier whose remove is certified is reclaimed, or — on the offset base axis,
/// where a frontier-local reclamation can be neither frontier-pure nor order-preserving — made RECLAIMABLE by a
/// consensus-carried follow-on.
/// </para>
/// <para>
/// The translation that serves dropped anchors reclaims payload, not identity. DOT-keyed translations compose
/// PERMANENTLY in both strategies — one entry per dropped dot, coalesced into spans in the run shape, served
/// through the strategy's <see cref="TranslateAnchorDelegate{TSequence, TAnchor}"/> for the life of the
/// sequence. The offset BASE-OFFSET map is different: a base-changing compaction re-materializes every base
/// position, so that map is REPLACED each generation and serves exactly the one-generation window the stability
/// rule permits, not the life of the sequence. A strategy without compaction leaves this slot null on its
/// context. The contract is enforced by the shared law harness, not trusted: compaction never changes the
/// visible values, is idempotent at a given frontier, commutes with merges whose operand is at or above the
/// frontier, and leaves every anchor above the waterline resolving identically. A strategy may additionally
/// impose its own compaction preconditions and fail closed: the base-materializing offset strategy requires an
/// insert-quiescent frontier and advertises that precondition by wiring the context's
/// <see cref="SequenceUnstableInsertsDelegate{TSequence}"/> probe.
/// </para>
/// </remarks>
public delegate TSequence CompactSequenceDelegate<TSequence, TValue>(
    TSequence sequence,
    VectorClock stabilityFrontier,
    ImmutableArray<SequenceCheckpointEntry<TValue>> checkpoint);
