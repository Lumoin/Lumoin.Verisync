using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a <see cref="RecordReply{TValue}"/> back to the proposer that issued the request. It is the reply
/// sink of a <see cref="QuePaxaNode{TValue}"/>, a push writer over the chosen transport, such as an in-memory
/// channel, a socket, or any duplex pipe.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="reply">The reply to send back to the proposer.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the reply has been handed to the transport.</returns>
/// <remarks>
/// The node awaits the sink after handling each request, and, when a durability hook is supplied and the
/// request changed the recorder, only after the new recorder state is durable, so a step and a first proposal
/// a proposer has read are never unpersisted. Throwing fails closed: the exception propagates out of
/// <see cref="QuePaxaNode{TValue}.RunAsync"/> and ends the node loop.
/// </remarks>
public delegate ValueTask SendRecordReplyDelegate<TValue>(RecordReply<TValue> reply, CancellationToken cancellationToken);
