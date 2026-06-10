using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips and hostile-input checks for the log-plane commitment JSON codecs. The seal codec is a
/// verifier as well as a parser: a tampered seal must fail closed because the digest re-derived from the
/// typed fields no longer matches the transmitted one.
/// </summary>
[TestClass]
internal sealed class LogCommitmentJsonTests
{
    [TestMethod]
    public void LogHeadRoundTrips()
    {
        var head = new LogHead(7, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        LogHead back = RoundTripLogHead(head);

        Assert.AreEqual(head, back);
    }


    [TestMethod]
    public void InclusionProofRoundTripsAndStillVerifies()
    {
        MerkleLogTree tree = BuildTree(5);
        ReadOnlyMemory<byte> root = tree.ComputeRoot(Sha256);
        MerkleInclusionProof proof = tree.ProveInclusion(2, Sha256);

        MerkleInclusionProof back = RoundTripInclusionProof(proof);

        Assert.AreEqual(proof.LeafIndex, back.LeafIndex);
        Assert.AreEqual(proof.TreeSize, back.TreeSize);
        Assert.HasCount(proof.Path.Length, back.Path);
        Assert.IsTrue(back.Verify(Leaf(2), root, Sha256));
    }


    [TestMethod]
    public void ConsistencyProofRoundTripsAndStillVerifies()
    {
        MerkleLogTree oldTree = BuildTree(3);
        MerkleLogTree newTree = BuildTree(6);
        ReadOnlyMemory<byte> oldRoot = oldTree.ComputeRoot(Sha256);
        ReadOnlyMemory<byte> newRoot = newTree.ComputeRoot(Sha256);
        MerkleConsistencyProof proof = newTree.ProveConsistency(3, Sha256);

        MerkleConsistencyProof back = RoundTripConsistencyProof(proof);

        Assert.AreEqual(proof.OldTreeSize, back.OldTreeSize);
        Assert.AreEqual(proof.NewTreeSize, back.NewTreeSize);
        Assert.HasCount(proof.Path.Length, back.Path);
        Assert.IsTrue(back.Verify(oldRoot, newRoot, Sha256));
    }


    [TestMethod]
    public void SegmentSealWithProofsRoundTrips()
    {
        SegmentSeal<string> first = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0x11 }, [], Sha256);
        SegmentSeal<string> seal = SegmentSeal<string>.Create(3, 7, first.Digest, new byte[] { 0xAB, 0xCD }, ["controller", "auditor"], Sha256);

        SegmentSeal<string> back = RoundTripSeal(seal);

        Assert.AreEqual(seal, back);
        CollectionAssert.AreEqual(seal.Proofs.ToArray(), back.Proofs.ToArray());
    }


    [TestMethod]
    public void FirstSegmentSealWithNullPreviousDigestRoundTrips()
    {
        SegmentSeal<string> seal = SegmentSeal<string>.Create(0, 4, null, new byte[] { 0x33 }, [], Sha256);

        SegmentSeal<string> back = RoundTripSeal(seal);

        Assert.IsNull(back.PreviousSealDigest);
        Assert.AreEqual(seal, back);
    }


    [TestMethod]
    public void TamperedSealCommitmentIsRejected()
    {
        //Flipping a commitment hex character re-derives a different digest than the one transmitted, so the
        //codec's byte-for-byte digest check must reject the seal rather than hand back a forged commitment.
        SegmentSeal<string> seal = SegmentSeal<string>.Create(0, 2, null, new byte[] { 0xAB, 0xCD }, [], Sha256);
        string json = SerializeSeal(seal);
        string tampered = FlipFirstCommitmentDigit(json);

        Assert.ThrowsExactly<JsonException>(() => DeserializeSeal(tampered));
    }


    [TestMethod]
    public void MalformedRootHexIsRejected()
    {
        string json = """{"treeSize":1,"root":"zz"}""";

        Assert.ThrowsExactly<JsonException>(
            () => Deserialize(json, LogCommitmentJson.CreateLogHeadDeserializer()));
    }


    [TestMethod]
    public void MalformedPathHexIsRejected()
    {
        string json = """{"leafIndex":0,"treeSize":2,"path":["zz"]}""";

        Assert.ThrowsExactly<JsonException>(
            () => Deserialize(json, LogCommitmentJson.CreateInclusionProofDeserializer()));
    }


    [TestMethod]
    public void NegativeTreeSizeIsRejected()
    {
        string json = """{"treeSize":-1,"root":"00"}""";

        Assert.ThrowsExactly<JsonException>(
            () => Deserialize(json, LogCommitmentJson.CreateLogHeadDeserializer()));
    }


    [TestMethod]
    public void InvertedSealRangeIsRejected()
    {
        //A last index below the first index is rejected by SegmentSeal.Create; the codec wraps it so the
        //hostile payload surfaces as a JsonException, not a raw ArgumentOutOfRangeException.
        string json = """{"firstIndex":5,"lastIndex":2,"previousSealDigest":null,"commitment":"ab","digest":"00","proofs":[]}""";

        Assert.ThrowsExactly<JsonException>(() => DeserializeSeal(json));
    }


    [TestMethod]
    public void TruncatedPayloadIsRejected()
    {
        string json = """{"treeSize":1,"root":"de""";

        //JsonDocument.Parse throws the internal JsonReaderException subclass, so match by base type.
        Assert.Throws<JsonException>(
            () => Deserialize(json, LogCommitmentJson.CreateLogHeadDeserializer()));
    }


    private static LogHead RoundTripLogHead(LogHead head)
    {
        var buffer = new ArrayBufferWriter<byte>();
        LogCommitmentJson.CreateLogHeadSerializer()(head, buffer);

        return LogCommitmentJson.CreateLogHeadDeserializer()(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static MerkleInclusionProof RoundTripInclusionProof(MerkleInclusionProof proof)
    {
        var buffer = new ArrayBufferWriter<byte>();
        LogCommitmentJson.CreateInclusionProofSerializer()(proof, buffer);

        return LogCommitmentJson.CreateInclusionProofDeserializer()(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static MerkleConsistencyProof RoundTripConsistencyProof(MerkleConsistencyProof proof)
    {
        var buffer = new ArrayBufferWriter<byte>();
        LogCommitmentJson.CreateConsistencyProofSerializer()(proof, buffer);

        return LogCommitmentJson.CreateConsistencyProofDeserializer()(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static SegmentSeal<string> RoundTripSeal(SegmentSeal<string> seal)
    {
        return DeserializeSeal(SerializeSeal(seal));
    }


    private static string SerializeSeal(SegmentSeal<string> seal)
    {
        var buffer = new ArrayBufferWriter<byte>();
        LogCommitmentJson.CreateSegmentSealSerializer<string>(WriteString)(seal, buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }


    private static SegmentSeal<string> DeserializeSeal(string json)
    {
        return Deserialize(json, LogCommitmentJson.CreateSegmentSealDeserializer(ReadString, Sha256));
    }


    private static string FlipFirstCommitmentDigit(string json)
    {
        const string marker = "\"commitment\":\"";
        int valueStart = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        char original = json[valueStart];
        char flipped = original == 'a' ? 'b' : 'a';

        return string.Concat(json.AsSpan(0, valueStart), flipped.ToString(), json.AsSpan(valueStart + 1));
    }


    private static TMessage Deserialize<TMessage>(string json, DeserializeMessageDelegate<TMessage> deserializer)
    {
        return deserializer(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static MerkleLogTree BuildTree(int leafCount)
    {
        MerkleLogTree tree = MerkleLogTree.Empty;
        for(int i = 0; i < leafCount; i++)
        {
            tree = tree.Append(Leaf(i));
        }

        return tree;
    }


    private static ReadOnlyMemory<byte> Leaf(int index) => Encoding.UTF8.GetBytes($"leaf-{index}");


    private static ReadOnlyMemory<byte> Sha256(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);


    private static void WriteString(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);


    private static string ReadString(JsonElement element) => element.GetString()!;
}
