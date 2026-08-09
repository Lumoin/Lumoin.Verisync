using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a record request to a single recorder and awaits its reply. This is the proposer's only view of a
/// recorder; the transport behind it may be an in-process call, an in-memory channel, or a socket.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="request">The request to send.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The recorder's reply.</returns>
/// <remarks>
/// <para>
/// A transport behind this delegate may retransmit freely. A second delivery of one identical request is the
/// identity on the recorder: the first delivery already moved the register to at least the request's step, so
/// the duplicate reaches only the same-step branch, where the fold keeps the incumbent on an exact key tie
/// and touches neither the step nor the first proposal, or the below-step branch, where nothing is written at
/// all. The downgrade cannot differ between the two deliveries either, because the recorder's configured
/// leader is fixed for the instance and the step and the proposal are identical.
/// </para>
/// <para>
/// An implementation must complete when <paramref name="cancellationToken"/> is signalled, with a result, a
/// fault or a cancellation. The proposer waits on <see cref="Task.WhenAny(Task[])"/>, which takes no
/// cancellation token, so it cannot interrupt its own wait, and an endpoint that ignores the token blocks a
/// cancelled proposal indefinitely.
/// </para>
/// <para>
/// An implementation that imposes its own deadline through a linked token may end its task cancelled or
/// throw <see cref="System.OperationCanceledException"/> while the proposer's own token is unsignalled. The
/// proposer treats that as a transport fault and retries within the recorder's attempt budget rather than
/// abandoning the proposal.
/// </para>
/// <para>
/// Correlation is per call and never per recorder. The proposer abandons rather than cancels the endpoints
/// still outstanding once a quorum has answered, and the next step calls every recorder again, so two calls to
/// one recorder can be outstanding at once. A reply carries the recorder's own step rather than the step of
/// the request it answers, so a transport holding a single slot per recorder would hand the older call's reply
/// to the newer call and nothing above it could tell. An implementation must correlate each reply to the call
/// it answers.
/// </para>
/// <para>
/// An implementation must not complete the returned operation while holding a lock this delegate itself needs.
/// The proposer resumes on whichever thread completed the operation and may issue that recorder's next send
/// synchronously from it, so a transport that completes a reply from inside its receive loop while holding the
/// connection's send lock deadlocks against its own retransmission. Completing through a task built with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, or outside the lock, avoids it.
/// </para>
/// </remarks>
public delegate ValueTask<RecordReply<TValue>> RecorderEndpointDelegate<TValue>(RecordRequest<TValue> request, CancellationToken cancellationToken);
