using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Persists an acceptor's state so a promise or accept survives a process crash. It is invoked after the
/// acceptor state has changed and <em>before</em> the corresponding reply is sent, and must not return until
/// the new state is durable — an <c>fsync</c>, a committed database transaction, or whatever durability means
/// for the host.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="acceptor">The new acceptor state to make durable before the reply is sent.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once <paramref name="acceptor"/> is durable.</returns>
/// <remarks>
/// <para>
/// Paxos safety across a crash requires the acceptor state to be durable before its reply leaves the process:
/// a restarted node that lost an in-flight promise comes back as <see cref="FastAcceptor{TValue}.Initial"/>
/// and re-promises ballots it already superseded, breaking agreement. Holding the reply until this delegate
/// returns closes that window.
/// </para>
/// <para>
/// Throwing prevents the reply from being sent, which is the correct fail-closed behavior: an unpersisted
/// promise must never be observable, so a host whose durable store is unavailable should throw rather than
/// let the reply escape. The exception then propagates out of <see cref="ConsensusNode{TValue}.RunAsync"/>.
/// </para>
/// <para>
/// The trivial no-durability implementation — <c>(acceptor, cancellationToken) =&gt; ValueTask.CompletedTask</c>
/// — reproduces today's in-memory behavior, which is suitable for tests and ephemeral clusters that need not
/// survive a restart.
/// </para>
/// </remarks>
public delegate ValueTask PersistAcceptorDelegate<TValue>(FastAcceptor<TValue> acceptor, CancellationToken cancellationToken);
