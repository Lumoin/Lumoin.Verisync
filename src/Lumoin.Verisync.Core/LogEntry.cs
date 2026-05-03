using System;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A single entry in an authenticated append-only log.
/// </summary>
/// <typeparam name="TOperation">The domain operation type carried by this entry.</typeparam>
/// <typeparam name="TProof">The proof type carried by this entry.</typeparam>
/// <remarks>
/// <para>
/// Each entry carries a typed operation, one or more typed proofs, the canonical byte representation
/// used to compute the entry digest, and the chain-linking digests that make the log tamper-evident.
/// </para>
/// <para>
/// <strong>Domain payload and proof.</strong> <typeparamref name="TOperation"/> is the domain payload
/// — a CRDT delta, a register write, a supply-chain event, or any other append-only action.
/// <typeparamref name="TProof"/> is the proof of authorization — a signature, a zero-knowledge role
/// proof, a hardware-bound attestation, or any other verifiable evidence. Neither type is constrained
/// by the infrastructure; the caller defines both.
/// </para>
/// <para>
/// <strong>What the digest chain guarantees.</strong> <see cref="PreviousDigest"/> and
/// <see cref="Digest"/> form a commitment chain. <see cref="Digest"/> is computed over
/// <see cref="CanonicalBytes"/>; <see cref="PreviousDigest"/> chains the entry to its predecessor.
/// Any modification to an earlier entry changes that entry's digest, invalidating every subsequent
/// <see cref="PreviousDigest"/> reference. A verifier threads the authoritative previous digest forward
/// rather than trusting the value the entry claims, so tampering is detectable at the point it occurs.
/// </para>
/// <para>
/// <strong>Multiple proofs.</strong> Multiple proofs represent co-authorizing evidence over the same
/// log stream — conventionally a controller proof first, then witness proofs. The threshold logic
/// (unanimity, k-of-n quorum, or controller-only) is the caller's responsibility above this shape.
/// </para>
/// <para>
/// <strong>Heartbeat entries.</strong> <see cref="Operation"/> is nullable to support entries that
/// carry no state mutation. A heartbeat re-witnesses the current digest to establish liveness; the
/// chain still advances and proofs are still validated.
/// </para>
/// </remarks>
public sealed class LogEntry<TOperation, TProof>: IEquatable<LogEntry<TOperation, TProof>>
{
    /// <summary>Gets the zero-based position of this entry in the log.</summary>
    public required ulong Index { get; init; }

    /// <summary>
    /// Gets the digest of the previous entry, or <see langword="null"/> for the genesis entry (index zero).
    /// </summary>
    /// <remarks>
    /// This is what the entry claims its predecessor's digest to be. A verifier compares it against the
    /// digest it independently observed from the previous entry; trusting this field directly would let
    /// an attacker forge a consistent chain from a tampered log.
    /// </remarks>
    public required ReadOnlyMemory<byte>? PreviousDigest { get; init; }

    /// <summary>Gets the digest of this entry, computed over <see cref="CanonicalBytes"/>.</summary>
    public required ReadOnlyMemory<byte> Digest { get; init; }

    /// <summary>
    /// Gets the canonical byte representation of this entry used to compute <see cref="Digest"/> and to
    /// verify chain linkage.
    /// </summary>
    /// <remarks>
    /// The canonicalization algorithm is caller-defined and lives at a serialization boundary outside
    /// this core. It must be deterministic: the same logical entry must always produce the same bytes,
    /// or digest verification fails non-deterministically across verifiers.
    /// </remarks>
    public required ReadOnlyMemory<byte> CanonicalBytes { get; init; }

    /// <summary>
    /// Gets the domain operation carried by this entry, or <see langword="null"/> for entries that carry
    /// no state mutation such as heartbeat entries.
    /// </summary>
    public required TOperation? Operation { get; init; }

    /// <summary>Gets the proofs of authorization for this entry.</summary>
    /// <remarks>
    /// Contains at least one proof. The first proof is conventionally the controller proof; subsequent
    /// proofs are witness proofs or other co-authorizing evidence over the same log stream.
    /// </remarks>
    public required ImmutableArray<TProof> Proofs { get; init; }


    /// <inheritdoc/>
    public bool Equals(LogEntry<TOperation, TProof>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Index == other.Index
            && Digest.Span.SequenceEqual(other.Digest.Span)
            && NullableSpanEqual(PreviousDigest, other.PreviousDigest)
            && CanonicalBytes.Span.SequenceEqual(other.CanonicalBytes.Span);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LogEntry<TOperation, TProof> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Index, MemoryHashCode(Digest), MemoryHashCode(CanonicalBytes));


    /// <summary>Determines whether two entries are equal.</summary>
    public static bool operator ==(LogEntry<TOperation, TProof>? left, LogEntry<TOperation, TProof>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two entries are not equal.</summary>
    public static bool operator !=(LogEntry<TOperation, TProof>? left, LogEntry<TOperation, TProof>? right) =>
        !(left == right);


    private static bool NullableSpanEqual(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right)
    {
        if(left is null && right is null)
        {
            return true;
        }

        if(left is null || right is null)
        {
            return false;
        }

        return left.Value.Span.SequenceEqual(right.Value.Span);
    }


    private static int MemoryHashCode(ReadOnlyMemory<byte> memory)
    {
        var hash = new HashCode();
        hash.AddBytes(memory.Span);

        return hash.ToHashCode();
    }
}
