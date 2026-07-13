using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Enumerates the vertex insert-dots a stability frontier does not cover, in deterministic
/// (Replica, Counter) order — the strategy's insert-quiescence probe.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <param name="sequence">The sequence to probe; immutable, never modified.</param>
/// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>.</param>
/// <returns>
/// The uncovered vertex insert-dots ascending by (Replica, Counter), or an empty array when the frontier
/// covers every vertex's insert-dot.
/// </returns>
/// <remarks>
/// A strategy whose compaction imposes no insert-quiescence precondition leaves this slot null on its
/// context; a strategy that wires it must return empty exactly when its compaction's quiescence
/// precondition holds at that frontier — for the shipped offset strategy this is an identity, not a
/// promise, because the probe and the compaction guard run the one shared scan.
/// </remarks>
public delegate ImmutableArray<Dot> SequenceUnstableInsertsDelegate<TSequence>(
    TSequence sequence,
    VectorClock stabilityFrontier);
