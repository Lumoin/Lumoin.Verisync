using Lumoin.Verisync.Core;
using System;
using System.Buffers;
using System.Text.Json;

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

        return JsonMessageGuard.FailClosed<ConsensusRequest<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string kind = RequireProperty(root, "kind", "A consensus request").GetString()!;
            FastBallot ballot = ReadBallot(RequireProperty(root, "ballot", "A consensus request"));

            return kind switch
            {
                "prepare" => new PrepareRequest<TValue>(ballot),
                "accept" => new AcceptRequest<TValue>(
                    ballot,
                    readValue(RequireProperty(root, "value", "A consensus request")),
                    //An absent (or null) next field decodes as no piggyback, so payloads that predate the
                    //field deserialize unchanged.
                    root.TryGetProperty("next", out JsonElement next) && next.ValueKind != JsonValueKind.Null ? ReadBallot(next) : null),
                _ => throw new NotSupportedException($"Unknown request kind '{kind}'.")
            };
        });
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

        return JsonMessageGuard.FailClosed<ConsensusReply<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string kind = RequireProperty(root, "kind", "A consensus reply").GetString()!;

            if(kind == "prepare-reply")
            {
                JsonElement acceptedValue = RequireProperty(root, "acceptedValue", "A consensus reply");

                return new PrepareReply<TValue>(
                    RequireProperty(root, "promised", "A consensus reply").GetBoolean(),
                    ReadBallot(RequireProperty(root, "acceptedBallot", "A consensus reply")),
                    acceptedValue.ValueKind == JsonValueKind.Null ? default : readValue(acceptedValue),
                    ReadBallot(RequireProperty(root, "conflictingBallot", "A consensus reply")));
            }

            if(kind == "accept-reply")
            {
                return new AcceptReply<TValue>(
                    RequireProperty(root, "accepted", "A consensus reply").GetBoolean(),
                    ReadBallot(RequireProperty(root, "ballot", "A consensus reply")));
            }

            throw new NotSupportedException($"Unknown reply kind '{kind}'.");
        });
    }


    private static JsonElement RequireProperty(JsonElement element, string name, string label)
    {
        //A required field absent from an object is malformed input, so it fails closed as JsonException
        //rather than the KeyNotFoundException the raw GetProperty accessor throws. A non-object element still
        //surfaces InvalidOperationException exactly as GetProperty did, so only the missing-field case changes.
        //The optional piggybacked next ballot is exempt: it keeps its own TryGetProperty so its absence stays
        //a legal "no piggyback" rather than a rejection.
        if(!element.TryGetProperty(name, out JsonElement property))
        {
            throw new JsonException($"{label} must carry a '{name}' field.");
        }

        return property;
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
        int round = RequireProperty(element, "round", "A ballot").GetInt32();
        JsonElement proposer = RequireProperty(element, "proposer", "A ballot");
        if(proposer.ValueKind == JsonValueKind.Null)
        {
            return new FastBallot(round, null);
        }

        ReplicaId proposerBytes = ReplicaId.FromSpan(Convert.FromHexString(proposer.GetString()!));

        return new FastBallot(round, proposerBytes);
    }
}
