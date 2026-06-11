using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Computes the group stability frontier: the element-wise minimum of every group member's advertised
/// causal summary. Below the frontier, every member has observed everything, so no peer can ever again
/// send a reference to it — the line beneath which discarding state is safe for merges.
/// </summary>
/// <remarks>
/// <para>
/// This lives in-library because the subtle rule is the one callers get wrong: the minimum must range
/// over the <em>full group membership</em>, one summary per member. A member that has observed nothing
/// of some replica pins that replica's frontier at zero — silence holds the floor down. Computing the
/// minimum over "whoever gossiped recently" instead silently lifts the frontier past what a lagging
/// member may still reference, which is exactly the dangling-reference bug the frontier exists to
/// prevent. Likewise, a permanently silent member disables compaction forever; evicting it is a group
/// membership decision made above this helper, never a default.
/// </para>
/// <para>
/// The frontier is the WHEN of waterline compaction; the agreed checkpoint is the WHAT. Compaction may
/// only touch state that is both captured by the checkpoint and below the frontier — see the
/// compaction delegate on <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}"/>.
/// </para>
/// </remarks>
public static class StabilityFrontier
{
    /// <summary>
    /// Computes the frontier from <paramref name="memberDigests"/> — exactly one advertised digest per
    /// group member.
    /// </summary>
    /// <param name="memberDigests">One <see cref="GossipDigest"/> per member of the group, including the local replica's own.</param>
    /// <returns>The element-wise minimum of the members' summaries.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="memberDigests"/> or any element is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="memberDigests"/> is empty — the frontier of an empty group is meaningless, and treating it as unbounded would discard everything.</exception>
    /// <remarks>
    /// Supplying multiple digests for the same member is harmless (the minimum only gets more
    /// conservative); supplying fewer members than the group has is the unsafe direction and cannot be
    /// detected here — membership is the caller's contract.
    /// </remarks>
    public static VectorClock Compute(IReadOnlyCollection<GossipDigest> memberDigests)
    {
        ArgumentNullException.ThrowIfNull(memberDigests);
        if(memberDigests.Count == 0)
        {
            throw new ArgumentException("The stability frontier requires at least one member digest.", nameof(memberDigests));
        }

        //Union of every replica named by any summary; a replica absent from a summary counts as zero
        //there, which the indexer already yields, so the minimum self-floors at zero.
        var replicas = new HashSet<ReplicaId>();
        foreach(GossipDigest digest in memberDigests)
        {
            ArgumentNullException.ThrowIfNull(digest, nameof(memberDigests));
            foreach(ReplicaCounterEntry entry in digest.Summary.ToState().Entries)
            {
                replicas.Add(ReplicaId.FromSpan(entry.Replica.AsSpan()));
            }
        }

        ImmutableArray<ReplicaCounterEntry>.Builder entries = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(replicas.Count);
        foreach(ReplicaId replica in replicas)
        {
            int minimum = int.MaxValue;
            foreach(GossipDigest digest in memberDigests)
            {
                int observed = digest.Summary[replica];
                if(observed < minimum)
                {
                    minimum = observed;
                }
            }

            if(minimum > 0)
            {
                entries.Add(new ReplicaCounterEntry(ImmutableArray.Create(replica.AsSpan()), minimum));
            }
        }

        return VectorClock.FromState(new VectorClockState(entries.ToImmutable()));
    }
}
