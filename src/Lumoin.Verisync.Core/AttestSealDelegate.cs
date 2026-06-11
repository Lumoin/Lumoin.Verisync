using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Produces the attestation evidence for a segment seal: proofs over the seal digest that make the seal
/// trustworthy to other replicas and external auditors.
/// </summary>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="sealDigest">The digest of the seal being attested.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The attestation evidence, attached to the seal with <see cref="SegmentSeal{TProof}.WithProofs"/>.</returns>
/// <remarks>
/// In the agreed-seal model the implementation runs the seal digest through a consensus decree — the
/// checkpoint-promotion decree carries it — so the deciding quorum is the witness set, and the returned
/// proofs are the decree evidence. In an attested model the implementation gathers signatures over the
/// digest instead. Either way the evidence covers the digest, which already commits to the canonical
/// bytes, so attestation never needs to re-encode the seal.
/// </remarks>
public delegate ValueTask<ImmutableArray<TProof>> AttestSealDelegate<TProof>(
    ReadOnlyMemory<byte> sealDigest,
    CancellationToken cancellationToken);
