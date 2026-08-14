using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one QuePaxa writer's attempt cost in one trial.
/// </summary>
/// <param name="Writer">The writer index.</param>
/// <param name="Site">The replica index the writer sits at.</param>
/// <param name="Outcome">The shipped outcome: whether it decided, the value, the deciding lane, the step it decided at, and the steps it took.</param>
/// <param name="ActivationMicroseconds">The instant this writer was activated at, which is its arrival offset and its stagger added together.</param>
/// <param name="DecisionMicroseconds">The time from this writer's own activation to its decision.</param>
/// <param name="AddedWaitMicroseconds">The stagger this writer paid before sending, which is the cost side of the ladder's ledger.</param>
/// <param name="PriorityDraws">How many phase-zero priorities this writer's source supplied, which is zero for a proposer that believes it leads.</param>
/// <remarks>
/// <para>
/// THE LATENCY IS MEASURED FROM THIS WRITER'S OWN ACTIVATION, which is the origin the Fast CASPaxos arm's
/// <see cref="FastWriterMeasurement.CommitMicroseconds"/> also reports from. The two arms' latency columns are
/// argmin'd against each other, so one origin is what makes that column one currency; the stagger itself is
/// reported separately as the cost side of the ladder's ledger and the activation is carried beside it, so
/// the client-visible currency stays reconstructable exactly.
/// </para>
/// <para>
/// The outcome is the shipped record rather than a restatement of it, so a step count or a deciding lane in
/// any report is the protocol's own answer.
/// </para>
/// </remarks>
internal sealed record QuePaxaWriterMeasurement(
    int Writer,
    int Site,
    QuePaxaOutcome<string> Outcome,
    long ActivationMicroseconds,
    long DecisionMicroseconds,
    long AddedWaitMicroseconds,
    int PriorityDraws)
{
    /// <summary>Whether this writer reached the fast path, which is a decision at the round's first step.</summary>
    public bool IsFastPath => Outcome.IsDecided && Outcome.DecidedAt == RecorderStep.RoundOnePhaseZero;
}
