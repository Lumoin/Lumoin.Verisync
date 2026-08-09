using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A stateful QuePaxa recorder node: it applies incoming <see cref="RecordRequest{TValue}"/> messages to its
/// immutable <see cref="QuePaxaRecorder{TValue}"/> and produces the matching <see cref="RecordReply{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The node holds the current recorder and replaces it on each request. <see cref="RunAsync"/> drives the node
/// over any inbound message stream and reply sink, so the same node runs over an in-memory channel or a socket
/// unchanged. A node processes its requests sequentially and is not safe for concurrent calls.
/// </para>
/// <para>
/// Every request is served and none is refused. A reserved-priority claim from a proposer that is not the
/// recorder's configured leader is downgraded to the lowest ordinary priority and recorded when it arrives
/// at <see cref="RecorderStep.RoundOnePhaseZero"/>, which is why <see cref="RecordReply{TValue}"/> carries
/// no rejection field. The downgrade itself lives in <see cref="QuePaxaRecorder{TValue}"/>; this type only
/// carries it across the message boundary.
/// </para>
/// <para>
/// The node holds its recorder in memory only. Safety across a crash requires the recorder's step and its
/// first proposal at that step to be durable <em>before</em> the reply leaves the process: the fast path rests
/// on the first proposal of a step never being overwritten, and a restarted node that came back at
/// <see cref="RecorderStep.Zero"/> would take a fresh first proposal for a step whose original first proposal
/// a proposer has already read. Pass a <see cref="PersistRecorderDelegate{TValue}"/> to
/// <see cref="RunAsync"/> to get this: after each request that changes the recorder it makes the new state
/// durable and only then sends the reply, so an unpersisted first proposal is never observable. Omitting it
/// (or supplying the no-durability implementation) sends each reply immediately and is suitable for tests and
/// ephemeral clusters. A host that needs different sequencing drives the node itself instead: call
/// <see cref="Handle"/>, persist <see cref="Recorder"/>, and only then send the reply.
/// </para>
/// <para>
/// The constructor takes the recorder rather than defaulting, because a recorder carries the instance's
/// configured leader: a node that defaulted to <see cref="QuePaxaRecorder{TValue}.Leaderless"/> would silently
/// downgrade every reserved claim and turn every fast path into a three-step round. There is no public way to
/// build a recorder holding restored state, because <see cref="QuePaxaRecorder{TValue}"/> and
/// <see cref="IntervalSummaryRegister{TValue}"/> both expose only factories that start from an unwritten
/// register; this node needs no change when one is added, because it never assumes an initial recorder.
/// </para>
/// <para>
/// A node serves one consensus instance, as <see cref="QuePaxaRecorder{TValue}"/> holds one instance, and no
/// request carries an instance or slot identifier.
/// </para>
/// </remarks>
public sealed class QuePaxaNode<TValue>
{
    /// <summary>
    /// Initializes a node over <paramref name="recorder"/>.
    /// </summary>
    /// <param name="recorder">The recorder this node starts from, carrying the instance's configured leader.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="recorder"/> is <see langword="null"/>.</exception>
    public QuePaxaNode(QuePaxaRecorder<TValue> recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        Recorder = recorder;
        Persisted = recorder;
    }


    /// <summary>The current recorder state.</summary>
    public QuePaxaRecorder<TValue> Recorder { get; private set; }


    /// <summary>
    /// The recorder state <see cref="RunAsync"/> last made durable, which is what its durability gate compares
    /// against.
    /// </summary>
    /// <remarks>
    /// This is node state rather than loop state, because a host whose durable write failed restarts the loop
    /// on this same node and would otherwise begin by treating whatever the failed attempt left in memory as
    /// already durable. It starts at the recorder the node was constructed with, which is durable by
    /// construction: either it records nothing, or the host restored it from what it had already written.
    /// </remarks>
    private QuePaxaRecorder<TValue> Persisted { get; set; }


    /// <summary>
    /// Applies <paramref name="request"/> to the node's recorder and returns the reply.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <returns>The reply to send back to the proposer.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A request that changes nothing leaves <see cref="Recorder"/> reference-identical to what it was, which
    /// <see cref="RunAsync"/> reads to decide whether the reply needs to be made durable first.
    /// </remarks>
    public RecordReply<TValue> Handle(RecordRequest<TValue> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        (QuePaxaRecorder<TValue> next, RecordSummary<TValue> summary) = Recorder.Record(request.Step, request.Proposal);
        Recorder = next;

        //The summary's first proposal is non-null here and nowhere else in general: a request cannot carry a
        //step below round one phase zero, and an initial register sits at step zero, so the first request a
        //recorder ever serves lands on the advancing branch and sets the first proposal, and every later
        //branch either leaves it alone or advances and resets it. That is what makes the conversion from the
        //nullable core type to the non-nullable wire type total at this point.
        return new RecordReply<TValue>(summary.Step, summary.First!, summary.PriorAggregate);
    }


    /// <summary>
    /// Drives the node over an inbound request stream, sending each reply to <paramref name="sendReply"/>
    /// until the stream ends or the token is signalled.
    /// </summary>
    /// <param name="requests">The inbound request stream.</param>
    /// <param name="sendReply">The reply sink — see <see cref="SendRecordReplyDelegate{TValue}"/>, a push writer over the chosen transport.</param>
    /// <param name="persistRecorder">
    /// An optional durability hook. When supplied, it is awaited before the matching reply is sent whenever the
    /// recorder is not already known to be durable, so the recorded step and first proposal are durable before
    /// they become observable. A request that changes nothing — one below the recorder's step, or an identical
    /// same-step re-delivery — leaves the recorder reference-identical and needs no further write. When
    /// <see langword="null"/>, replies are sent immediately, reproducing the in-memory behavior suitable for
    /// tests and ephemeral clusters.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the request stream ends.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="requests"/> or <paramref name="sendReply"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// If <paramref name="persistRecorder"/> throws, the exception propagates out of this method and the reply
    /// for that request is never sent — the correct fail-closed behavior, since an unpersisted first proposal
    /// must never be observed. A throwing <paramref name="sendReply"/> likewise propagates out and ends the
    /// loop: a node whose transport has failed cannot keep serving requests.
    /// </para>
    /// <para>
    /// The gate is durability rather than mutation, and the difference is only visible after a failed write.
    /// The loop remembers the last recorder it persisted rather than comparing against the state this request
    /// found. Comparing against the request would fail open on exactly the sequence the re-send rule makes
    /// ordinary: a request advances the recorder, the write fails and the reply is correctly withheld, the
    /// proposer re-delivers the identical request, the re-delivery changes nothing and so would skip the write,
    /// and the reply would then carry a first proposal that never reached the disk. Remembering what was
    /// persisted makes the retransmission retry the write instead, and costs nothing on the ordinary path,
    /// where the two references are already the same object.
    /// </para>
    /// </remarks>
    public async Task RunAsync(
        IAsyncEnumerable<RecordRequest<TValue>> requests,
        SendRecordReplyDelegate<TValue> sendReply,
        PersistRecorderDelegate<TValue>? persistRecorder = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(sendReply);

        await foreach(RecordRequest<TValue> request in requests.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            RecordReply<TValue> reply = Handle(request);

            if(persistRecorder is not null && !ReferenceEquals(Recorder, Persisted))
            {
                await persistRecorder(Recorder, cancellationToken).ConfigureAwait(false);
                Persisted = Recorder;
            }

            await sendReply(reply, cancellationToken).ConfigureAwait(false);
        }
    }
}
