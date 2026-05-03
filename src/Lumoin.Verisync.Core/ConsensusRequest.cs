namespace Lumoin.Verisync.Core;

/// <summary>
/// A request sent from a proposer to an acceptor in the Fast CASPaxos protocol — either a
/// <see cref="PrepareRequest{TValue}"/> or an <see cref="AcceptRequest{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
public abstract record ConsensusRequest<TValue>;
