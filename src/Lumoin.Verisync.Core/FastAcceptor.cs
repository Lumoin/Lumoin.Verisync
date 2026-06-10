using System.Collections.Generic;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A Fast CASPaxos acceptor: the per-replica safety state. It is pre-promised to the shared fast-round
/// ballot, so a proposer can have a value accepted on the fast path without a preceding prepare.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// An acceptor is an immutable value; <see cref="Prepare(FastBallot)"/> and <see cref="Accept(FastBallot, TValue, FastBallot?)"/>
/// return a new acceptor alongside their response. The accept rules mirror the reference engine: a retry of
/// the exact accepted (ballot, value) pair is idempotent, a ballot below the promise is rejected, and a
/// ballot equal to the accepted ballot but carrying a different value is rejected because a ballot may carry
/// only one value. Accepting raises the promise to the accepted ballot, so the promise never trails the
/// accepted ballot — otherwise a stale lower-ballot accept arriving late could overwrite the record of a
/// possibly-chosen value. A fast ballot is accepted only while it equals the current promise: only the
/// pre-promised initial fast round ever satisfies this, because prepares promise classic ballots exclusively,
/// so a fast round beyond the initial one is never blind-writable — advancing past a contended fast round
/// always goes through a classic recovery, <em>or</em> through a piggybacked next ballot. A successful accept
/// may piggyback a next fast ballot: the promise then rises to that ballot too, establishing the next fast
/// round so it satisfies the equality rule and becomes blind-writable. This is exactly the coordination the
/// original Fast CASPaxos design uses to chain coordinator-free fast rounds; an acceptor that never saw the
/// piggyback keeps rejecting that fast ballot via the equality rule, so blind writes at an un-established fast
/// round remain impossible.
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
    /// Processes a prepare request: promises <paramref name="ballot"/> when it is a classic ballot at least
    /// the promised ballot, otherwise rejects it. The response always reports the currently accepted ballot
    /// and value so the proposer can recover an in-progress value.
    /// </summary>
    /// <param name="ballot">The proposing ballot.</param>
    /// <returns>The acceptor after the request and the response.</returns>
    /// <remarks>
    /// Fast ballots are rejected outright: promising one would re-open uncoordinated blind writes at that
    /// round, and nothing in the protocol performs the recovery a fast round needs before it is writable.
    /// Only the initial fast round is safe to blind-write, by construction of <see cref="Initial"/>.
    /// </remarks>
    public (FastAcceptor<TValue> Acceptor, FastPrepareResponse<TValue> Response) Prepare(FastBallot ballot)
    {
        if(ballot.IsFast || ballot < Promised)
        {
            return (this, new FastPrepareResponse<TValue>(false, AcceptedBallot, AcceptedValue, Promised));
        }

        var promised = new FastAcceptor<TValue>(ballot, AcceptedBallot, AcceptedValue);

        return (promised, new FastPrepareResponse<TValue>(true, AcceptedBallot, AcceptedValue, FastBallot.Zero));
    }


    /// <summary>
    /// Processes an accept request for <paramref name="ballot"/> and <paramref name="value"/>, optionally
    /// piggybacking <paramref name="next"/> as the next fast round to establish.
    /// </summary>
    /// <param name="ballot">The proposing ballot.</param>
    /// <param name="value">The value to accept.</param>
    /// <param name="next">
    /// An optional fast ballot to piggyback. On a successful accept the new promise becomes the maximum of
    /// the accepted ballot and this next ballot, so the next fast round is established and becomes
    /// blind-writable on this acceptor. A <see langword="null"/> value carries no piggyback.
    /// </param>
    /// <returns>The acceptor after the request and whether it accepted.</returns>
    /// <remarks>
    /// The piggybacked promise is exactly the coordination that makes the next fast round writable: an
    /// acceptor that took the raise will later satisfy the fast-ballot equality rule at <paramref name="next"/>
    /// and accept it without a prepare, while an acceptor that never saw the piggyback keeps rejecting that
    /// fast ballot via the same equality rule. Blind writes at an un-established fast round therefore remain
    /// impossible — the piggyback only ever <em>adds</em> a coordinated round, never re-opens an uncoordinated
    /// one. Every reject rule is evaluated against the original ballot and ignores <paramref name="next"/>
    /// entirely; only a successful accept (including an idempotent retry of the already-accepted pair) applies
    /// the raise, and a raise never lowers the promise because the new promise is taken as a maximum.
    /// </remarks>
    public (FastAcceptor<TValue> Acceptor, bool Accepted) Accept(FastBallot ballot, TValue value, FastBallot? next = null)
    {
        if(ballot == AcceptedBallot && EqualityComparer<TValue>.Default.Equals(value, AcceptedValue))
        {
            //An idempotent retry keeps the accepted pair identical, but a piggybacked next ballot must still
            //be able to raise the promise so the next fast round becomes writable even on a duplicate.
            FastBallot retryPromise = MaxBallot(Promised, next ?? FastBallot.Zero);
            FastAcceptor<TValue> retried = retryPromise == Promised
                ? this
                : new FastAcceptor<TValue>(retryPromise, AcceptedBallot, AcceptedValue);

            return (retried, true);
        }

        if(ballot < Promised)
        {
            return (this, false);
        }

        //A fast ballot above the promise belongs to a round no coordinator has initialized; blind-writing
        //it could overwrite a value chosen below. Classic ballots above the promise are the normal
        //accept-without-prepare case and remain acceptable.
        if(ballot.IsFast && ballot != Promised)
        {
            return (this, false);
        }

        if(ballot == AcceptedBallot)
        {
            return (this, false);
        }

        //The promise rises with the accept (ballot is at least the promise here) and, when a next ballot is
        //piggybacked, to that ballot too — the maximum keeps the invariant that the promise never trails the
        //accepted ballot while establishing the next fast round for a subsequent blind write.
        FastBallot promise = MaxBallot(ballot, next ?? FastBallot.Zero);
        var accepted = new FastAcceptor<TValue>(promise, ballot, value);

        return (accepted, true);
    }


    private static FastBallot MaxBallot(FastBallot left, FastBallot right) => left >= right ? left : right;


    private string DebuggerDisplay => $"FastAcceptor: promised={Promised}, accepted={AcceptedBallot}";
}
