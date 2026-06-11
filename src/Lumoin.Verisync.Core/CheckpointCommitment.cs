using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The consensus-grade anchor for a promoted checkpoint: the digest of the snapshot's canonical bytes.
/// This — never the snapshot itself — is what rides the CASPaxos register, keeping consensus payloads
/// metadata-sized regardless of sequence length; the content travels the CRDT plane and is verifiable
/// against the commitment.
/// </summary>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class CheckpointCommitment: IEquatable<CheckpointCommitment>
{
    /// <summary>
    /// Initializes a new commitment.
    /// </summary>
    /// <param name="digest">The digest of the checkpoint's canonical bytes. Never empty.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="digest"/> is empty.</exception>
    public CheckpointCommitment(ReadOnlyMemory<byte> digest)
    {
        if(digest.IsEmpty)
        {
            throw new ArgumentException("A checkpoint commitment digest cannot be empty.", nameof(digest));
        }

        Digest = digest;
    }


    /// <summary>The digest of the checkpoint's canonical bytes.</summary>
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

        return Digest.Span.SequenceEqual(other.Digest.Span);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CheckpointCommitment other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(Digest.Span);

        return hash.ToHashCode();
    }


    private string DebuggerDisplay => $"CheckpointCommitment: {Digest.Length} bytes";
}
