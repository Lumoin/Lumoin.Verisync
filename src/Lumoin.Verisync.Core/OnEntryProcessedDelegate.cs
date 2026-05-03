using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Called after each entry is successfully processed during replay, delivering the result to an attached listener.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="result">The result produced for the processed entry.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A <see cref="ValueTask"/> that completes when the listener is done.</returns>
/// <remarks>
/// Use this to drive audit sinks, notifications, or other downstream consumers that react to each verified
/// state transition without taking ownership of the replay stream.
/// </remarks>
public delegate ValueTask OnEntryProcessedDelegate<TState, TOperation, TProof>(
    LogReplayResult<TState, TOperation, TProof> result,
    CancellationToken cancellationToken);
