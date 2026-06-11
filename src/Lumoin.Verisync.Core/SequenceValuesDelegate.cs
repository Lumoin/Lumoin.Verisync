using System.Collections.Generic;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Materializes the visible values of a sequence CRDT in sequence order.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="sequence">The sequence to read.</param>
/// <returns>The visible values in order.</returns>
public delegate IReadOnlyList<TValue> SequenceValuesDelegate<in TSequence, out TValue>(
    TSequence sequence);
