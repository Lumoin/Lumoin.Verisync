using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Persists a Raft node's durable state so its Figure 2 triple — current term, vote, and log — survives a
/// process crash. It receives an immutable snapshot and is invoked after a work item has been handled and
/// <em>before</em> any output that depends on it leaves the process, and must not return until the snapshot
/// is durable: an <c>fsync</c>, a committed database transaction, or whatever durability means for the host.
/// </summary>
/// <typeparam name="TCommand">The application command type replicated by the log.</typeparam>
/// <param name="state">The immutable state snapshot to make durable before the dependent output is sent.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once <paramref name="state"/> is durable.</returns>
/// <remarks>
/// <para>
/// Raft safety across a crash requires the durable triple to be on stable storage before the reply that
/// depends on it leaves the process: a restarted node that lost a granted vote can vote twice in a term, and
/// one that lost an appended entry can drop a committed entry, breaking election safety and log matching.
/// Holding the outbound send until this delegate returns closes that window.
/// </para>
/// <para>
/// Throwing prevents the dependent output from being sent, which is the correct fail-closed behavior: an
/// unpersisted vote must never be observable, so a host whose durable store is unavailable should throw
/// rather than let the reply escape. The exception then propagates out of
/// <see cref="RaftRunner{TCommand}.RunAsync"/>.
/// </para>
/// <para>
/// The trivial no-durability implementation — <c>(state, cancellationToken) =&gt; ValueTask.CompletedTask</c>
/// — reproduces the in-memory behavior suitable for tests and ephemeral clusters that need not survive a
/// restart; omitting it entirely from <see cref="RaftRunner{TCommand}.RunAsync"/> does the same.
/// </para>
/// </remarks>
public delegate ValueTask PersistRaftStateDelegate<TCommand>(RaftNodeState<TCommand> state, CancellationToken cancellationToken);
