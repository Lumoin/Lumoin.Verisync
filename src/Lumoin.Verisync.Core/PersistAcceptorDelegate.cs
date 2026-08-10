using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Persists an acceptor's state so a promise or accept survives a process crash. It is invoked
/// <em>before</em> a reply is sent whenever the acceptor state the reply rests on is not yet durable, and
/// must not return until the new state is durable — an <c>fsync</c>, a committed database transaction, or
/// whatever durability means for the host.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="acceptor">The new acceptor state to make durable before the reply is sent.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once <paramref name="acceptor"/> is durable.</returns>
/// <remarks>
/// <para>
/// Paxos safety across a crash requires the acceptor state to be durable before its reply leaves the process:
/// a restarted node that lost an in-flight promise comes back as <see cref="FastAcceptor{TValue}.Initial"/>
/// and re-promises ballots it already superseded, breaking agreement. Everything the acceptor carries must
/// be durable, which is its promise, its accepted ballot and its accepted value — exactly
/// <see cref="FastAcceptor{TValue}.ToState"/>, which <see cref="FastAcceptor{TValue}.FromState"/> restores
/// into the acceptor a restarting host constructs its <see cref="ConsensusNode{TValue}"/> over. The promise
/// alone is not enough: a prepare reply reports the accepted ballot and value, and a recovering proposer
/// decides on them, so a host that persisted the promise alone would answer a recovery from fields it never
/// wrote. Holding the reply until this delegate returns closes those windows. The delegate receives the
/// acceptor itself rather than a snapshot, because the acceptor is an immutable value: a store that reads it
/// after an await reads the same fields it was handed, where the versioned recorder host passes a snapshot
/// because that host is mutable.
/// </para>
/// <para>
/// The three fields are one durable write and not three. A store that lands some of them and not others can
/// leave a mix of two faithful snapshots that is itself a state an acceptor can hold, so it passes every
/// rule <see cref="FastAcceptor{TValue}.FromState"/> owns and still contradicts a reply already sent. The
/// accepted ballot of the newer snapshot beside the accepted value of the older one is such a mix: the
/// restored acceptor answers a later prepare with one value under a ballot a proposer was already told
/// carried another, and a ballot may carry only one value. The rules do refuse the mixes that show
/// themselves in one state — a promise trailing its accepted ballot is refused wherever the tear left it
/// visible — but no rule over the stored fields can close the mixes that do not, so the write must land
/// whole or not at all, or the stored document must be self-checking so that a torn one is refused rather
/// than restored.
/// </para>
/// <para>
/// Throwing prevents the reply from being sent, which is the correct fail-closed behavior: an unpersisted
/// promise must never be observable, so a host whose durable store is unavailable should throw rather than
/// let the reply escape. The exception then propagates out of <see cref="ConsensusNode{TValue}.RunAsync"/>,
/// and a later delivery finds the state still unpersisted and retries the write before any reply is sent.
/// </para>
/// <para>
/// The trivial no-durability implementation — <c>(acceptor, cancellationToken) =&gt; ValueTask.CompletedTask</c>
/// — reproduces today's in-memory behavior, which is suitable for tests and ephemeral clusters that need not
/// survive a restart.
/// </para>
/// </remarks>
public delegate ValueTask PersistAcceptorDelegate<TValue>(FastAcceptor<TValue> acceptor, CancellationToken cancellationToken);
