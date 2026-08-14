using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A leader's replication position for one follower: the next log index it will send, and the highest index
/// it knows replicated there.
/// </summary>
/// <param name="NextIndex">The next log index the leader will send. Must be <see cref="LogIndex.First"/> or above.</param>
/// <param name="MatchIndex">
/// The highest index known replicated on the follower, which is <see cref="LogIndex.BeforeFirst"/> when none
/// is.
/// </param>
/// <remarks>
/// <para>
/// The two move together and mean nothing apart, which is why they are one value rather than two arrays a
/// caller has to keep the same length and reinitialize in step. A leader holds one of these per member.
/// </para>
/// <para>
/// The pair is volatile by the Raft paper's Figure 2: it is rebuilt from scratch whenever a node becomes
/// leader, and a restored node rediscovers it rather than reading it from durable state.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct FollowerProgress(LogIndex NextIndex, LogIndex MatchIndex)
{
    /// <summary>
    /// The next log index the leader will send. It is validated on construction and on a <c>with</c>
    /// expression alike, because the initializer writes the backing field directly and no accessor runs
    /// for it. <see cref="LogIndex"/> carries non-negativity, so the floor at <see cref="LogIndex.First"/>
    /// is the only rule left here: the empty prefix is a position every log matches rather than one a leader
    /// can send from.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is below <see cref="LogIndex.First"/>.</exception>
    public LogIndex NextIndex { get; init { field = ValidateNext(value); } } = ValidateNext(NextIndex);


    /// <summary>The position a leader starts a follower at, which is one past <paramref name="lastLogIndex"/> with nothing known replicated.</summary>
    /// <param name="lastLogIndex">The leader's last log index, or <see cref="LogIndex.BeforeFirst"/> when its log is empty.</param>
    /// <returns>The initial progress.</returns>
    public static FollowerProgress StartingFrom(LogIndex lastLogIndex) => new(lastLogIndex.Next(), LogIndex.BeforeFirst);


    /// <summary>
    /// This progress after the follower confirmed replication up to <paramref name="matchIndex"/>.
    /// </summary>
    /// <param name="matchIndex">The index the follower reported replicated.</param>
    /// <returns>The advanced progress.</returns>
    /// <remarks>
    /// The match index never regresses, because replies arrive out of order and an older one carries a
    /// shorter prefix the follower has not lost. The next index follows the report rather than the retained
    /// maximum, which is what makes a reply that arrived late cost one probe rather than a wrong prefix.
    /// </remarks>
    public FollowerProgress Confirmed(LogIndex matchIndex) => new(matchIndex.Next(), LogIndex.Max(MatchIndex, matchIndex));


    /// <summary>
    /// This progress after the follower rejected the consistency check, which retreats the next index by one.
    /// </summary>
    /// <returns>The retreated progress.</returns>
    /// <remarks>
    /// The retreat stops at <see cref="LogIndex.First"/>, because the empty prefix below it is the position
    /// every log matches.
    /// </remarks>
    public FollowerProgress Retreated() => new(LogIndex.Max(LogIndex.First, NextIndex.Previous()), MatchIndex);


    private static LogIndex ValidateNext(LogIndex value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a position and not the
        //validator's own parameter.
        ArgumentOutOfRangeException.ThrowIfLessThan(value, LogIndex.First, nameof(NextIndex));

        return value;
    }


    private string DebuggerDisplay => $"FollowerProgress: next={NextIndex.Value}, match={MatchIndex.Value}";
}
