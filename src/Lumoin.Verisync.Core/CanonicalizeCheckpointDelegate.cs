using System;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the deterministic canonical byte encoding of a dotted checkpoint, the input to the checkpoint
/// commitment digest.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <param name="entries">The checkpoint entries in projection order, each a serialized dot paired with its value.</param>
/// <returns>The canonical bytes.</returns>
/// <remarks>
/// The encoding must be deterministic and must cover BOTH the dot and the value of every entry: the same
/// checkpoint must always produce the same bytes, on every replica, or replicas computing the commitment
/// independently disagree on what consensus decided. Covering the dots — not only the values — makes the digest
/// unambiguous under repeated values and pins WHICH elements the projection captured. The serialization choice
/// lives at the composition root, outside the core, like every other canonicalization in this library.
/// </remarks>
public delegate ReadOnlyMemory<byte> CanonicalizeCheckpointDelegate<TValue>(ImmutableArray<SequenceCheckpointEntry<TValue>> entries);
