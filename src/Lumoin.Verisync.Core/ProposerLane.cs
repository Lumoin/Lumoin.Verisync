using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The proposer identity a proposal is attributed to: a replica paired with the lane it proposed on. A lane
/// distinguishes concurrent proposals one replica makes for one consensus instance, and a single-flight
/// caller proposes on <see cref="For(ReplicaId)"/>, which is lane zero.
/// </summary>
/// <param name="Replica">The replica the proposal is attributed to.</param>
/// <param name="Lane">The lane within the replica. Must not be negative.</param>
/// <remarks>
/// <para>
/// The protocol orders proposals by priority and then by proposer identity, so the identity needs a total
/// order; this type supplies one lexicographically, replica first and lane second. A lane carries no meaning
/// outside a proposer identity, and the recorder's leader binding binds the pair rather than the replica
/// alone.
/// </para>
/// <para>
/// The lane makes <see cref="ProposalKey"/>'s uniqueness contract enforceable. A replica with two concurrent
/// callers writing at one version would otherwise attach one key to two values, and the aggregate fold would
/// become arrival-order dependent. The reserved priority is granted to a lane rather than to a replica for
/// the same reason: two lanes of the leader's own replica each claiming the reserved priority would
/// reproduce the divergence hazard from inside the leader.
/// </para>
/// <para>
/// The checked configurations ran two proposers. Lanes make three or more concurrent proposer identities
/// reachable on a three-replica deployment, a contention width no configuration explored.
/// </para>
/// <para>
/// The <see langword="default"/> value is lane zero of the all-zero replica identity, which is degenerate
/// rather than illegal. No accessor can defend a default value, so the zero value of this type must stay a
/// legal one.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct ProposerLane(ReplicaId Replica, int Lane): IComparable<ProposerLane>
{
    /// <summary>
    /// The lane within <see cref="Replica"/>. It is validated on construction and on a <c>with</c> expression
    /// alike, because the initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the lane is negative.</exception>
    public int Lane { get; init { field = Validate(value); } } = Validate(Lane);


    /// <summary>Lane zero of <paramref name="replica"/>, which is the lane a single-flight caller proposes on.</summary>
    /// <param name="replica">The proposing replica.</param>
    /// <returns>The replica's lane zero.</returns>
    public static ProposerLane For(ReplicaId replica) => new(replica, 0);


    /// <summary>
    /// Compares this identity with <paramref name="other"/> lexicographically: the replica first in
    /// <see cref="ReplicaId.CompareTo(ReplicaId)"/>'s byte order, then the lane.
    /// </summary>
    /// <param name="other">The identity to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(ProposerLane other)
    {
        int byReplica = Replica.CompareTo(other.Replica);
        if(byReplica != 0)
        {
            return byReplica;
        }

        return Lane.CompareTo(other.Lane);
    }


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(ProposerLane left, ProposerLane right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(ProposerLane left, ProposerLane right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(ProposerLane left, ProposerLane right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(ProposerLane left, ProposerLane right) => left.CompareTo(right) >= 0;


    private static int Validate(int value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a lane and not the
        //validator's own parameter, and an exception naming "value" would send a reader to the wrong place.
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Lane));

        return value;
    }


    private string DebuggerDisplay => $"ProposerLane: {Replica}, lane {Lane}";
}
