namespace Lumoin.Verisync.Core;

/// <summary>
/// Answers which transport reaches one member of a versioned register's membership, so that a register whose
/// recorder set changes under it addresses the members the instance runs over rather than a list fixed at
/// construction.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="member">The member to reach.</param>
/// <returns>The endpoint that carries requests to that member: a
/// <see cref="VersionedRecorderEndpointDelegate{TValue}"/> instantiated at <see cref="VersionedValue{TValue}"/>
/// rather than at <typeparamref name="TValue"/>, because a versioned register's consensus value is the record
/// and not the application value inside it.</returns>
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
/// asked about identities that were not in the deployment's original list.
/// </para>
/// <para>
/// A host that lags across a complete membership turnover is recovered by the deployment's locator and not by
/// the protocol. Such a host holds a record naming a membership every member of which has since been
/// replaced, and it can address only the members that record names; no path walks forward through memberships
/// it never saw, because the register keeps the latest record and not a log of the ones before it. What
/// closes the gap is that this seam is asked about a replica rather than handed a list: a deployment is
/// entitled to answer for identities no current membership names, and a locator that outlives memberships is
/// what makes a stranded host reachable again.
/// </para>
/// </remarks>
public delegate VersionedRecorderEndpointDelegate<VersionedValue<TValue>> ResolveRecorderEndpointDelegate<TValue>(ReplicaId member);
