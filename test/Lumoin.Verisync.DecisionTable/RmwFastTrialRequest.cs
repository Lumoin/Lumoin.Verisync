using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One Fast CASPaxos read-modify-write trial's arguments.
/// </summary>
/// <param name="Topology">The placement, whose site count is the replica count.</param>
/// <param name="WriterCount">How many writers each apply one change.</param>
/// <param name="ArrivalMicroseconds">When each writer's client starts, in writer order, before any hedging delay.</param>
/// <param name="HedgingBaseDelay">
/// The base delay of the shipped hedging schedule. Zero reproduces the unhedged behaviour exactly, which is
/// the shipped type's own documented contract rather than a property of this harness.
/// </param>
/// <param name="TrialSeed">The seed every jitter draw in this trial derives from.</param>
/// <param name="Jitter">The per-leg jitter distribution.</param>
/// <param name="MaxRecoveryRounds">
/// How many classic ballots one writer may spend before the trial gives up on it. A writer whose round was
/// pre-empted must be able to try a higher ballot, or the measurement would report a permanent failure where a
/// deployment would report another round.
/// </param>
/// <param name="EventBudget">The pump's dispatch bound.</param>
/// <remarks>
/// The record carries data and validates nothing, so a <c>with</c> expression cannot produce a request the
/// constructor would have rejected. The arm validates what it runs.
/// </remarks>
internal sealed record RmwFastTrialRequest(
    Topology Topology,
    int WriterCount,
    ImmutableArray<long> ArrivalMicroseconds,
    TimeSpan HedgingBaseDelay,
    ulong TrialSeed,
    JitterModel Jitter,
    int MaxRecoveryRounds,
    long EventBudget);
