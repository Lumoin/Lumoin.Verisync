using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a consensus request to a single acceptor and awaits its reply. This is the proposer's only view of
/// an acceptor — the transport behind it may be an in-process call, an in-memory channel, or a socket, which
/// is what makes the proposer transport-agnostic.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="request">The request to send.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The acceptor's reply.</returns>
public delegate ValueTask<ConsensusReply<TValue>> ConsensusEndpointDelegate<TValue>(ConsensusRequest<TValue> request, CancellationToken cancellationToken);
