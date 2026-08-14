using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A proposal's ordering key: its priority paired with the proposer lane that owns it. The owner is in the
/// key so that proposals from different proposers stay distinct when their priorities agree.
/// </summary>
/// <param name="Priority">The proposal's priority.</param>
/// <param name="Owner">The proposer lane the proposal is attributed to.</param>
/// <remarks>
/// <para>
/// The order is lexicographic, priority first and owner second, matching the paper's Appendix A tiebreaking.
/// It is total whenever the proposer identities are distinct, and the agreement argument assumes untied
/// priorities, which the tiebreak supplies.
/// </para>
/// <para>
/// Within one consensus instance a key identifies at most one value. The aggregate fold keeps the incumbent
/// on an exact key tie, so two proposals sharing a key and differing in value fold differently at recorders
/// that saw them in a different order, and the disagreement is unrecoverable once it has spread. Nothing in
/// the core enforces this, because a constant-space register retains only two proposals and cannot see a
/// third. The caller keeps the contract by allocating a fresh <see cref="ProposerLane"/> per concurrent
/// proposal.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct ProposalKey(ProposalPriority Priority, ProposerLane Owner): IComparable<ProposalKey>
{
    /// <summary>Returns this key under a different priority, keeping <see cref="Owner"/> unchanged.</summary>
    /// <param name="priority">The new priority.</param>
    /// <returns>The re-prioritized key.</returns>
    /// <remarks>
    /// Re-prioritizing never restamps the owner, because the phase-zero redraw changes only the priority of
    /// the working proposal and leaves the proposer identity attached to it.
    /// </remarks>
    public ProposalKey WithPriority(ProposalPriority priority) => new(priority, Owner);


    /// <summary>Compares this key with <paramref name="other"/> lexicographically: priority first, then owner.</summary>
    /// <param name="other">The key to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(ProposalKey other)
    {
        int byPriority = Priority.CompareTo(other.Priority);
        if(byPriority != 0)
        {
            return byPriority;
        }

        return Owner.CompareTo(other.Owner);
    }


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(ProposalKey left, ProposalKey right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(ProposalKey left, ProposalKey right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(ProposalKey left, ProposalKey right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(ProposalKey left, ProposalKey right) => left.CompareTo(right) >= 0;


    private string DebuggerDisplay => $"ProposalKey: priority {Priority.Value}, owner {Owner.Replica} lane {Owner.Lane}";
}
