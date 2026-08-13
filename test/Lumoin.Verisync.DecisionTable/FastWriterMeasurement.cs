namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one Fast CASPaxos writer's write cost in one trial, measured through the shipped proposer.
/// </summary>
/// <param name="Writer">The writer index.</param>
/// <param name="Site">The replica index the writer sits at.</param>
/// <param name="Activated">Whether the writer sent at all, which a stand-down signal makes false.</param>
/// <param name="ArrivalMicroseconds">The instant this writer's client started, before it waited its hedging delay.</param>
/// <param name="AddedWaitMicroseconds">
/// The hedging delay this writer waited before sending, taken from the shipped writer's own report. This
/// writer's activation is its arrival and this wait added together, so the client-visible latency of any
/// reading below is that reading plus this wait.
/// </param>
/// <param name="FastAcceptedCount">How many acceptors accepted the fast round, across all of them.</param>
/// <param name="FastWriteReturnedMicroseconds">
/// THE SHIPPED INSTANT: when the fast write returned, measured from this writer's own activation. The shipped
/// proposer gathers every phase over all acceptors and does not act on the first quorum, so this is paced by
/// the FARTHEST acceptor.
/// </param>
/// <param name="FastQuorumReachedMicroseconds">
/// THE QUORUM INSTANT: when the fast-quorum-th accepting reply landed, measured from this writer's own
/// activation, or <see langword="null"/> when no fast quorum ever did. This is what the distance arithmetic
/// prices and what a first-quorum proposer would get.
/// </param>
/// <param name="ReachedFastQuorum">The oracle reading: whether this write reached a fast quorum across every acceptor.</param>
/// <param name="RecoveryEntered">Whether the writer fell back to a classic recovery round.</param>
/// <param name="RecoveryAttempts">How many classic ballots the writer spent, which is above one exactly where recoveries duelled.</param>
/// <param name="PhasesExecuted">
/// The phases actually executed, counted at the transport: one for a fast commit and three for a completed
/// recovery, measured rather than looked up.
/// </param>
/// <param name="IsCommitted">Whether the write committed at all within its recovery budget.</param>
/// <param name="CommitMicroseconds">When the write committed, measured from this writer's own activation, or <see langword="null"/> when it did not.</param>
/// <param name="GiveUpMicroseconds">
/// When the writer abandoned its bounded recovery ladder, measured from this writer's own activation, or
/// <see langword="null"/> when it committed or never activated. A censored write costs at least this much
/// and the row's percentiles rank it above every write that finished, so the instant is what makes the
/// censoring visible rather than merely counted.
/// </param>
/// <param name="CommittedValue">The value the write left committed, which is the recovered value when the writer lost its fast round.</param>
/// <remarks>
/// <para>
/// EVERY READING IS MEASURED FROM THIS WRITER'S OWN ACTIVATION, which is the origin the QuePaxa arm's
/// <see cref="QuePaxaWriterMeasurement.DecisionMicroseconds"/> already uses. The two arms' latency columns are
/// argmin'd against each other, so a hedging delay inside one origin and a stagger outside the other would
/// make that column two currencies. The wait itself is reported separately as the cost side of the ladder's
/// ledger, and the arrival is carried beside it so the client-visible currency stays reconstructable exactly.
/// </para>
/// <para>
/// BOTH LATENCY READINGS ARE REPORTED, ALWAYS. Reporting only the shipped instant makes the distance
/// arithmetic look wrong; reporting only the quorum instant measures a proposer that does not exist.
/// </para>
/// </remarks>
internal sealed record FastWriterMeasurement(
    int Writer,
    int Site,
    bool Activated,
    long ArrivalMicroseconds,
    long AddedWaitMicroseconds,
    int FastAcceptedCount,
    long FastWriteReturnedMicroseconds,
    long? FastQuorumReachedMicroseconds,
    bool ReachedFastQuorum,
    bool RecoveryEntered,
    int RecoveryAttempts,
    int PhasesExecuted,
    bool IsCommitted,
    long? CommitMicroseconds,
    long? GiveUpMicroseconds,
    string? CommittedValue)
{
    /// <summary>
    /// Whether the writer stood down on a learn signal, which is a disposition of its own rather than an
    /// unfinished write: it sent nothing, owes no recovery, and the host must reissue it.
    /// </summary>
    public bool StoodDown => !Activated;

    /// <summary>
    /// Whether the writer activated and exhausted its recovery ladder without committing, which is the
    /// censored write the row's percentiles rank above every write that finished.
    /// </summary>
    public bool IsCensored => Activated && !IsCommitted;
}
