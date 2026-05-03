namespace Lumoin.Verisync.Core;

/// <summary>
/// An acceptor's reply to a <see cref="PrepareRequest{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Promised">Whether the acceptor promised the proposing ballot.</param>
/// <param name="AcceptedBallot">The ballot of the value the acceptor has accepted, or <see cref="FastBallot.Zero"/> if none.</param>
/// <param name="AcceptedValue">The value the acceptor has accepted, or the default if none.</param>
/// <param name="ConflictingBallot">The higher ballot that caused a rejection, or <see cref="FastBallot.Zero"/> when promised.</param>
public sealed record PrepareReply<TValue>(bool Promised, FastBallot AcceptedBallot, TValue? AcceptedValue, FastBallot ConflictingBallot): ConsensusReply<TValue>;
