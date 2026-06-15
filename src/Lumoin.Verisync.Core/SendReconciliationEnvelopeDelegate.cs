using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a <see cref="ReconciliationEnvelope{TElement}"/> to the session's single peer: the outbound transport
/// edge of an <see cref="AntiEntropySession{TElement}"/>, a push writer over the chosen transport — an
/// in-memory channel, a socket, or any duplex pipe. A session runs point-to-point over a dedicated channel, so
/// the envelope carries no peer address and this sink addresses no peer.
/// </summary>
/// <typeparam name="TElement">The application element type carried by an elements payload.</typeparam>
/// <param name="envelope">The envelope to send.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes once the envelope has been handed to the transport.</returns>
/// <remarks>
/// The session awaits this sink only from its single consumer loop, so every send is serialized by
/// construction and an implementation needs no write synchronization of its own. Throwing fails closed: the
/// exception propagates out of <see cref="AntiEntropySession{TElement}.RunAsync"/> and ends the loop, since a
/// session whose transport has failed cannot keep serving.
/// </remarks>
public delegate ValueTask SendReconciliationEnvelopeDelegate<TElement>(ReconciliationEnvelope<TElement> envelope, CancellationToken cancellationToken);
