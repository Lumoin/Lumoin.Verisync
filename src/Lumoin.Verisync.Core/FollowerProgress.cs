using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A leader's replication position for one follower: the next log index it will send, and the highest index
/// it knows replicated there.
/// </summary>
/// <param name="NextIndex">The next log index the leader will send. Must be at least one.</param>
/// <param name="MatchIndex">The highest index known replicated on the follower, which is zero when none is.</param>
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
public readonly record struct FollowerProgress(long NextIndex, long MatchIndex)
{
    /// <summary>
    /// The next log index the leader will send. It is validated on construction and on a <c>with</c>
    /// expression alike, because the initializer writes the backing field directly and no accessor runs
    /// for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is below one.</exception>
    public long NextIndex { get; init { field = ValidateNext(value); } } = ValidateNext(NextIndex);

    /// <summary>
    /// The highest index known replicated on the follower. It is validated on construction and on a
    /// <c>with</c> expression alike, for the same reason.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public long MatchIndex { get; init { field = ValidateMatch(value); } } = ValidateMatch(MatchIndex);


    /// <summary>The position a leader starts a follower at, which is one past a log of <paramref name="lastLogIndex"/> entries with nothing known replicated.</summary>
    /// <param name="lastLogIndex">The leader's last log index, or zero when its log is empty.</param>
    /// <returns>The initial progress.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="lastLogIndex"/> is negative.</exception>
    public static FollowerProgress StartingFrom(long lastLogIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastLogIndex);

        return new FollowerProgress(lastLogIndex + 1, 0);
    }


    /// <summary>
    /// This progress after the follower confirmed replication up to <paramref name="matchIndex"/>.
    /// </summary>
    /// <param name="matchIndex">The index the follower reported replicated.</param>
    /// <returns>The advanced progress.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="matchIndex"/> is negative.</exception>
    /// <remarks>
    /// The match index never regresses, because replies arrive out of order and an older one carries a
    /// shorter prefix the follower has not lost. The next index follows the report rather than the retained
    /// maximum, which is what makes a reply that arrived late cost one probe rather than a wrong prefix.
    /// </remarks>
    public FollowerProgress Confirmed(long matchIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(matchIndex);

        return new FollowerProgress(matchIndex + 1, Math.Max(MatchIndex, matchIndex));
    }


    /// <summary>
    /// This progress after the follower rejected the consistency check, which retreats the next index by one.
    /// </summary>
    /// <returns>The retreated progress.</returns>
    /// <remarks>
    /// The retreat stops at one, because index zero is the empty-prefix sentinel and every log matches there.
    /// </remarks>
    public FollowerProgress Retreated() => new(Math.Max(1, NextIndex - 1), MatchIndex);


    private static long ValidateNext(long value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a position and not the
        //validator's own parameter.
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1, nameof(NextIndex));

        return value;
    }


    private static long ValidateMatch(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MatchIndex));

        return value;
    }


    private string DebuggerDisplay => $"FollowerProgress: next={NextIndex}, match={MatchIndex}";
}
