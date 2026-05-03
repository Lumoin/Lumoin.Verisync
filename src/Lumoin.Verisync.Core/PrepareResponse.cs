namespace Lumoin.Verisync.Core;

/// <summary>
/// An acceptor's response to a CASPaxos prepare request.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Promised">
/// <see langword="true"/> if the acceptor promised the proposing ballot; <see langword="false"/> if it
/// rejected the ballot because it had already promised a higher one.
/// </param>
/// <param name="AcceptedBallot">The ballot of the value the acceptor has accepted, or <see langword="null"/> if it has accepted none.</param>
/// <param name="AcceptedValue">The value the acceptor has accepted, or the default if it has accepted none.</param>
/// <remarks>
/// When a quorum promises, the proposer adopts the value carried by the highest <see cref="AcceptedBallot"/>
/// across the responses — this is what recovers an in-progress value a previous proposer may have written
/// before applying its own change function.
/// </remarks>
public sealed record PrepareResponse<TValue>(bool Promised, Ballot? AcceptedBallot, TValue? AcceptedValue);
