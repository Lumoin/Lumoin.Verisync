namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one Fast CASPaxos writer's read-modify-write cost in one trial, measured through the shipped proposer
/// and the shipped hedged writer.
/// </summary>
/// <param name="Writer">The writer index.</param>
/// <param name="Site">The replica index the writer sits at.</param>
/// <param name="Token">The token this writer's change appends.</param>
/// <param name="Activated">Whether the writer sent at all, which a stand-down signal makes false.</param>
/// <param name="ArrivalMicroseconds">The instant this writer's client started, before it waited its hedging delay.</param>
/// <param name="AddedWaitMicroseconds">The hedging delay this writer waited before sending, taken from the shipped writer's own report.</param>
/// <param name="FastAcceptedCount">How many acceptors accepted the blind fast round, across all of them.</param>
/// <param name="ReachedFastQuorum">Whether the blind fast round reached a fast quorum, which commits the change in one round trip.</param>
/// <param name="RecoveryEntered">Whether the writer fell back to a classic round that applies the change to the recovered value.</param>
/// <param name="RecoveryRounds">How many classic ballots the writer spent.</param>
/// <param name="ConflictRounds">
/// How many classic ballots beyond the first the writer spent, which is a round it had to run again because
/// another writer's round got in first. THIS IS THE FAST CASPAXOS SIDE'S CONFLICT COST, and it is not the same
/// quantity as the QuePaxa side's: committed state moving under this writer is absorbed by the round it is
/// already running, so only a pre-empted ballot costs another round.
/// </param>
/// <param name="PhasesExecuted">The phases actually executed, counted at the transport rather than looked up.</param>
/// <param name="ComposeCalls">
/// How many times the change function ran against a value recovered inside a round, which is once per classic
/// ballot that reached a promising quorum.
/// </param>
/// <param name="ApplyOnceTokenFirings">
/// How many times the change function found this writer's own token already applied, which is the recovery
/// tallying this writer's own partially accepted fast value back into its own round.
/// </param>
/// <param name="RecomposedAgainstAnotherWriter">Whether at least one in-round composition ran against a value another writer had already committed.</param>
/// <param name="LastRecoveredValue">The value the last in-round composition ran against, which is what the round recovered.</param>
/// <param name="IsCommitted">Whether this writer's change committed at all within its round budget.</param>
/// <param name="CommitMicroseconds">When the change committed, measured from this writer's own activation, or <see langword="null"/> when it did not.</param>
/// <param name="GiveUpMicroseconds">When the writer abandoned its bounded round budget, measured from its own activation, or <see langword="null"/> when it committed or never activated.</param>
/// <param name="CommittedValue">The value this writer left committed, which carries its own change composed on top of whatever it recovered.</param>
/// <remarks>
/// <para>
/// EVERY READING IS MEASURED FROM THIS WRITER'S OWN ACTIVATION, which is the origin the QuePaxa
/// read-modify-write arm also reports from.
/// </para>
/// <para>
/// THE BLIND FAST ROUND IS PART OF A READ-MODIFY-WRITE HERE ONLY BECAUSE THE CHANGE CARRIES AN APPLY-ONCE
/// TOKEN. A fast write proposes a value computed outside the round, exactly as a QuePaxa proposal does, and
/// its acceptances can be recovered back into this writer's own later round; without the token that recovery
/// would compose the change on top of itself. The firings are counted rather than assumed away.
/// </para>
/// </remarks>
internal sealed record RmwFastWriterMeasurement(
    int Writer,
    int Site,
    char Token,
    bool Activated,
    long ArrivalMicroseconds,
    long AddedWaitMicroseconds,
    int FastAcceptedCount,
    bool ReachedFastQuorum,
    bool RecoveryEntered,
    int RecoveryRounds,
    int ConflictRounds,
    int PhasesExecuted,
    int ComposeCalls,
    int ApplyOnceTokenFirings,
    bool RecomposedAgainstAnotherWriter,
    string? LastRecoveredValue,
    bool IsCommitted,
    long? CommitMicroseconds,
    long? GiveUpMicroseconds,
    string? CommittedValue)
{
    /// <summary>Whether the writer stood down on a learn signal, which is a disposition of its own rather than an unfinished write.</summary>
    public bool StoodDown => !Activated;

    /// <summary>Whether the writer activated and spent its round budget without committing.</summary>
    public bool IsCensored => Activated && !IsCommitted;

    /// <summary>Whether the change committed in one round trip, which is the blind fast round succeeding.</summary>
    public bool TookFastPath => IsCommitted && !RecoveryEntered;
}
