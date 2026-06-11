namespace Lumoin.Verisync.Core;

/// <summary>
/// An acceptor's reply to an <see cref="AcceptRequest{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Accepted">Whether the acceptor accepted the value.</param>
/// <param name="Ballot">
/// The accepted ballot when accepted; otherwise the acceptor's current promise, which is at least — not
/// necessarily above — the rejected ballot, because an accept can be rejected at its own ballot when that
/// ballot already carries a different value.
/// </param>
public sealed record AcceptReply<TValue>(bool Accepted, FastBallot Ballot): ConsensusReply<TValue>;
