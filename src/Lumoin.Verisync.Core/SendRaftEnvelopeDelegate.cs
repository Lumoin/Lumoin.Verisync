using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a <see cref="RaftEnvelope{TCommand}"/> to a single addressed peer: the outbound transport edge of a
/// <see cref="RaftRunner{TCommand}"/>, a push writer over the chosen transport — an in-memory channel, a
/// socket, or any duplex pipe. The runner addresses one peer per call rather than broadcasting, so a
/// per-follower request reaches exactly its follower.
/// </summary>
/// <typeparam name="TCommand">The application command type replicated by the log.</typeparam>
/// <param name="to">The peer the envelope is addressed to.</param>
/// <param name="envelope">The envelope to send.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the envelope has been handed to the transport.</returns>
/// <remarks>
/// The runner awaits this sink only after the durability hook (when supplied) has made the node's new state
/// durable, so an unpersisted vote or appended entry is never observable to a peer. Throwing fails closed:
/// the exception propagates out of <see cref="RaftRunner{TCommand}.RunAsync"/> and ends the runner loop,
/// since a node whose transport has failed cannot keep serving.
/// </remarks>
public delegate ValueTask SendRaftEnvelopeDelegate<TCommand>(ReplicaId to, RaftEnvelope<TCommand> envelope, CancellationToken cancellationToken);
