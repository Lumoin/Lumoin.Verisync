using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Computes the digest of an entry's canonical bytes.
/// </summary>
/// <param name="canonicalBytes">The canonical byte representation of the entry content.</param>
/// <returns>The digest committing to those bytes.</returns>
/// <remarks>
/// The hash algorithm is a cryptographic concern supplied by the caller (for example a SHA-256 or BLAKE3
/// implementation from a hashing project). The core orchestrates the commit pipeline but does not embed a
/// hash implementation.
/// </remarks>
public delegate ReadOnlyMemory<byte> ComputeDigestDelegate(ReadOnlyMemory<byte> canonicalBytes);
