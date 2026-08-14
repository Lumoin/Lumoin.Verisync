using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Persists a recorder's state so a recorded proposal survives a process crash. It is invoked
/// <em>before</em> a reply is sent whenever the recorder state the reply rests on is not yet durable, and
/// must not return until the new state is durable — an <c>fsync</c>, a committed database transaction, or
/// whatever durability means for the host.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="recorder">The new recorder state to make durable before the reply is sent.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once <paramref name="recorder"/> is durable.</returns>
/// <remarks>
/// <para>
/// Everything the recorder carries must be durable before a reply escapes, which is its step, the first
/// proposal at that step, the aggregate accumulating there and the aggregate carried from the step below —
/// exactly <see cref="QuePaxaRecorder{TValue}.ToState"/>, which
/// <see cref="QuePaxaRecorder{TValue}.FromState"/> restores. The fast path rests on Lemma C.10's argument that
/// the first proposal of a step is never overwritten, and a restarted recorder that came back at
/// <see cref="RecorderStep.Zero"/> would take a fresh first proposal for a step whose original first proposal
/// a proposer has already read. The prior aggregate is load-bearing beside it: every reply carries it and a
/// proposer's phase two and phase three tests read it, while the current aggregate is what an advance by one
/// step carries down as the next step's prior aggregate. A host that made the step and the first proposal
/// durable and nothing else would lose a field a proposer has already acted on. Holding the reply until this
/// delegate returns closes those windows.
/// </para>
/// <para>
/// The four fields are one durable write and not four. A store that lands some of them and not others can
/// leave a mix of two faithful snapshots that is itself a state a recorder-driven register can hold at a
/// step already answered from, so it passes every rule <see cref="QuePaxaRecorder{TValue}.FromState"/>
/// owns and still contradicts a reply a proposer has acted on. No rule over the stored fields can close
/// that, so the write must land whole or not at all, or the stored document must be self-checking so that
/// a torn one is refused rather than restored.
/// </para>
/// <para>
/// A request that changes nothing still gets its reply, and once the state it found is durable it costs no
/// fresh write. The recorder returns its own instance exactly when no field would have changed — a record
/// below its step, or an identical same-step record — which is how the node knows the state it already
/// persisted still stands; a retransmission that follows a failed write instead retries the write before the
/// reply is sent.
/// </para>
/// <para>
/// Throwing prevents the reply from being sent and so fails closed: a recorded proposal that is not durable
/// must never be observable, so a host whose durable store is unavailable should throw rather than let the
/// reply escape. The exception then propagates out of <see cref="QuePaxaNode{TValue}.RunAsync"/>.
/// </para>
/// <para>
/// The trivial no-durability implementation, <c>(recorder, cancellationToken) =&gt; ValueTask.CompletedTask</c>,
/// reproduces the in-memory behavior and suits tests and ephemeral clusters that need not survive a restart.
/// </para>
/// </remarks>
public delegate ValueTask PersistRecorderDelegate<TValue>(QuePaxaRecorder<TValue> recorder, CancellationToken cancellationToken);
