using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Asks one member of a versioned register's membership which version it has learned, which is what makes
/// dissemination observable before an operator acts on it.
/// </summary>
/// <param name="member">The member to ask.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>That member's answer: the highest committed version it reports beside the identity it asserts
/// for itself, with <see cref="RegisterVersion.Unwritten"/> the version of a member that has learned
/// none.</returns>
/// <remarks>
/// <para>
/// It sits beside <see cref="ObserveCommittedVersionDelegate"/> rather than replacing it, because the two
/// answer different questions. The aggregate is what a delayed writer stands down on, where only the highest
/// version anywhere matters and which host holds it does not. This one is what a readiness report is built
/// from, where the answer is worthless without the identity beside it: an operator deciding whether a joiner
/// has caught up, or whether a host may be decommissioned, is asking about one named replica.
/// </para>
/// <para>
/// The answer names its answerer because the report it feeds is counted over distinct members. An
/// implementation carries the identity from the answering host itself — the wire reply's own field, never
/// the member the caller aimed at — and the register refuses an answer naming another member rather than
/// counting it or reporting it unreachable, because two probe routes landing on one host is the wiring error
/// a decommission gate must not clear through. The identity is the host's own claim and is not
/// authentication.
/// </para>
/// <para>
/// A member that does not answer is reported unreachable rather than reported at
/// <see cref="RegisterVersion.Unwritten"/>. A host that has learned nothing and a host that cannot be
/// reached are different situations, and a readiness gate that confused them would clear a decommission
/// against a silent cluster.
/// </para>
/// <para>
/// It is the flat seam beside a curried one. <see cref="ResolveCommittedRecordReaderDelegate{TValue}"/>,
/// its per-member neighbour in the register's constructor, returns a query that is invoked in a second
/// call, while this one answers in the call it is given; the difference is the resolve step, not the
/// question asked.
/// </para>
/// </remarks>
public delegate ValueTask<MemberVersionReport> ObserveMemberVersionDelegate(ReplicaId member, CancellationToken cancellationToken);
