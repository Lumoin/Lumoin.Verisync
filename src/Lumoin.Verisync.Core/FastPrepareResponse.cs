namespace Lumoin.Verisync.Core;

/// <summary>
/// A Fast CASPaxos acceptor's response to a prepare request.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Promised">
/// <see langword="true"/> if the acceptor promised the proposing ballot; <see langword="false"/> if it
/// rejected the ballot because it had already promised a higher one.
/// </param>
/// <param name="AcceptedBallot">The ballot of the value the acceptor has accepted, or <see cref="FastBallot.Zero"/> if none.</param>
/// <param name="AcceptedValue">The value the acceptor has accepted, or the default if none.</param>
/// <param name="ConflictingBallot">The higher ballot that caused a rejection, or <see cref="FastBallot.Zero"/> when promised.</param>
/// <remarks>
/// During recovery the proposer groups responses by <see cref="AcceptedBallot"/>; for the highest fast
/// ballot seen it tallies the distinct <see cref="AcceptedValue"/>s by count to recover the fast-round
/// winner, the key step that preserves safety after a contended fast round.
/// </remarks>
public sealed record FastPrepareResponse<TValue>(bool Promised, FastBallot AcceptedBallot, TValue? AcceptedValue, FastBallot ConflictingBallot);
