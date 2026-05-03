namespace Lumoin.Verisync.Core;

/// <summary>
/// A reply sent from an acceptor to a proposer — either a <see cref="PrepareReply{TValue}"/> or an
/// <see cref="AcceptReply{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
public abstract record ConsensusReply<TValue>;
