using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Verifies that one <see cref="LogHead"/> extends another — the anti-equivocation check of the
/// sealed-segments design, kept in-library so the subtle parts are never a caller obligation.
/// </summary>
/// <remarks>
/// <para>
/// The check binds the supplied <see cref="MerkleConsistencyProof"/> to the two heads before trusting
/// it: the proof's recorded sizes must equal the heads' sizes, closing the proof-substitution mistake
/// where a proof relating two <em>other</em> trees verifies and is misread as relating these heads. A
/// verification failure between two honestly transmitted heads is evidence of a fork: the peer's
/// history does not extend the observed prefix. To make that evidence portable to third parties, pair
/// each head with its attestation — in the single-tree composition, the
/// <see cref="SegmentSeal{TProof}"/> whose commitment is that head's root.
/// </para>
/// <para>
/// When the two heads have equal sizes no prover is needed: the verifier supplies the trivial empty
/// proof, <c>new MerkleConsistencyProof(n, n, ImmutableArray&lt;ReadOnlyMemory&lt;byte&gt;&gt;.Empty)</c>,
/// and the check reduces to root equality.
/// </para>
/// </remarks>
public static class LogHeadConsistency
{
    /// <summary>
    /// Verifies that <paramref name="newer"/> extends <paramref name="older"/> through
    /// <paramref name="proof"/>.
    /// </summary>
    /// <param name="older">The smaller (or equal) head — typically the verifier's own.</param>
    /// <param name="newer">The larger head — typically the peer's claim.</param>
    /// <param name="proof">The consistency proof relating exactly these two heads.</param>
    /// <param name="computeDigest">The digest function the trees were built with.</param>
    /// <returns><see langword="null"/> when the newer head extends the older one, or an error message; a mismatch between honestly transmitted heads is fork evidence.</returns>
    /// <exception cref="ArgumentNullException">Thrown if any argument is <see langword="null"/>.</exception>
    public static string? Verify(LogHead older, LogHead newer, MerkleConsistencyProof proof, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(computeDigest);

        if(older.TreeSize > newer.TreeSize)
        {
            return $"the older head claims {older.TreeSize} leaves, more than the newer head's {newer.TreeSize}";
        }

        if(proof.OldTreeSize != older.TreeSize || proof.NewTreeSize != newer.TreeSize)
        {
            return $"the proof relates sizes {proof.OldTreeSize} and {proof.NewTreeSize}, not the heads' {older.TreeSize} and {newer.TreeSize}";
        }

        if(!proof.Verify(older.Root, newer.Root, computeDigest))
        {
            return "the newer head does not extend the older head; if both heads are authentic this is evidence of a fork";
        }

        return null;
    }
}
