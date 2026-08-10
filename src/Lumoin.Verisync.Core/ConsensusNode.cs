using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A stateful Fast CASPaxos acceptor node: it applies incoming <see cref="ConsensusRequest{TValue}"/> messages
/// to its immutable <see cref="FastAcceptor{TValue}"/> and produces the matching <see cref="ConsensusReply{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The node is the boundary between the pure, immutable acceptor value and the stateful runtime: it holds the
/// current acceptor and replaces it on each request. <see cref="RunAsync"/> drives the node over any inbound
/// message stream and reply sink, so the same node runs over an in-memory channel or a socket unchanged.
/// A node processes its requests sequentially and is not safe for concurrent calls.
/// </para>
/// <para>
/// The node holds its acceptor in memory only. Paxos safety across a crash requires a promise or accept
/// to be durable <em>before</em> its reply leaves the process — a restarted node otherwise returns as
/// <see cref="FastAcceptor{TValue}.Initial"/> and will re-promise ballots it already superseded, breaking
/// agreement. Pass a <see cref="PersistAcceptorDelegate{TValue}"/> to <see cref="RunAsync"/> to get this:
/// before each reply it makes the acceptor durable unless the state the reply rests on already is, so an
/// unpersisted promise is never observable. Durability needs the restore as its other half: a restarting
/// host turns the persisted bytes back into an acceptor with <see cref="FastAcceptor{TValue}.FromState"/>
/// and constructs the node over it, so the node returns on the state it answered from rather than at the
/// initial acceptor. Omitting the delegate (or supplying the no-durability
/// implementation) sends each reply immediately and is suitable for tests and ephemeral clusters. A host
/// that needs different sequencing drives the node itself instead: call <see cref="Handle"/>, persist
/// <see cref="Acceptor"/>, and only then send the reply.
/// </para>
/// </remarks>
public sealed class ConsensusNode<TValue>
{
    /// <summary>
    /// Initializes a node whose acceptor starts at <see cref="FastAcceptor{TValue}.Initial"/>.
    /// </summary>
    /// <remarks>
    /// This is the node a host builds when it has nothing to restore. It seeds through the same path a
    /// restore does, over the initial acceptor, which needs no write because a node that lost everything
    /// returns exactly there.
    /// </remarks>
    public ConsensusNode(): this(FastAcceptor<TValue>.Initial)
    {
    }


    /// <summary>
    /// Initializes a node over <paramref name="acceptor"/>, which the node treats as already durable.
    /// </summary>
    /// <param name="acceptor">
    /// The acceptor this node starts from, which a restarting host builds with
    /// <see cref="FastAcceptor{TValue}.FromState"/> over the state it last made durable.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="acceptor"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The node owes no write for the acceptor it was constructed with: either it is the initial acceptor,
    /// or the host restored it from what it had already written. The first write a run owes is therefore the
    /// one for the first request that changes the acceptor, and a retransmission the restored acceptor
    /// answers idempotently costs none.
    /// </remarks>
    public ConsensusNode(FastAcceptor<TValue> acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);

        Acceptor = acceptor;
        Persisted = acceptor;
    }


    /// <summary>The current acceptor state.</summary>
    public FastAcceptor<TValue> Acceptor { get; private set; }


    /// <summary>
    /// The acceptor state <see cref="RunAsync"/> last made durable, which is what its durability gate compares
    /// against.
    /// </summary>
    /// <remarks>
    /// This is node state rather than loop state, because a host whose durable write failed restarts the loop
    /// on this same node and would otherwise begin by treating whatever the failed attempt left in memory as
    /// already durable. It starts at the acceptor the node was constructed with, which is durable by
    /// construction: either it is the initial acceptor, which needs no write because a node that lost
    /// everything returns exactly there, or the host restored it from what it had already written. The
    /// acceptor starts from the same reference, so the gate's comparison holds before the first request.
    /// </remarks>
    private FastAcceptor<TValue> Persisted { get; set; }


    /// <summary>
    /// Applies <paramref name="request"/> to the node's acceptor and returns the reply.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <returns>The reply to send back to the proposer.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the request is of an unknown kind.</exception>
    /// <remarks>
    /// A request that changes nothing leaves <see cref="Acceptor"/> reference-identical to what it was, so a
    /// state once persisted stays reference-equal to what <see cref="RunAsync"/> last made durable, which is
    /// how its gate detects that a reply needs no further write.
    /// </remarks>
    public ConsensusReply<TValue> Handle(ConsensusRequest<TValue> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if(request is PrepareRequest<TValue> prepare)
        {
            (FastAcceptor<TValue> next, FastPrepareResponse<TValue> response) = Acceptor.Prepare(prepare.Ballot);
            Acceptor = next;

            return new PrepareReply<TValue>(response.Promised, response.AcceptedBallot, response.AcceptedValue, response.ConflictingBallot);
        }

        if(request is AcceptRequest<TValue> accept)
        {
            (FastAcceptor<TValue> next, bool accepted) = Acceptor.Accept(accept.Ballot, accept.Value, accept.Next);
            Acceptor = next;

            return new AcceptReply<TValue>(accepted, accepted ? accept.Ballot : Acceptor.Promised);
        }

        throw new ArgumentException($"Unknown request kind '{request.GetType().Name}'.", nameof(request));
    }


    /// <summary>
    /// Drives the node over an inbound request stream, sending each reply to <paramref name="sendReply"/> until
    /// the stream ends or the token is signalled.
    /// </summary>
    /// <param name="requests">The inbound request stream.</param>
    /// <param name="sendReply">The reply sink — see <see cref="SendReplyDelegate{TValue}"/>, a push writer over the chosen transport.</param>
    /// <param name="persistAcceptor">
    /// An optional durability hook. When supplied, it is awaited before the matching reply is sent whenever the
    /// acceptor is not already known to be durable, so the promise or accept is durable before it becomes
    /// observable. A request the acceptor rejects without changing its state (for example a stale prepare below
    /// the promise) leaves the acceptor reference-identical and, once that state is durable, needs no further
    /// write. When
    /// <see langword="null"/>, replies are sent immediately, reproducing the in-memory behavior suitable for
    /// tests and ephemeral clusters.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the request stream ends.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="requests"/> or <paramref name="sendReply"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// If <paramref name="persistAcceptor"/> throws, the exception propagates out of this method and the reply
    /// for that request is never sent — the correct fail-closed behavior, since an unpersisted promise must
    /// never be observed. A throwing <paramref name="sendReply"/> likewise propagates out and ends the loop:
    /// a node whose transport has failed cannot keep serving requests.
    /// </para>
    /// <para>
    /// The gate is durability rather than mutation, and the two come apart only where the acceptor has moved
    /// past what was last made durable without the current request changing it — after a failed write, after
    /// requests handled directly through <see cref="Handle"/>, or after a run without a delegate. The loop
    /// remembers the last acceptor it persisted rather than comparing against the state this request
    /// found. Comparing against the request would fail open on exactly the sequence the idempotent accept
    /// retry makes ordinary: an accept advances the acceptor, the write fails and the reply is correctly
    /// withheld, the proposer re-delivers the identical accept, the re-delivery returns the same instance and
    /// so would skip the write, and the reply would then announce an accept that never reached the disk.
    /// Remembering what was persisted makes the retransmission retry the write instead, and costs nothing on
    /// the ordinary path, where the two references are already the same object.
    /// </para>
    /// </remarks>
    public async Task RunAsync(
        IAsyncEnumerable<ConsensusRequest<TValue>> requests,
        SendReplyDelegate<TValue> sendReply,
        PersistAcceptorDelegate<TValue>? persistAcceptor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(sendReply);

        await foreach(ConsensusRequest<TValue> request in requests.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ConsensusReply<TValue> reply = Handle(request);

            if(persistAcceptor is not null && !ReferenceEquals(Acceptor, Persisted))
            {
                await persistAcceptor(Acceptor, cancellationToken).ConfigureAwait(false);
                Persisted = Acceptor;
            }

            await sendReply(reply, cancellationToken).ConfigureAwait(false);
        }
    }
}
