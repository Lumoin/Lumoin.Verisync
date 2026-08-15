using System;
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
    /// the raise, and a raise never lowers the promise because the new promise is taken as a maximum. The
    /// proposer-side counterpart of this rule is the arming rule documented on
    /// <see cref="FastProposer{TValue}.TryFastWriteAsync"/>: because an acceptor that missed the raise keeps
    /// rejecting, a round armed on fewer than a fast quorum is one no later fast write can complete.
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


    /// <summary>
    /// Snapshots the acceptor's durable state: the promise, the accepted ballot and the accepted value.
    /// </summary>
    /// <returns>The durable state to make stable before any dependent reply is sent.</returns>
    /// <remarks>
    /// No copy is taken, because every field is immutable. Unlike <see cref="QuePaxaRecorder{TValue}.ToState"/>,
    /// this pair is an inverse everywhere including the bottom of the range: <see cref="Initial"/> snapshots
    /// and restores, because the initial acceptor is not unwritten but pre-promised to the initial fast
    /// ballot, which is exactly the state a node that lost everything returns as.
    /// </remarks>
    public FastAcceptorState<TValue> ToState() => new(Promised, AcceptedBallot, AcceptedValue);


    /// <summary>
    /// Reconstructs an acceptor from durable <paramref name="state"/>, refusing fail-closed every state no
    /// acceptor can hold.
    /// </summary>
    /// <param name="state">The durable state to restore.</param>
    /// <returns>An acceptor standing at the restored promise.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the durable state is one no acceptor can hold: a
    /// <see cref="FastAcceptorState{TValue}.Promised"/> below <see cref="FastBallot.InitialFast"/>; a
    /// <see cref="FastAcceptorState{TValue}.AcceptedBallot"/> that is neither <see cref="FastBallot.Zero"/>
    /// nor at least <see cref="FastBallot.InitialFast"/>; an accepted ballot above the promise; or a
    /// non-default <see cref="FastAcceptorState{TValue}.AcceptedValue"/> under the zero accepted ballot.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The rules are read off <see cref="Prepare"/> and <see cref="Accept"/>: the promise starts pre-promised
    /// at the initial fast ballot and only ever rises, and the accepted ballot is written only beside the
    /// value and only by an accept whose ballot stood at or above the promise. That makes them exact in both
    /// directions — they refuse every state those transitions cannot produce and admit every state they can —
    /// so a state a host produced through this type restores unchanged, including the initial acceptor. Two
    /// of the rules are range checks over a single slot, which would normally live in a value constructor,
    /// but <see cref="FastBallot"/>'s constructor is public and unvalidated by necessity — the zero ballot is
    /// its default and it is the ballot the wire carries — so the restore owns them.
    /// </para>
    /// <para>
    /// The rules read what <see cref="Accept"/> can produce, not what this library's proposer chooses to
    /// send: a promise raised by a classic ballot arriving as a piggybacked next ballot is a state
    /// <see cref="Accept"/> produces and this library's proposer never asks for, and the restore admits it
    /// because refusing it would refuse a state the type can hold. A fast accepted round above the first and
    /// a fast accepted value under a later classic promise are the protocol's ordinary shapes, and the
    /// restore admits them the same way.
    /// </para>
    /// <para>
    /// The rules read one state, and that bounds what they refuse. A per-field mix of two states a faithful
    /// host wrote can itself be a state an acceptor can hold, so it restores under these rules and still
    /// contradicts a reply already sent from the older of its sources. Detecting that needs history no
    /// snapshot carries, so the rules are not a substitute for the store landing the write whole, which is
    /// <see cref="PersistAcceptorDelegate{TValue}"/>'s obligation.
    /// </para>
    /// <para>
    /// The state carries no replica identity, because an acceptor has none, so restoring one replica's
    /// snapshot onto another passes every rule while making two acceptors report an accept only one of them
    /// made. Which snapshot belongs to which replica is the host's filing obligation, not a rule this factory
    /// can own. The restore always allocates; that an initial-equal state comes back as a fresh instance
    /// rather than <see cref="Initial"/> is deliberately not a contract.
    /// </para>
    /// </remarks>
    public static FastAcceptor<TValue> FromState(FastAcceptorState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if(state.Promised < FastBallot.InitialFast())
        {
            throw new StateRestoreException(StateRestoreRefusal.AcceptorPromiseBelowInitialBallot, $"A restored promise cannot stand below the initial fast ballot, got {Describe(state.Promised)}. An acceptor is pre-promised to that ballot, a prepare promises only a ballot at or above the standing promise, and an accept raises the promise rather than lowering it, so no acceptor holds a promise below it.", nameof(state));
        }

        if(!state.AcceptedBallot.IsZero && state.AcceptedBallot < FastBallot.InitialFast())
        {
            throw new StateRestoreException(StateRestoreRefusal.AcceptorAcceptedBallotBelowInitialBallot, $"A restored accepted ballot is either the zero ballot or at least the initial fast ballot, got {Describe(state.AcceptedBallot)}. An accept records the ballot it accepted, which stood at or above the promise, and a promise never stands below the initial fast ballot, so the only accepted ballot below it is the zero ballot an acceptor that accepted nothing carries.", nameof(state));
        }

        if(state.AcceptedBallot > state.Promised)
        {
            throw new StateRestoreException(StateRestoreRefusal.AcceptorPromiseTrailsAcceptedBallot, $"A restored promise cannot trail the accepted ballot, got {DescribeTrailingContrast(state.Promised, state.AcceptedBallot)}. Accepting raises the promise to at least the accepted ballot and nothing lowers it, which is what stops a stale lower-ballot accept arriving late from overwriting the record of a possibly-chosen value.", nameof(state));
        }

        if(state.AcceptedBallot.IsZero && !EqualityComparer<TValue>.Default.Equals(state.AcceptedValue, default))
        {
            throw new StateRestoreException(StateRestoreRefusal.AcceptorValueWithoutAcceptedBallot, "A restored acceptor holding the zero accepted ballot cannot carry a value, because the accepted ballot and the accepted value are assigned together and only by an accept, whose ballot stands at or above the promise and so is never the zero ballot.", nameof(state));
        }

        return new FastAcceptor<TValue>(state.Promised, state.AcceptedBallot, state.AcceptedValue);
    }


    private static FastBallot MaxBallot(FastBallot left, FastBallot right) => left >= right ? left : right;


    private static string Describe(FastBallot ballot) => ballot.IsFast
        ? $"round {ballot.Round} with no proposer"
        : $"round {ballot.Round} with a proposer";


    //Two classic ballots at one round differ only by proposer, which Describe deliberately does not render,
    //so that shape gets its own sentence naming the discriminating dimension without identity bytes.
    private static string DescribeTrailingContrast(FastBallot promised, FastBallot accepted) =>
        promised.Round == accepted.Round && !promised.IsFast && !accepted.IsFast
            ? $"a promise and an accepted ballot both at round {promised.Round}, each with a proposer, the accepted ballot's proposer ordering above the promise's"
            : $"a promise at {Describe(promised)} under an accepted ballot at {Describe(accepted)}";


    private string DebuggerDisplay => $"FastAcceptor: promised={Promised}, accepted={AcceptedBallot}";
}
