using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one QuePaxa writer's read-modify-write cost in one trial, measured through the shipped versioned
/// register.
/// </summary>
/// <param name="Writer">The writer index.</param>
/// <param name="Site">The replica index the writer sits at, which is the member it writes as.</param>
/// <param name="Token">The token this writer's change appends.</param>
/// <param name="Outcome">The shipped outcome of the whole write: its status, the version it settled at, the step it decided at, and the attempts it spent.</param>
/// <param name="ArrivalMicroseconds">The instant this writer's client started, before it waited its hedging delay.</param>
/// <param name="AddedWaitMicroseconds">
/// The hedging delay this writer's first attempt waited before sending, read from the register's own schedule.
/// This writer's activation is its arrival and this wait added together, so the client-visible latency of any
/// reading below is that reading plus this wait.
/// </param>
/// <param name="CommitMicroseconds">When this writer's own change committed, measured from its own activation, or <see langword="null"/> when it never did.</param>
/// <param name="GiveUpMicroseconds">
/// When the writer abandoned its bounded attempt budget, measured from its own activation, or
/// <see langword="null"/> when it committed. A censored write costs at least this much and the row's
/// percentiles rank it above every write that finished.
/// </param>
/// <param name="ConflictRecomputes">
/// How many times this write recomputed its value against a value it had not held before, which is a
/// re-proposal forced because committed state moved under it. THIS IS THE QUANTITY THE WORKLOAD GATE READS.
/// </param>
/// <param name="UndecidedRecomputes">
/// How many times this write recomputed against the value it already held, which is a retry that learned
/// nothing and is a different cost from a conflict.
/// </param>
/// <param name="ApplyOnceTokenFirings">
/// How many times the change function found this writer's own token already applied. A superseded proposal is
/// discarded whole rather than composed, so this counts only the other route: an attempt that reached no
/// decision, was carried by another proposer and was decided afterwards, which the writer then learns.
/// </param>
/// <param name="RecomposedAgainstAnotherWriter">Whether at least one recompute ran against a value another writer had already committed.</param>
/// <param name="LastConflictBase">The value the last conflict recompute ran against, which is the winner this write rebuilt on top of.</param>
/// <param name="CommittedValue">The value decided at the version this write settled at, whoever wrote it.</param>
/// <remarks>
/// <para>
/// THE LATENCY IS MEASURED FROM THIS WRITER'S OWN ACTIVATION, which is the origin the Fast CASPaxos
/// read-modify-write arm also reports from and the origin both plain arms already use. The two arms' latency
/// columns are argmin'd against each other, so one origin is what makes that column one currency.
/// </para>
/// <para>
/// The recompute counts come from the change function's own argument and not from a version number, because
/// the quantity the settled rule names is committed state MOVING under a writer. A retry that recomputed
/// against the same value learned nothing and cost a round; a retry that recomputed against a different value
/// is the conflict the rule prices.
/// </para>
/// </remarks>
internal sealed record RmwQuePaxaWriterMeasurement(
    int Writer,
    int Site,
    char Token,
    QuePaxaWriteOutcome<string> Outcome,
    long ArrivalMicroseconds,
    long AddedWaitMicroseconds,
    long? CommitMicroseconds,
    long? GiveUpMicroseconds,
    int ConflictRecomputes,
    int UndecidedRecomputes,
    int ApplyOnceTokenFirings,
    bool RecomposedAgainstAnotherWriter,
    string? LastConflictBase,
    string? CommittedValue)
{
    /// <summary>Whether this writer's own change is the one that was decided.</summary>
    public bool IsCommitted => Outcome.Status == QuePaxaWriteStatus.Committed;

    /// <summary>Whether the writer spent its attempt budget without its own change committing.</summary>
    public bool IsCensored => !IsCommitted;

    /// <summary>How many consensus attempts this write spent, which the shipped outcome counts.</summary>
    public int Attempts => Outcome.Attempts;

    /// <summary>Whether the attempt that settled this write decided at the round's first step, which is the leader's one-round-trip commit.</summary>
    public bool TookFastPath => Outcome.TookFastPath;
}
