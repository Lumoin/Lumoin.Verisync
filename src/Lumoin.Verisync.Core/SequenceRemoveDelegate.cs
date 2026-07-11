namespace Lumoin.Verisync.Core;

/// <summary>
/// Removes the element identified by an anchor from a sequence CRDT.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type elements are referred to by.</typeparam>
/// <param name="sequence">The sequence to remove from; immutable, never modified.</param>
/// <param name="anchor">The anchor of the element to remove.</param>
/// <param name="replica">The replica performing the removal; strategies with dotted removes mint the remove event on its axis, strategies whose removes are not yet dotted ignore it.</param>
/// <returns>The new sequence.</returns>
public delegate TSequence SequenceRemoveDelegate<TSequence, in TAnchor>(
    TSequence sequence,
    TAnchor anchor,
    ReplicaId replica);
