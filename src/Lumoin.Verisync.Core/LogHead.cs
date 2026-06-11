using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A replica's log-plane head claim: the size of its append-only Merkle log tree and the root at that
/// size. Two heads plus a <see cref="MerkleConsistencyProof"/> let any party check that the larger
/// history extends the smaller one — the anti-equivocation exchange of the sealed-segments design.
/// </summary>
/// <remarks>
/// <para>
/// A head is the log-plane counterpart of the CRDT-plane <see cref="GossipDigest"/>: the digest
/// summarizes causality, the head summarizes the committed log. They travel side by side during
/// anti-entropy and are deliberately separate types — the two planes have different visibility and
/// trust properties.
/// </para>
/// <para>
/// A bare head is a claim, not evidence. In the single-tree composition — an
/// <see cref="AuthenticatedRegister{TState, TOperation, TProof, TContext, TAccumulator}"/> whose
/// accumulator is one ever-growing <see cref="MerkleLogTree"/> over entry digests — a
/// <see cref="SegmentSeal{TProof}"/> whose commitment is the tree root at size
/// <c>LastIndex + 1</c> is exactly an <em>attested</em> head, so fork evidence built from two attested
/// heads and a failing consistency proof is verifiable by third parties.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class LogHead: IEquatable<LogHead>
{
    /// <summary>
    /// Initializes a new head claim.
    /// </summary>
    /// <param name="treeSize">The number of leaves in the claimed tree.</param>
    /// <param name="root">The Merkle root at that size. Never empty — even the empty tree has a root, the digest of empty input.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="treeSize"/> is negative.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="root"/> is empty.</exception>
    public LogHead(int treeSize, ReadOnlyMemory<byte> root)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(treeSize);
        if(root.IsEmpty)
        {
            throw new ArgumentException("A head root cannot be empty; the empty tree's root is the digest of empty input.", nameof(root));
        }

        TreeSize = treeSize;
        Root = root;
    }


    /// <summary>The number of leaves in the claimed tree.</summary>
    public int TreeSize { get; }

    /// <summary>The Merkle root at <see cref="TreeSize"/>.</summary>
    public ReadOnlyMemory<byte> Root { get; }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] LogHead? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return TreeSize == other.TreeSize && Root.Span.SequenceEqual(other.Root.Span);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LogHead other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(TreeSize);
        hash.AddBytes(Root.Span);

        return hash.ToHashCode();
    }


    private string DebuggerDisplay => $"LogHead: {TreeSize} leaves";
}
