using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a <see cref="ConsensusReply{TValue}"/> back to the proposer that issued the request: it is the
/// reply sink of a <see cref="ConsensusNode{TValue}"/>, a push writer over the chosen transport — an
/// in-memory channel, a socket, or any duplex pipe.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="reply">The reply to send back to the proposer.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the reply has been handed to the transport.</returns>
/// <remarks>
/// The node awaits the sink after handling each request — and, when a durability hook is supplied, only
/// after the new acceptor state is durable, so an unpersisted promise is never observable. Throwing fails
/// closed: the exception propagates out of <see cref="ConsensusNode{TValue}.RunAsync"/> and ends the node
/// loop, since a node whose transport has failed cannot keep serving requests.
/// </remarks>
public delegate ValueTask SendReplyDelegate<TValue>(ConsensusReply<TValue> reply, CancellationToken cancellationToken);
