using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A consistency proof showing that a <see cref="MerkleLogTree"/> of size <see cref="NewTreeSize"/> is a
/// prefix-preserving extension of one of size <see cref="OldTreeSize"/> — that the first
/// <see cref="OldTreeSize"/> leaves are unchanged and only appends followed — per RFC 9162 §2.1.4.1.
/// Verification reconstructs both roots from the proof per §2.1.4.2 and compares them byte-for-byte.
/// </summary>
/// <remarks>
/// <para>
/// The proof is an immutable value.
/// <see cref="Verify(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}, ComputeDigestDelegate)"/> never throws on
/// hostile data: inconsistent sizes, a path of the wrong length, or hashes that do not reconstruct both
/// roots all make it return <see langword="false"/>.
/// </para>
/// <para>
/// Two edge cases carry an empty path. When <see cref="OldTreeSize"/> equals <see cref="NewTreeSize"/> the
/// proof verifies only that the supplied old and new roots are equal byte-for-byte. When
/// <see cref="OldTreeSize"/> is zero the proof verifies that the supplied old root equals the empty-tree root
/// (the digest of empty input); every tree is consistent with the empty tree.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class MerkleConsistencyProof
{
    /// <summary>
    /// Initializes a new consistency proof.
    /// </summary>
    /// <param name="oldTreeSize">The size of the earlier tree.</param>
    /// <param name="newTreeSize">The size of the later tree.</param>
    /// <param name="path">The proof hashes per RFC 9162 §2.1.4.1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="oldTreeSize"/> or <paramref name="newTreeSize"/> is negative.</exception>
    public MerkleConsistencyProof(int oldTreeSize, int newTreeSize, ImmutableArray<ReadOnlyMemory<byte>> path)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(oldTreeSize);
        ArgumentOutOfRangeException.ThrowIfNegative(newTreeSize);

        OldTreeSize = oldTreeSize;
        NewTreeSize = newTreeSize;
        Path = path;
    }


    /// <summary>The size of the earlier tree.</summary>
    public int OldTreeSize { get; }

    /// <summary>The size of the later tree.</summary>
    public int NewTreeSize { get; }

    /// <summary>The consistency proof hashes.</summary>
    public ImmutableArray<ReadOnlyMemory<byte>> Path { get; }


    /// <summary>
    /// Verifies this consistency proof by reconstructing the old and new roots from the proof per
    /// RFC 9162 §2.1.4.2 and comparing them byte-for-byte to <paramref name="oldRoot"/> and
    /// <paramref name="newRoot"/>.
    /// </summary>
    /// <param name="oldRoot">The root of the earlier tree.</param>
    /// <param name="newRoot">The root of the later tree.</param>
    /// <param name="computeDigest">The digest function applied to the domain-separated byte layout.</param>
    /// <returns><see langword="true"/> if both reconstructed roots match; <see langword="false"/> for any mismatch, inconsistent sizes, or structurally invalid path.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    public bool Verify(ReadOnlyMemory<byte> oldRoot, ReadOnlyMemory<byte> newRoot, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(computeDigest);

        if(Path.IsDefault || OldTreeSize < 0 || NewTreeSize < 0 || OldTreeSize > NewTreeSize)
        {
            return false;
        }

        if(OldTreeSize == 0)
        {
            return Path.Length == 0 && oldRoot.Span.SequenceEqual(computeDigest(ReadOnlyMemory<byte>.Empty).Span);
        }

        if(OldTreeSize == NewTreeSize)
        {
            return Path.Length == 0 && oldRoot.Span.SequenceEqual(newRoot.Span);
        }

        // RFC 9162 §2.1.4.2. When OldTreeSize is an exact power of two the old root is itself a node in the
        // new tree and is not transmitted; it is seeded into the proof so the two reconstructions share a
        // common starting hash.
        ReadOnlyMemory<byte>[] proof;
        if(IsPowerOfTwo(OldTreeSize))
        {
            proof = new ReadOnlyMemory<byte>[Path.Length + 1];
            proof[0] = oldRoot;
            for(int i = 0; i < Path.Length; i++)
            {
                proof[i + 1] = Path[i];
            }
        }
        else
        {
            proof = new ReadOnlyMemory<byte>[Path.Length];
            for(int i = 0; i < Path.Length; i++)
            {
                proof[i] = Path[i];
            }
        }

        int node = OldTreeSize - 1;
        int lastNode = NewTreeSize - 1;
        while((node & 1) != 0)
        {
            node >>= 1;
            lastNode >>= 1;
        }

        if(proof.Length == 0)
        {
            return false;
        }

        ReadOnlyMemory<byte> oldHash = proof[0];
        ReadOnlyMemory<byte> newHash = proof[0];
        int position = 1;
        while(node > 0)
        {
            if(position >= proof.Length)
            {
                return false;
            }

            if((node & 1) != 0)
            {
                oldHash = MerkleLogTree.InteriorHash(proof[position], oldHash, computeDigest);
                newHash = MerkleLogTree.InteriorHash(proof[position], newHash, computeDigest);
                position++;
            }
            else if(node < lastNode)
            {
                newHash = MerkleLogTree.InteriorHash(newHash, proof[position], computeDigest);
                position++;
            }

            node >>= 1;
            lastNode >>= 1;
        }

        while(lastNode > 0)
        {
            if(position >= proof.Length)
            {
                return false;
            }

            newHash = MerkleLogTree.InteriorHash(newHash, proof[position], computeDigest);
            position++;
            lastNode >>= 1;
        }

        if(position != proof.Length)
        {
            return false;
        }

        return oldHash.Span.SequenceEqual(oldRoot.Span) && newHash.Span.SequenceEqual(newRoot.Span);
    }


    /// <summary>
    /// Returns whether <paramref name="value"/> is a positive power of two.
    /// </summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> if <paramref name="value"/> is a positive power of two.</returns>
    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }


    private string DebuggerDisplay => $"MerkleConsistencyProof: {OldTreeSize} to {NewTreeSize}, {(Path.IsDefault ? 0 : Path.Length)} steps";
}
