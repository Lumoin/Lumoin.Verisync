using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A CASPaxos acceptor: the durable per-replica state that promises ballots and accepts values. It is the
/// unit of safety — a value is chosen only when a majority of acceptors have accepted it under the same ballot.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// An acceptor is an immutable value; <see cref="Prepare(Ballot)"/> and <see cref="Accept(Ballot, TValue)"/>
/// return a new acceptor alongside their response rather than mutating in place.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class Acceptor<TValue>
{
    private Acceptor(Ballot? promise, Ballot? acceptedBallot, TValue? acceptedValue)
    {
        Promise = promise;
        AcceptedBallot = acceptedBallot;
        AcceptedValue = acceptedValue;
    }


    /// <summary>An acceptor that has made no promise and accepted no value.</summary>
    public static Acceptor<TValue> Initial { get; } = new(null, null, default);


    /// <summary>The highest ballot this acceptor has promised, or <see langword="null"/> if none.</summary>
    public Ballot? Promise { get; }

    /// <summary>The ballot under which <see cref="AcceptedValue"/> was accepted, or <see langword="null"/> if none.</summary>
    public Ballot? AcceptedBallot { get; }

    /// <summary>The value this acceptor has accepted, or the default if none.</summary>
    public TValue? AcceptedValue { get; }


    /// <summary>
    /// Processes a prepare request for <paramref name="ballot"/>: promises it when it is higher than any
    /// previously promised ballot, otherwise rejects it.
    /// </summary>
    /// <param name="ballot">The proposing ballot.</param>
    /// <returns>The acceptor after the request and the response to return to the proposer.</returns>
    public (Acceptor<TValue> Acceptor, PrepareResponse<TValue> Response) Prepare(Ballot ballot)
    {
        if(Promise is null || ballot > Promise.Value)
        {
            var promised = new Acceptor<TValue>(ballot, AcceptedBallot, AcceptedValue);

            return (promised, new PrepareResponse<TValue>(true, AcceptedBallot, AcceptedValue));
        }

        return (this, new PrepareResponse<TValue>(false, null, default));
    }


    /// <summary>
    /// Processes an accept request for <paramref name="ballot"/> and <paramref name="value"/>: accepts when
    /// the ballot is at least the promised ballot, otherwise rejects it.
    /// </summary>
    /// <param name="ballot">The proposing ballot.</param>
    /// <param name="value">The value to accept.</param>
    /// <returns>The acceptor after the request and whether it accepted.</returns>
    public (Acceptor<TValue> Acceptor, bool Accepted) Accept(Ballot ballot, TValue value)
    {
        if(Promise is null || ballot >= Promise.Value)
        {
            var accepted = new Acceptor<TValue>(ballot, ballot, value);

            return (accepted, true);
        }

        return (this, false);
    }


    private string DebuggerDisplay => $"Acceptor: promise={Promise?.ToString() ?? "(none)"}, accepted={AcceptedBallot?.ToString() ?? "(none)"}";
}
