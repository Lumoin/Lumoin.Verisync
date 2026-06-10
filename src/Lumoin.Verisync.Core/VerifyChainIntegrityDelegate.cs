using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Verifies the chain integrity of a <see cref="LogEntry{TOperation, TProof}"/> against the digest of
/// the preceding entry.
/// </summary>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="entry">The entry to verify.</param>
/// <param name="previousEntryDigest">
/// The digest of the preceding committed entry, or <see langword="null"/> for the genesis entry. This is
/// the authoritative value threaded forward, not the value the entry claims in its
/// <see cref="LogEntry{TOperation, TProof}.PreviousDigest"/> field.
/// </param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns><see langword="null"/> when integrity holds, or an error message when the chain is broken.</returns>
/// <remarks>
/// <para>
/// Comparing digest links alone is not enough for tamper evidence. A complete implementation must also:
/// recompute <see cref="LogEntry{TOperation, TProof}.Digest"/> from
/// <see cref="LogEntry{TOperation, TProof}.CanonicalBytes"/> rather than trusting the stored value;
/// verify that the canonical bytes are in fact the canonical encoding of the entry's index, the
/// authoritative previous digest, the typed <see cref="LogEntry{TOperation, TProof}.Operation"/>, and the
/// proofs — replay applies the <em>typed</em> operation, so without this correspondence check an entry's
/// operation can be swapped while its bytes and digest stay intact; and verify index continuity against
/// the previous entry. Omitting any of these reduces the log to detecting accidental corruption only.
/// </para>
/// <para>
/// For Merkle-tree backed logs the delegate may verify an inclusion proof instead of a sequential hash chain.
/// </para>
/// </remarks>
public delegate ValueTask<string?> VerifyChainIntegrityDelegate<TOperation, TProof>(
    LogEntry<TOperation, TProof> entry,
    ReadOnlyMemory<byte>? previousEntryDigest,
    CancellationToken cancellationToken);
