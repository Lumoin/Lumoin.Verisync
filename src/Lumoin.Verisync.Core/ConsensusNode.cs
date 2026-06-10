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
/// after each request that changes the acceptor it makes the new state durable and only then sends the
/// reply, so an unpersisted promise is never observable. Omitting it (or supplying the no-durability
/// implementation) sends each reply immediately and is suitable for tests and ephemeral clusters. A host
/// that needs different sequencing drives the node itself instead: call <see cref="Handle"/>, persist
/// <see cref="Acceptor"/>, and only then send the reply.
/// </para>
/// </remarks>
public sealed class ConsensusNode<TValue>
{
    /// <summary>The current acceptor state.</summary>
    public FastAcceptor<TValue> Acceptor { get; private set; } = FastAcceptor<TValue>.Initial;


    /// <summary>
    /// Applies <paramref name="request"/> to the node's acceptor and returns the reply.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <returns>The reply to send back to the proposer.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the request is of an unknown kind.</exception>
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
    /// <param name="sendReply">The reply sink — a push writer over the chosen transport.</param>
    /// <param name="persistAcceptor">
    /// An optional durability hook. When supplied, it is awaited after every request that changes the acceptor
    /// and before the matching reply is sent, so the promise or accept is durable before it becomes observable.
    /// A request the acceptor rejects without changing its state (for example a stale prepare below the promise)
    /// returns the same immutable instance and is not persisted. When <see langword="null"/>, replies are sent
    /// immediately, reproducing the in-memory behavior suitable for tests and ephemeral clusters.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the request stream ends.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="requests"/> or <paramref name="sendReply"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// If <paramref name="persistAcceptor"/> throws, the exception propagates out of this method and the reply
    /// for that request is never sent — the correct fail-closed behavior, since an unpersisted promise must
    /// never be observed.
    /// </remarks>
    public async Task RunAsync(
        IAsyncEnumerable<ConsensusRequest<TValue>> requests,
        Func<ConsensusReply<TValue>, CancellationToken, ValueTask> sendReply,
        PersistAcceptorDelegate<TValue>? persistAcceptor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(sendReply);

        await foreach(ConsensusRequest<TValue> request in requests.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            FastAcceptor<TValue> before = Acceptor;
            ConsensusReply<TValue> reply = Handle(request);

            if(persistAcceptor is not null && !ReferenceEquals(Acceptor, before))
            {
                await persistAcceptor(Acceptor, cancellationToken).ConfigureAwait(false);
            }

            await sendReply(reply, cancellationToken).ConfigureAwait(false);
        }
    }
}
