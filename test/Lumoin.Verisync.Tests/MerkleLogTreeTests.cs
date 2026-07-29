using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Adversarial tests for <see cref="MerkleLogTree"/>, an RFC 9162 (CT v2) append-only Merkle log tree.
///
/// The truth source is an independent, naive recursive RFC 9162 reference (<see cref="Oracle"/>) derived
/// directly from the structural definitions in the specification and RFC 6962/9162: the Merkle Tree Hash
/// (MTH), the audit (inclusion) path PATH(m, D[n]), and the consistency proof PROOF(m, D[n]). The reference
/// is intentionally written from the generating definition - never from how an implementation might compute
/// things - so that a divergent reading of the RFC by either side is caught.
///
/// The hard-pinned known-answer vectors are the classic RFC 6962/CT reference corpus (the empty-tree root
/// and the well-known eight-leaf test tree), recomputed and confirmed against the canonical values; they are
/// the third opinion. The oracle-based sweeps are the primary coverage.
/// </summary>
[TestClass]
internal sealed class MerkleLogTreeTests
{
    private static ComputeDigestDelegate Sha256 { get; } = static bytes => SHA256.HashData(bytes.Span);


    [TestMethod]
    public void EmptyHasZeroCount()
    {
        Assert.AreEqual(0, MerkleLogTree.Empty.Count);
    }


    [TestMethod]
    public void EmptyRootIsSha256OfEmptyInput()
    {
        //RFC 6962/9162: MTH({}) = SHA-256() = e3b0c442...; the digest of the empty input.
        byte[] expected = SHA256.HashData([]);

        byte[] actual = MerkleLogTree.Empty.ComputeRoot(Sha256).ToArray();

        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public void EmptyRootMatchesPinnedHex()
    {
        //Known-answer vector. Source: RFC 6962 / RFC 9162, SHA-256("") = the empty Merkle tree root.
        byte[] expected = FromHex("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

        byte[] actual = MerkleLogTree.Empty.ComputeRoot(Sha256).ToArray();

        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public void SingleLeafRootIsLeafHash()
    {
        byte[] leaf = [0x61, 0x62, 0x63];
        MerkleLogTree tree = MerkleLogTree.Empty.Append(leaf);

        //MTH(D[1]) = H(0x00 || leaf), the leaf hash itself.
        byte[] expected = SHA256.HashData([0x00, 0x61, 0x62, 0x63]);

        byte[] actual = tree.ComputeRoot(Sha256).ToArray();

        Assert.AreSequenceEqual(expected, actual);
    }


    [TestMethod]
    public void AppendDoesNotMutateReceiver()
    {
        MerkleLogTree original = MerkleLogTree.Empty.Append(new byte[] { 1 }).Append(new byte[] { 2 });
        byte[] rootBefore = original.ComputeRoot(Sha256).ToArray();
        int countBefore = original.Count;

        _ = original.Append(new byte[] { 3 });

        Assert.AreEqual(countBefore, original.Count);
        Assert.AreSequenceEqual(rootBefore, original.ComputeRoot(Sha256).ToArray());
    }


    [TestMethod]
    public void AppendIncrementsCount()
    {
        MerkleLogTree tree = MerkleLogTree.Empty.Append(new byte[] { 1 }).Append(new byte[] { 2 }).Append(new byte[] { 3 });

        Assert.AreEqual(3, tree.Count);
    }


    [TestMethod]
    public void AppendingSameBytesTwiceGivesCountTwoAndDifferentRoot()
    {
        byte[] leaf = [0x41, 0x42];
        MerkleLogTree one = MerkleLogTree.Empty.Append(leaf);
        MerkleLogTree two = one.Append(leaf);

        Assert.AreEqual(2, two.Count);

        //A two-leaf tree of identical leaves still differs from the single-leaf tree:
        //MTH(D[2]) = H(0x01 || leafHash || leafHash) != leafHash.
        Assert.AreNotSequenceEqual(one.ComputeRoot(Sha256).ToArray(), two.ComputeRoot(Sha256).ToArray());
    }


    [TestMethod]
    public void KnownAnswerEightLeafCorpusRoots()
    {
        //Known-answer vectors. Source: the canonical RFC 6962 / CT reference eight-leaf test corpus.
        //Each entry is the MTH of the first n leaves of the corpus.
        string[] expectedRoots =
        [
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", //n = 0 (empty)
            "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d", //n = 1
            "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125", //n = 2
            "aeb6bcfe274b70a14fb067a5e5578264db0fa9b51af5e0ba159158f329e06e77", //n = 3
            "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7", //n = 4
            "4e3bbb1f7b478dcfe71fb631631519a3bca12c9aefca1612bfce4c13a86264d4", //n = 5
            "76e67dadbcdf1e10e1b74ddc608abd2f98dfb16fbce75277b5232a127f2087ef", //n = 6
            "ddb89be403809e325750d3d263cd78929c2942b7942a34b77e122c9594a74c8c", //n = 7
            "5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328", //n = 8
        ];

        for(int n = 0; n <= 8; n++)
        {
            MerkleLogTree tree = Build(CorpusLeaves(n));
            byte[] expected = FromHex(expectedRoots[n]);

            Assert.AreSequenceEqual(expected, tree.ComputeRoot(Sha256).ToArray(), $"root for n = {n}");
        }
    }


    [TestMethod]
    public void KnownAnswerInclusionPathsForSevenLeafCorpus()
    {
        //Known-answer vectors. Source: RFC 6962 §2.1.3 worked examples, PATH(m, D[7]).
        string[][] expectedPaths =
        [
            //PATH(0, D[7])
            ["96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7", "5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],
            //PATH(1, D[7])
            ["6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d", "5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],
            //PATH(2, D[7])
            ["07506a85fd9dd2f120eb694f86011e5bb4662e5c415a62917033d4a9624487e7", "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],
            //PATH(3, D[7])
            ["0298d122906dcfc10892cb53a73992fc5b9f493ea4c9badb27b791b4127a7fe7", "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],
            //PATH(4, D[7])
            ["4271a26be0d8a84f0bd54c8c302e7cb3a3b5d1fa6780a40bcce2873477dab658", "b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f", "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7"],
            //PATH(5, D[7])
            ["bc1a0643b12e4d2d7c77918f44e0f4f79a838b6cf9ec5b5c283e1f4d88599e6b", "b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f", "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7"],
            //PATH(6, D[7])
            ["0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a", "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7"],
        ];

        MerkleLogTree tree = Build(CorpusLeaves(7));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();

        for(int leafIndex = 0; leafIndex < 7; leafIndex++)
        {
            MerkleInclusionProof proof = tree.ProveInclusion(leafIndex, Sha256);

            string[] actualHex = proof.Path.Select(static h => ToHex(h.Span)).ToArray();
            Assert.AreSequenceEqual(expectedPaths[leafIndex], actualHex, $"path for leaf {leafIndex}");

            Assert.IsTrue(proof.Verify(CorpusLeaves(7)[leafIndex], root, Sha256), $"verify for leaf {leafIndex}");
        }
    }


    [TestMethod]
    public void KnownAnswerConsistencyPathsForSevenLeafCorpus()
    {
        //Known-answer vectors. Source: RFC 6962 §2.1.4 worked examples, PROOF(m, D[7]).
        string[][] expectedProofs =
        [
            [],                                                                                                                                                                                                                                                                                                       //PROOF(0, D[7]) - empty path
            ["96a296d224f285c67bee93c30f8a309157f0daa35dc5b87e410b78630a09cfc7", "5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],                                                                                          //PROOF(1, D[7])
            ["5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],                                                                                                                                                                  //PROOF(2, D[7])
            ["0298d122906dcfc10892cb53a73992fc5b9f493ea4c9badb27b791b4127a7fe7", "07506a85fd9dd2f120eb694f86011e5bb4662e5c415a62917033d4a9624487e7", "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125", "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],                          //PROOF(3, D[7])
            ["837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e"],                                                                                                                                                                                                                                       //PROOF(4, D[7])
            ["bc1a0643b12e4d2d7c77918f44e0f4f79a838b6cf9ec5b5c283e1f4d88599e6b", "4271a26be0d8a84f0bd54c8c302e7cb3a3b5d1fa6780a40bcce2873477dab658", "b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f", "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7"],                          //PROOF(5, D[7])
            ["0ebc5d3437fbe2db158b9f126a1d118e308181031d0a949f8dededebc558ef6a", "b08693ec2e721597130641e8211e7eedccb4c26413963eee6c1e2ed16ffb1a5f", "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7"],                                                                                          //PROOF(6, D[7])
            [],                                                                                                                                                                                                                                                                                                       //PROOF(7, D[7]) - equal sizes, empty path
        ];

        MerkleLogTree tree = Build(CorpusLeaves(7));
        byte[] newRoot = tree.ComputeRoot(Sha256).ToArray();

        for(int oldSize = 0; oldSize <= 7; oldSize++)
        {
            MerkleConsistencyProof proof = tree.ProveConsistency(oldSize, Sha256);

            string[] actualHex = proof.Path.Select(static h => ToHex(h.Span)).ToArray();
            Assert.AreSequenceEqual(expectedProofs[oldSize], actualHex, $"consistency proof for oldSize = {oldSize}");

            byte[] oldRoot = Build(CorpusLeaves(oldSize)).ComputeRoot(Sha256).ToArray();
            Assert.IsTrue(proof.Verify(oldRoot, newRoot, Sha256), $"verify consistency for oldSize = {oldSize}");
        }
    }


    [TestMethod]
    public void ExhaustiveSmallTreeRootsMatchOracle()
    {
        for(int n = 0; n <= 10; n++)
        {
            List<byte[]> leaves = DistinctLeaves(n);
            MerkleLogTree tree = Build(leaves);

            byte[] expected = Oracle.Mth(leaves);

            Assert.AreSequenceEqual(expected, tree.ComputeRoot(Sha256).ToArray(), $"root for n = {n}");
        }
    }


    [TestMethod]
    public void ExhaustiveSmallTreeInclusionProofsMatchOracleAndVerify()
    {
        for(int n = 1; n <= 10; n++)
        {
            List<byte[]> leaves = DistinctLeaves(n);
            MerkleLogTree tree = Build(leaves);
            byte[] root = tree.ComputeRoot(Sha256).ToArray();

            for(int leafIndex = 0; leafIndex < n; leafIndex++)
            {
                MerkleInclusionProof proof = tree.ProveInclusion(leafIndex, Sha256);

                Assert.AreEqual(leafIndex, proof.LeafIndex, $"LeafIndex for n = {n}, i = {leafIndex}");
                Assert.AreEqual(n, proof.TreeSize, $"TreeSize for n = {n}, i = {leafIndex}");

                List<byte[]> expectedPath = Oracle.InclusionPath(leafIndex, leaves);
                AssertPathEqual(expectedPath, proof.Path, $"inclusion path for n = {n}, i = {leafIndex}");

                Assert.IsTrue(proof.Verify(leaves[leafIndex], root, Sha256), $"verify for n = {n}, i = {leafIndex}");
            }
        }
    }


    [TestMethod]
    public void ExhaustiveSmallTreeConsistencyProofsMatchOracleAndVerify()
    {
        for(int n = 0; n <= 10; n++)
        {
            List<byte[]> newLeaves = DistinctLeaves(n);
            MerkleLogTree newTree = Build(newLeaves);
            byte[] newRoot = newTree.ComputeRoot(Sha256).ToArray();

            for(int m = 0; m <= n; m++)
            {
                MerkleConsistencyProof proof = newTree.ProveConsistency(m, Sha256);

                Assert.AreEqual(m, proof.OldTreeSize, $"OldTreeSize for n = {n}, m = {m}");
                Assert.AreEqual(n, proof.NewTreeSize, $"NewTreeSize for n = {n}, m = {m}");

                List<byte[]> expectedPath = Oracle.ConsistencyPath(m, newLeaves);
                AssertPathEqual(expectedPath, proof.Path, $"consistency path for n = {n}, m = {m}");

                //The old root is the MTH over the first m leaves (a prefix of the same corpus).
                byte[] oldRoot = Build(DistinctLeaves(m)).ComputeRoot(Sha256).ToArray();
                Assert.IsTrue(proof.Verify(oldRoot, newRoot, Sha256), $"verify consistency for n = {n}, m = {m}");
            }
        }
    }


    [TestMethod]
    public void TamperedLeafFailsInclusion()
    {
        MerkleLogTree tree = Build(DistinctLeaves(6));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();
        MerkleInclusionProof proof = tree.ProveInclusion(3, Sha256);

        //Genuine leaf bytes verify; a flipped byte must not.
        Assert.IsTrue(proof.Verify(new byte[] { 3 }, root, Sha256));
        Assert.IsFalse(proof.Verify(new byte[] { 3, 0xFF }, root, Sha256));
        Assert.IsFalse(proof.Verify(new byte[] { 4 }, root, Sha256));
    }


    [TestMethod]
    public void WrongRootFailsInclusion()
    {
        MerkleLogTree tree = Build(DistinctLeaves(6));
        MerkleInclusionProof proof = tree.ProveInclusion(2, Sha256);

        byte[] wrongRoot = tree.ComputeRoot(Sha256).ToArray();
        wrongRoot[0] ^= 0xFF;

        Assert.IsFalse(proof.Verify(new byte[] { 2 }, wrongRoot, Sha256));
    }


    [TestMethod]
    public void InclusionProofForLeafIsRejectedForAnotherLeafsBytes()
    {
        MerkleLogTree tree = Build(DistinctLeaves(7));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();

        //The proof generated for leaf i must not verify leaf j's bytes (i != j).
        MerkleInclusionProof proofForI = tree.ProveInclusion(2, Sha256);

        Assert.IsTrue(proofForI.Verify(new byte[] { 2 }, root, Sha256));
        Assert.IsFalse(proofForI.Verify(new byte[] { 5 }, root, Sha256));
    }


    [TestMethod]
    public void TruncatedInclusionPathFails()
    {
        MerkleLogTree tree = Build(DistinctLeaves(7));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();
        MerkleInclusionProof original = tree.ProveInclusion(1, Sha256);

        ImmutableArray<ReadOnlyMemory<byte>> truncated = original.Path.RemoveAt(original.Path.Length - 1);
        MerkleInclusionProof tampered = new(original.LeafIndex, original.TreeSize, truncated);

        Assert.IsFalse(tampered.Verify(new byte[] { 1 }, root, Sha256));
    }


    [TestMethod]
    public void ExtendedInclusionPathFails()
    {
        MerkleLogTree tree = Build(DistinctLeaves(7));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();
        MerkleInclusionProof original = tree.ProveInclusion(1, Sha256);

        ReadOnlyMemory<byte> extra = SHA256.HashData([0xDE, 0xAD]);
        ImmutableArray<ReadOnlyMemory<byte>> extended = original.Path.Add(extra);
        MerkleInclusionProof tampered = new(original.LeafIndex, original.TreeSize, extended);

        Assert.IsFalse(tampered.Verify(new byte[] { 1 }, root, Sha256));
    }


    [TestMethod]
    public void ReorderedInclusionPathFails()
    {
        MerkleLogTree tree = Build(DistinctLeaves(7));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();
        MerkleInclusionProof original = tree.ProveInclusion(0, Sha256);

        //Leaf 0 in a 7-leaf tree has a three-element path; swapping the two ends breaks the chain.
        Assert.IsGreaterThanOrEqualTo(2, original.Path.Length);
        ReadOnlyMemory<byte>[] reordered = original.Path.ToArray();
        (reordered[0], reordered[^1]) = (reordered[^1], reordered[0]);
        MerkleInclusionProof tampered = new(original.LeafIndex, original.TreeSize, [.. reordered]);

        Assert.IsFalse(tampered.Verify(new byte[] { 0 }, root, Sha256));
    }


    [TestMethod]
    public void InclusionProofFromTreeAFailsAgainstTreeBRoot()
    {
        MerkleLogTree treeA = Build(DistinctLeaves(6));
        MerkleLogTree treeB = Build(DistinctLeaves(6).Select(static b => (byte[])[(byte)(b[0] + 100)]).ToList());

        MerkleInclusionProof proofA = treeA.ProveInclusion(2, Sha256);
        byte[] rootB = treeB.ComputeRoot(Sha256).ToArray();

        //A proof valid in tree A must not verify against tree B's root, even for the same leaf index.
        Assert.IsFalse(proofA.Verify(new byte[] { 2 }, rootB, Sha256));
    }


    [TestMethod]
    public void InclusionVerifyWithWrongLengthRootReturnsFalse()
    {
        MerkleLogTree tree = Build(DistinctLeaves(5));
        MerkleInclusionProof proof = tree.ProveInclusion(1, Sha256);

        byte[] shortRoot = [1, 2, 3];
        byte[] longRoot = new byte[64];

        //Hostile root lengths must be rejected by returning false, never by throwing.
        Assert.IsFalse(proof.Verify(new byte[] { 1 }, shortRoot, Sha256));
        Assert.IsFalse(proof.Verify(new byte[] { 1 }, longRoot, Sha256));
        Assert.IsFalse(proof.Verify(new byte[] { 1 }, ReadOnlyMemory<byte>.Empty, Sha256));
    }


    [TestMethod]
    public void ConsistencySwappedOldNewRootsFails()
    {
        MerkleLogTree newTree = Build(DistinctLeaves(7));
        byte[] newRoot = newTree.ComputeRoot(Sha256).ToArray();
        byte[] oldRoot = Build(DistinctLeaves(4)).ComputeRoot(Sha256).ToArray();
        MerkleConsistencyProof proof = newTree.ProveConsistency(4, Sha256);

        Assert.IsTrue(proof.Verify(oldRoot, newRoot, Sha256));

        //Swapping old and new must fail (the proof is directional).
        Assert.IsFalse(proof.Verify(newRoot, oldRoot, Sha256));
    }


    [TestMethod]
    public void ConsistencyFailsAgainstRootOfDifferentPrefix()
    {
        MerkleLogTree newTree = Build(DistinctLeaves(7));
        byte[] newRoot = newTree.ComputeRoot(Sha256).ToArray();
        MerkleConsistencyProof proof = newTree.ProveConsistency(4, Sha256);

        //The proof is for old size 4; feeding the root of a different prefix (size 3) must fail.
        byte[] wrongPrefixRoot = Build(DistinctLeaves(3)).ComputeRoot(Sha256).ToArray();

        Assert.IsFalse(proof.Verify(wrongPrefixRoot, newRoot, Sha256));
    }


    [TestMethod]
    public void ConsistencyFailsAgainstDifferentCorpusNewRoot()
    {
        //A consistency proof from m to n in corpus A must not verify against an unrelated tree's new root.
        MerkleLogTree treeA = Build(DistinctLeaves(7));
        MerkleConsistencyProof proof = treeA.ProveConsistency(4, Sha256);
        byte[] oldRoot = Build(DistinctLeaves(4)).ComputeRoot(Sha256).ToArray();

        List<byte[]> otherLeaves = DistinctLeaves(7).Select(static b => (byte[])[(byte)(b[0] + 50)]).ToList();
        byte[] otherNewRoot = Build(otherLeaves).ComputeRoot(Sha256).ToArray();

        Assert.IsFalse(proof.Verify(oldRoot, otherNewRoot, Sha256));
    }


    [TestMethod]
    public void ConsistencyGarbagePathOfPlausibleLengthFails()
    {
        MerkleLogTree newTree = Build(DistinctLeaves(7));
        byte[] newRoot = newTree.ComputeRoot(Sha256).ToArray();
        byte[] oldRoot = Build(DistinctLeaves(4)).ComputeRoot(Sha256).ToArray();
        MerkleConsistencyProof genuine = newTree.ProveConsistency(4, Sha256);

        //Same length as the genuine proof, but every node is a garbage 32-byte hash.
        ReadOnlyMemory<byte>[] garbage = new ReadOnlyMemory<byte>[genuine.Path.Length];
        for(int i = 0; i < garbage.Length; i++)
        {
            garbage[i] = SHA256.HashData([(byte)i, 0xBA, 0xAD]);
        }

        MerkleConsistencyProof forged = new(genuine.OldTreeSize, genuine.NewTreeSize, [.. garbage]);

        Assert.IsFalse(forged.Verify(oldRoot, newRoot, Sha256));
    }


    [TestMethod]
    public void ConsistencyVerifyWithWrongLengthRootReturnsFalse()
    {
        MerkleLogTree newTree = Build(DistinctLeaves(7));
        byte[] newRoot = newTree.ComputeRoot(Sha256).ToArray();
        byte[] oldRoot = Build(DistinctLeaves(4)).ComputeRoot(Sha256).ToArray();
        MerkleConsistencyProof proof = newTree.ProveConsistency(4, Sha256);

        byte[] shortHash = [9, 9, 9];

        Assert.IsFalse(proof.Verify(shortHash, newRoot, Sha256));
        Assert.IsFalse(proof.Verify(oldRoot, shortHash, Sha256));
    }


    [TestMethod]
    public void ConsistencyEqualSizesEmptyPathTrueOnlyForEqualRoots()
    {
        MerkleLogTree tree = Build(DistinctLeaves(5));
        byte[] root = tree.ComputeRoot(Sha256).ToArray();

        MerkleConsistencyProof proof = tree.ProveConsistency(5, Sha256);

        Assert.IsTrue(proof.Path.IsEmpty, "equal-size consistency proof has an empty path");
        Assert.IsTrue(proof.Verify(root, root, Sha256));

        byte[] otherRoot = root.ToArray();
        otherRoot[0] ^= 0xFF;
        Assert.IsFalse(proof.Verify(otherRoot, root, Sha256));
    }


    [TestMethod]
    public void ConsistencyFromZeroEmptyPathTrueOnlyForEmptyTreeRoot()
    {
        MerkleLogTree tree = Build(DistinctLeaves(5));
        byte[] newRoot = tree.ComputeRoot(Sha256).ToArray();

        MerkleConsistencyProof proof = tree.ProveConsistency(0, Sha256);

        Assert.IsTrue(proof.Path.IsEmpty, "consistency from size zero has an empty path");

        byte[] emptyRoot = SHA256.HashData([]);
        Assert.IsTrue(proof.Verify(emptyRoot, newRoot, Sha256));

        //Any non-empty-tree old root must fail.
        Assert.IsFalse(proof.Verify(newRoot, newRoot, Sha256));
    }


    [TestMethod]
    public void ComputeRootRejectsNullDelegate()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MerkleLogTree.Empty.Append(new byte[] { 1 }).ComputeRoot(null!));
    }


    [TestMethod]
    public void ProveInclusionRejectsNullDelegate()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MerkleLogTree.Empty.Append(new byte[] { 1 }).ProveInclusion(0, null!));
    }


    [TestMethod]
    public void ProveConsistencyRejectsNullDelegate()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MerkleLogTree.Empty.Append(new byte[] { 1 }).ProveConsistency(0, null!));
    }


    [TestMethod]
    public void InclusionVerifyRejectsNullDelegate()
    {
        MerkleLogTree tree = Build(DistinctLeaves(3));
        MerkleInclusionProof proof = tree.ProveInclusion(0, Sha256);
        byte[] root = tree.ComputeRoot(Sha256).ToArray();

        Assert.ThrowsExactly<ArgumentNullException>(() => proof.Verify(new byte[] { 0 }, root, null!));
    }


    [TestMethod]
    public void ConsistencyVerifyRejectsNullDelegate()
    {
        MerkleLogTree tree = Build(DistinctLeaves(3));
        MerkleConsistencyProof proof = tree.ProveConsistency(2, Sha256);
        byte[] oldRoot = Build(DistinctLeaves(2)).ComputeRoot(Sha256).ToArray();

        Assert.ThrowsExactly<ArgumentNullException>(() => proof.Verify(oldRoot, tree.ComputeRoot(Sha256), null!));
    }


    [TestMethod]
    public void ProveInclusionRejectsOutOfRangeLeafIndex()
    {
        MerkleLogTree tree = Build(DistinctLeaves(4));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tree.ProveInclusion(-1, Sha256));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tree.ProveInclusion(4, Sha256));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MerkleLogTree.Empty.ProveInclusion(0, Sha256));
    }


    [TestMethod]
    public void ProveConsistencyRejectsOutOfRangeOldTreeSize()
    {
        MerkleLogTree tree = Build(DistinctLeaves(4));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tree.ProveConsistency(-1, Sha256));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => tree.ProveConsistency(5, Sha256));
    }


    [TestMethod]
    public void InclusionProofConstructorRejectsInvalidArguments()
    {
        ImmutableArray<ReadOnlyMemory<byte>> emptyPath = [];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MerkleInclusionProof(-1, 4, emptyPath));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MerkleInclusionProof(0, -1, emptyPath));
        //leafIndex must be strictly less than treeSize.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MerkleInclusionProof(4, 4, emptyPath));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MerkleInclusionProof(5, 4, emptyPath));
    }


    [TestMethod]
    public void ConsistencyProofConstructorRejectsNegativeSizes()
    {
        ImmutableArray<ReadOnlyMemory<byte>> emptyPath = [];

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MerkleConsistencyProof(-1, 4, emptyPath));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MerkleConsistencyProof(0, -1, emptyPath));
    }


    //--- Test helpers -----------------------------------------------------------------------------

    private static MerkleLogTree Build(IEnumerable<byte[]> leaves)
    {
        MerkleLogTree tree = MerkleLogTree.Empty;
        foreach(byte[] leaf in leaves)
        {
            tree = tree.Append(leaf);
        }

        return tree;
    }


    private static List<byte[]> DistinctLeaves(int count)
    {
        var leaves = new List<byte[]>(count);
        for(int i = 0; i < count; i++)
        {
            leaves.Add([(byte)i]);
        }

        return leaves;
    }


    private static List<byte[]> CorpusLeaves(int count)
    {
        //The canonical RFC 6962 / CT reference eight-leaf test corpus, truncated to the first `count` leaves.
        byte[][] corpus =
        [
            [],
            [0x00],
            [0x10],
            [0x20, 0x21],
            [0x30, 0x31],
            [0x40, 0x41, 0x42, 0x43],
            [0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57],
            [0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f],
        ];

        return corpus.Take(count).ToList();
    }


    private static void AssertPathEqual(List<byte[]> expected, ImmutableArray<ReadOnlyMemory<byte>> actual, string because)
    {
        Assert.HasCount(expected.Count, actual, $"path length mismatch: {because}");
        for(int i = 0; i < expected.Count; i++)
        {
            Assert.AreSequenceEqual(expected[i], actual[i].ToArray(), $"path element {i}: {because}");
        }
    }


    private static byte[] FromHex(string hex)
    {
        return Convert.FromHexString(hex);
    }


    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }


    /// <summary>
    /// Independent, naive recursive RFC 9162 / RFC 6962 reference. It computes the Merkle Tree Hash, the
    /// inclusion (audit) path, and the consistency proof directly from the generating definitions. This is
    /// the test truth source and is deliberately not written to mirror any particular implementation strategy.
    /// </summary>
    private static class Oracle
    {
        private static byte[] Sha256(ReadOnlySpan<byte> bytes)
        {
            return SHA256.HashData(bytes);
        }


        private static byte[] LeafHash(byte[] leaf)
        {
            //H(0x00 || leaf).
            byte[] buffer = new byte[1 + leaf.Length];
            buffer[0] = 0x00;
            leaf.CopyTo(buffer, 1);

            return Sha256(buffer);
        }


        private static byte[] NodeHash(byte[] left, byte[] right)
        {
            //H(0x01 || left || right).
            byte[] buffer = new byte[1 + left.Length + right.Length];
            buffer[0] = 0x01;
            left.CopyTo(buffer, 1);
            right.CopyTo(buffer, 1 + left.Length);

            return Sha256(buffer);
        }


        private static int LargestPowerOfTwoStrictlyLessThan(int n)
        {
            //k: the largest power of two strictly less than n (n > 1).
            int k = 1;
            while(k * 2 < n)
            {
                k *= 2;
            }

            return k;
        }


        public static byte[] Mth(List<byte[]> leaves)
        {
            int n = leaves.Count;
            if(n == 0)
            {
                return Sha256([]);
            }

            if(n == 1)
            {
                return LeafHash(leaves[0]);
            }

            int k = LargestPowerOfTwoStrictlyLessThan(n);
            byte[] left = Mth(Slice(leaves, 0, k));
            byte[] right = Mth(Slice(leaves, k, n));

            return NodeHash(left, right);
        }


        public static List<byte[]> InclusionPath(int m, List<byte[]> leaves)
        {
            //RFC 6962 §2.1.3 PATH(m, D[n]), bottom-up audit path.
            int n = leaves.Count;
            if(n == 1)
            {
                return [];
            }

            int k = LargestPowerOfTwoStrictlyLessThan(n);
            if(m < k)
            {
                List<byte[]> path = InclusionPath(m, Slice(leaves, 0, k));
                path.Add(Mth(Slice(leaves, k, n)));

                return path;
            }
            else
            {
                List<byte[]> path = InclusionPath(m - k, Slice(leaves, k, n));
                path.Add(Mth(Slice(leaves, 0, k)));

                return path;
            }
        }


        public static List<byte[]> ConsistencyPath(int m, List<byte[]> leaves)
        {
            //RFC 6962 §2.1.4 PROOF(m, D[n]). The documented edge cases (m == 0 and m == n) yield an empty path.
            int n = leaves.Count;
            if(m == 0 || m == n)
            {
                return [];
            }

            return SubProof(m, leaves, true);
        }


        private static List<byte[]> SubProof(int m, List<byte[]> leaves, bool b)
        {
            //RFC 6962 §2.1.4 SUBPROOF(m, D[n], b), 0 < m <= n.
            int n = leaves.Count;
            if(m == n)
            {
                //When the prefix is the whole subtree: omit its root if it is on the original path (b),
                //otherwise include MTH(D[n]).
                return b ? [] : [Mth(leaves)];
            }

            int k = LargestPowerOfTwoStrictlyLessThan(n);
            if(m <= k)
            {
                List<byte[]> proof = SubProof(m, Slice(leaves, 0, k), b);
                proof.Add(Mth(Slice(leaves, k, n)));

                return proof;
            }
            else
            {
                List<byte[]> proof = SubProof(m - k, Slice(leaves, k, n), false);
                proof.Add(Mth(Slice(leaves, 0, k)));

                return proof;
            }
        }


        private static List<byte[]> Slice(List<byte[]> leaves, int start, int end)
        {
            var slice = new List<byte[]>(end - start);
            for(int i = start; i < end; i++)
            {
                slice.Add(leaves[i]);
            }

            return slice;
        }
    }
}
