using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The consensus-grade anchor of a SEALED checkpoint: the stability frontier the checkpoint was sealed at,
/// paired with the digest of the certified dotted projection's canonical bytes at exactly that frontier. This
/// pair — never the snapshot itself — is what rides the CASPaxos register, keeping consensus payloads
/// metadata-sized regardless of sequence length; the content travels the CRDT plane and is verifiable against
/// the digest.
/// </summary>
/// <remarks>
/// The committed line is a CHAIN: each committed frontier strictly dominates its predecessor in the
/// happens-before partial order, or repeats it byte-identically (an idempotent re-seal). The frontier is what
/// the container's monotone refusal rule compares — see
/// <see cref="CheckpointedSequence{TSequence, TValue, TAnchor}.Seal"/> — so a competing seal can never regress
/// the committed checkpoint.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class CheckpointCommitment: IEquatable<CheckpointCommitment>
{
    /// <summary>
    /// Initializes a new commitment.
    /// </summary>
    /// <param name="frontier">The stability frontier the checkpoint was sealed at.</param>
    /// <param name="digest">The digest of the certified dotted projection's canonical bytes at <paramref name="frontier"/>. Never empty.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="frontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="digest"/> is empty.</exception>
    public CheckpointCommitment(VectorClock frontier, ReadOnlyMemory<byte> digest)
    {
        ArgumentNullException.ThrowIfNull(frontier);
        if(digest.IsEmpty)
        {
            throw new ArgumentException("A checkpoint commitment digest cannot be empty.", nameof(digest));
        }

        Frontier = frontier;
        Digest = digest;
    }


    /// <summary>The stability frontier the checkpoint was sealed at.</summary>
    public VectorClock Frontier { get; }

    /// <summary>The digest of the certified dotted projection's canonical bytes at <see cref="Frontier"/>.</summary>
    public ReadOnlyMemory<byte> Digest { get; }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] CheckpointCommitment? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Frontier.Equals(other.Frontier) && Digest.Span.SequenceEqual(other.Digest.Span);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CheckpointCommitment other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Frontier);
        hash.AddBytes(Digest.Span);

        return hash.ToHashCode();
    }


    private string DebuggerDisplay => $"CheckpointCommitment: {Digest.Length} bytes @ frontier";
}
