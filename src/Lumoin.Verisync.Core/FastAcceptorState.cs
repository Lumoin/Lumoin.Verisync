namespace Lumoin.Verisync.Core;

/// <summary>
/// The durable state of a <see cref="FastAcceptor{TValue}"/>: the promise, the accepted ballot and the
/// accepted value an acceptor must have on stable storage before any reply that depends on them leaves the
/// process. Obtain it with <see cref="FastAcceptor{TValue}.ToState"/> and reconstruct with
/// <see cref="FastAcceptor{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Promised">The highest ballot the acceptor has promised.</param>
/// <param name="AcceptedBallot">
/// The ballot <paramref name="AcceptedValue"/> was accepted under, or <see cref="FastBallot.Zero"/> if none.
/// </param>
/// <param name="AcceptedValue">
/// The accepted value, or the default if none. The default is also a legitimate accepted value at a non-zero
/// accepted ballot, so whether anything was accepted is told by the ballot and never by the value.
/// </param>
/// <remarks>
/// <para>
/// These three fields are exactly the state of <see cref="FastAcceptor{TValue}"/> and nothing else. All
/// three are durable and not the promise alone: a prepare reply carries the accepted ballot and value so a
/// recovering proposer can decide on them, so a host that persisted the promise alone would answer a
/// recovery from fields it never wrote.
/// </para>
/// <para>
/// No configuration accompanies the state and no replica identity is inside it, because an acceptor carries
/// neither: <see cref="FastAcceptor{TValue}.Initial"/> is parameterless and identical at every replica, and
/// a proposer addresses acceptors positionally through its endpoints. Which replica a snapshot belongs to is
/// therefore the host's filing obligation, and the restore cannot check it.
/// </para>
/// <para>
/// The persist-before-reply obligation is what makes this durable state rather than a convenience: an
/// acceptor that restarted below its promise would re-promise ballots it already superseded.
/// <see cref="PersistAcceptorDelegate{TValue}"/> is the hook that sequences the write ahead of the reply.
/// </para>
/// </remarks>
public sealed record FastAcceptorState<TValue>(
    FastBallot Promised,
    FastBallot AcceptedBallot,
    TValue? AcceptedValue);
