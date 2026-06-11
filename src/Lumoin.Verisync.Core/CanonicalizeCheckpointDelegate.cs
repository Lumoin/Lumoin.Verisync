using System;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the deterministic canonical byte encoding of a checkpoint snapshot, the input to the
/// checkpoint commitment digest.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="values">The snapshot values in sequence order.</param>
/// <returns>The canonical bytes.</returns>
/// <remarks>
/// The encoding must be deterministic: the same snapshot must always produce the same bytes, on every
/// replica, or replicas computing the commitment independently disagree on what consensus decided.
/// The serialization choice lives at the composition root, outside the core, like every other
/// canonicalization in this library.
/// </remarks>
public delegate ReadOnlyMemory<byte> CanonicalizeCheckpointDelegate<TValue>(ImmutableArray<TValue> values);
