using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Applies a classified log entry to the current log state, producing a new log state or an error.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="classification">The classification of the entry.</param>
/// <param name="currentState">The current log state before this entry is applied.</param>
/// <param name="entry">The entry to apply.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The new log state and <see langword="null"/> on success, or the unchanged state and an error message on failure.</returns>
/// <remarks>
/// This is the single dispatch point for all state transitions. The caller implements it as a pattern
/// match over <paramref name="classification"/> and the <see cref="LogState{TState}"/> variant of
/// <paramref name="currentState"/>, enforcing correct lifecycle transitions.
/// </remarks>
public delegate ValueTask<(LogState<TState> State, string? Error)> ApplyDelegate<TState, TOperation, TProof>(
    LogEntryClassification classification,
    LogState<TState> currentState,
    LogEntry<TOperation, TProof> entry,
    CancellationToken cancellationToken);
