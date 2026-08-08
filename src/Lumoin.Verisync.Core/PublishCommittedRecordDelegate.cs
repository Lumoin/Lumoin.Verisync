using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Carries a committed record to the recorder hosts, which is what makes the next version servable.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="committed">The decided record.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A task that completes when the record has been offered to the hosts.</returns>
/// <remarks>
/// <para>
/// A recorder host serves the one instance whose leader it can derive, which is the version after the record
/// it has learned. Until a quorum of hosts has learned version v, nothing can be written at v+1, so
/// dissemination is the precondition of the next write: a retry whose predecessor has not reached the hosts
/// has nowhere to run.
/// </para>
/// <para>
/// It is offered for every decided record and not only for this replica's own. A write that lost still learned
/// what won, and the replica that learned it can carry it as readily as the winner; a deployment where only
/// winners disseminate stalls when a winner fails between deciding and telling anyone.
/// </para>
/// <para>
/// A record is not verified by whoever receives it. The protocol assumes crash faults, and a deployment
/// needing more owes its transport authentication.
/// </para>
/// <para>
/// An implementation may send the record with the next request rather than as a round of its own. The record
/// and the first request of the next version may travel together, so a writer that commits and immediately
/// writes again pays no additional round trip.
/// </para>
/// </remarks>
public delegate ValueTask PublishCommittedRecordDelegate<TValue>(VersionedValue<TValue> committed, CancellationToken cancellationToken);
