using System;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the canonical byte representation of a log entry's content, over which its digest is computed.
/// </summary>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="index">The entry's index.</param>
/// <param name="previousDigest">The digest of the preceding entry, or <see langword="null"/> for genesis.</param>
/// <param name="operation">The operation, or <see langword="null"/> for entries that carry no mutation.</param>
/// <param name="proofs">The entry's proofs.</param>
/// <returns>The deterministic canonical bytes of the entry content.</returns>
/// <remarks>
/// Canonicalization is a serialization concern and lives outside the core (in a JSON or CBOR project, or
/// any caller-supplied encoder). The encoding must be deterministic so the same logical content always
/// yields the same bytes; otherwise digest verification fails non-deterministically across verifiers.
/// </remarks>
public delegate ReadOnlyMemory<byte> CanonicalizeEntryDelegate<TOperation, TProof>(
    ulong index,
    ReadOnlyMemory<byte>? previousDigest,
    TOperation? operation,
    ImmutableArray<TProof> proofs);
