namespace Lumoin.Verisync.Core;

/// <summary>
/// A request that an acceptor accept a value under a ballot. On the fast path this is sent directly,
/// without a preceding prepare, because acceptors are pre-promised to the fast ballot.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Ballot">The proposing ballot.</param>
/// <param name="Value">The value to accept.</param>
public sealed record AcceptRequest<TValue>(FastBallot Ballot, TValue Value): ConsensusRequest<TValue>;
