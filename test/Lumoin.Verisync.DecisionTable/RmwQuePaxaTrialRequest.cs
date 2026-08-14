using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One QuePaxa read-modify-write trial's arguments.
/// </summary>
/// <param name="Topology">The placement, whose site count is the replica count and whose sites are the chain's members.</param>
/// <param name="WriterCount">How many replicas each apply one change. Must not exceed the replica count, because a writer is a member.</param>
/// <param name="ArrivalMicroseconds">When each writer's client starts, in writer order, before any hedging delay.</param>
/// <param name="BaseDelayMicroseconds">
/// The hedging delay increment per position the registers wait, which is the stagger rung an operator
/// configures. Zero activates every position at once, and the register waits it once per attempt rather than
/// once per write.
/// </param>
/// <param name="TrialSeed">The seed every jitter draw and every priority stream in this trial derives from.</param>
/// <param name="Jitter">The per-leg jitter distribution.</param>
/// <param name="MaxAttempts">
/// How many consensus attempts one write may spend before the trial gives up on it. A writer that keeps losing
/// its version must be able to re-read and re-propose, or the measurement would report a permanent failure
/// where a deployment would report another instance.
/// </param>
/// <param name="AttemptsPerRecorder">How many times one step may send to one recorder before abandoning it for that step.</param>
/// <param name="EventBudget">The pump's dispatch bound.</param>
/// <remarks>
/// The record carries data and validates nothing, so a <c>with</c> expression cannot produce a request the
/// constructor would have rejected. The arm validates what it runs.
/// </remarks>
internal sealed record RmwQuePaxaTrialRequest(
    Topology Topology,
    int WriterCount,
    ImmutableArray<long> ArrivalMicroseconds,
    long BaseDelayMicroseconds,
    ulong TrialSeed,
    JitterModel Jitter,
    int MaxAttempts,
    int AttemptsPerRecorder,
    long EventBudget);
