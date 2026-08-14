using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Carries a committed record to the hosts named in its audience, which is what makes the next version
/// servable.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="committed">The decided record.</param>
/// <param name="audience">The hosts to offer it to: the union of the membership that decided it and the membership it installs.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes when the record has been offered to those hosts.</returns>
/// <remarks>
/// <para>
/// A recorder host serves the one instance whose leader it can derive, which is the version after the record
/// it has learned. Until a quorum of hosts has learned version v, nothing can be written at v+1, so
/// dissemination is the precondition of the next write: a retry whose predecessor has not reached the hosts
/// has nowhere to run.
/// </para>
/// <para>
/// The audience is computed by the register and named here rather than left to the implementation, because
/// membership makes "the hosts" ambiguous exactly where it matters. For an ordinary decide the union
/// degenerates to the deciding membership and this is the list a deployment would have written down anyway.
/// For a decide that installs a new membership it does not, and each half carries a duty the other cannot:
/// the incoming half is the eager push that hands a joiner the installing record instead of leaving it to
/// learn the record by some other route, and the outgoing half hands a departing host the record that
/// removed it, so a decommissioned replica learns it is out from the protocol rather than from silence. A
/// leaver can still adopt that record, because a learn is not membership-filtered.
/// </para>
/// <para>
/// Both halves are operability and neither is safety. Agreement holds within an instance over its one fixed
/// recorder set, so a deployment that disseminates nothing at all is equally safe and unequally available,
/// and an implementation that reaches fewer hosts than the audience names has slowed the cluster down rather
/// than endangered it.
/// </para>
/// <para>
/// It is offered for every decided record and not only for this replica's own. A write that lost still
/// learned what won, and the replica that learned it can carry it as readily as the winner; a deployment
/// where only winners disseminate stalls when a winner fails between deciding and telling anyone.
/// </para>
/// <para>
/// What one offer owes each audience host is that host's own learn, and the shape it invokes is
/// <see cref="ReceiveCommittedRecordDelegate{TValue}"/>:
/// <see cref="QuePaxaVersionedRunner{TValue}.LearnAsync"/> where a runner owns the host — a receive
/// delegate by method-group conversion — and <see cref="QuePaxaVersionedNode{TValue}.Learn"/> wrapped where
/// none does. A co-located <see cref="QuePaxaVersionedRegister{TValue}"/> may take the same record through
/// its own <see cref="QuePaxaVersionedRegister{TValue}.Learn"/> to move its local belief sooner, and owes
/// nothing: a register that was not told catches up through its own read and its own superseded attempts.
/// </para>
/// <para>
/// A record is not verified by whoever receives it. The protocol assumes crash faults, and a deployment
/// needing more owes its transport authentication.
/// </para>
/// <para>
/// An implementation may send the record with the next request rather than as a round of its own. The record
/// and the first request of the next version may travel together, so a writer that commits and immediately
/// writes again pays no additional round trip. Completion marks the offer rather than the delivery, which is
/// also how an implementation that wants its caller unblocked sooner returns early and carries the push on
/// its own time.
/// </para>
/// <para>
/// Throwing does not fail the write. The register awaits this after the decision is taken and returns the
/// decided outcome whatever happens here, cancellation included and the caller's own token included, because
/// a caller told its committed write failed would retry a write that had already landed. A push that did not
/// reach its audience is an operability event, and its observable is a readiness report rather than a
/// write's result.
/// </para>
/// <para>
/// A boundary push names <see cref="LearnDurability.Durable"/> at the receiving host. The push contract lives
/// out here in a deployment-implemented delegate, so it states its own durability obligation instead of
/// assuming what the receiver does: the record that installs a membership may be the new membership's only
/// copy, and the sender is the one that knows the push is a boundary one.
/// </para>
/// </remarks>
public delegate ValueTask PublishCommittedRecordDelegate<TValue>(
    VersionedValue<TValue> committed,
    ImmutableArray<ReplicaId> audience,
    CancellationToken cancellationToken);
