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
/// For Merkle-tree backed logs the delegate may verify an inclusion proof instead of a sequential hash chain.
/// </remarks>
public delegate ValueTask<string?> VerifyChainIntegrityDelegate<TOperation, TProof>(
    LogEntry<TOperation, TProof> entry,
    ReadOnlyMemory<byte>? previousEntryDigest,
    CancellationToken cancellationToken);
