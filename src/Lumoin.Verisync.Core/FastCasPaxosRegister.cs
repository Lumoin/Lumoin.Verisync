using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A Fast CASPaxos register: a value-agnostic register that commits uncontended writes in one round-trip on
/// a leaderless fast path, and falls back to a leadered classic recovery round when concurrent proposers
/// conflict.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// Acceptors are pre-promised to the shared fast ballot, so <see cref="ProposeFast(FastBallot, TValue)"/>
/// has acceptors accept directly without a prepare. A value is fast-committed when a <em>fast quorum</em> —
/// a supermajority of <c>(3N + 3) / 4</c> acceptors — accept the same value. The larger quorum guarantees
/// that any later classic recovery quorum still observes the fast-round winner as dominant.
/// </para>
/// <para>
/// When the fast round splits, <see cref="Recover(FastBallot, Func{TValue, TValue})"/> runs a classic
/// ballot: it prepares a majority, recovers the value by tallying the distinct values reported for the
/// highest fast ballot and adopting the most frequent, then commits the change at the classic ballot. This
/// is an in-memory model of the protocol's safety core; the networked proposer, retries, and mode-switching
/// policy live above the core.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class FastCasPaxosRegister<TValue>
{
    private ImmutableArray<FastAcceptor<TValue>> Acceptors { get; }


    private FastCasPaxosRegister(ImmutableArray<FastAcceptor<TValue>> acceptors)
    {
        Acceptors = acceptors;
    }


    /// <summary>
    /// Creates a register with <paramref name="acceptorCount"/> acceptors, each pre-promised to the fast ballot.
    /// </summary>
    /// <param name="acceptorCount">The number of acceptors.</param>
    /// <returns>A new register.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="acceptorCount"/> is less than one.</exception>
    public static FastCasPaxosRegister<TValue> WithAcceptors(int acceptorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acceptorCount, 1);

        ImmutableArray<FastAcceptor<TValue>>.Builder builder = ImmutableArray.CreateBuilder<FastAcceptor<TValue>>(acceptorCount);
        for(int i = 0; i < acceptorCount; i++)
        {
            builder.Add(FastAcceptor<TValue>.Initial);
        }

        return new FastCasPaxosRegister<TValue>(builder.ToImmutable());
    }


    /// <summary>The number of acceptors in the register.</summary>
    public int AcceptorCount => Acceptors.Length;

    /// <summary>The fast-quorum size: a supermajority of <c>(3N + 3) / 4</c>.</summary>
    public int FastQuorum => ((3 * Acceptors.Length) + 3) / 4;

    /// <summary>The classic-quorum size: a strict majority.</summary>
    public int ClassicQuorum => (Acceptors.Length / 2) + 1;


    /// <summary>Whether <paramref name="acceptedCount"/> acceptors accepting the same value form a fast quorum.</summary>
    /// <param name="acceptedCount">The number of acceptors that accepted the value.</param>
    public bool IsFastQuorum(int acceptedCount) => (4 * acceptedCount) >= (3 * Acceptors.Length);


    /// <summary>
    /// Proposes <paramref name="value"/> on the fast path to every acceptor.
    /// </summary>
    /// <param name="fastBallot">The fast-round ballot. Must be a fast ballot.</param>
    /// <param name="value">The value to propose.</param>
    /// <returns>The register after the proposal and the number of acceptors that accepted.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="fastBallot"/> is not a fast ballot.</exception>
    public (FastCasPaxosRegister<TValue> Register, int AcceptedCount) ProposeFast(FastBallot fastBallot, TValue value)
    {
        return ProposeFastReaching(fastBallot, value, Enumerable.Range(0, Acceptors.Length).ToImmutableArray());
    }


    /// <summary>
    /// Proposes <paramref name="value"/> on the fast path to the acceptors at the given indices, modelling a
    /// proposer's message reaching only part of the cluster — the way concurrent proposers split a fast round.
    /// </summary>
    /// <param name="fastBallot">The fast-round ballot. Must be a fast ballot.</param>
    /// <param name="value">The value to propose.</param>
    /// <param name="acceptorIndices">The indices of the acceptors the proposal reaches.</param>
    /// <returns>The register after the proposal and the number of acceptors that accepted.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="fastBallot"/> is not a fast ballot or an index is out of range.</exception>
    public (FastCasPaxosRegister<TValue> Register, int AcceptedCount) ProposeFastReaching(FastBallot fastBallot, TValue value, ImmutableArray<int> acceptorIndices)
    {
        if(!fastBallot.IsFast)
        {
            throw new ArgumentException("A fast proposal requires a fast ballot.", nameof(fastBallot));
        }

        ImmutableArray<FastAcceptor<TValue>>.Builder working = Acceptors.ToBuilder();
        int accepted = 0;
        foreach(int index in acceptorIndices)
        {
            if(index < 0 || index >= working.Count)
            {
                throw new ArgumentException($"Acceptor index {index} is out of range.", nameof(acceptorIndices));
            }

            (FastAcceptor<TValue> acceptor, bool ok) = working[index].Accept(fastBallot, value);
            working[index] = acceptor;
            if(ok)
            {
                accepted++;
            }
        }

        return (new FastCasPaxosRegister<TValue>(working.ToImmutable()), accepted);
    }


    /// <summary>
    /// Runs a classic recovery round under <paramref name="classicBallot"/>: prepares a majority, recovers the
    /// current value (tallying the fast-round winner when the highest accepted ballot is a fast ballot), applies
    /// <paramref name="update"/> to it, and commits the result.
    /// </summary>
    /// <param name="classicBallot">The classic recovery ballot. Must be a proposer-owned (non-fast) ballot.</param>
    /// <param name="update">The change function applied to the recovered value.</param>
    /// <returns>The register after recovery and the outcome.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="classicBallot"/> is a fast ballot.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="update"/> is <see langword="null"/>.</exception>
    public (FastCasPaxosRegister<TValue> Register, ChangeOutcome<TValue> Outcome) Recover(FastBallot classicBallot, Func<TValue?, TValue> update)
    {
        if(classicBallot.IsFast)
        {
            throw new ArgumentException("Recovery requires a classic (proposer-owned) ballot.", nameof(classicBallot));
        }

        ArgumentNullException.ThrowIfNull(update);

        ImmutableArray<FastAcceptor<TValue>>.Builder working = Acceptors.ToBuilder();
        var responses = new List<FastPrepareResponse<TValue>>(working.Count);
        int promises = 0;
        FastBallot highestAccepted = FastBallot.Zero;
        TValue? recovered = default;
        for(int i = 0; i < working.Count; i++)
        {
            (FastAcceptor<TValue> acceptor, FastPrepareResponse<TValue> response) = working[i].Prepare(classicBallot);
            working[i] = acceptor;
            if(response.Promised)
            {
                promises++;
                responses.Add(response);
                if(response.AcceptedBallot > highestAccepted)
                {
                    highestAccepted = response.AcceptedBallot;
                    recovered = response.AcceptedValue;
                }
            }
        }

        if(promises < ClassicQuorum)
        {
            return (new FastCasPaxosRegister<TValue>(working.ToImmutable()), new ChangeOutcome<TValue>(false, default));
        }

        if(!highestAccepted.IsZero && highestAccepted.IsFast)
        {
            recovered = TallyFastWinner(responses, highestAccepted, recovered);
        }

        TValue newValue = update(recovered);
        int accepts = 0;
        for(int i = 0; i < working.Count; i++)
        {
            (FastAcceptor<TValue> acceptor, bool ok) = working[i].Accept(classicBallot, newValue);
            working[i] = acceptor;
            if(ok)
            {
                accepts++;
            }
        }

        ChangeOutcome<TValue> outcome = accepts >= ClassicQuorum
            ? new ChangeOutcome<TValue>(true, newValue)
            : new ChangeOutcome<TValue>(false, default);

        return (new FastCasPaxosRegister<TValue>(working.ToImmutable()), outcome);
    }


    private static TValue? TallyFastWinner(List<FastPrepareResponse<TValue>> responses, FastBallot highestAccepted, TValue? fallback)
    {
        var counts = new List<(TValue? Value, int Count)>();
        foreach(FastPrepareResponse<TValue> response in responses)
        {
            if(response.AcceptedBallot != highestAccepted)
            {
                continue;
            }

            bool found = false;
            for(int i = 0; i < counts.Count; i++)
            {
                if(EqualityComparer<TValue>.Default.Equals(counts[i].Value, response.AcceptedValue))
                {
                    counts[i] = (counts[i].Value, counts[i].Count + 1);
                    found = true;
                    break;
                }
            }

            if(!found)
            {
                counts.Add((response.AcceptedValue, 1));
            }
        }

        if(counts.Count == 0)
        {
            return fallback;
        }

        return counts.MaxBy(static entry => entry.Count).Value;
    }


    private string DebuggerDisplay => $"FastCasPaxosRegister: {Acceptors.Length} acceptors, fast quorum {FastQuorum}, classic quorum {ClassicQuorum}";
}
