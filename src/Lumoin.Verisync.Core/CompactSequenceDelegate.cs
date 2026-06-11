using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Compacts a sequence below the waterline: state that is both captured by the agreed checkpoint (the
/// WHAT) and below the group stability frontier (the WHEN) is reclaimed, without changing the visible
/// sequence or breaking anchors above the waterline.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="sequence">The sequence to compact; immutable, never modified.</param>
/// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>. Identities at or below it can never again be referenced by any member.</param>
/// <param name="checkpoint">The agreed checkpoint content the compacted region collapses into.</param>
/// <returns>The compacted sequence.</returns>
/// <remarks>
/// The contract is enforced by the shared law harness, not trusted: compaction never changes the
/// visible values, is idempotent at a given frontier, commutes with merges whose operand is at or
/// above the frontier, and leaves every anchor above the waterline resolving identically. Anchors
/// below the waterline are served through the strategy's
/// <see cref="TranslateAnchorDelegate{TSequence, TAnchor}"/> for as long as the strategy's transition window
/// requires. A strategy without compaction simply leaves this slot null on its context.
/// </remarks>
public delegate TSequence CompactSequenceDelegate<TSequence, TValue>(
    TSequence sequence,
    VectorClock stabilityFrontier,
    ImmutableArray<TValue> checkpoint);
