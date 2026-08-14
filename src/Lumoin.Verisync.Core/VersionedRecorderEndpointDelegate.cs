using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Sends a versioned record request to a single recorder host and awaits its reply. This is a versioned
/// register's only view of a recorder, and the transport behind it may be an in-process call, an in-memory
/// channel, or a socket.
/// </summary>
/// <typeparam name="TValue">The consensus value type, which a versioned register instantiates at <see cref="VersionedValue{TValue}"/>.</typeparam>
/// <param name="request">The request to send.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The recorder host's reply.</returns>
/// <remarks>
/// <para>
/// Every contract <see cref="RecorderEndpointDelegate{TValue}"/> states binds here unchanged: a transport may
/// retransmit freely, it must complete when the token is signalled, it owes per-call correlation because calls
/// for consecutive steps of one instance overlap, and it must not complete the returned operation while
/// holding a lock this delegate itself needs.
/// </para>
/// <para>
/// The envelope adds one obligation and removes none. A reply must carry the version of the request it
/// answers, and a versioned register checks it, so a mis-routed reply is a transport fault rather than an
/// answer. Without the check a reply from another instance would be counted toward this instance's quorum,
/// and a decision taken on an answer set that is a minority of the instance breaks the majority intersection
/// agreement rests on.
/// </para>
/// <para>
/// A host may refuse to serve an instance, and it says so by faulting rather than by answering. A recorder
/// host serves the one instance it can derive a leader for and throws for any other; the fault reaches a
/// proposer as an unreachable recorder, which is retried within the attempt budget and otherwise concludes a
/// missed quorum. Nothing in the protocol learns that a refusal happened, because the protocol has no refusal
/// path.
/// </para>
/// </remarks>
public delegate ValueTask<VersionedRecordReply<TValue>> VersionedRecorderEndpointDelegate<TValue>(VersionedRecordRequest<TValue> request, CancellationToken cancellationToken);
