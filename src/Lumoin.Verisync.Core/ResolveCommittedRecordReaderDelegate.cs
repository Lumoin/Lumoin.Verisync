namespace Lumoin.Verisync.Core;

/// <summary>
/// Answers which catch-up query reaches one member of a versioned register's membership, so that a read
/// asks the members the register currently runs with.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="member">The member to ask.</param>
/// <returns>The query that asks that member what it has learned.</returns>
/// <remarks>
/// <para>
/// It is the read side of <see cref="ResolveRecorderEndpointDelegate{TValue}"/> and follows the same rules:
/// called per member, synchronous, and asked about identities a configuration change introduced. A member it
/// cannot resolve reports so by throwing or by returning a query that faults, which a read skips like any
/// other failing host. The one rule the two do not share is the nesting level: the query returned here is
/// instantiated at <typeparamref name="TValue"/> and yields the record as its result, where the resolved
/// endpoint is itself instantiated at <see cref="VersionedValue{TValue}"/>, so the two resolvers' inner type
/// arguments differ by exactly the record wrap.
/// </para>
/// <para>
/// Nothing here is counted, unlike the recorder side. A catch-up takes no quorum, because a committed record
/// is a decided fact and one honest host reporting a version settles it, so a member missing from a read is
/// a weaker result rather than a wrong one. The rule that keeps the recorder array at full length is a
/// quorum rule and it has no counterpart here.
/// </para>
/// <para>
/// A host that lags across a complete membership turnover is recovered by the deployment's locator and not by
/// the protocol, and this is the seam it is recovered through. A read asks the members of the membership the
/// reader holds, so a host whose held membership has been replaced outright asks replicas that may all be
/// gone and catches up from none of them. Answering for identities no current membership names is what a
/// locator outliving memberships is for; the alternative, a register keeping the memberships it has left
/// behind, would put a log inside a register and is deliberately not taken.
/// </para>
/// </remarks>
public delegate ReadCommittedRecordDelegate<TValue> ResolveCommittedRecordReaderDelegate<TValue>(ReplicaId member);
