namespace Lumoin.Verisync.Core;

/// <summary>
/// Represents the state of a log at a given point in replay.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <remarks>
/// <para>
/// <see cref="LogState{TState}"/> eliminates null from the replay pipeline by giving each phase of a
/// log's lifecycle a distinct, named type. Replay starts with <see cref="EmptyLogState{TState}"/> and
/// transitions through <see cref="ActiveLogState{TState}"/> on genesis. A deactivation entry produces
/// <see cref="DeactivatedLogState{TState}"/>, which is terminal.
/// </para>
/// <para>
/// Apply logic receives the current <see cref="LogState{TState}"/> and pattern-matches on the variant
/// to enforce correct lifecycle transitions without null checks. Logic that receives an
/// <see cref="EmptyLogState{TState}"/> where an active state was required knows immediately that the
/// log is malformed.
/// </para>
/// </remarks>
public abstract record LogState<TState>;
