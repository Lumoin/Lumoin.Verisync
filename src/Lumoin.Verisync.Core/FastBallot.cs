using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A Fast CASPaxos ballot: a round paired with an optional proposer. A <see langword="null"/> proposer is
/// the shared <em>fast-round</em> ballot that every acceptor is pre-promised to; a non-null proposer owns a
/// <em>classic</em> recovery ballot.
/// </summary>
/// <param name="Round">The round number.</param>
/// <param name="Proposer">The owning proposer, or <see langword="null"/> for the shared fast-round ballot.</param>
/// <remarks>
/// Ballots order by round, then the fast ballot before any classic ballot of the same round, then by
/// proposer. So a classic recovery ballot at round <c>r</c> always supersedes the fast ballot at round
/// <c>r</c> — which is how a leadered recovery round takes over from a contended leaderless fast round.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct FastBallot(int Round, ReplicaId? Proposer): IComparable<FastBallot>
{
    /// <summary>The zero ballot, below every real ballot, held before anything has been accepted.</summary>
    public static FastBallot Zero => default;

    /// <summary>Whether this is the zero/uninitialized ballot.</summary>
    public bool IsZero => Round == 0 && Proposer is null;

    /// <summary>Whether this is a shared fast-round ballot (no owning proposer).</summary>
    public bool IsFast => Proposer is null;


    /// <summary>The initial shared fast-round ballot, round one.</summary>
    public static FastBallot InitialFast() => new(1, null);

    /// <summary>The shared fast-round ballot for the given round.</summary>
    /// <param name="round">The round number. Must be positive.</param>
    /// <remarks>
    /// Only the initial fast round is pre-promised and therefore blind-writable; acceptors accept a fast
    /// ballot only while it equals their promise, so fast rounds beyond <see cref="InitialFast"/> exist for
    /// ordering purposes but cannot carry writes — a contended fast round is superseded by a classic
    /// recovery ballot, never by a higher fast round.
    /// </remarks>
    public static FastBallot Fast(int round)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);

        return new FastBallot(round, null);
    }

    /// <summary>A classic recovery ballot owned by <paramref name="proposer"/> at the given round.</summary>
    /// <param name="round">The round number. Must be positive.</param>
    /// <param name="proposer">The owning proposer.</param>
    public static FastBallot Classic(int round, ReplicaId proposer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);

        return new FastBallot(round, proposer);
    }


    /// <inheritdoc/>
    public int CompareTo(FastBallot other)
    {
        int byRound = Round.CompareTo(other.Round);
        if(byRound != 0)
        {
            return byRound;
        }

        if(Proposer is null)
        {
            return other.Proposer is null ? 0 : -1;
        }

        return other.Proposer is null ? 1 : Proposer.Value.CompareTo(other.Proposer.Value);
    }

    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(FastBallot left, FastBallot right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(FastBallot left, FastBallot right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(FastBallot left, FastBallot right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(FastBallot left, FastBallot right) => left.CompareTo(right) >= 0;


    private string DebuggerDisplay => IsZero ? "FastBallot(zero)" : IsFast ? $"FastBallot(r{Round}, fast)" : $"FastBallot(r{Round}, {Proposer})";
}
