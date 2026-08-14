using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Asks one recorder host for the committed record it has learned, which is how a replica catches up on
/// versions it missed without running a consensus instance for them.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The host's committed record, or <see langword="null"/> when it has learned none.</returns>
/// <remarks>
/// <para>
/// This is not a consensus operation and it needs no quorum. A committed record is a decided fact, and the
/// protocol assumes crash faults rather than Byzantine ones, so one honest host reporting a version settles
/// it and a reader adopts the highest version any host reports.
/// </para>
/// <para>
/// Learning nothing above what the reader already holds does not mean the reader is current: it is equally
/// the state left behind when a writer committed a version and crashed before telling anyone. The two are
/// indistinguishable from this call alone and a caller resolves them by writing, which either commits,
/// proving the reader was current, or comes back superseded carrying the record the recorders were already
/// holding. That is also the recovery for the crash: the recorders still serve the instance whose leader they
/// can derive, so a write at that version converges on the record already decided there.
/// </para>
/// <para>
/// The host-side implementation is <see cref="QuePaxaVersionedRunner{TValue}.ReadCommittedAsync"/>, which IS
/// this delegate by method-group conversion and answers through the runner's own queue after making the
/// host's state durable. A host that answers a catch-up read off the loop instead both reads state the loop
/// is writing and may republish a record it has learned and not persisted, which a peer adopts and moves to
/// the next version on.
/// </para>
/// </remarks>
public delegate ValueTask<VersionedValue<TValue>?> ReadCommittedRecordDelegate<TValue>(CancellationToken cancellationToken);
