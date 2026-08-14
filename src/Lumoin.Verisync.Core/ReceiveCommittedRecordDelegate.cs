using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Offers a committed record to one host on the dissemination receive leg.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="committed">The decided record being offered.</param>
/// <param name="durability">How far the learn must get before the call completes.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns><see langword="true"/> if the record advanced this host; <see langword="false"/> if the host
/// already held it or a later one.</returns>
/// <remarks>
/// <para>
/// This is the receiving counterpart of <see cref="PublishCommittedRecordDelegate{TValue}"/>: a publisher
/// fans a record out to its audience, and what it invokes per host — directly in process, or across whatever
/// transport a deployment runs — is this shape. <see cref="QuePaxaVersionedRunner{TValue}.LearnAsync"/> is a
/// <see cref="ReceiveCommittedRecordDelegate{TValue}"/> by method-group conversion, which is the sequenced
/// assignment a runner-owned host uses; a host driven without a runner wraps
/// <see cref="QuePaxaVersionedNode{TValue}.Learn"/> instead.
/// </para>
/// <para>
/// A refusal — a record of another chain — faults the call rather than reporting <see langword="false"/>,
/// because <see langword="false"/> is the answer for a record that merely did not advance the host.
/// </para>
/// </remarks>
public delegate ValueTask<bool> ReceiveCommittedRecordDelegate<TValue>(VersionedValue<TValue> committed, LearnDurability durability, CancellationToken cancellationToken);
