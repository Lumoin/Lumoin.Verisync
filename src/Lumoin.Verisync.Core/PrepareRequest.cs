namespace Lumoin.Verisync.Core;

/// <summary>
/// A request that an acceptor promise a ballot, returning whatever value it has already accepted so the
/// proposer can recover an in-progress value.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Ballot">The proposing ballot.</param>
public sealed record PrepareRequest<TValue>(FastBallot Ballot): ConsensusRequest<TValue>;
