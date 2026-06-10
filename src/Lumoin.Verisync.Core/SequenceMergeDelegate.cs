namespace Lumoin.Verisync.Core;

/// <summary>
/// Merges two sequence CRDT states into their join.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT type.</typeparam>
/// <param name="left">The first operand; immutable, never modified.</param>
/// <param name="right">The second operand; immutable, never modified.</param>
/// <returns>The joined sequence.</returns>
/// <remarks>
/// The implementation must be a join-semilattice merge — commutative, associative, and idempotent —
/// over states produced by the same strategy. These laws are not negotiable per strategy; they are
/// what makes replicas converge regardless of delivery order, and every strategy is expected to pass
/// the shared law tests.
/// </remarks>
public delegate TSequence SequenceMergeDelegate<TSequence>(
    TSequence left,
    TSequence right);
