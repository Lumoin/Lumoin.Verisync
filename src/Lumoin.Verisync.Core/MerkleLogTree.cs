using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// An append-only Merkle log tree following the hashing structure of RFC 9162 (Certificate Transparency v2):
/// an ordered list of opaque leaf byte strings whose Merkle Tree Hash commits to the exact contents and
/// order of the leaves, with audit paths proving the inclusion of a single leaf and consistency proofs
/// proving that one tree is a prefix-preserving extension of an earlier one.
/// </summary>
/// <remarks>
/// <para>
/// The tree is an immutable value; <see cref="Append(ReadOnlyMemory{byte})"/> returns a new tree and leaves
/// the receiver unchanged. Hashing is a cryptographic concern injected through
/// <see cref="ComputeDigestDelegate"/>; the tree owns only the structure and the domain-separated byte layout
/// of what is hashed.
/// </para>
/// <para>
/// The byte layout is the versioned cross-stack contract that any other implementation must reproduce
/// byte-for-byte to arrive at the same roots and proofs. Writing <c>H</c> for the injected digest, <c>||</c>
/// for concatenation, and <c>D[n]</c> for an ordered list of <c>n</c> leaf byte strings:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     Leaf hash: <c>H(0x00 || leafBytes)</c>. The single <c>0x00</c> domain-separation prefix is structural
///     and mandatory; it distinguishes a leaf from an interior node so that no leaf can be presented as a
///     subtree and vice versa.
///     </description>
///   </item>
///   <item>
///     <description>
///     Interior node: <c>H(0x01 || leftHash || rightHash)</c>. The single <c>0x01</c> prefix is likewise
///     structural and mandatory, and the left and right child hashes are concatenated in that order.
///     </description>
///   </item>
///   <item>
///     <description>Root of the empty tree (<c>n = 0</c>): <c>H(empty input)</c> — the digest of an empty byte string.</description>
///   </item>
///   <item>
///     <description>Root of a single-leaf tree (<c>n = 1</c>): the leaf hash of that leaf, with no interior node.</description>
///   </item>
///   <item>
///     <description>
///     Split rule for <c>n &gt; 1</c>: let <c>k</c> be the largest power of two strictly less than <c>n</c>;
///     then <c>MTH(D[n]) = H(0x01 || MTH(D[0:k]) || MTH(D[k:n]))</c>. The left subtree is therefore a
///     perfect (full) binary tree, and the right subtree holds the remainder.
///     </description>
///   </item>
/// </list>
/// <para>
/// Inclusion (audit) paths are produced per RFC 9162 §2.1.3.1 as a bottom-up list of sibling hashes and
/// verified per §2.1.3.2; consistency proofs are produced per §2.1.4.1 and verified per §2.1.4.2. All root
/// and hash comparisons are byte-for-byte over spans.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class MerkleLogTree
{
    private ImmutableArray<ReadOnlyMemory<byte>> Leaves { get; }


    private MerkleLogTree(ImmutableArray<ReadOnlyMemory<byte>> leaves)
    {
        Leaves = leaves;
    }


    /// <summary>An empty log tree, holding no leaves.</summary>
    public static MerkleLogTree Empty { get; } = new(ImmutableArray<ReadOnlyMemory<byte>>.Empty);


    /// <summary>The number of leaves in the tree.</summary>
    public int Count => Leaves.Length;


    /// <summary>
    /// Returns a new tree with <paramref name="leafBytes"/> appended as the last leaf. The receiver is
    /// unchanged.
    /// </summary>
    /// <param name="leafBytes">The opaque leaf byte string to append.</param>
    /// <returns>A new <see cref="MerkleLogTree"/> with one additional leaf.</returns>
    public MerkleLogTree Append(ReadOnlyMemory<byte> leafBytes)
    {
        return new MerkleLogTree(Leaves.Add(leafBytes));
    }


    /// <summary>
    /// Computes the Merkle Tree Hash (root) of the tree using <paramref name="computeDigest"/>.
    /// </summary>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout.</param>
    /// <returns>The root hash: the digest of empty input for an empty tree, the leaf hash for a single leaf, otherwise the recursively split interior hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    public ReadOnlyMemory<byte> ComputeRoot(ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);

        return MerkleTreeHash(Leaves, 0, Leaves.Length, computeDigest);
    }


    /// <summary>
    /// Produces a bottom-up inclusion (audit) path for the leaf at <paramref name="leafIndex"/> per
    /// RFC 9162 §2.1.3.1.
    /// </summary>
    /// <param name="leafIndex">The zero-based index of the leaf to prove.</param>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout.</param>
    /// <returns>An inclusion proof carrying the leaf index, the current tree size, and the sibling hashes from the leaf upward.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="leafIndex"/> is negative or not less than <see cref="Count"/>.</exception>
    public MerkleInclusionProof ProveInclusion(int leafIndex, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);
        ArgumentOutOfRangeException.ThrowIfNegative(leafIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(leafIndex, Leaves.Length);

        ImmutableArray<ReadOnlyMemory<byte>>.Builder path = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        BuildInclusionPath(Leaves, 0, Leaves.Length, leafIndex, computeDigest, path);

        return new MerkleInclusionProof(leafIndex, Leaves.Length, path.ToImmutable());
    }


    /// <summary>
    /// Produces a consistency proof showing this tree is a prefix-preserving extension of its own first
    /// <paramref name="oldTreeSize"/> leaves, per RFC 9162 §2.1.4.1.
    /// </summary>
    /// <param name="oldTreeSize">The size of the earlier tree the proof relates this tree to.</param>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout.</param>
    /// <returns>A consistency proof carrying the old and new tree sizes and the proof hashes; the path is empty when <paramref name="oldTreeSize"/> is zero or equals <see cref="Count"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="oldTreeSize"/> is negative or greater than <see cref="Count"/>.</exception>
    public MerkleConsistencyProof ProveConsistency(int oldTreeSize, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);
        ArgumentOutOfRangeException.ThrowIfNegative(oldTreeSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(oldTreeSize, Leaves.Length);

        ImmutableArray<ReadOnlyMemory<byte>> path;
        if(oldTreeSize == 0 || oldTreeSize == Leaves.Length)
        {
            path = ImmutableArray<ReadOnlyMemory<byte>>.Empty;
        }
        else
        {
            ImmutableArray<ReadOnlyMemory<byte>>.Builder builder = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
            BuildConsistencySubProof(Leaves, 0, Leaves.Length, oldTreeSize, true, computeDigest, builder);
            path = builder.ToImmutable();
        }

        return new MerkleConsistencyProof(oldTreeSize, Leaves.Length, path);
    }


    /// <summary>
    /// Computes the Merkle Tree Hash of the leaf sub-range <c>[start, end)</c> of <paramref name="leaves"/>.
    /// </summary>
    /// <param name="leaves">The full ordered leaf list.</param>
    /// <param name="start">The inclusive lower bound of the sub-range.</param>
    /// <param name="end">The exclusive upper bound of the sub-range.</param>
    /// <param name="computeDigest">The digest function.</param>
    /// <returns>The Merkle Tree Hash of the sub-range.</returns>
    private static ReadOnlyMemory<byte> MerkleTreeHash(ImmutableArray<ReadOnlyMemory<byte>> leaves, int start, int end, ComputeDigestDelegate computeDigest)
    {
        int count = end - start;
        if(count == 0)
        {
            return computeDigest(ReadOnlyMemory<byte>.Empty);
        }

        if(count == 1)
        {
            return LeafHash(leaves[start], computeDigest);
        }

        int k = LargestPowerOfTwoLessThan(count);
        ReadOnlyMemory<byte> left = MerkleTreeHash(leaves, start, start + k, computeDigest);
        ReadOnlyMemory<byte> right = MerkleTreeHash(leaves, start + k, end, computeDigest);

        return InteriorHash(left, right, computeDigest);
    }


    /// <summary>
    /// Appends to <paramref name="path"/> the bottom-up sibling hashes for the leaf at
    /// <paramref name="leafIndex"/> within the sub-range <c>[start, end)</c>.
    /// </summary>
    /// <param name="leaves">The full ordered leaf list.</param>
    /// <param name="start">The inclusive lower bound of the sub-range.</param>
    /// <param name="end">The exclusive upper bound of the sub-range.</param>
    /// <param name="leafIndex">The absolute index of the leaf being proved.</param>
    /// <param name="computeDigest">The digest function.</param>
    /// <param name="path">The path builder to append sibling hashes to.</param>
    private static void BuildInclusionPath(ImmutableArray<ReadOnlyMemory<byte>> leaves, int start, int end, int leafIndex, ComputeDigestDelegate computeDigest, ImmutableArray<ReadOnlyMemory<byte>>.Builder path)
    {
        int count = end - start;
        if(count == 1)
        {
            return;
        }

        int k = LargestPowerOfTwoLessThan(count);
        if(leafIndex - start < k)
        {
            BuildInclusionPath(leaves, start, start + k, leafIndex, computeDigest, path);
            path.Add(MerkleTreeHash(leaves, start + k, end, computeDigest));
        }
        else
        {
            BuildInclusionPath(leaves, start + k, end, leafIndex, computeDigest, path);
            path.Add(MerkleTreeHash(leaves, start, start + k, computeDigest));
        }
    }


    /// <summary>
    /// Appends to <paramref name="proof"/> the consistency sub-proof relating the first <paramref name="m"/>
    /// leaves of the sub-range <c>[start, end)</c> to the whole sub-range, per RFC 9162 §2.1.4.1.
    /// </summary>
    /// <param name="leaves">The full ordered leaf list.</param>
    /// <param name="start">The inclusive lower bound of the sub-range.</param>
    /// <param name="end">The exclusive upper bound of the sub-range.</param>
    /// <param name="m">The size of the earlier sub-tree within the sub-range.</param>
    /// <param name="oldIsCompleteSubtree">Whether the earlier tree is, at this level, the complete sub-range (the <c>b</c> flag).</param>
    /// <param name="computeDigest">The digest function.</param>
    /// <param name="proof">The proof builder to append hashes to.</param>
    private static void BuildConsistencySubProof(ImmutableArray<ReadOnlyMemory<byte>> leaves, int start, int end, int m, bool oldIsCompleteSubtree, ComputeDigestDelegate computeDigest, ImmutableArray<ReadOnlyMemory<byte>>.Builder proof)
    {
        int count = end - start;
        if(m == count)
        {
            if(!oldIsCompleteSubtree)
            {
                proof.Add(MerkleTreeHash(leaves, start, end, computeDigest));
            }

            return;
        }

        int k = LargestPowerOfTwoLessThan(count);
        if(m <= k)
        {
            BuildConsistencySubProof(leaves, start, start + k, m, oldIsCompleteSubtree, computeDigest, proof);
            proof.Add(MerkleTreeHash(leaves, start + k, end, computeDigest));
        }
        else
        {
            BuildConsistencySubProof(leaves, start + k, end, m - k, false, computeDigest, proof);
            proof.Add(MerkleTreeHash(leaves, start, start + k, computeDigest));
        }
    }


    /// <summary>
    /// Computes a leaf hash: <c>H(0x00 || leafBytes)</c>.
    /// </summary>
    /// <param name="leafBytes">The opaque leaf byte string.</param>
    /// <param name="computeDigest">The digest function.</param>
    /// <returns>The leaf hash.</returns>
    internal static ReadOnlyMemory<byte> LeafHash(ReadOnlyMemory<byte> leafBytes, ComputeDigestDelegate computeDigest)
    {
        var buffer = new byte[1 + leafBytes.Length];
        buffer[0] = 0x00;
        leafBytes.Span.CopyTo(buffer.AsSpan(1));

        return computeDigest(buffer);
    }


    /// <summary>
    /// Computes an interior node hash: <c>H(0x01 || leftHash || rightHash)</c>.
    /// </summary>
    /// <param name="leftHash">The left child hash.</param>
    /// <param name="rightHash">The right child hash.</param>
    /// <param name="computeDigest">The digest function.</param>
    /// <returns>The interior node hash.</returns>
    internal static ReadOnlyMemory<byte> InteriorHash(ReadOnlyMemory<byte> leftHash, ReadOnlyMemory<byte> rightHash, ComputeDigestDelegate computeDigest)
    {
        var buffer = new byte[1 + leftHash.Length + rightHash.Length];
        buffer[0] = 0x01;
        leftHash.Span.CopyTo(buffer.AsSpan(1));
        rightHash.Span.CopyTo(buffer.AsSpan(1 + leftHash.Length));

        return computeDigest(buffer);
    }


    /// <summary>
    /// Returns the largest power of two strictly less than <paramref name="n"/>, for <c>n &gt; 1</c>.
    /// </summary>
    /// <param name="n">The split count, greater than one.</param>
    /// <returns>The largest power of two strictly less than <paramref name="n"/>.</returns>
    private static int LargestPowerOfTwoLessThan(int n)
    {
        int k = 1;
        while((k << 1) < n)
        {
            k <<= 1;
        }

        return k;
    }


    private string DebuggerDisplay => $"MerkleLogTree: {Count} leaves";
}
