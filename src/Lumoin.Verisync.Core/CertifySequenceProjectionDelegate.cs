using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the certified dotted projection of a sequence CRDT at a stability frontier: the checkpoint a
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}"/> commits to when it seals.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="sequence">The sequence to project; immutable, never modified.</param>
/// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>.</param>
/// <returns>
/// The visible order filtered to stable insert-dots, excluding elements whose remove is certified at the
/// frontier, each paired with its serialized identity.
/// </returns>
/// <remarks>
/// The projection is a pure function of the frontier for every member whose context dominates it, so two
/// honest members at the same frontier certify a byte-identical checkpoint — the determinism the seal protocol
/// relies on. A strategy that cannot certify a projection (one whose removes leave no causal footprint) leaves
/// this slot null on its context and cannot seal.
/// </remarks>
public delegate ImmutableArray<SequenceCheckpointEntry<TValue>> CertifySequenceProjectionDelegate<TSequence, TValue>(
    TSequence sequence,
    VectorClock stabilityFrontier);
