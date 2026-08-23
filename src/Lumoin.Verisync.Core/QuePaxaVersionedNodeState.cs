using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The durable state of a <see cref="QuePaxaVersionedNode{TValue}"/>: the store's own incarnation, the
/// committed record the host has learned, and the recorder serving the instance that record implies. Obtain it with
/// <see cref="QuePaxaVersionedNode{TValue}.ToState"/> and reconstruct with
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="Incarnation">
/// The store this state was written by, which is the incarnation minted when that store was created.
/// </param>
/// <param name="Committed">
/// The committed record this host has learned, or <see langword="null"/> when it has learned none.
/// </param>
/// <param name="RecorderVersion">
/// The version <paramref name="Recorder"/> serves. Must not be <see cref="RegisterVersion.Unwritten"/>, because
/// a host serves the version after the record it holds and no such version is the unwritten one.
/// </param>
/// <param name="ConfiguredLeader">
/// The lane whose reserved-priority claims <paramref name="Recorder"/> honours, or <see langword="null"/> when
/// the instance is leaderless.
/// </param>
/// <param name="ActiveConfiguration">
/// The membership the instance <paramref name="Recorder"/> serves runs under, which is the committed record's
/// next configuration or the host's genesis when it has learned none. Must not be <see langword="null"/>.
/// </param>
/// <param name="Recorder">The recorder's own durable state, which is the register serving <paramref name="RecorderVersion"/>.</param>
/// <remarks>
/// <para>
/// The incarnation is the one field that is nobody's derivation. It is what the store says about itself, and
/// it is here rather than in a parameter of
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/> because a store's identity has to survive a restart
/// without anyone restating it: a caller that restated it could hand back the value the configuration admits
/// however its store came back, and the membership filter would then test a claim rather than a fact. The
/// replica this store answers under is not here for the mirror-image reason. A replica is a role a deployment
/// assigns and a configuration change can move, so it is configuration and reaches the restore as its
/// parameter, and the two halves are composed into one <see cref="HostId"/> there.
/// </para>
/// <para>
/// Three of the six fields are derivable from <paramref name="Committed"/> and are stored anyway. That
/// redundancy is the point rather than an oversight: a restore that recomputed the leader, the version and the
/// membership from the committed record would compare each with itself and could never fail, while a stored
/// copy lets the restore compare what a host wrote against what its own record implies, which is how a snapshot
/// torn across two writes announces itself instead of restoring as a second leader on one instance.
/// </para>
/// <para>
/// The fields are one durable write and not two. The recorder serves the version the committed record
/// implies, so a host that wrote the record and the register separately and crashed between them comes back
/// holding a register from one instance beside a record from another, and
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/> refuses exactly that pairing. Making the record durable
/// before any reply that depends on it leaves the process is what
/// <see cref="PersistRecorderDelegate{TValue}"/> already sequences one layer down, and the obligation reaches
/// the committed record here for the same reason: the leader a recorder enforces is derived from it.
/// </para>
/// <para>
/// The membership is stored for the same reason and one field further along. A register from one instance
/// beside a configuration from another is a snapshot written in two parts and torn between them, and a stored
/// copy is what makes that pairing refusable; it is also what lets a restore tell a store attached to the
/// wrong chain from one that merely lags, since the chain identity inside it is compared against the genesis
/// the host was handed.
/// </para>
/// <para>
/// A durable store that came back empty is still not detectable here, and it is not this record's job to
/// detect it. A wiped store has no snapshot at all, so nothing reaches a restore and the host is constructed
/// as a bootstrap one; what separates it from a store that has genuinely learned nothing is that it can no
/// longer present the incarnation this record held, and a configuration admitting that incarnation refuses
/// the store that replaced it.
/// </para>
/// </remarks>
public sealed record QuePaxaVersionedNodeState<TValue>(
    StoreIncarnation Incarnation,
    VersionedValue<TValue>? Committed,
    RegisterVersion RecorderVersion,
    ProposerLane? ConfiguredLeader,
    QuePaxaConfiguration ActiveConfiguration,
    QuePaxaRecorderState<VersionedValue<TValue>> Recorder)
{
    /// <summary>
    /// The version the recorder serves. It is validated on construction and on a <c>with</c> expression alike,
    /// because the initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the version is <see cref="RegisterVersion.Unwritten"/>.</exception>
    public RegisterVersion RecorderVersion { get; init { field = ValidateRecorderVersion(value); } } = ValidateRecorderVersion(RecorderVersion);


    /// <summary>
    /// The membership the restored instance runs under. It is validated on construction and on a <c>with</c>
    /// expression alike, for the same reason the version is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the configuration is <see langword="null"/>.</exception>
    public QuePaxaConfiguration ActiveConfiguration { get; init { field = ValidateActiveConfiguration(value); } } = ValidateActiveConfiguration(ActiveConfiguration);


    /// <summary>
    /// The recorder's own durable state. It is validated on construction and on a <c>with</c> expression alike,
    /// for the same reason the version is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the recorder state is <see langword="null"/>.</exception>
    public QuePaxaRecorderState<VersionedValue<TValue>> Recorder { get; init { field = ValidateRecorder(value); } } = ValidateRecorder(Recorder);


    private static RegisterVersion ValidateRecorderVersion(RegisterVersion value)
    {
        //The exception must name the public property, not the validator's parameter.
        ArgumentOutOfRangeException.ThrowIfEqual(value.Value, RegisterVersion.Unwritten.Value, nameof(RecorderVersion));

        return value;
    }


    private static QuePaxaConfiguration ValidateActiveConfiguration(QuePaxaConfiguration value)
    {
        //A snapshot naming no membership names no recorder set for the instance it restores.
        ArgumentNullException.ThrowIfNull(value, nameof(ActiveConfiguration));

        return value;
    }


    private static QuePaxaRecorderState<VersionedValue<TValue>> ValidateRecorder(QuePaxaRecorderState<VersionedValue<TValue>> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Recorder));

        return value;
    }
}
