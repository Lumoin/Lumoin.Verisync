namespace Lumoin.Verisync.Core;

/// <summary>
/// Identifies one host: the replica identity it serves under and the store instance backing it.
/// </summary>
/// <param name="Replica">The replica identity, which is what quorum arithmetic counts and what a schedule orders.</param>
/// <param name="Incarnation">The store instance answering for <paramref name="Replica"/>.</param>
/// <remarks>
/// <para>
/// A replica is a role and a host is a process filling it. <see cref="ReplicaId"/> names the role, and two
/// hosts can be provisioned under one role without either of them lying about it; what separates them is the
/// store each holds, which is what this pair carries. A configuration lists the hosts admitted to its roles,
/// and a reply names the host that produced it, so the same value serves as an admission and as a claim.
/// </para>
/// <para>
/// The pair is one value and not two fields. Held separately, a replica and an incarnation could be set
/// inconsistently — a member admitted under another member's store, or a reply naming one host's role and
/// another's store — and nothing would relate them; holding the pair makes that unconstructible, for the same
/// reason a configuration's member list is not split into a recorder set and a hedging order.
/// </para>
/// <para>
/// A configuration lists a replica at most once however many hosts exist for it. Quorums are counted over
/// distinct replicas, so two hosts of one replica in one configuration would answer twice and be counted
/// twice, and a decision would be taken by fewer replicas than the arithmetic claims. Replacing a replica's
/// host is therefore a configuration change and not an addition.
/// </para>
/// </remarks>
public readonly record struct HostId(ReplicaId Replica, StoreIncarnation Incarnation);
