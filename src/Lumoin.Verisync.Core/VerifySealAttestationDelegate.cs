using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Verifies the attestation evidence carried by a segment seal against the caller's trust anchors.
/// </summary>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TContext">The caller-defined validation context type carrying trust anchors.</typeparam>
/// <param name="seal">The seal whose <see cref="SegmentSeal{TProof}.Proofs"/> to verify.</param>
/// <param name="validationContext">The validation context — quorum membership, public keys, or other trust anchors.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns><see langword="null"/> when the attestation is valid, or an error message when it is not.</returns>
/// <remarks>
/// A complete implementation verifies that every required proof covers exactly
/// <see cref="SegmentSeal{TProof}.Digest"/> — never a value the verifier did not recompute or receive
/// through a trusted channel — and that the evidence meets the caller's threshold (a quorum decree, a
/// signature count). Chain structure between seals is verified separately and in-library by
/// <see cref="SegmentSeal{TProof}.VerifyLink"/>; this delegate owns only the trust decision.
/// </remarks>
public delegate ValueTask<string?> VerifySealAttestationDelegate<TProof, in TContext>(
    SegmentSeal<TProof> seal,
    TContext validationContext,
    CancellationToken cancellationToken);
