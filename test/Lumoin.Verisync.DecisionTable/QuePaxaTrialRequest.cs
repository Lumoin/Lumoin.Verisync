using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One QuePaxa trial's arguments.
/// </summary>
/// <param name="Topology">The placement, whose site count is the replica count.</param>
/// <param name="WriterCount">How many writers contend for the instance.</param>
/// <param name="Leadership">How the recorders are led.</param>
/// <param name="ActivationsMicroseconds">When each writer starts, in writer order.</param>
/// <param name="StaggerMicroseconds">
/// How much of each writer's activation the stagger policy imposed, as against the arrival pattern. It is the
/// cost side of the ladder's ledger and cannot be recovered from the activation alone, because an activation
/// is an arrival offset and a stagger added together.
/// </param>
/// <param name="TrialSeed">The seed every jitter draw and every priority stream in this trial derives from.</param>
/// <param name="Jitter">The per-leg jitter distribution.</param>
/// <param name="EventBudget">The pump's dispatch bound.</param>
/// <remarks>
/// The record carries data and validates nothing, so a <c>with</c> expression cannot produce a request the
/// constructor would have rejected. The arm validates what it runs.
/// </remarks>
internal sealed record QuePaxaTrialRequest(
    Topology Topology,
    int WriterCount,
    LeadershipMode Leadership,
    ImmutableArray<long> ActivationsMicroseconds,
    ImmutableArray<long> StaggerMicroseconds,
    ulong TrialSeed,
    JitterModel Jitter,
    long EventBudget);
