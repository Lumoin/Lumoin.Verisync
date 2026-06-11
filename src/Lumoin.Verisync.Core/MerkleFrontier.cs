using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The bounded-state companion to <see cref="MerkleLogTree"/>: an immutable, append-only tracker of the
/// Merkle Tree Hash (root) that retains only the frontier peaks — one perfect-subtree hash per set bit of
/// the leaf count, <c>O(log n)</c> state — instead of every leaf, yet produces roots byte-identical to
/// <see cref="MerkleLogTree.ComputeRoot(ComputeDigestDelegate)"/> for every size.
/// </summary>
/// <remarks>
/// <para>
/// A frontier is the right-state of the RFC 9162 (Certificate Transparency v2) Merkle log tree carried by a
/// live replica that wants to keep its root current without holding the leaves: the leaves are archived, and
/// <see cref="MerkleLogTree"/> over those archived leaves is what produces inclusion and consistency proofs.
/// A frontier produces no proofs of its own — it commits only to the root — so the two types are
/// complementary, not interchangeable.
/// </para>
/// <para>
/// The state is the peak decomposition of the leaf range, which follows the binary representation of
/// <see cref="Count"/>: each set bit of the count contributes one perfect-subtree root whose height is the
/// bit position, and the peaks are held with heights strictly descending from left (highest, oldest leaves)
/// to right (lowest, newest leaves). The hashing contract is exactly that of <see cref="MerkleLogTree"/> and
/// is reused from it — leaf hash <c>H(0x00 || leafBytes)</c>, interior hash
/// <c>H(0x01 || left || right)</c>, and the digest of empty input as the root of the empty frontier — so any
/// claim that the two roots agree is a structural identity, not a coincidence, and is asserted across an
/// exhaustive size sweep in the tests.
/// </para>
/// <para>
/// Hashing happens at <see cref="Append(ReadOnlyMemory{byte}, ComputeDigestDelegate)"/> time, so the
/// <see cref="ComputeDigestDelegate"/> is supplied there and again at
/// <see cref="ComputeRoot(ComputeDigestDelegate)"/>. The same digest function must be used for every call on
/// a given frontier and its descendants: the peaks are stored as already-hashed bytes, so mixing digest
/// functions between appends, or between appends and the final root computation, silently produces a root
/// that commits to nothing meaningful. This is a caller obligation, not a runtime check — the type cannot
/// recover the algorithm from an opaque hash.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class MerkleFrontier
{
    /// <summary>
    /// A single frontier peak: the root hash of one perfect (full) binary subtree together with its
    /// <see cref="Height"/> (the base-two logarithm of the number of leaves it covers).
    /// </summary>
    /// <param name="Height">The height of the perfect subtree; height <c>0</c> is a single leaf.</param>
    /// <param name="Hash">The Merkle Tree Hash of the perfect subtree at this peak.</param>
    private readonly record struct Peak(int Height, ReadOnlyMemory<byte> Hash);


    private ImmutableArray<Peak> Peaks { get; }


    private MerkleFrontier(ImmutableArray<Peak> peaks, int count)
    {
        Peaks = peaks;
        Count = count;
    }


    /// <summary>An empty frontier, tracking no leaves.</summary>
    public static MerkleFrontier Empty { get; } = new(ImmutableArray<Peak>.Empty, 0);


    /// <summary>The number of leaves committed to by this frontier.</summary>
    public int Count { get; }


    /// <summary>
    /// Returns a new frontier with <paramref name="leafBytes"/> committed as the last leaf. The receiver is
    /// unchanged.
    /// </summary>
    /// <param name="leafBytes">The opaque leaf byte string to append.</param>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout; the same function must be used for every call on this frontier.</param>
    /// <returns>A new <see cref="MerkleFrontier"/> committing to one additional leaf.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The new leaf enters as a height-<c>0</c> peak, then equal-height peaks are merged pairwise from the
    /// right with the interior hash — the carry of a binary counter — until the heights are again strictly
    /// descending. The number of merges is the number of trailing one-bits in the old count, so the work is
    /// <c>O(log n)</c> amortized to <c>O(1)</c>.
    /// </remarks>
    public MerkleFrontier Append(ReadOnlyMemory<byte> leafBytes, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);

        ImmutableArray<Peak>.Builder peaks = Peaks.ToBuilder();
        peaks.Add(new Peak(0, MerkleLogTree.LeafHash(leafBytes, computeDigest)));

        //Binary-counter carry: while the rightmost two peaks share a height, combine them into the
        //next-higher perfect subtree. Heights are strictly descending, so only the right end can ever carry.
        while(peaks.Count >= 2 && peaks[^1].Height == peaks[^2].Height)
        {
            Peak right = peaks[^1];
            Peak left = peaks[^2];
            peaks.RemoveAt(peaks.Count - 1);
            peaks[^1] = new Peak(left.Height + 1, MerkleLogTree.InteriorHash(left.Hash, right.Hash, computeDigest));
        }

        return new MerkleFrontier(peaks.ToImmutable(), Count + 1);
    }


    /// <summary>
    /// Computes the Merkle Tree Hash (root) of the frontier using <paramref name="computeDigest"/>. The
    /// result is byte-identical to <see cref="MerkleLogTree.ComputeRoot(ComputeDigestDelegate)"/> over the
    /// same leaves.
    /// </summary>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout; the same function must have been used for every <see cref="Append(ReadOnlyMemory{byte}, ComputeDigestDelegate)"/>.</param>
    /// <returns>The root hash: the digest of empty input for an empty frontier, the single peak hash for a perfect tree, otherwise the peaks bagged right-to-left.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The peaks are bagged from the right: the accumulator starts at the rightmost (lowest) peak, and each
    /// peak to its left becomes the left child of a fresh interior node — <c>acc = H(0x01 || peak || acc)</c>.
    /// This reproduces the RFC 9162 split rule for non-power-of-two sizes, where the left subtree at every
    /// level is a perfect tree (a single peak) and the right subtree holds the lower-order remainder.
    /// </remarks>
    public ReadOnlyMemory<byte> ComputeRoot(ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);

        if(Peaks.IsEmpty)
        {
            return computeDigest(ReadOnlyMemory<byte>.Empty);
        }

        ReadOnlyMemory<byte> accumulator = Peaks[^1].Hash;
        for(int i = Peaks.Length - 2; i >= 0; i--)
        {
            accumulator = MerkleLogTree.InteriorHash(Peaks[i].Hash, accumulator, computeDigest);
        }

        return accumulator;
    }


    private string DebuggerDisplay => $"MerkleFrontier: {Count} leaves, {Peaks.Length} peak(s)";
}
