namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of a versioned register's write.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="Status">What the attempt established.</param>
/// <param name="Version">The version the attempt ran at, which a writer that stood down reports as well.</param>
/// <param name="Value">
/// The decided value when the version was decided, whoever wrote it; otherwise the default.
/// </param>
/// <param name="Writer">
/// The replica whose record was decided, which is this register's own replica exactly when
/// <paramref name="Status"/> is <see cref="QuePaxaWriteStatus.Committed"/> and the replica that superseded it
/// when <paramref name="Status"/> is <see cref="QuePaxaWriteStatus.Superseded"/>. It is
/// <see langword="null"/> only where nothing was decided.
/// </param>
/// <param name="DecidedAt">
/// The protocol step the decision was taken at, or <see cref="RecorderStep.Zero"/> when nothing was decided.
/// </param>
/// <param name="Attempts">The number of consensus attempts the write spent.</param>
/// <param name="Activated">
/// Whether the writer sent anything at all. A writer that stood down on its hedging delay sent nothing, which
/// is distinct from a write that ran and reached no decision.
/// </param>
/// <remarks>
/// <para>
/// A <paramref name="DecidedAt"/> of <see cref="RecorderStep.RoundOnePhaseZero"/> is the leader's
/// one-round-trip commit, and any later step took the ordinary phases. Nothing else on this record
/// distinguishes them.
/// </para>
/// <para>
/// A <see cref="QuePaxaWriteStatus.Superseded"/> outcome carries the version that closed, the value that won
/// and who wrote it, so a caller retries by recomputing from <paramref name="Value"/> rather than by reading
/// again.
/// </para>
/// </remarks>
public sealed record QuePaxaWriteOutcome<TValue>(
    QuePaxaWriteStatus Status,
    RegisterVersion Version,
    TValue? Value,
    ReplicaId? Writer,
    RecorderStep DecidedAt,
    int Attempts,
    bool Activated)
{
    /// <summary>Whether this register's own write was the one decided.</summary>
    public bool IsCommitted => Status == QuePaxaWriteStatus.Committed;

    /// <summary>
    /// Whether the decision was taken on the leader's fast path, which is one round trip rather than one
    /// round.
    /// </summary>
    /// <remarks>
    /// It is true only for a decision at the protocol's first step. It says nothing about which writer won,
    /// because a fast decision carries whichever proposal the recorders uniformly recorded first.
    /// </remarks>
    public bool TookFastPath => DecidedAt == RecorderStep.RoundOnePhaseZero;
}
