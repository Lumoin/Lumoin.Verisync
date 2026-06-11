namespace Lumoin.Verisync.Core;

/// <summary>
/// Inserts a value at the head of a sequence CRDT, returning the new sequence and the anchor assigned
/// to the inserted element.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type elements are referred to by.</typeparam>
/// <param name="sequence">The sequence to insert into; immutable, never modified.</param>
/// <param name="value">The value to insert.</param>
/// <param name="replica">The replica performing the edit.</param>
/// <returns>The new sequence and the anchor of the inserted element.</returns>
public delegate (TSequence Sequence, TAnchor InsertedId) SequenceInsertAtHeadDelegate<TSequence, in TValue, TAnchor>(
    TSequence sequence,
    TValue value,
    ReplicaId replica);
