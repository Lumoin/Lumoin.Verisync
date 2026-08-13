using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// What a versioned register decides: a value, the version it was written at, the replica that wrote it, and
/// the membership the next instance runs under.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="Version">The version this write produces. Must not be <see cref="RegisterVersion.Unwritten"/>.</param>
/// <param name="Writer">The replica proposing the write.</param>
/// <param name="NextConfiguration">The membership the version after this one runs under. Must not be <see langword="null"/>.</param>
/// <param name="Value">The application value.</param>
/// <remarks>
/// <para>
/// The writer is a field of the decided value and not an attribute beside it. The next version's leader is
/// derived from the previous version's writer, so every replica must agree on who wrote it, and consensus
/// agrees on the decided value. <see cref="QuePaxaOutcome{TValue}.DecidedBy"/> cannot carry that weight: the
/// agreement invariant quantifies over the decided value and the decision drops the winning proposal's owner,
/// so the owner is covered by no checked property, and its uniqueness would rest on
/// <see cref="ProposalKey"/>'s uniqueness contract, which nothing in the core enforces. Inside the value the
/// writer is covered by the invariant that is checked.
/// </para>
/// <para>
/// The configuration is a field of the decided value for the same reason and one step further along. The
/// membership the next instance runs under has to be a function of an agreed fact, and the decided value is
/// the only agreed thing: a configuration held beside the record, or inside the application value the register
/// cannot read, is a membership each replica derives for itself. Carried here it is what a quorum settled, so
/// every replica that holds the record for a version derives the same recorder set and the same hedging order
/// for the version after it.
/// </para>
/// <para>
/// A committed record is therefore self-describing: a replica that has the record knows the version, the
/// writer and the membership without being told any of them separately, so it can reach a recorder through any
/// transport and still carry its own meaning.
/// </para>
/// <para>
/// <typeparamref name="TValue"/> must have value equality. The protocol compares whole proposals, and
/// <see cref="PrioritizedProposal{TValue}"/> states the same obligation against whatever it carries. This
/// record's synthesized equality routes the value through
/// <see cref="System.Collections.Generic.EqualityComparer{T}.Default"/>, so a value type with reference
/// equality breaks the phase-two comparison here exactly as it does one layer down, and it breaks it only
/// after a codec round trip, where the proposer's object and the recorder's are no longer the same instance.
/// </para>
/// <para>
/// The version is carried even though the consensus instance already names it, because the record outlives
/// the instance: it is what a replica retains, disseminates and restores, so a version recovered from a
/// record cannot disagree with the one it was decided at.
/// </para>
/// </remarks>
public sealed record VersionedValue<TValue>(
    RegisterVersion Version,
    ReplicaId Writer,
    QuePaxaConfiguration NextConfiguration,
    TValue Value)
{
    /// <summary>
    /// The version this write produces. It is validated on construction and on a <c>with</c> expression
    /// alike, because the initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the version is <see cref="RegisterVersion.Unwritten"/>.</exception>
    public RegisterVersion Version { get; init { field = ValidateVersion(value); } } = ValidateVersion(Version);


    /// <summary>
    /// The membership the version after this one runs under. It is validated on construction and on a
    /// <c>with</c> expression alike, for the same reason the version is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the configuration is <see langword="null"/>.</exception>
    public QuePaxaConfiguration NextConfiguration { get; init { field = ValidateNextConfiguration(value); } } = ValidateNextConfiguration(NextConfiguration);


    private static RegisterVersion ValidateVersion(RegisterVersion value)
    {
        //A record carrying the unwritten sentinel is indistinguishable from the absence of a decision.
        ArgumentOutOfRangeException.ThrowIfEqual(value.Value, RegisterVersion.Unwritten.Value, nameof(Version));

        return value;
    }


    private static QuePaxaConfiguration ValidateNextConfiguration(QuePaxaConfiguration value)
    {
        //A record naming no membership names no recorder set for the version that follows it.
        ArgumentNullException.ThrowIfNull(value, nameof(NextConfiguration));

        return value;
    }
}
