using Lumoin.Verisync.Core;
using System;
using System.Buffers;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the Fast CASPaxos protocol DTOs
/// and for the durable state a host persists, <see cref="FastAcceptorState{TValue}"/>, so they can cross a
/// Verisync message channel (in-memory pipe or socket) or be persisted by a host.
/// </summary>
/// <remarks>
/// The polymorphic request/reply envelopes are written with a <c>kind</c> discriminator rather than relying on
/// source-generated polymorphism, and replica ids are hex-encoded — keeping the encoding explicit, AOT-safe,
/// and free of reflection. The caller supplies how to read and write <c>TValue</c>, since the
/// value is application-defined.
/// </remarks>
public static class ConsensusMessageJson
{
    /// <summary>Creates a serializer for <see cref="ConsensusRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<ConsensusRequest<TValue>> CreateRequestSerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
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
    public static DeserializeMessageDelegate<ConsensusRequest<TValue>> CreateRequestDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
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
    public static SerializeMessageDelegate<ConsensusReply<TValue>> CreateReplySerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
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
    public static DeserializeMessageDelegate<ConsensusReply<TValue>> CreateReplyDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
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


    /// <summary>Creates a serializer for <see cref="FastAcceptorState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The state is what a host makes stable before a dependent reply leaves the process, so a payload this
    /// writes is the payload <see cref="CreateAcceptorStateDeserializer{TValue}"/> reads back on a restart.
    /// The accepted ballot and the accepted value are encoded exactly as a prepare reply's matching fields,
    /// so the two payloads agree where they overlap; the promise is a ballot here where a reply's
    /// <c>promised</c> is a boolean, which is why the state has its own factory pair rather than reusing the
    /// reply's.
    /// </remarks>
    public static SerializeMessageDelegate<FastAcceptorState<TValue>> CreateAcceptorStateSerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            WriteBallot(writer, "promised", state.Promised);
            WriteBallot(writer, "acceptedBallot", state.AcceptedBallot);
            writer.WritePropertyName("acceptedValue");
            if(state.AcceptedValue is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writeValue(writer, state.AcceptedValue);
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="FastAcceptorState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A state this returns is decoded and not validated as a whole. Ballots are decoded through the same
    /// reader the wire messages use, which builds them without checking a round or a proposer, so a negative
    /// round and a round-zero ballot owning a proposer both decode. Every restore rule belongs to
    /// <see cref="FastAcceptor{TValue}.FromState"/> — the single-slot range checks that refuse those two
    /// ballots as well as the relational rules, because <see cref="FastBallot"/> validates nothing — so a
    /// host restores by passing the decoded state there and lets that factory refuse a snapshot no acceptor
    /// can hold. A missing <c>acceptedValue</c> field is
    /// malformed and fails closed; only a present JSON null decodes as the absent value.
    /// </remarks>
    public static DeserializeMessageDelegate<FastAcceptorState<TValue>> CreateAcceptorStateDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<FastAcceptorState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            JsonElement acceptedValue = RequireProperty(root, "acceptedValue", "An acceptor state");

            return new FastAcceptorState<TValue>(
                ReadBallot(RequireProperty(root, "promised", "An acceptor state")),
                ReadBallot(RequireProperty(root, "acceptedBallot", "An acceptor state")),
                acceptedValue.ValueKind == JsonValueKind.Null ? default : readValue(acceptedValue));
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
