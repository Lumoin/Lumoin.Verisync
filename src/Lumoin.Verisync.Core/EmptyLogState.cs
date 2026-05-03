namespace Lumoin.Verisync.Core;

/// <summary>
/// The log state before the genesis entry has been applied.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <remarks>
/// This is the initial state supplied to replay. Genesis apply logic receives this variant and must
/// produce an <see cref="ActiveLogState{TState}"/>. Any other apply logic receiving this variant
/// indicates a malformed log.
/// </remarks>
public sealed record EmptyLogState<TState>: LogState<TState>;
