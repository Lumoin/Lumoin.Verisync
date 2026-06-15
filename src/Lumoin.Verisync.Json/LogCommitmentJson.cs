using Lumoin.Verisync.Core;
using System;
using System.Collections.Immutable;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the log-plane commitment types —
/// <see cref="LogHead"/>, <see cref="MerkleInclusionProof"/>, <see cref="MerkleConsistencyProof"/>, and
/// <see cref="SegmentSeal{TProof}"/> — so a host can persist them or carry them over a Verisync message
/// channel during the anti-equivocation exchange.
/// </summary>
/// <remarks>
/// <para>
/// The encoding is hand-written and explicit — no reflection, AOT- and trim-safe — and every hash is
/// hex-encoded, matching <see cref="ConsensusMessageJson"/> and <see cref="CrdtStateJson"/>. Hostile input
/// fails closed at the codec: malformed hex, out-of-range sizes, and inverted ranges all surface as a
/// <see cref="JsonException"/> rather than a raw argument exception or a half-built value.
/// </para>
/// <para>
/// The seal deserializer is deliberately a verifier, not just a parser. It rebuilds the seal through
/// <see cref="SegmentSeal{TProof}.Create"/> — which re-derives the canonical bytes and digest from the typed
/// fields — and then compares the re-derived digest byte-for-byte to the transmitted digest. A tampered seal
/// (a flipped commitment byte, a doctored index) therefore cannot round-trip; this is why the deserializer
/// factory takes the <see cref="ComputeDigestDelegate"/>. The caller supplies how to read and write
/// <c>TProof</c>, since the attestation evidence is application-defined.
/// </para>
/// </remarks>
public static class LogCommitmentJson
{
    /// <summary>Creates a serializer for <see cref="LogHead"/>.</summary>
    /// <returns>A serialize delegate.</returns>
    public static SerializeMessageDelegate<LogHead> CreateLogHeadSerializer()
    {
        return (head, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("treeSize", head.TreeSize);
            writer.WriteString("root", Convert.ToHexStringLower(head.Root.Span));
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="LogHead"/>.</summary>
    /// <returns>A deserialize delegate.</returns>
    public static DeserializeMessageDelegate<LogHead> CreateLogHeadDeserializer()
    {
        return JsonMessageGuard.FailClosed<LogHead>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int treeSize = RequireProperty(root, "treeSize", "A log head").GetInt32();
            ReadOnlyMemory<byte> rootHash = ReadHex(RequireProperty(root, "root", "A log head").GetString()!);

            return Construct(() => new LogHead(treeSize, rootHash));
        });
    }


    /// <summary>Creates a serializer for <see cref="MerkleInclusionProof"/>.</summary>
    /// <returns>A serialize delegate.</returns>
    public static SerializeMessageDelegate<MerkleInclusionProof> CreateInclusionProofSerializer()
    {
        return (proof, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("leafIndex", proof.LeafIndex);
            writer.WriteNumber("treeSize", proof.TreeSize);
            WriteHexArray(writer, "path", proof.Path);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="MerkleInclusionProof"/>.</summary>
    /// <returns>A deserialize delegate.</returns>
    public static DeserializeMessageDelegate<MerkleInclusionProof> CreateInclusionProofDeserializer()
    {
        return JsonMessageGuard.FailClosed<MerkleInclusionProof>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int leafIndex = RequireProperty(root, "leafIndex", "An inclusion proof").GetInt32();
            int treeSize = RequireProperty(root, "treeSize", "An inclusion proof").GetInt32();
            ImmutableArray<ReadOnlyMemory<byte>> path = ReadHexArray(RequireProperty(root, "path", "An inclusion proof"));

            return Construct(() => new MerkleInclusionProof(leafIndex, treeSize, path));
        });
    }


    /// <summary>Creates a serializer for <see cref="MerkleConsistencyProof"/>.</summary>
    /// <returns>A serialize delegate.</returns>
    public static SerializeMessageDelegate<MerkleConsistencyProof> CreateConsistencyProofSerializer()
    {
        return (proof, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("oldTreeSize", proof.OldTreeSize);
            writer.WriteNumber("newTreeSize", proof.NewTreeSize);
            WriteHexArray(writer, "path", proof.Path);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="MerkleConsistencyProof"/>.</summary>
    /// <returns>A deserialize delegate.</returns>
    public static DeserializeMessageDelegate<MerkleConsistencyProof> CreateConsistencyProofDeserializer()
    {
        return JsonMessageGuard.FailClosed<MerkleConsistencyProof>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int oldTreeSize = RequireProperty(root, "oldTreeSize", "A consistency proof").GetInt32();
            int newTreeSize = RequireProperty(root, "newTreeSize", "A consistency proof").GetInt32();
            ImmutableArray<ReadOnlyMemory<byte>> path = ReadHexArray(RequireProperty(root, "path", "A consistency proof"));

            return Construct(() => new MerkleConsistencyProof(oldTreeSize, newTreeSize, path));
        });
    }


    /// <summary>Creates a serializer for <see cref="SegmentSeal{TProof}"/>.</summary>
    /// <typeparam name="TProof">The attestation proof type.</typeparam>
    /// <param name="writeProof">Writes a single proof to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeProof"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<SegmentSeal<TProof>> CreateSegmentSealSerializer<TProof>(Action<Utf8JsonWriter, TProof> writeProof)
    {
        ArgumentNullException.ThrowIfNull(writeProof);

        return (seal, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("firstIndex", seal.FirstIndex);
            writer.WriteNumber("lastIndex", seal.LastIndex);
            if(seal.PreviousSealDigest is { } previous)
            {
                writer.WriteString("previousSealDigest", Convert.ToHexStringLower(previous.Span));
            }
            else
            {
                writer.WriteNull("previousSealDigest");
            }

            writer.WriteString("commitment", Convert.ToHexStringLower(seal.Commitment.Span));
            writer.WriteString("digest", Convert.ToHexStringLower(seal.Digest.Span));

            writer.WriteStartArray("proofs");
            foreach(TProof proof in seal.Proofs)
            {
                writeProof(writer, proof);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="SegmentSeal{TProof}"/>.</summary>
    /// <typeparam name="TProof">The attestation proof type.</typeparam>
    /// <param name="readProof">Reads a single proof from a JSON element.</param>
    /// <param name="computeDigest">The digest function used to re-derive and verify the seal digest.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <remarks>
    /// The seal is rebuilt through <see cref="SegmentSeal{TProof}.Create"/>, which re-derives the digest from
    /// the typed fields. The re-derived digest is compared byte-for-byte to the transmitted <c>digest</c>; a
    /// mismatch — the signature of a tampered seal — throws a <see cref="JsonException"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readProof"/> or <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<SegmentSeal<TProof>> CreateSegmentSealDeserializer<TProof>(Func<JsonElement, TProof> readProof, ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(readProof);
        ArgumentNullException.ThrowIfNull(computeDigest);

        return JsonMessageGuard.FailClosed<SegmentSeal<TProof>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            ulong firstIndex = RequireProperty(root, "firstIndex", "A segment seal").GetUInt64();
            ulong lastIndex = RequireProperty(root, "lastIndex", "A segment seal").GetUInt64();

            //A genuine null must survive: a conditional that mixes a null literal with a non-nullable
            //ReadOnlyMemory<byte> lifts the null branch to an empty default rather than a null nullable, so the
            //previous digest is read with an explicit branch to keep the first-seal null intact.
            JsonElement previousElement = RequireProperty(root, "previousSealDigest", "A segment seal");
            ReadOnlyMemory<byte>? previousSealDigest = null;
            if(previousElement.ValueKind != JsonValueKind.Null)
            {
                previousSealDigest = ReadHex(previousElement.GetString()!);
            }

            ReadOnlyMemory<byte> commitment = ReadHex(RequireProperty(root, "commitment", "A segment seal").GetString()!);
            ReadOnlyMemory<byte> transmittedDigest = ReadHex(RequireProperty(root, "digest", "A segment seal").GetString()!);

            JsonElement proofsElement = RequireProperty(root, "proofs", "A segment seal");
            ImmutableArray<TProof>.Builder proofs = ImmutableArray.CreateBuilder<TProof>(proofsElement.GetArrayLength());
            foreach(JsonElement proof in proofsElement.EnumerateArray())
            {
                proofs.Add(readProof(proof));
            }

            //Reconstruction re-derives the canonical bytes and digest from the typed fields, so a hostile
            //payload that flipped a commitment byte or an index re-derives a different digest than the one it
            //transmitted. The byte-for-byte check makes the codec fail closed on tampering.
            SegmentSeal<TProof> seal = Construct(() => SegmentSeal<TProof>.Create(firstIndex, lastIndex, previousSealDigest, commitment, ImmutableArray<TProof>.Empty, computeDigest));
            if(!seal.Digest.Span.SequenceEqual(transmittedDigest.Span))
            {
                throw new JsonException("The seal digest does not match the digest re-derived from its fields; the seal has been tampered with.");
            }

            return seal.WithProofs(proofs.MoveToImmutable());
        });
    }


    private static void WriteHexArray(Utf8JsonWriter writer, string name, ImmutableArray<ReadOnlyMemory<byte>> hashes)
    {
        writer.WriteStartArray(name);
        foreach(ReadOnlyMemory<byte> hash in hashes)
        {
            writer.WriteStringValue(Convert.ToHexStringLower(hash.Span));
        }

        writer.WriteEndArray();
    }


    private static ImmutableArray<ReadOnlyMemory<byte>> ReadHexArray(JsonElement element)
    {
        ImmutableArray<ReadOnlyMemory<byte>>.Builder hashes = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>(element.GetArrayLength());
        foreach(JsonElement entry in element.EnumerateArray())
        {
            hashes.Add(ReadHex(entry.GetString()!));
        }

        return hashes.MoveToImmutable();
    }


    private static JsonElement RequireProperty(JsonElement element, string name, string label)
    {
        //A required field absent from an object is malformed input, so it fails closed as JsonException
        //rather than the KeyNotFoundException the raw GetProperty accessor throws. A non-object element still
        //surfaces InvalidOperationException exactly as GetProperty did, so only the missing-field case changes.
        if(!element.TryGetProperty(name, out JsonElement property))
        {
            throw new JsonException($"{label} must carry a '{name}' field.");
        }

        return property;
    }


    private static ReadOnlyMemory<byte> ReadHex(string hex)
    {
        //The payload may come from an untrusted peer, so the hex is validated before the bytes are allowed
        //to stand in for a hash or commitment.
        try
        {
            return Convert.FromHexString(hex);
        }
        catch(FormatException exception)
        {
            throw new JsonException("A hash must be hex-encoded.", exception);
        }
    }


    private static T Construct<T>(Func<T> construct)
    {
        //The typed fields come from an untrusted payload; the domain constructors reject hostile values
        //(negative sizes, inverted ranges, an empty commitment) with argument exceptions, which are wrapped
        //so the raw argument exception never surfaces from the codec.
        try
        {
            return construct();
        }
        catch(ArgumentException exception)
        {
            throw new JsonException("The payload carries values a log commitment rejects.", exception);
        }
    }
}
