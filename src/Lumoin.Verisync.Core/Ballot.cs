using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A CASPaxos ballot number: a monotonically increasing round paired with the proposing
/// <see cref="ReplicaId"/>, giving every ballot a unique position in a total order.
/// </summary>
/// <param name="Round">The proposer's monotonically increasing round number.</param>
/// <param name="Proposer">The replica that issued the ballot.</param>
/// <remarks>
/// Ballots are ordered by round first and by proposer second, so two proposers that pick the same round
/// still produce distinct, totally ordered ballots. This total order is what lets acceptors reject stale
/// proposals and lets a higher ballot supersede a lower one.
/// </remarks>
[DebuggerDisplay("Ballot({Round}, {Proposer})")]
public readonly record struct Ballot(int Round, ReplicaId Proposer): IComparable<Ballot>
{
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
