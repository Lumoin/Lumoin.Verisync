using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Folds a committed entry and its verified proofs into the cryptographic accumulator, producing the
/// next accumulator value.
/// </summary>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TAccumulator">The accumulator type.</typeparam>
/// <param name="entry">The entry whose operation and proofs are being folded.</param>
/// <param name="currentAccumulator">The accumulator value before this entry is folded.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The accumulator value after folding this entry.</returns>
/// <remarks>
/// This delegate is writer-only — the reader does not fold. The fold realisation is application-defined:
/// a Nova folding scheme, a Merkle accumulator, a chained hash, or no accumulation at all. It lets an
/// external verifier later check, in constant size, that the chain's history is a valid sequence.
/// </remarks>
public delegate ValueTask<TAccumulator> FoldStepDelegate<TOperation, TProof, TAccumulator>(
    LogEntry<TOperation, TProof> entry,
    TAccumulator currentAccumulator,
    CancellationToken cancellationToken);
