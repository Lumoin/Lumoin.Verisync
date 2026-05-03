namespace Lumoin.Verisync.Core;

/// <summary>
/// An acceptor's reply to an <see cref="AcceptRequest{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Accepted">Whether the acceptor accepted the value.</param>
/// <param name="Ballot">The proposing ballot when accepted, or the acceptor's higher promise when rejected.</param>
public sealed record AcceptReply<TValue>(bool Accepted, FastBallot Ballot): ConsensusReply<TValue>;
