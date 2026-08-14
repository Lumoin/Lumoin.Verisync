namespace Lumoin.Verisync.Core;

/// <summary>
/// One member's answer to a version probe: the highest committed version it reports, beside the identity it
/// asserts for itself.
/// </summary>
/// <param name="Recorder">The host that produced the answer, which is that host's own identity.</param>
/// <param name="Version">The highest committed version that host reports, or
/// <see cref="RegisterVersion.Unwritten"/> when it has learned none.</param>
/// <remarks>
/// The identity is here for the reason <see cref="VersionedRecordReply{TValue}"/> carries its recorder: a
/// readiness report counts distinct members of the membership it measures, reached through an endpoint map a
/// deployment wires by hand, and two entries of that map pointing at one host would let one replica answer
/// twice and a decommission gate clear on fewer distinct replicas than it claims. The register refuses a
/// report naming a member other than the one it asked. Like the reply's field, this is not authentication:
/// the answering host asserts its own name, which is exact under crash faults and worthless against a host
/// that lies.
/// </remarks>
public readonly record struct MemberVersionReport(ReplicaId Recorder, RegisterVersion Version);
