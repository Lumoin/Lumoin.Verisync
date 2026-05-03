using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Validates the proofs carried by a <see cref="LogEntry{TOperation, TProof}"/>.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TContext">The caller-defined proof validation context type.</typeparam>
/// <param name="entry">The entry whose proofs are to be validated.</param>
/// <param name="currentState">The log state before this entry is applied.</param>
/// <param name="context">The caller-supplied validation context carrying trust anchors, time, and revocation inputs.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns><see langword="null"/> when validation succeeds, or an error message when it fails.</returns>
/// <remarks>
/// The delegate interprets the entry's proofs under whatever authorisation model the application uses —
/// signature checking, threshold quorum, PIC CAT validation, KERI event verification — and composes the
/// threshold logic itself. The infrastructure runs the delegate and acts on its result.
/// </remarks>
public delegate ValueTask<string?> ValidateProofDelegate<TState, TOperation, TProof, TContext>(
    LogEntry<TOperation, TProof> entry,
    LogState<TState> currentState,
    TContext context,
    CancellationToken cancellationToken);
