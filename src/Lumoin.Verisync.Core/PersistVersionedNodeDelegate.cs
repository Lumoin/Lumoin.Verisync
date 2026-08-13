using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Persists a versioned recorder host's state so the committed record and the register serving the
/// instance that record implies survive a process crash. It is invoked <em>before</em> any reply that
/// depends on them leaves the process, and must not return until the state is durable — an <c>fsync</c>,
/// a committed database transaction, or whatever durability means for the host.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="state">The host state to make durable before the reply is sent.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once <paramref name="state"/> is durable.</returns>
/// <remarks>
/// <para>
/// The delegate receives the immutable <see cref="QuePaxaVersionedNodeState{TValue}"/> rather than the
/// host, because the host is mutable and a store that read it after an await could make durable a state
/// the gate then marks as written; the record handed over is the exact value
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/> restores. Its fields are one durable write and
/// not two: a register from one instance beside a record from another is the torn snapshot
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/> refuses, and the committed record is
/// load-bearing beside the register because the leader every recorder enforces is derived from it, so a
/// host that persisted the register alone would come back serving an instance that has already decided,
/// with an empty register.
/// </para>
/// <para>
/// The state is snapshotted only when the durability gate fires, so a host that owes no write allocates
/// nothing. A request that changes nothing costs no fresh write once the state it found is durable; a
/// retransmission that follows a failed write retries the write before the reply is sent.
/// </para>
/// <para>
/// Four paths besides a reply await this, and they are the reason "before any dependent reply leaves" is
/// not the whole rule: <see cref="QuePaxaVersionedRunner{TValue}.MakeDurableAsync"/>, the caller-driven
/// checkpoint; a <see cref="LearnDurability.Durable"/> learn through
/// <see cref="QuePaxaVersionedRunner{TValue}.LearnAsync"/>, which owes the write before it reports an
/// adoption; a learn that moved
/// <see cref="QuePaxaVersionedNode{TValue}.ActiveConfiguration"/>, which owes it whatever durability was
/// asked for because the record installing a membership may be the only copy of it inside that membership;
/// and <see cref="QuePaxaVersionedRunner{TValue}.ReadCommittedAsync"/>, which owes it before it
/// republishes a record to a peer that will move to the next version on it.
/// </para>
/// <para>
/// Throwing prevents the reply from being sent and so fails closed: the call the reply belonged to is
/// faulted instead, and the exception ends <see cref="QuePaxaVersionedRunner{TValue}.RunAsync"/>.
/// </para>
/// <para>
/// The trivial no-durability implementation, <c>(state, cancellationToken) =&gt; ValueTask.CompletedTask</c>,
/// reproduces the in-memory behavior and suits tests and ephemeral clusters that need not survive a
/// restart.
/// </para>
/// </remarks>
public delegate ValueTask PersistVersionedNodeDelegate<TValue>(QuePaxaVersionedNodeState<TValue> state, CancellationToken cancellationToken);
