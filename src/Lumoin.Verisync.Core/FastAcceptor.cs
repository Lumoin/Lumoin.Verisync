using System.Collections.Generic;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A Fast CASPaxos acceptor: the per-replica safety state. It is pre-promised to the shared fast-round
/// ballot, so a proposer can have a value accepted on the fast path without a preceding prepare.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// An acceptor is an immutable value; <see cref="Prepare(FastBallot)"/> and <see cref="Accept(FastBallot, TValue)"/>
/// return a new acceptor alongside their response. The accept rules mirror the reference engine: a retry of
/// the exact accepted (ballot, value) pair is idempotent, a ballot below the promise is rejected, and a
/// ballot equal to the accepted ballot but carrying a different value is rejected because a ballot may carry
/// only one value.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class FastAcceptor<TValue>
{
    private FastAcceptor(FastBallot promised, FastBallot acceptedBallot, TValue? acceptedValue)
    {
        Promised = promised;
        AcceptedBallot = acceptedBallot;
        AcceptedValue = acceptedValue;
    }


    /// <summary>An acceptor pre-promised to the initial fast-round ballot, having accepted nothing.</summary>
    public static FastAcceptor<TValue> Initial { get; } = new(FastBallot.InitialFast(), FastBallot.Zero, default);


    /// <summary>The highest ballot this acceptor has promised.</summary>
    public FastBallot Promised { get; }

    /// <summary>The ballot under which <see cref="AcceptedValue"/> was accepted, or <see cref="FastBallot.Zero"/> if none.</summary>
    public FastBallot AcceptedBallot { get; }

    /// <summary>The value this acceptor has accepted, or the default if none.</summary>
    public TValue? AcceptedValue { get; }


    /// <summary>
    /// Processes a prepare request: promises <paramref name="ballot"/> when it is at least the promised
    /// ballot, otherwise rejects it. The response always reports the currently accepted ballot and value so
    /// the proposer can recover an in-progress value.
    /// </summary>
    /// <param name="ballot">The proposing ballot.</param>
    /// <returns>The acceptor after the request and the response.</returns>
    public (FastAcceptor<TValue> Acceptor, FastPrepareResponse<TValue> Response) Prepare(FastBallot ballot)
    {
        if(ballot < Promised)
        {
            return (this, new FastPrepareResponse<TValue>(false, AcceptedBallot, AcceptedValue, Promised));
        }

        var promised = new FastAcceptor<TValue>(ballot, AcceptedBallot, AcceptedValue);

        return (promised, new FastPrepareResponse<TValue>(true, AcceptedBallot, AcceptedValue, FastBallot.Zero));
    }


    /// <summary>
    /// Processes an accept request for <paramref name="ballot"/> and <paramref name="value"/>.
    /// </summary>
    /// <param name="ballot">The proposing ballot.</param>
    /// <param name="value">The value to accept.</param>
    /// <returns>The acceptor after the request and whether it accepted.</returns>
    public (FastAcceptor<TValue> Acceptor, bool Accepted) Accept(FastBallot ballot, TValue value)
    {
        if(ballot == AcceptedBallot && EqualityComparer<TValue>.Default.Equals(value, AcceptedValue))
        {
            return (this, true);
        }

        if(ballot < Promised)
        {
            return (this, false);
        }

        if(ballot == AcceptedBallot)
        {
            return (this, false);
        }

        var accepted = new FastAcceptor<TValue>(Promised, ballot, value);

        return (accepted, true);
    }


    private string DebuggerDisplay => $"FastAcceptor: promised={Promised}, accepted={AcceptedBallot}";
}
