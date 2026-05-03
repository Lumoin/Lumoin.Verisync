namespace Lumoin.Verisync.Core;

/// <summary>
/// The terminal log state after a deactivation entry has been applied.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <param name="Value">The domain state at the time of deactivation.</param>
/// <remarks>
/// No further state-mutating entries are valid after this variant is reached. <see cref="Value"/>
/// carries the domain state at deactivation time, preserved for audit and historical resolution. The
/// terminal nature is enforced by apply logic, which returns an error if further mutating entries
/// arrive after deactivation.
/// </remarks>
public sealed record DeactivatedLogState<TState>(TState Value): LogState<TState>;
