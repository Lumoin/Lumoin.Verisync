using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A sealed log segment: a commitment to the contiguous range of authenticated-log entries
/// <see cref="FirstIndex"/> through <see cref="LastIndex"/>, chained to the previous seal. Seals bound a
/// replica's state — entries below the latest seal can be archived or discarded and remain provable
/// through the segment <see cref="Commitment"/> — and give truncation and fork evidence at segment
/// granularity.
/// </summary>
/// <typeparam name="TProof">The attestation proof type — a consensus decree, signatures, or any other verifiable evidence over the seal digest.</typeparam>
/// <remarks>
/// <para>
/// The <see cref="Commitment"/> is opaque to the seal: typically the root of a
/// <see cref="MerkleLogTree"/> over the segment's entry digests (giving per-entry inclusion proofs via
/// <see cref="MerkleInclusionProof"/>), but a fold commitment, a state commitment, or a concatenation of
/// several may ride the same field. The seal never interprets it.
/// </para>
/// <para>
/// The canonical byte layout is fixed and versioned — the cross-stack contract any other implementation
/// reproduces byte-for-byte. Layout version <c>0x01</c>, with all integers big-endian:
/// <c>version(1) || firstIndex(8) || lastIndex(8) || previousLength(4) || previousSealDigest(previousLength) || commitmentLength(4) || commitment(commitmentLength)</c>.
/// A first seal has no previous digest, encoded as <c>previousLength == 0</c>; an empty previous digest is
/// therefore not representable, which is intended — digests are never empty.
/// </para>
/// <para>
/// <see cref="Digest"/> is computed over <see cref="CanonicalBytes"/>, and <see cref="Proofs"/> attest the
/// digest, so proofs are deliberately <em>outside</em> the digested bytes: attestation evidence (a
/// consensus decree, signatures gathered from a quorum) is produced over the finished digest. Construction
/// always derives the canonical bytes and digest from the typed fields — rehydration from storage or wire
/// goes through <see cref="Create"/> again, so a seal object is internally consistent by construction and
/// <see cref="VerifyLink"/> checks the relation <em>between</em> seals: digest linkage, index continuity,
/// and the genesis rules.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class SegmentSeal<TProof>: IEquatable<SegmentSeal<TProof>>
{
    private SegmentSeal(ulong firstIndex, ulong lastIndex, ReadOnlyMemory<byte>? previousSealDigest, ReadOnlyMemory<byte> commitment, ReadOnlyMemory<byte> canonicalBytes, ReadOnlyMemory<byte> digest, ImmutableArray<TProof> proofs)
    {
        FirstIndex = firstIndex;
        LastIndex = lastIndex;
        PreviousSealDigest = previousSealDigest;
        Commitment = commitment;
        CanonicalBytes = canonicalBytes;
        Digest = digest;
        Proofs = proofs;
    }


    /// <summary>The canonical byte-layout version this implementation produces.</summary>
    public const byte CurrentVersion = 0x01;


    /// <summary>The index of the first log entry covered by this seal.</summary>
    public ulong FirstIndex { get; }

    /// <summary>The index of the last log entry covered by this seal.</summary>
    public ulong LastIndex { get; }

    /// <summary>The digest of the previous seal, or <see langword="null"/> for the first seal.</summary>
    public ReadOnlyMemory<byte>? PreviousSealDigest { get; }

    /// <summary>The opaque commitment to the segment contents. The seal never interprets it.</summary>
    public ReadOnlyMemory<byte> Commitment { get; }

    /// <summary>The canonical byte encoding of this seal, per the documented versioned layout.</summary>
    public ReadOnlyMemory<byte> CanonicalBytes { get; }

    /// <summary>The digest of <see cref="CanonicalBytes"/>; what <see cref="Proofs"/> attest and the next seal links to.</summary>
    public ReadOnlyMemory<byte> Digest { get; }

    /// <summary>The attestation evidence over <see cref="Digest"/>; empty until the seal is attested.</summary>
    public ImmutableArray<TProof> Proofs { get; }


    /// <summary>
    /// Creates a seal over the entry range <c>[firstIndex, lastIndex]</c>, deriving the canonical bytes
    /// and digest from the typed fields.
    /// </summary>
    /// <param name="firstIndex">The index of the first covered entry. Must be zero when <paramref name="previousSealDigest"/> is <see langword="null"/>.</param>
    /// <param name="lastIndex">The index of the last covered entry. Must be at least <paramref name="firstIndex"/>.</param>
    /// <param name="previousSealDigest">The digest of the previous seal, or <see langword="null"/> for the first seal.</param>
    /// <param name="commitment">The opaque segment commitment. Must be non-empty.</param>
    /// <param name="proofs">The attestation evidence, or empty to attest later with <see cref="WithProofs"/>.</param>
    /// <param name="computeDigest">The digest function applied to the canonical bytes.</param>
    /// <returns>A new seal.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="lastIndex"/> is less than <paramref name="firstIndex"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="commitment"/> is empty, an empty <paramref name="previousSealDigest"/> is supplied, or <paramref name="previousSealDigest"/> is <see langword="null"/> while <paramref name="firstIndex"/> is not zero.</exception>
    public static SegmentSeal<TProof> Create(
        ulong firstIndex,
        ulong lastIndex,
        ReadOnlyMemory<byte>? previousSealDigest,
        ReadOnlyMemory<byte> commitment,
        ImmutableArray<TProof> proofs,
        ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastIndex, firstIndex);
        if(commitment.IsEmpty)
        {
            throw new ArgumentException("A seal commitment cannot be empty.", nameof(commitment));
        }

        if(previousSealDigest is { IsEmpty: true })
        {
            throw new ArgumentException("An empty previous seal digest is not representable; pass null for the first seal.", nameof(previousSealDigest));
        }

        if(previousSealDigest is null && firstIndex != 0)
        {
            throw new ArgumentException("The first seal must start at entry index zero.", nameof(firstIndex));
        }

        ReadOnlyMemory<byte> canonicalBytes = EncodeCanonical(firstIndex, lastIndex, previousSealDigest, commitment);
        ReadOnlyMemory<byte> digest = computeDigest(canonicalBytes);

        return new SegmentSeal<TProof>(firstIndex, lastIndex, previousSealDigest, commitment, canonicalBytes, digest, proofs.IsDefault ? ImmutableArray<TProof>.Empty : proofs);
    }


    /// <summary>
    /// Returns a copy of this seal carrying <paramref name="proofs"/>. The canonical bytes and digest are
    /// unchanged — proofs attest the digest and are outside the digested bytes — so a seal is created
    /// first, its digest attested, and the evidence attached here.
    /// </summary>
    /// <param name="proofs">The attestation evidence over <see cref="Digest"/>.</param>
    /// <returns>A new seal with the same content and the given proofs.</returns>
    public SegmentSeal<TProof> WithProofs(ImmutableArray<TProof> proofs)
    {
        return new SegmentSeal<TProof>(FirstIndex, LastIndex, PreviousSealDigest, Commitment, CanonicalBytes, Digest, proofs.IsDefault ? ImmutableArray<TProof>.Empty : proofs);
    }


    /// <summary>
    /// Verifies this seal's chain relation to <paramref name="previous"/>: the previous-digest linkage,
    /// index continuity, and the genesis rules. Attestation is verified separately through a
    /// <see cref="VerifySealAttestationDelegate{TProof, TContext}"/>.
    /// </summary>
    /// <param name="previous">The preceding seal, or <see langword="null"/> when this should be the first seal.</param>
    /// <returns><see langword="null"/> when the link holds, or an error message describing the break.</returns>
    public string? VerifyLink(SegmentSeal<TProof>? previous)
    {
        if(previous is null)
        {
            if(PreviousSealDigest is not null)
            {
                return "the seal claims a previous seal but none was supplied";
            }

            if(FirstIndex != 0)
            {
                return "the first seal must start at entry index zero";
            }

            return null;
        }

        if(PreviousSealDigest is null)
        {
            return "the seal claims to be first but a previous seal was supplied";
        }

        if(!PreviousSealDigest.Value.Span.SequenceEqual(previous.Digest.Span))
        {
            return "the previous seal digest does not match the preceding seal";
        }

        if(FirstIndex != previous.LastIndex + 1)
        {
            return $"the seal covers entries from {FirstIndex} but the preceding seal ends at {previous.LastIndex}";
        }

        return null;
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] SegmentSeal<TProof>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Digest.Span.SequenceEqual(other.Digest.Span)
            && CanonicalBytes.Span.SequenceEqual(other.CanonicalBytes.Span);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SegmentSeal<TProof> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Digest.Span);

        return hash.ToHashCode();
    }


    private static ReadOnlyMemory<byte> EncodeCanonical(ulong firstIndex, ulong lastIndex, ReadOnlyMemory<byte>? previousSealDigest, ReadOnlyMemory<byte> commitment)
    {
        int previousLength = previousSealDigest?.Length ?? 0;
        var buffer = new byte[1 + 8 + 8 + 4 + previousLength + 4 + commitment.Length];
        buffer[0] = CurrentVersion;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(1), firstIndex);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(9), lastIndex);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(17), previousLength);
        if(previousSealDigest is { } previous)
        {
            previous.Span.CopyTo(buffer.AsSpan(21));
        }

        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(21 + previousLength), commitment.Length);
        commitment.Span.CopyTo(buffer.AsSpan(25 + previousLength));

        return buffer;
    }


    private string DebuggerDisplay => $"SegmentSeal: entries {FirstIndex}..{LastIndex}, {Proofs.Length} proof(s)";
}
