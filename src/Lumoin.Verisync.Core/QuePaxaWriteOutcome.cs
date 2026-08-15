namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of a versioned register's write.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="Status">What the attempt established.</param>
/// <param name="Version">The version the attempt ran at, which a writer that stood down reports as well.</param>
/// <param name="Record">
/// The record the version was decided at, whoever wrote it, and <see langword="null"/> where nothing was
/// decided.
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
/// THE DECIDED RECORD IS THE CARRIER AND THE VALUE AND WRITER ARE READ OFF IT. A record names the version, the
/// value, the writer and the membership the version after it runs under, so a caller learns what its write
/// established without reading the register again. That matters most where the write was a reconfiguration:
/// the membership a caller just installed is <see cref="VersionedValue{TValue}.NextConfiguration"/> of this
/// record, and reading it from <see cref="QuePaxaVersionedRegister{TValue}.ActiveConfiguration"/> instead
/// would read a memo any learn arriving meanwhile has already moved.
/// </para>
/// <para>
/// <see cref="Version"/> is carried beside the record rather than read from it, because it is reported where
/// no record exists at all. An undecided attempt, a writer that stood down and a write refused for membership
/// each name the version they addressed while deciding nothing. Where a record does exist the two agree:
/// <see cref="Record"/> is <see langword="null"/> or its version is <see cref="Version"/>, because a round
/// deciding a record of another version is refused rather than adopted.
/// </para>
/// <para>
/// A <see cref="DecidedAt"/> of <see cref="RecorderStep.RoundOnePhaseZero"/> is the leader's one-round-trip
/// commit, and any later step took the ordinary phases. Nothing else on this record distinguishes them.
/// </para>
/// <para>
/// A <see cref="QuePaxaWriteStatus.Superseded"/> outcome carries the record that won, so a caller retries by
/// recomputing from it rather than by reading again.
/// </para>
/// </remarks>
public sealed record QuePaxaWriteOutcome<TValue>(
    QuePaxaWriteStatus Status,
    RegisterVersion Version,
    VersionedValue<TValue>? Record,
    RecorderStep DecidedAt,
    int Attempts,
    bool Activated)
{
    /// <summary>The decided value when the version was decided, whoever wrote it; otherwise the default.</summary>
    public TValue? Value => Record is { } decided ? decided.Value : default;

    /// <summary>
    /// The replica whose record was decided, which is this register's own replica exactly when
    /// <see cref="Status"/> is <see cref="QuePaxaWriteStatus.Committed"/> and the replica that superseded it
    /// when <see cref="Status"/> is <see cref="QuePaxaWriteStatus.Superseded"/>. It is <see langword="null"/>
    /// only where nothing was decided.
    /// </summary>
    public ReplicaId? Writer => Record?.Writer;

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
