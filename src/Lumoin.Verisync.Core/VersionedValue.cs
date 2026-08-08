using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// What a versioned register decides: a value, the version it was written at, and the replica that wrote it.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="Version">The version this write produces. Must not be <see cref="RegisterVersion.Unwritten"/>.</param>
/// <param name="Writer">The replica proposing the write.</param>
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
/// A committed record is therefore self-describing: a replica that has the record knows the version and the
/// writer without being told either separately, so it can reach a recorder through any transport and still
/// carry its own meaning.
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
public sealed record VersionedValue<TValue>(RegisterVersion Version, ReplicaId Writer, TValue Value)
{
    /// <summary>
    /// The version this write produces. It is validated on construction and on a <c>with</c> expression
    /// alike, because the initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the version is <see cref="RegisterVersion.Unwritten"/>.</exception>
    public RegisterVersion Version { get; init { field = Validate(value); } } = Validate(Version);


    private static RegisterVersion Validate(RegisterVersion value)
    {
        //A record carrying the unwritten sentinel is indistinguishable from the absence of a decision.
        ArgumentOutOfRangeException.ThrowIfEqual(value.Value, RegisterVersion.Unwritten.Value, nameof(Version));

        return value;
    }
}
