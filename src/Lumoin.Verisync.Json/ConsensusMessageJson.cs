using System;
using System.Buffers;
using System.Text.Json;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the Fast CASPaxos protocol DTOs,
/// so they can cross a Verisync message channel (in-memory pipe or socket).
/// </summary>
/// <remarks>
/// The polymorphic request/reply envelopes are written with a <c>kind</c> discriminator rather than relying on
/// source-generated polymorphism, and replica ids are hex-encoded — keeping the encoding explicit, AOT-safe,
/// and free of reflection. The caller supplies how to read and write <typeparamref name="TValue"/>, since the
/// value is application-defined.
/// </remarks>
public static class ConsensusMessageJson
{
    /// <summary>Creates a serializer for <see cref="ConsensusRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<ConsensusRequest<TValue>> CreateRequestSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (request, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();

            if(request is PrepareRequest<TValue> prepare)
            {
                writer.WriteString("kind", "prepare");
                WriteBallot(writer, "ballot", prepare.Ballot);
            }
            else if(request is AcceptRequest<TValue> accept)
            {
                writer.WriteString("kind", "accept");
                WriteBallot(writer, "ballot", accept.Ballot);
                writer.WritePropertyName("value");
                writeValue(writer, accept.Value);

                //The piggybacked next ballot is optional; an absent field decodes as no piggyback, keeping
                //wire back-compat with payloads that predate the field.
                if(accept.Next is { } next)
                {
                    WriteBallot(writer, "next", next);
                }
            }
            else
            {
                throw new NotSupportedException($"Unknown request kind '{request.GetType().Name}'.");
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="ConsensusRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<ConsensusRequest<TValue>> CreateRequestDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string kind = root.GetProperty("kind").GetString()!;
            FastBallot ballot = ReadBallot(root.GetProperty("ballot"));

            return kind switch
            {
                "prepare" => new PrepareRequest<TValue>(ballot),
                "accept" => new AcceptRequest<TValue>(
                    ballot,
                    readValue(root.GetProperty("value")),
                    //An absent (or null) next field decodes as no piggyback, so payloads that predate the
                    //field deserialize unchanged.
                    root.TryGetProperty("next", out JsonElement next) && next.ValueKind != JsonValueKind.Null ? ReadBallot(next) : null),
                _ => throw new NotSupportedException($"Unknown request kind '{kind}'.")
            };
        };
    }


    /// <summary>Creates a serializer for <see cref="ConsensusReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<ConsensusReply<TValue>> CreateReplySerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (reply, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();

            if(reply is PrepareReply<TValue> prepareReply)
            {
                writer.WriteString("kind", "prepare-reply");
                writer.WriteBoolean("promised", prepareReply.Promised);
                WriteBallot(writer, "acceptedBallot", prepareReply.AcceptedBallot);
                writer.WritePropertyName("acceptedValue");
                if(prepareReply.AcceptedValue is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writeValue(writer, prepareReply.AcceptedValue);
                }

                WriteBallot(writer, "conflictingBallot", prepareReply.ConflictingBallot);
            }
            else if(reply is AcceptReply<TValue> acceptReply)
            {
                writer.WriteString("kind", "accept-reply");
                writer.WriteBoolean("accepted", acceptReply.Accepted);
                WriteBallot(writer, "ballot", acceptReply.Ballot);
            }
            else
            {
                throw new NotSupportedException($"Unknown reply kind '{reply.GetType().Name}'.");
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="ConsensusReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<ConsensusReply<TValue>> CreateReplyDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string kind = root.GetProperty("kind").GetString()!;

            if(kind == "prepare-reply")
            {
                JsonElement acceptedValue = root.GetProperty("acceptedValue");

                return new PrepareReply<TValue>(
                    root.GetProperty("promised").GetBoolean(),
                    ReadBallot(root.GetProperty("acceptedBallot")),
                    acceptedValue.ValueKind == JsonValueKind.Null ? default : readValue(acceptedValue),
                    ReadBallot(root.GetProperty("conflictingBallot")));
            }

            if(kind == "accept-reply")
            {
                return new AcceptReply<TValue>(
                    root.GetProperty("accepted").GetBoolean(),
                    ReadBallot(root.GetProperty("ballot")));
            }

            throw new NotSupportedException($"Unknown reply kind '{kind}'.");
        };
    }


    private static void WriteBallot(Utf8JsonWriter writer, string name, FastBallot ballot)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("round", ballot.Round);
        if(ballot.Proposer is { } proposer)
        {
            writer.WriteString("proposer", Convert.ToHexStringLower(proposer.AsSpan()));
        }
        else
        {
            writer.WriteNull("proposer");
        }

        writer.WriteEndObject();
    }


    private static FastBallot ReadBallot(JsonElement element)
    {
        int round = element.GetProperty("round").GetInt32();
        JsonElement proposer = element.GetProperty("proposer");
        if(proposer.ValueKind == JsonValueKind.Null)
        {
            return new FastBallot(round, null);
        }

        ReplicaId proposerBytes = ReplicaId.FromSpan(Convert.FromHexString(proposer.GetString()!));

        return new FastBallot(round, proposerBytes);
    }
}
