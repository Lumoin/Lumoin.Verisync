using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A <see cref="RecordReply{TValue}"/> answering a <see cref="VersionedRecordRequest{TValue}"/>, carrying the
/// version of the instance that produced it.
/// </summary>
/// <typeparam name="TValue">The consensus value type, which a versioned register instantiates at <see cref="VersionedValue{TValue}"/>.</typeparam>
/// <param name="Version">The register version of the instance that answered. Must not be <see cref="RegisterVersion.Unwritten"/>.</param>
/// <param name="Recorder">The host that produced the reply, which is that host's own identity: the replica it serves under and the store answering for it.</param>
/// <param name="Reply">The reply itself, unchanged. Must not be <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// The version is here so that a mis-route is detectable, which is the one correlation failure a caller can
/// catch. A caller holds the version it asked about, so comparing it against this one is a single test; a
/// reply from another instance that passed unchecked would be counted toward this instance's quorum, and a
/// decision taken on an answer set whose members are a minority of the instance is exactly the majority
/// intersection the agreement argument rests on.
/// </para>
/// <para>
/// It does not make the pair self-correlating. Two calls to one recorder for consecutive steps of one
/// instance overlap by design, and a reply carries the recorder's own step rather than the step of the
/// request it answers, so a transport still owes per-call correlation. The version removes the cross-instance
/// case only.
/// </para>
/// <para>
/// The recorder identity is here for the same shape of failure one dimension over. A quorum is counted as a
/// number of distinct members of the addressed membership, and a register reaches those members through an
/// endpoint map a deployment wires by hand; two entries of that map pointing at one host would let one host
/// answer twice and be counted twice, and a decision would be taken by fewer replicas than the arithmetic
/// claims. The writer holds the member it addressed each slot to, so comparing that against this field is a
/// single test at the counting site, and it is the one place a wiring error stops being an operator artefact
/// and becomes a safety error.
/// </para>
/// <para>
/// It names the host and not the replica, which is what makes the comparison reach the store as well as the
/// role. A configuration admits one store per replica, so an answer carrying the admitted replica under
/// another incarnation came from a store the membership never admitted — a replica reprovisioned onto a
/// second host, or one whose store was wiped and restarted under the identity it used to hold. Counting it
/// would put two stores that have agreed on nothing into one slot of a quorum, which is the majority
/// intersection failing at a member rather than at an arithmetic. The pair is carried as one value for the
/// reason <see cref="HostId"/> gives: a reply naming one host's role beside another's store would be
/// constructible if these were two fields.
/// </para>
/// <para>
/// THIS IS NOT AUTHENTICATION AND NOTHING HERE MAY BE READ AS SUCH. The field is a claim the answering host
/// makes about itself, unsigned and unverifiable, so it is exact under the crash faults this protocol assumes
/// — a host that has not failed states its own identity correctly, and a mis-wired map is caught because
/// the honest host it reached names itself and not the member the writer meant — and worthless against a
/// host that lies. A deployment needing more owes its transport authentication;
/// <see cref="ReplicaId"/>-level signing is a different protocol, not a stronger reading of this field.
/// </para>
/// </remarks>
public sealed record VersionedRecordReply<TValue>(RegisterVersion Version, HostId Recorder, RecordReply<TValue> Reply)
{
    /// <summary>
    /// The register version of the instance that answered. It is validated on construction and on a
    /// <c>with</c> expression alike, because the initializer writes the backing field directly and no accessor
    /// runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the version is <see cref="RegisterVersion.Unwritten"/>.</exception>
    public RegisterVersion Version { get; init { field = ValidateVersion(value); } } = ValidateVersion(Version);


    /// <summary>
    /// The reply itself. It is validated on construction and on a <c>with</c> expression alike, for the same
    /// reason the version is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the reply is <see langword="null"/>.</exception>
    public RecordReply<TValue> Reply { get; init { field = ValidateReply(value); } } = ValidateReply(Reply);


    private static RegisterVersion ValidateVersion(RegisterVersion value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value.Value, RegisterVersion.Unwritten.Value, nameof(Version));

        return value;
    }


    private static RecordReply<TValue> ValidateReply(RecordReply<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Reply));

        return value;
    }
}
