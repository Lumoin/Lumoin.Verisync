using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The membership a QuePaxa versioned register instance runs under: the chain it belongs to and one ordered,
/// duplicate-free list of replicas that is both the recorder set and the hedging order.
/// </summary>
/// <remarks>
/// <para>
/// The members are one list and not two. The recorder set is what a quorum is counted over and the hedging
/// order is what decides who writes first, and nothing relates two separately supplied lists: a recorder set
/// and a replica order of different sizes would commit on a quorum of the smaller one. Holding a single list
/// makes that unconstructible.
/// </para>
/// <para>
/// The order is part of the value, not an incidental arrangement of a set. The first member is the bootstrap
/// leader, and <see cref="ClusterId.FromGenesisMembers(ImmutableArray{HostId})"/> is order-sensitive for
/// exactly that reason, so two configurations listing the same replicas in different orders are different
/// configurations and are unequal here.
/// </para>
/// <para>
/// The hedging base delay is not part of the configuration. A delay orders sending and settles no protocol
/// rule, so replicas may disagree on it at the cost of redundant rounds and never of agreement;
/// <see cref="ScheduleWith(TimeSpan)"/> takes it from local tuning instead.
/// </para>
/// <para>
/// This is a sealed class rather than a record, and its equality and hash are written out by hand. A
/// synthesized equality would route <see cref="Members"/> through
/// <see cref="System.Collections.Generic.EqualityComparer{T}.Default"/>, which compares an
/// <see cref="ImmutableArray{T}"/> by the identity of its backing array: two configurations decoded from the
/// same bytes would then be unequal, whole-proposal comparison would fail across a codec round trip, and the
/// register would never decide while reporting every own write superseded. The same defect is invisible in
/// any bench where both sides share one array instance. <see cref="ReconciliationDrop"/> replaces the
/// synthesized equality for the same reason, with the deliberate difference that a drop's equality is
/// order-independent and this one must not be.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class QuePaxaConfiguration: IEquatable<QuePaxaConfiguration>
{
    private QuePaxaConfiguration(ClusterId cluster, ImmutableArray<HostId> members)
    {
        Cluster = cluster;
        Members = members;
    }


    /// <summary>
    /// Creates a configuration over an existing chain.
    /// </summary>
    /// <param name="cluster">The chain identity, minted at genesis and carried forward unchanged.</param>
    /// <param name="members">The ordered member list; the first member leads a chain's first instance.</param>
    /// <returns>A new configuration.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="members"/> is default or empty, or lists the same replica twice.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Nothing is refused about the size beyond emptiness. Majorities intersect at every size, so no safety
    /// floor is derivable: two members is safe with no fault tolerance and one member is a single point of
    /// failure, and both are choices a deployment is allowed to make.
    /// </para>
    /// <para>
    /// The duplicate refusal is load-bearing for quorum injectivity and is not hygiene. A quorum is counted as
    /// a number of distinct member slots, so a replica listed twice would answer twice and count twice, and a
    /// decision would be taken by fewer replicas than the arithmetic claims.
    /// </para>
    /// </remarks>
    public static QuePaxaConfiguration Create(ClusterId cluster, ImmutableArray<HostId> members)
    {
        if(members.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A configuration requires at least one member.", nameof(members));
        }

        for(int i = 0; i < members.Length; i++)
        {
            for(int j = i + 1; j < members.Length; j++)
            {
                if(members[i].Replica.Equals(members[j].Replica))
                {
                    throw new ArgumentException("A configuration cannot list the same replica twice.", nameof(members));
                }
            }
        }

        return new QuePaxaConfiguration(cluster, members);
    }


    /// <summary>
    /// Creates the genesis configuration of a new chain, minting the chain identity from
    /// <paramref name="members"/>.
    /// </summary>
    /// <param name="members">The ordered genesis member list; the first member is the bootstrap leader.</param>
    /// <returns>A new configuration whose <see cref="Cluster"/> is the digest of that member list.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="members"/> is default or empty, or lists the same replica twice.
    /// </exception>
    /// <remarks>
    /// Minting and listing are one call so that a host cannot mint an identity from one list and then run
    /// under another. Every host of a chain reaches this with the same member list in the same order, and a
    /// host that does not mints a different identity and is declined.
    /// </remarks>
    public static QuePaxaConfiguration CreateGenesis(ImmutableArray<HostId> members)
    {
        return Create(ClusterId.FromGenesisMembers(members), members);
    }


    /// <summary>The chain identity, minted at genesis and carried unchanged by every configuration change.</summary>
    public ClusterId Cluster { get; }

    /// <summary>The ordered member list, which is both the recorder set and the hedging order.</summary>
    public ImmutableArray<HostId> Members { get; }

    /// <summary>The number of members a decision under this configuration must be taken by.</summary>
    public int Quorum => (Members.Length / 2) + 1;


    /// <summary>
    /// The store incarnation admitted to answer for <paramref name="replica"/>, or <see langword="null"/> when
    /// the replica is not a member.
    /// </summary>
    /// <param name="replica">The replica to look up.</param>
    /// <returns>The admitted incarnation, or <see langword="null"/>.</returns>
    /// <remarks>
    /// This is what a counting site compares an answer against. A host answering for an admitted identity
    /// under any other incarnation is a different store than the one admitted, whatever it calls itself.
    /// </remarks>
    public StoreIncarnation? IncarnationOf(ReplicaId replica)
    {
        int index = IndexOf(replica);

        return index < 0 ? null : Members[index].Incarnation;
    }


    /// <summary>Whether <paramref name="replica"/> is a member of this configuration.</summary>
    /// <param name="replica">The replica to look for.</param>
    /// <returns><see langword="true"/> when the replica is listed.</returns>
    public bool Contains(ReplicaId replica) => IndexOf(replica) >= 0;


    /// <summary>
    /// The configuration with <paramref name="host"/> added at the end of the member list, or this one when
    /// it is already a member.
    /// </summary>
    /// <param name="host">The host to add.</param>
    /// <returns>The resulting configuration, on the same chain.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the member's replica is already listed under another store incarnation, because replacing a
    /// member's store retires one member and admits another rather than adding one.
    /// </exception>
    /// <remarks>
    /// A joiner is appended rather than inserted, so the existing members keep their positions and the
    /// bootstrap leader keeps its own. Adding a member that is already listed returns the receiver, which is
    /// what lets a change be re-applied against a winning configuration after a superseded write instead of
    /// failing the operator's whole request. That idempotence is over the whole member: an addition naming a
    /// listed replica under a different incarnation is a store replacement, and answering it with the receiver
    /// would drop the operator's request silently.
    /// </remarks>
    public QuePaxaConfiguration With(HostId host)
    {
        int index = IndexOf(host.Replica);
        if(index >= 0)
        {
            if(!Members[index].Equals(host))
            {
                throw new InvalidOperationException("A member's store cannot be replaced by an addition; retire the member and admit the replacement.");
            }

            return this;
        }

        //The receiver is duplicate-free and the addition was just proven absent, so the appended list is
        //duplicate-free without a second scan.
        return new QuePaxaConfiguration(Cluster, Members.Add(host));
    }


    /// <summary>
    /// The configuration with <paramref name="replica"/> removed from the member list, or this one when it is
    /// not a member.
    /// </summary>
    /// <param name="replica">The replica to remove.</param>
    /// <returns>The resulting configuration, on the same chain.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="replica"/> is the only member, because a register with no members can
    /// neither decide nor be reconfigured back into existence.
    /// </exception>
    /// <remarks>
    /// Removing a replica that is not listed returns the receiver, which is what lets a change be re-applied
    /// against a winning configuration after a superseded write instead of failing the operator's whole
    /// request. The removal of a member that is present but last is refused instead, because the absence of
    /// the named replica and the emptying of the set are different situations and only one of them is a
    /// mistake.
    /// </remarks>
    public QuePaxaConfiguration Without(ReplicaId replica)
    {
        int index = IndexOf(replica);
        if(index < 0)
        {
            return this;
        }

        if(Members.Length == 1)
        {
            throw new InvalidOperationException("A configuration cannot be emptied; its last member cannot be removed.");
        }

        return new QuePaxaConfiguration(Cluster, Members.RemoveAt(index));
    }


    /// <summary>
    /// The members of <paramref name="incoming"/> this membership does not hold: who a change to it would
    /// admit.
    /// </summary>
    /// <param name="incoming">The membership a change would install.</param>
    /// <returns>The joiners, in <paramref name="incoming"/>'s own order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="incoming"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="incoming"/> names another chain.</exception>
    /// <remarks>
    /// The delta is exported because the boundary is where an operator acts: the joiners are who an admission
    /// disseminates to first, and the leavers are who a readiness gate must stop counting. Two memberships of
    /// different chains have no delta, because they never were one fleet.
    /// </remarks>
    public ImmutableArray<HostId> Joining(QuePaxaConfiguration incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if(!Cluster.Equals(incoming.Cluster))
        {
            throw new ArgumentException("The membership names another chain, so the two never were one fleet and have no delta.", nameof(incoming));
        }

        ImmutableArray<HostId>.Builder joiners = ImmutableArray.CreateBuilder<HostId>();
        foreach(HostId member in incoming.Members)
        {
            if(!Members.Contains(member))
            {
                joiners.Add(member);
            }
        }

        return joiners.ToImmutable();
    }


    /// <summary>
    /// The members this membership holds that <paramref name="incoming"/> does not: who a change to it would
    /// retire.
    /// </summary>
    /// <param name="incoming">The membership a change would install.</param>
    /// <returns>The leavers, in this membership's own order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="incoming"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="incoming"/> names another chain.</exception>
    /// <remarks>
    /// The other half of <see cref="Joining(QuePaxaConfiguration)"/>, and the half a decommission gate reads:
    /// a leaver is retired only once a quorum of the incoming membership has learned the record that removes
    /// it.
    /// </remarks>
    public ImmutableArray<HostId> Leaving(QuePaxaConfiguration incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if(!Cluster.Equals(incoming.Cluster))
        {
            throw new ArgumentException("The membership names another chain, so the two never were one fleet and have no delta.", nameof(incoming));
        }

        ImmutableArray<HostId>.Builder leavers = ImmutableArray.CreateBuilder<HostId>();
        foreach(HostId member in Members)
        {
            if(!incoming.Members.Contains(member))
            {
                leavers.Add(member);
            }
        }

        return leavers.ToImmutable();
    }


    /// <summary>
    /// Determines whether <paramref name="other"/> names the same chain and the same members in the same
    /// order.
    /// </summary>
    /// <param name="other">The configuration to compare with.</param>
    /// <returns><see langword="true"/> when both name one chain and one ordered member list.</returns>
    /// <remarks>
    /// Element-wise and order-sensitive. The comparison reads the members' bytes and their positions rather
    /// than the backing array's identity, so a configuration that crossed a codec equals the one that was
    /// encoded, and a reordered member list does not.
    /// </remarks>
    public bool Equals([NotNullWhen(true)] QuePaxaConfiguration? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(!Cluster.Equals(other.Cluster))
        {
            return false;
        }

        if(Members.Length != other.Members.Length)
        {
            return false;
        }

        for(int i = 0; i < Members.Length; i++)
        {
            if(!Members[i].Equals(other.Members[i]))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as QuePaxaConfiguration);


    /// <inheritdoc/>
    /// <remarks>
    /// Order-sensitive over the members and derived from their bytes, not from the backing array's identity,
    /// so two configurations that compare equal hash equally and a hash-keyed collection finds one where one
    /// was stored.
    /// </remarks>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Cluster);
        foreach(HostId member in Members)
        {
            hash.Add(member);
        }

        return hash.ToHashCode();
    }


    /// <summary>
    /// The hedging schedule this configuration's members activate on, staggered by
    /// <paramref name="baseDelay"/>.
    /// </summary>
    /// <param name="baseDelay">The delay increment per position. Zero activates every member at once.</param>
    /// <returns>A schedule over the member list in its configured order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="baseDelay"/> is negative, or is large enough that the last position's delay
    /// would not fit in a <see cref="TimeSpan"/>.
    /// </exception>
    internal HedgingSchedule ScheduleWith(TimeSpan baseDelay)
    {
        //A lane belongs to an identity: the schedule orders who writes first and never which store answers.
        return HedgingSchedule.Create(ImmutableArray.CreateRange(Members, static member => member.Replica), baseDelay);
    }


    private int IndexOf(ReplicaId replica)
    {
        for(int i = 0; i < Members.Length; i++)
        {
            if(Members[i].Replica.Equals(replica))
            {
                return i;
            }
        }

        return -1;
    }


    private string DebuggerDisplay => $"QuePaxaConfiguration: {Members.Length} members, quorum {Quorum}, {Cluster}";
}
