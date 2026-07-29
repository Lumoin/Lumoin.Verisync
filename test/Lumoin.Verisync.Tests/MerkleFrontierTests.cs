using Lumoin.Verisync.Core;
using System.Security.Cryptography;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Tests for <see cref="MerkleFrontier"/>, the bounded-state companion to <see cref="MerkleLogTree"/>.
///
/// The truth source here is <see cref="MerkleLogTree"/> itself: the frontier holds only the O(log n) peaks,
/// yet must produce roots byte-identical to the full tree at every size. The central correctness story is
/// therefore an equivalence sweep — for sizes 0 through 64, append the same leaves to both a
/// <see cref="MerkleLogTree"/> and a <see cref="MerkleFrontier"/> and assert the roots match at every step.
/// The tree (independently covered against an RFC 9162 oracle) is the oracle here.
/// </summary>
[TestClass]
internal sealed class MerkleFrontierTests
{
    private static ComputeDigestDelegate Sha256 { get; } = static bytes => SHA256.HashData(bytes.Span);


    [TestMethod]
    public void EmptyHasZeroCount()
    {
        Assert.AreEqual(0, MerkleFrontier.Empty.Count);
    }


    [TestMethod]
    public void EmptyRootIsSha256OfEmptyInput()
    {
        //An empty frontier commits to the digest of empty input, exactly like the empty tree.
        byte[] expected = SHA256.HashData([]);

        byte[] actual = MerkleFrontier.Empty.ComputeRoot(Sha256).ToArray();

        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public void SingleLeafRootIsLeafHash()
    {
        byte[] leaf = [0x61, 0x62, 0x63];
        MerkleFrontier frontier = MerkleFrontier.Empty.Append(leaf, Sha256);

        //MTH(D[1]) = H(0x00 || leaf), the leaf hash itself, with no interior node.
        byte[] expected = SHA256.HashData([0x00, 0x61, 0x62, 0x63]);

        byte[] actual = frontier.ComputeRoot(Sha256).ToArray();

        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public void RootsMatchTreeForSizesZeroThroughSixtyFour()
    {
        //THE core test. The full tree is the oracle: appending the same leaves to a frontier (which keeps
        //only the O(log n) peaks) must yield a byte-identical root at every single size, spanning powers of
        //two (perfect trees, one peak) and the ragged sizes between them (many peaks, bagged right-to-left).
        MerkleLogTree tree = MerkleLogTree.Empty;
        MerkleFrontier frontier = MerkleFrontier.Empty;

        //Size 0: both are empty before any append.
        Assert.AreSequenceEqual(tree.ComputeRoot(Sha256).ToArray(), frontier.ComputeRoot(Sha256).ToArray(), "root for n = 0");

        for(int n = 1; n <= 64; n++)
        {
            byte[] leaf = LeafBytes(n - 1);
            tree = tree.Append(leaf);
            frontier = frontier.Append(leaf, Sha256);

            Assert.AreEqual(n, frontier.Count, $"count for n = {n}");

            byte[] treeRoot = tree.ComputeRoot(Sha256).ToArray();
            byte[] frontierRoot = frontier.ComputeRoot(Sha256).ToArray();

            Assert.AreSequenceEqual(treeRoot, frontierRoot, $"root for n = {n}");
        }
    }


    [TestMethod]
    public void AppendIncrementsCount()
    {
        MerkleFrontier frontier = MerkleFrontier.Empty
            .Append(new byte[] { 1 }, Sha256)
            .Append(new byte[] { 2 }, Sha256)
            .Append(new byte[] { 3 }, Sha256);

        Assert.AreEqual(3, frontier.Count);
    }


    [TestMethod]
    public void AppendDoesNotMutateReceiver()
    {
        MerkleFrontier original = MerkleFrontier.Empty.Append(new byte[] { 1 }, Sha256).Append(new byte[] { 2 }, Sha256);
        byte[] rootBefore = original.ComputeRoot(Sha256).ToArray();
        int countBefore = original.Count;

        _ = original.Append(new byte[] { 3 }, Sha256);

        Assert.AreEqual(countBefore, original.Count);
        Assert.AreSequenceEqual(rootBefore, original.ComputeRoot(Sha256).ToArray());
    }


    [TestMethod]
    public void DistinctAppendsProduceDistinctFrontiersFromSharedPrefix()
    {
        //A shared two-leaf prefix that is then extended two different ways must keep both extensions intact
        //and distinct, confirming Append never reaches back into the shared peaks of the receiver.
        MerkleFrontier shared = MerkleFrontier.Empty.Append(new byte[] { 1 }, Sha256).Append(new byte[] { 2 }, Sha256);

        MerkleFrontier withThree = shared.Append(new byte[] { 3 }, Sha256);
        MerkleFrontier withFour = shared.Append(new byte[] { 4 }, Sha256);

        Assert.AreEqual(2, shared.Count);
        Assert.AreEqual(3, withThree.Count);
        Assert.AreEqual(3, withFour.Count);
        Assert.AreNotSequenceEqual(withThree.ComputeRoot(Sha256).ToArray(), withFour.ComputeRoot(Sha256).ToArray());
    }


    [TestMethod]
    public void AppendRejectsNullDelegate()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MerkleFrontier.Empty.Append(new byte[] { 1 }, null!));
    }


    [TestMethod]
    public void ComputeRootRejectsNullDelegate()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MerkleFrontier.Empty.Append(new byte[] { 1 }, Sha256).ComputeRoot(null!));
        //The empty frontier still validates the delegate before producing the empty-input digest.
        Assert.ThrowsExactly<ArgumentNullException>(() => MerkleFrontier.Empty.ComputeRoot(null!));
    }


    //--- Test helpers -----------------------------------------------------------------------------

    private static byte[] LeafBytes(int index)
    {
        //Distinct leaves so position is meaningful; two bytes keeps them distinct well past 255 entries.
        return [(byte)(index & 0xFF), (byte)((index >> 8) & 0xFF)];
    }
}
