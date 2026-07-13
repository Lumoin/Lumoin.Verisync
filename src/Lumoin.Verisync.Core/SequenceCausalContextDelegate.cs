namespace Lumoin.Verisync.Core;

/// <summary>
/// Reads the causal context of a sequence CRDT for gossip digests and stability frontiers.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT type.</typeparam>
/// <param name="sequence">The sequence to read; immutable, never modified.</param>
/// <returns>The sequence's causal context.</returns>
public delegate VectorClock SequenceCausalContextDelegate<in TSequence>(TSequence sequence);
