namespace Lumoin.Verisync.Core;

/// <summary>
/// Inserts a value immediately after the element identified by an anchor, returning the new sequence
/// and the anchor assigned to the inserted element.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type elements are referred to by.</typeparam>
/// <param name="sequence">The sequence to insert into; immutable, never modified.</param>
/// <param name="after">The anchor of the element to insert after.</param>
/// <param name="value">The value to insert.</param>
/// <param name="replica">The replica performing the edit.</param>
/// <returns>The new sequence and the anchor of the inserted element.</returns>
/// <remarks>
/// The intention-preservation contract: in the inserting replica's local view the new element appears
/// immediately after <paramref name="after"/>. Strategies differ in how they realise this under
/// concurrency, but a strategy that cannot honour it locally is not a valid sequence strategy.
/// </remarks>
public delegate (TSequence Sequence, TAnchor InsertedId) SequenceInsertAfterDelegate<TSequence, in TValue, TAnchor>(
    TSequence sequence,
    TAnchor after,
    TValue value,
    ReplicaId replica);
