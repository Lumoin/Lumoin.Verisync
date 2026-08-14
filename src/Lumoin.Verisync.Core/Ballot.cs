using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A CASPaxos ballot number: a monotonically increasing round paired with the proposing
/// <see cref="ReplicaId"/>, giving every ballot a unique position in a total order.
/// </summary>
/// <remarks>
/// Ballots are ordered by round first and by proposer second, so two proposers that pick the same round
/// still produce distinct, totally ordered ballots. This total order is what lets acceptors reject stale
/// proposals and lets a higher ballot supersede a lower one.
/// </remarks>
[DebuggerDisplay("Ballot({Round}, {Proposer})")]
public readonly record struct Ballot: IComparable<Ballot>
{
    /// <summary>Initializes a new <see cref="Ballot"/>.</summary>
    /// <param name="Round">The proposer's monotonically increasing round number. Must be positive.</param>
    /// <param name="Proposer">The replica that issued the ballot.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="Round"/> is less than one. A round is monotonically increasing from one, so a
    /// non-positive round (in particular a negative round produced by counter overflow) is rejected at
    /// construction rather than admitted into the ballot total order.
    /// </exception>
    public Ballot(int Round, ReplicaId Proposer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Round, 1);

        this.Round = Round;
        this.Proposer = Proposer;
    }


    /// <summary>The proposer's monotonically increasing round number.</summary>
    public int Round { get; init; }


    /// <summary>The replica that issued the ballot.</summary>
    public ReplicaId Proposer { get; init; }


    /// <inheritdoc/>
    public int CompareTo(Ballot other)
    {
        int byRound = Round.CompareTo(other.Round);

        return byRound != 0 ? byRound : Proposer.CompareTo(other.Proposer);
    }

    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(Ballot left, Ballot right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(Ballot left, Ballot right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(Ballot left, Ballot right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(Ballot left, Ballot right) => left.CompareTo(right) >= 0;
}
