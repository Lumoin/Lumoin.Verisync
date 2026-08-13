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
/// other failing host.
/// </para>
/// <para>
/// Nothing here is counted, unlike the recorder side. A catch-up takes no quorum, because a committed record
/// is a decided fact and one honest host reporting a version settles it, so a member missing from a read is
/// a weaker result rather than a wrong one. The rule that keeps the recorder array at full length is a
/// quorum rule and it has no counterpart here.
/// </para>
/// </remarks>
public delegate ReadCommittedRecordDelegate<TValue> ResolveCommittedRecordReaderDelegate<TValue>(ReplicaId member);
