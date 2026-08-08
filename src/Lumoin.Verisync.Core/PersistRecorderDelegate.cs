using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Persists a recorder's state so a recorded proposal survives a process crash. It is invoked after the
/// recorder state has changed and <em>before</em> the corresponding reply is sent, and must not return until
/// the new state is durable — an <c>fsync</c>, a committed database transaction, or whatever durability means
/// for the host.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="recorder">The new recorder state to make durable before the reply is sent.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once <paramref name="recorder"/> is durable.</returns>
/// <remarks>
/// <para>
/// WHAT MUST BE DURABLE BEFORE A REPLY ESCAPES IS THE RECORDER'S STEP AND ITS FIRST PROPOSAL AT THAT STEP.
/// Lemma C.10's argument that the first proposal of a step is never overwritten is what the fast path rests
/// on, and a restarted recorder that came back at <see cref="RecorderStep.Zero"/> would take a fresh first
/// proposal for a step whose original first proposal a proposer has already read. Holding the reply until
/// this delegate returns closes that window.
/// </para>
/// <para>
/// A request that changes nothing is not persisted, and the reply is still sent. The recorder returns its own
/// instance exactly when no field would have changed, which is the case for a record below its step and for
/// an identical same-step record, so a retransmission costs no <c>fsync</c> that would make nothing durable.
/// </para>
/// <para>
/// Throwing prevents the reply from being sent, which is the correct fail-closed behavior: a recorded
/// proposal that is not durable must never be observable, so a host whose durable store is unavailable should
/// throw rather than let the reply escape. The exception then propagates out of
/// <see cref="QuePaxaNode{TValue}.RunAsync"/>.
/// </para>
/// <para>
/// The trivial no-durability implementation — <c>(recorder, cancellationToken) =&gt; ValueTask.CompletedTask</c>
/// — reproduces the in-memory behavior, which is suitable for tests and ephemeral clusters that need not
/// survive a restart.
/// </para>
/// </remarks>
public delegate ValueTask PersistRecorderDelegate<TValue>(QuePaxaRecorder<TValue> recorder, CancellationToken cancellationToken);
