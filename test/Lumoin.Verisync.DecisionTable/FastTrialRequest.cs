using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One pumped Fast CASPaxos trial's arguments.
/// </summary>
/// <param name="Topology">The placement, whose site count is the replica count.</param>
/// <param name="WriterCount">How many writers contend for the register transition.</param>
/// <param name="ArrivalMicroseconds">When each writer's client starts, in writer order, before any hedging delay.</param>
/// <param name="HedgingBaseDelay">
/// The base delay of the shipped hedging schedule. Zero reproduces the unhedged behaviour exactly, which is
/// the shipped type's own documented contract rather than a property of this harness.
/// </param>
/// <param name="TrialSeed">The seed every jitter draw in this trial derives from.</param>
/// <param name="Jitter">The per-leg jitter distribution.</param>
/// <param name="MaxRecoveryAttempts">
/// How many classic ballots one writer may spend before the trial gives up on it. A loser of a duelled
/// recovery must be able to try a higher ballot, or the measurement would report a permanent failure where a
/// deployment would report a second round trip.
/// </param>
/// <param name="EventBudget">The pump's dispatch bound.</param>
/// <param name="LearnSignal">
/// The per-writer learn signal a hedged writer consults before it activates, or <see langword="null"/> when
/// the trial carries none. The grid carries none, so its hedged writers always activate.
/// </param>
/// <remarks>
/// The record carries data and validates nothing, so a <c>with</c> expression cannot produce a request the
/// constructor would have rejected. The arm validates what it runs.
/// </remarks>
internal sealed record FastTrialRequest(
    Topology Topology,
    int WriterCount,
    ImmutableArray<long> ArrivalMicroseconds,
    TimeSpan HedgingBaseDelay,
    ulong TrialSeed,
    JitterModel Jitter,
    int MaxRecoveryAttempts,
    long EventBudget,
    FastLearnSignalDelegate? LearnSignal = null);
