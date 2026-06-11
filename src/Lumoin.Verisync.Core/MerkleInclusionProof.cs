using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A bottom-up inclusion (audit) path proving that a single leaf occupies a given index in a
/// <see cref="MerkleLogTree"/> of a given size, per RFC 9162 §2.1.3.1. Verification reconstructs the root
/// from the leaf and the path per §2.1.3.2 and compares it byte-for-byte to an expected root.
/// </summary>
/// <remarks>
/// The proof is an immutable value. <see cref="Verify(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, ComputeDigestDelegate)"/>
/// takes the raw leaf byte string — not a pre-hashed leaf — and applies the mandatory <c>0x00</c>
/// domain-separation prefix itself. Any structural mismatch (a path of the wrong length for the index and
/// size, or hashes that do not reconstruct the expected root) makes verification return
/// <see langword="false"/> rather than throw.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class MerkleInclusionProof
{
    /// <summary>
    /// Initializes a new inclusion proof.
    /// </summary>
    /// <param name="leafIndex">The zero-based index of the proved leaf.</param>
    /// <param name="treeSize">The size of the tree the proof was produced against.</param>
    /// <param name="path">The bottom-up sibling hashes from the leaf upward.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="leafIndex"/> is negative, if <paramref name="treeSize"/> is negative, or if <paramref name="leafIndex"/> is not less than <paramref name="treeSize"/>.</exception>
    public MerkleInclusionProof(int leafIndex, int treeSize, ImmutableArray<ReadOnlyMemory<byte>> path)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(treeSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leafIndex, treeSize);

        LeafIndex = leafIndex;
        TreeSize = treeSize;
        Path = path;
    }


    /// <summary>The zero-based index of the proved leaf.</summary>
    public int LeafIndex { get; }

    /// <summary>The size of the tree the proof was produced against.</summary>
    public int TreeSize { get; }

    /// <summary>The bottom-up audit path: the sibling hashes from the leaf upward to the root.</summary>
    public ImmutableArray<ReadOnlyMemory<byte>> Path { get; }


    /// <summary>
    /// Verifies this inclusion proof by reconstructing the root from the raw leaf bytes and the audit path
    /// per RFC 9162 §2.1.3.2 and comparing it byte-for-byte to <paramref name="expectedRoot"/>.
    /// </summary>
    /// <param name="leafBytes">The raw, un-hashed leaf byte string; the <c>0x00</c> leaf prefix is applied internally.</param>
    /// <param name="expectedRoot">The root the reconstruction must match.</param>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout.</param>
    /// <returns><see langword="true"/> if the reconstructed root equals <paramref name="expectedRoot"/>; <see langword="false"/> for any mismatch or structurally invalid path.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    public bool Verify(ReadOnlyMemory<byte> leafBytes, ReadOnlyMemory<byte> expectedRoot, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);

        if(Path.IsDefault)
        {
            return false;
        }

        // The number of sibling hashes must equal the height of the leaf's position in a tree of TreeSize
        // leaves: the count of bits remaining once the shared low-order run between index and the last index
        // is consumed (RFC 9162 §2.1.3.2).
        int expectedPathLength = 0;
        int index = LeafIndex;
        int lastIndex = TreeSize - 1;
        while(lastIndex > 0)
        {
            if((index & 1) != 0 || index < lastIndex)
            {
                expectedPathLength++;
            }

            index >>= 1;
            lastIndex >>= 1;
        }

        if(Path.Length != expectedPathLength)
        {
            return false;
        }

        ReadOnlyMemory<byte> hash = MerkleLogTree.LeafHash(leafBytes, computeDigest);
        int nodeIndex = LeafIndex;
        int lastNode = TreeSize - 1;
        int pathPosition = 0;
        while(lastNode > 0)
        {
            if((nodeIndex & 1) != 0)
            {
                hash = MerkleLogTree.InteriorHash(Path[pathPosition], hash, computeDigest);
                pathPosition++;
            }
            else if(nodeIndex < lastNode)
            {
                hash = MerkleLogTree.InteriorHash(hash, Path[pathPosition], computeDigest);
                pathPosition++;
            }

            nodeIndex >>= 1;
            lastNode >>= 1;
        }

        return hash.Span.SequenceEqual(expectedRoot.Span);
    }


    private string DebuggerDisplay => $"MerkleInclusionProof: leaf {LeafIndex} of {TreeSize}, {(Path.IsDefault ? 0 : Path.Length)} steps";
}
