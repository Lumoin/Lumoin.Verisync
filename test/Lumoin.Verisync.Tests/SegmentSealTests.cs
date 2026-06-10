using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class SegmentSealTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ImmutableArray<string> Proof { get; } = ["controller"];


    [TestMethod]
    public void CanonicalLayoutOfAFirstSealIsPinned()
    {
        //The byte layout is the versioned cross-stack contract; this test pins it exactly.
        SegmentSeal<string> seal = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0xAA, 0xBB }, [], Sha256);

        byte[] expected =
        [
            0x01,                                           //Version.
            0, 0, 0, 0, 0, 0, 0, 0,                         //FirstIndex 0, big-endian.
            0, 0, 0, 0, 0, 0, 0, 2,                         //LastIndex 2, big-endian.
            0, 0, 0, 0,                                     //Previous digest length 0: first seal.
            0, 0, 0, 2,                                     //Commitment length 2.
            0xAA, 0xBB                                      //Commitment.
        ];
        CollectionAssert.AreEqual(expected, seal.CanonicalBytes.ToArray());
        CollectionAssert.AreEqual(SHA256.HashData(seal.CanonicalBytes.Span), seal.Digest.ToArray());
    }


    [TestMethod]
    public void CanonicalLayoutWithAPreviousDigestIsPinned()
    {
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 0, null, new byte[] { 0x11 }, [], Sha256);

        SegmentSeal<string> second = SegmentSeal<string>.Create(1, 3, first.Digest, new byte[] { 0x22 }, [], Sha256);

        byte[] canonical = second.CanonicalBytes.ToArray();
        Assert.AreEqual(0x01, canonical[0]);
        Assert.AreEqual(1UL, ReadUInt64(canonical, 1));
        Assert.AreEqual(3UL, ReadUInt64(canonical, 9));
        Assert.AreEqual(32, ReadInt32(canonical, 17));
        CollectionAssert.AreEqual(first.Digest.ToArray(), canonical[21..53]);
        Assert.AreEqual(1, ReadInt32(canonical, 53));
        Assert.AreEqual(0x22, canonical[57]);
        Assert.HasCount(58, canonical);
    }


    [TestMethod]
    public void CreateRejectsInvalidArguments()
    {
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 0, null, new byte[] { 0x11 }, [], Sha256);

        Assert.ThrowsExactly<ArgumentNullException>(() => SegmentSeal<string>.Create(0, 0, null, new byte[] { 1 }, [], null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SegmentSeal<string>.Create(3, 2, first.Digest, new byte[] { 1 }, [], Sha256));
        Assert.ThrowsExactly<ArgumentException>(() => SegmentSeal<string>.Create(0, 0, null, ReadOnlyMemory<byte>.Empty, [], Sha256));
        Assert.ThrowsExactly<ArgumentException>(() => SegmentSeal<string>.Create(1, 2, ReadOnlyMemory<byte>.Empty, new byte[] { 1 }, [], Sha256));
        Assert.ThrowsExactly<ArgumentException>(() => SegmentSeal<string>.Create(1, 2, null, new byte[] { 1 }, [], Sha256));
    }


    [TestMethod]
    public void WithProofsPreservesCanonicalBytesAndDigest()
    {
        //Proofs attest the digest and live outside the digested bytes, so attaching them after
        //attestation must not disturb what was attested.
        SegmentSeal<string> unattested = SegmentSeal<string>.Create(0, 4, null, new byte[] { 0x33 }, [], Sha256);

        SegmentSeal<string> attested = unattested.WithProofs(Proof);

        CollectionAssert.AreEqual(unattested.CanonicalBytes.ToArray(), attested.CanonicalBytes.ToArray());
        CollectionAssert.AreEqual(unattested.Digest.ToArray(), attested.Digest.ToArray());
        Assert.HasCount(1, attested.Proofs);
        Assert.AreEqual(unattested, attested);
    }


    [TestMethod]
    public void VerifyLinkAcceptsAnHonestChain()
    {
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, Proof, Sha256);
        SegmentSeal<string> second = SegmentSeal<string>.Create(3, 7, first.Digest, new byte[] { 0x02 }, Proof, Sha256);
        SegmentSeal<string> third = SegmentSeal<string>.Create(8, 8, second.Digest, new byte[] { 0x03 }, Proof, Sha256);

        Assert.IsNull(first.VerifyLink(null));
        Assert.IsNull(second.VerifyLink(first));
        Assert.IsNull(third.VerifyLink(second));
    }


    [TestMethod]
    public void VerifyLinkRejectsAForgedPreviousDigest()
    {
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, Proof, Sha256);
        byte[] forged = first.Digest.ToArray();
        forged[0] ^= 0x01;

        SegmentSeal<string> second = SegmentSeal<string>.Create(3, 7, forged, new byte[] { 0x02 }, Proof, Sha256);

        Assert.AreEqual("the previous seal digest does not match the preceding seal", second.VerifyLink(first));
    }


    [TestMethod]
    public void VerifyLinkRejectsAReplacedPredecessor()
    {
        //An attacker who rewrites a sealed segment produces a different seal digest; the successor's
        //link to the original seal exposes the substitution.
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, Proof, Sha256);
        SegmentSeal<string> second = SegmentSeal<string>.Create(3, 7, first.Digest, new byte[] { 0x02 }, Proof, Sha256);
        SegmentSeal<string> rewritten = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0xEE }, Proof, Sha256);

        Assert.AreEqual("the previous seal digest does not match the preceding seal", second.VerifyLink(rewritten));
    }


    [TestMethod]
    public void VerifyLinkRejectsAnIndexGap()
    {
        //A gap means entries vanished between segments: covered by neither seal, undetectable later.
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, Proof, Sha256);

        SegmentSeal<string> gapped = SegmentSeal<string>.Create(5, 7, first.Digest, new byte[] { 0x02 }, Proof, Sha256);

        Assert.AreEqual("the seal covers entries from 5 but the preceding seal ends at 2", gapped.VerifyLink(first));
    }


    [TestMethod]
    public void VerifyLinkRejectsMismatchedChainPositions()
    {
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, Proof, Sha256);
        SegmentSeal<string> second = SegmentSeal<string>.Create(3, 7, first.Digest, new byte[] { 0x02 }, Proof, Sha256);

        Assert.AreEqual("the seal claims a previous seal but none was supplied", second.VerifyLink(null));
        Assert.AreEqual("the seal claims to be first but a previous seal was supplied", first.VerifyLink(second));
    }


    [TestMethod]
    public void EqualityFollowsTheDigest()
    {
        SegmentSeal<string> a = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, Proof, Sha256);
        SegmentSeal<string> same = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x01 }, [], Sha256);
        SegmentSeal<string> different = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x02 }, Proof, Sha256);

        Assert.AreEqual(a, same);
        Assert.AreNotEqual(a, different);
    }


    [TestMethod]
    public async Task SealsTheAuthenticatedLogThroughTheFoldAccumulator()
    {
        //The end-to-end pattern: the register's accumulator IS a MerkleLogTree folding entry digests,
        //so sealing a segment is reading the accumulator root — no new machinery in the register.
        AuthenticatedRegister<string, string, string, string, MerkleLogTree> register =
            AuthenticatedRegister<string, string, string, string, MerkleLogTree>.Create(
                NewWriterContext(), Canonicalize, Sha256, MerkleLogTree.Empty);

        var entries = new List<LogEntry<string, string>>();
        foreach(string operation in (string[])["create", "edit", "close"])
        {
            (register, CommitResult<string, string> result) = await register.CommitAsync(operation, Proof, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(result.IsCommitted, result.Error);
            entries.Add(result.Entry!);
        }

        //Seal the segment: the commitment is the accumulator's root, attested over the seal digest.
        MerkleLogTree segmentTree = register.Accumulator;
        Assert.AreEqual(3, segmentTree.Count);
        AttestSealDelegate<string> attest = (digest, _) =>
            ValueTask.FromResult<ImmutableArray<string>>([Sign(digest)]);
        SegmentSeal<string> seal = SegmentSeal<string>.Create(0, 2, null, segmentTree.ComputeRoot(Sha256), [], Sha256);
        seal = seal.WithProofs(await attest(seal.Digest, TestContext.CancellationToken).ConfigureAwait(false));

        Assert.IsNull(seal.VerifyLink(null));

        //The attestation verifies against the seal digest through the verification seam.
        VerifySealAttestationDelegate<string, string> verifyAttestation = (candidate, _, _) =>
            ValueTask.FromResult<string?>(candidate.Proofs.Contains(Sign(candidate.Digest)) ? null : "unattested");
        Assert.IsNull(await verifyAttestation(seal, "trust-anchors", TestContext.CancellationToken).ConfigureAwait(false));

        //Entry membership in the sealed segment is provable without the log: an inclusion proof for the
        //entry digest verifies against the seal's commitment.
        MerkleInclusionProof inclusion = segmentTree.ProveInclusion(1, Sha256);
        Assert.IsTrue(inclusion.Verify(entries[1].Digest, seal.Commitment, Sha256));
        Assert.IsFalse(inclusion.Verify(entries[2].Digest, seal.Commitment, Sha256));

        //A second segment continues the chain: a fresh tree for its entries, linked by seal digest.
        (register, CommitResult<string, string> fourth) = await register.CommitAsync("reopen", Proof, TestContext.CancellationToken).ConfigureAwait(false);
        MerkleLogTree secondTree = MerkleLogTree.Empty.Append(fourth.Entry!.Digest);
        SegmentSeal<string> secondSeal = SegmentSeal<string>.Create(3, 3, seal.Digest, secondTree.ComputeRoot(Sha256), [], Sha256);

        Assert.IsNull(secondSeal.VerifyLink(seal));
    }


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);


    private static string Sign(ReadOnlyMemory<byte> digest) => "sig:" + Convert.ToHexStringLower(digest.Span);


    private static ReadOnlyMemory<byte> Canonicalize(ulong index, ReadOnlyMemory<byte>? previousDigest, string? operation, ImmutableArray<string> proofs)
    {
        string previous = previousDigest is null ? "genesis" : Convert.ToHexStringLower(previousDigest.Value.Span);

        return Encoding.UTF8.GetBytes($"{index}|{previous}|{operation ?? "<heartbeat>"}|{string.Join(",", proofs)}");
    }


    private static LogCommitContext<string, string, string, string, MerkleLogTree> NewWriterContext()
    {
        return new LogCommitContext<string, string, string, string, MerkleLogTree>
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = (entry, previousEntryDigest, _) =>
                ValueTask.FromResult<string?>(NullableEqual(entry.PreviousDigest, previousEntryDigest) ? null : "chain broken"),
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult(ApplyEntry(classification, state, entry)),
            FoldStep = (entry, tree, _) => ValueTask.FromResult(tree.Append(entry.Digest)),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };
    }


    private static (LogState<string> State, string? Error) ApplyEntry(LogEntryClassification classification, LogState<string> state, LogEntry<string, string> entry)
    {
        if(classification == LogEntryClassification.Genesis)
        {
            return (new ActiveLogState<string>(entry.Operation!), null);
        }

        if(state is ActiveLogState<string> active)
        {
            return (new ActiveLogState<string>(active.Value + ";" + entry.Operation), null);
        }

        return (state, "register is not active");
    }


    private static bool NullableEqual(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right)
    {
        if(left is null && right is null)
        {
            return true;
        }

        if(left is null || right is null)
        {
            return false;
        }

        return left.Value.Span.SequenceEqual(right.Value.Span);
    }


    private static ulong ReadUInt64(byte[] buffer, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset));


    private static int ReadInt32(byte[] buffer, int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset));
}
