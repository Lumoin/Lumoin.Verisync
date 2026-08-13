namespace Lumoin.Verisync.Core;

/// <summary>
/// Answers which transport reaches one member of a versioned register's membership, so that a register whose
/// recorder set changes under it addresses the members the instance runs over rather than a list fixed at
/// construction.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="member">The member to reach.</param>
/// <returns>The endpoint that carries requests to that member.</returns>
/// <remarks>
/// <para>
/// It is called once per member per attempt, synchronously and on the attempt's own path, so it looks a
/// member up rather than connecting to one. A resolver that blocks delays every send the attempt makes,
/// including the leader's, and a deployment that must dial does it behind the endpoint the resolver returns.
/// </para>
/// <para>
/// A member it cannot resolve is not an omission. The quorum a decision is taken by is counted over the
/// number of endpoints the register built, so a member left out of that array shrinks the majority rather
/// than the reachability, and a decision would then be taken by fewer replicas than the arithmetic claims. A
/// resolver reports an unresolvable member by throwing or by returning an endpoint that always faults, and
/// either way the register keeps the slot: a member with no route is an unreachable recorder, which the
/// protocol already handles, and never a smaller cluster.
/// </para>
/// <para>
/// A member this register has never heard of is ordinary rather than exceptional. A configuration change
/// adds replicas, and a register learns of a joiner from the record that installed it, so a resolver is
/// asked about identities that were not in the deployment's original list. Where a member has lagged across
/// a complete membership turnover, recovering it is the deployment's locator's work and not the protocol's.
/// </para>
/// </remarks>
public delegate VersionedRecorderEndpointDelegate<VersionedValue<TValue>> ResolveRecorderEndpointDelegate<TValue>(ReplicaId member);
