namespace Lumoin.Verisync.Core;

/// <summary>
/// The log state after the genesis entry has been applied and before deactivation.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <param name="Value">The current domain state.</param>
/// <remarks>
/// Update and heartbeat apply logic receives this variant. <see cref="Value"/> carries the current
/// domain state — a CRDT snapshot, a register value, or any other accumulation of the operations
/// applied so far.
/// </remarks>
public sealed record ActiveLogState<TState>(TState Value): LogState<TState>;
