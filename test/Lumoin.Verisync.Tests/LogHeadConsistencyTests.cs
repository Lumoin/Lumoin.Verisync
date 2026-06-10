using System;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LogHeadConsistencyTests
{
    [TestMethod]
    public void HeadRejectsInvalidArguments()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new LogHead(-1, new byte[] { 1 }));
        Assert.ThrowsExactly<ArgumentException>(() => new LogHead(0, ReadOnlyMemory<byte>.Empty));
    }


    [TestMethod]
    public void HeadEqualityFollowsSizeAndRoot()
    {
        Assert.AreEqual(new LogHead(3, new byte[] { 1, 2 }), new LogHead(3, new byte[] { 1, 2 }));
        Assert.AreNotEqual(new LogHead(3, new byte[] { 1, 2 }), new LogHead(4, new byte[] { 1, 2 }));
        Assert.AreNotEqual(new LogHead(3, new byte[] { 1, 2 }), new LogHead(3, new byte[] { 1, 3 }));
    }


    [TestMethod]
    public void AGrowingLogVerifiesAgainstItsEarlierHead()
    {
        MerkleLogTree atThree = Grow(MerkleLogTree.Empty, 0, 3);
        MerkleLogTree atFive = Grow(atThree, 3, 5);
        LogHead older = new(3, atThree.ComputeRoot(Sha256));
        LogHead newer = new(5, atFive.ComputeRoot(Sha256));

        MerkleConsistencyProof proof = atFive.ProveConsistency(3, Sha256);

        Assert.IsNull(LogHeadConsistency.Verify(older, newer, proof, Sha256));
    }


    [TestMethod]
    public void EveryHeadExtendsTheEmptyHead()
    {
        MerkleLogTree tree = Grow(MerkleLogTree.Empty, 0, 4);
        LogHead empty = new(0, MerkleLogTree.Empty.ComputeRoot(Sha256));
        LogHead grown = new(4, tree.ComputeRoot(Sha256));

        Assert.IsNull(LogHeadConsistency.Verify(empty, grown, tree.ProveConsistency(0, Sha256), Sha256));
    }


    [TestMethod]
    public void EqualSizeHeadsCompareByRootThroughTheTrivialProof()
    {
        //When sizes match no prover is needed: the verifier supplies the trivial empty proof itself.
        MerkleLogTree tree = Grow(MerkleLogTree.Empty, 0, 3);
        LogHead mine = new(3, tree.ComputeRoot(Sha256));
        LogHead same = new(3, tree.ComputeRoot(Sha256));
        LogHead other = new(3, Grow(MerkleLogTree.Empty, 10, 13).ComputeRoot(Sha256));
        MerkleConsistencyProof trivial = new(3, 3, ImmutableArray<ReadOnlyMemory<byte>>.Empty);

        Assert.IsNull(LogHeadConsistency.Verify(mine, same, trivial, Sha256));
        Assert.IsNotNull(LogHeadConsistency.Verify(mine, other, trivial, Sha256));
    }


    [TestMethod]
    public void AProofRelatingOtherSizesIsRejectedBeforeVerification()
    {
        //The proof-substitution mistake: a proof that genuinely verifies for two other tree sizes must
        //not be accepted as relating these heads.
        MerkleLogTree atTwo = Grow(MerkleLogTree.Empty, 0, 2);
        MerkleLogTree atFive = Grow(atTwo, 2, 5);
        LogHead older = new(3, Grow(MerkleLogTree.Empty, 0, 3).ComputeRoot(Sha256));
        LogHead newer = new(5, atFive.ComputeRoot(Sha256));

        MerkleConsistencyProof unrelated = atFive.ProveConsistency(2, Sha256);

        string? error = LogHeadConsistency.Verify(older, newer, unrelated, Sha256);
        Assert.AreEqual("the proof relates sizes 2 and 5, not the heads' 3 and 5", error);
    }


    [TestMethod]
    public void AnOlderHeadLargerThanTheNewerIsRejected()
    {
        MerkleLogTree tree = Grow(MerkleLogTree.Empty, 0, 5);
        LogHead larger = new(5, tree.ComputeRoot(Sha256));
        LogHead smaller = new(3, Grow(MerkleLogTree.Empty, 0, 3).ComputeRoot(Sha256));
        MerkleConsistencyProof proof = tree.ProveConsistency(3, Sha256);

        string? error = LogHeadConsistency.Verify(larger, smaller, proof, Sha256);
        Assert.AreEqual("the older head claims 5 leaves, more than the newer head's 3", error);
    }


    [TestMethod]
    public void AForkedHistoryIsDetected()
    {
        //Two histories share two entries and then diverge. The forker's own consistency proof relates
        //its private history's prefix, not the prefix the verifier holds, so verification fails — and
        //with attested heads that failure is portable fork evidence.
        MerkleLogTree shared = Grow(MerkleLogTree.Empty, 0, 2);
        MerkleLogTree mine = Grow(shared, 2, 3);
        MerkleLogTree theirs = Grow(shared, 100, 103);
        LogHead myHead = new(3, mine.ComputeRoot(Sha256));
        LogHead theirHead = new(5, theirs.ComputeRoot(Sha256));

        MerkleConsistencyProof theirProof = theirs.ProveConsistency(3, Sha256);

        string? error = LogHeadConsistency.Verify(myHead, theirHead, theirProof, Sha256);
        Assert.AreEqual("the newer head does not extend the older head; if both heads are authentic this is evidence of a fork", error);
    }


    [TestMethod]
    public void SealsActAsAttestedHeadsInTheSingleTreeComposition()
    {
        //The STH-style flow: one ever-growing tree, each seal's commitment is the root at the seal's
        //boundary, so consecutive seals are attested heads whose consistency the tree itself proves.
        MerkleLogTree tree = Grow(MerkleLogTree.Empty, 0, 3);
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, tree.ComputeRoot(Sha256), [], Sha256);

        tree = Grow(tree, 3, 5);
        SegmentSeal<string> second = SegmentSeal<string>.Create(3, 4, first.Digest, tree.ComputeRoot(Sha256), [], Sha256);

        Assert.IsNull(second.VerifyLink(first));
        LogHead olderHead = new((int)(first.LastIndex + 1), first.Commitment);
        LogHead newerHead = new((int)(second.LastIndex + 1), second.Commitment);

        Assert.IsNull(LogHeadConsistency.Verify(olderHead, newerHead, tree.ProveConsistency(3, Sha256), Sha256));
    }


    [TestMethod]
    public void VerifyRejectsNullArguments()
    {
        LogHead head = new(0, new byte[] { 1 });
        MerkleConsistencyProof proof = new(0, 0, ImmutableArray<ReadOnlyMemory<byte>>.Empty);

        Assert.ThrowsExactly<ArgumentNullException>(() => LogHeadConsistency.Verify(null!, head, proof, Sha256));
        Assert.ThrowsExactly<ArgumentNullException>(() => LogHeadConsistency.Verify(head, null!, proof, Sha256));
        Assert.ThrowsExactly<ArgumentNullException>(() => LogHeadConsistency.Verify(head, head, null!, Sha256));
        Assert.ThrowsExactly<ArgumentNullException>(() => LogHeadConsistency.Verify(head, head, proof, null!));
    }


    private static MerkleLogTree Grow(MerkleLogTree tree, int firstLabel, int pastLastLabel)
    {
        for(int i = firstLabel; i < pastLastLabel; i++)
        {
            tree = tree.Append(Encoding.UTF8.GetBytes($"entry-{i}"));
        }

        return tree;
    }


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);
}
