namespace Lumoin.Verisync.Core;

/// <summary>
/// A request that an acceptor accept a value under a ballot. On the fast path this is sent directly,
/// without a preceding prepare, because acceptors are pre-promised to the fast ballot.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Ballot">The proposing ballot.</param>
/// <param name="Value">The value to accept.</param>
/// <param name="Next">
/// An optional fast ballot to piggyback: a successful accept also raises the acceptor's promise to it, so a
/// subsequent fast write at that ballot can be blind-written on the acceptors that saw the raise. A
/// <see langword="null"/> value (the default) carries no piggyback and leaves the promise at the accepted
/// ballot. The piggyback is a liveness optimization only — safety never depends on how many acceptors saw it.
/// </param>
public sealed record AcceptRequest<TValue>(FastBallot Ballot, TValue Value, FastBallot? Next = null): ConsensusRequest<TValue>;
