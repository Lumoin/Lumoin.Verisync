using Lumoin.Verisync.Core;
using System;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the QuePaxa protocol messages,
/// <see cref="RecordRequest{TValue}"/> and <see cref="RecordReply{TValue}"/>, so they can cross a Verisync
/// message channel.
/// </summary>
/// <remarks>
/// <para>
/// THERE IS NO DISCRIMINATOR FIELD, unlike <see cref="ConsensusMessageJson"/>. QuePaxa has one message in
/// each direction rather than a family to discriminate, so each direction gets its own factory pair and the
/// caller picks by type.
/// </para>
/// <para>
/// THE RESERVED PRIORITY IS A BARE JSON NUMBER, AND A DOUBLE-PARSING CONSUMER SILENTLY DEMOTES IT.
/// <see cref="ProposalPriority.Reserved"/> is <see cref="ulong.MaxValue"/>, which is above two to the
/// fifty-third, and it is indistinguishable from the priority one below it once either has passed through an
/// IEEE double. These factories read the field with <see cref="JsonElement.GetUInt64"/> and are therefore
/// exact, but a JavaScript consumer, a JSON-Schema validator or any pipeline that reparses and re-emits the
/// payload collapses the two, and a demoted reserved priority costs the round-one leader its fast path. A
/// deployment that carries QuePaxa messages through a non-.NET JSON reader needs an exact encoding, and this
/// codec is not one.
/// </para>
/// <para>
/// THE MESSAGE PAIR IS NOT SELF-CORRELATING, and no encoding here can make it so. A reply carries the
/// recorder's own step rather than the step of the request it answers, and
/// <see cref="RecorderEndpointDelegate{TValue}"/> states that calls for consecutive steps overlap, so a
/// transport holding one slot per recorder hands an older call's reply to a newer call and nothing above it
/// can tell. Correlating a reply to the call it answers is the transport's obligation.
/// <see cref="QuePaxaProposer{TValue}"/>'s step floor is a one-sided backstop that drops a reply from below
/// the current step, and it cannot detect the opposite mis-correlation.
/// </para>
/// <para>
/// The caller supplies how to read and write <typeparamref name="TValue"/>, since the value is
/// application-defined. That seam is a naked delegate rather than a named delegate type, which is the local
/// convention every codec in this assembly follows.
/// </para>
/// </remarks>
public static class QuePaxaMessageJson
{
    private const string RequestLabel = "A record request";
    private const string ReplyLabel = "A record reply";
    private const string ProposalLabel = "A proposal";
    private const string LaneLabel = "A proposer lane";


    /// <summary>Creates a serializer for <see cref="RecordRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RecordRequest<TValue>> CreateRequestSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (request, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("step", request.Step.Value);
            WriteProposal(writer, "proposal", request.Proposal, writeValue);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="RecordRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RecordRequest<TValue>> CreateRequestDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<RecordRequest<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int step = RequireProperty(root, "step", RequestLabel).GetInt32();
            PrioritizedProposal<TValue> proposal = ReadProposal(RequireProperty(root, "proposal", RequestLabel), readValue);

            return Construct(() => new RecordRequest<TValue>(new RecorderStep(step), proposal));
        });
    }


    /// <summary>Creates a serializer for <see cref="RecordReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RecordReply<TValue>> CreateReplySerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (reply, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("step", reply.Step.Value);
            WriteProposal(writer, "first", reply.First, writeValue);

            //The prior aggregate is written as an explicit null rather than omitted, because a skipped step
            //legitimately clears it and a required field can be swept for by a missing-field test where an
            //optional one cannot.
            if(reply.PriorAggregate is { } priorAggregate)
            {
                WriteProposal(writer, "priorAggregate", priorAggregate, writeValue);
            }
            else
            {
                writer.WriteNull("priorAggregate");
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="RecordReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RecordReply<TValue>> CreateReplyDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<RecordReply<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int step = RequireProperty(root, "step", ReplyLabel).GetInt32();
            PrioritizedProposal<TValue> first = ReadProposal(RequireProperty(root, "first", ReplyLabel), readValue);
            JsonElement priorAggregate = RequireProperty(root, "priorAggregate", ReplyLabel);
            PrioritizedProposal<TValue>? prior = priorAggregate.ValueKind == JsonValueKind.Null
                ? null
                : ReadProposal(priorAggregate, readValue);

            return Construct(() => new RecordReply<TValue>(new RecorderStep(step), first, prior));
        });
    }


    private static JsonElement RequireProperty(JsonElement element, string name, string label)
    {
        //A required field absent from an object is malformed input, so it fails closed as JsonException rather
        //than the KeyNotFoundException the raw GetProperty accessor throws. A non-object element still
        //surfaces InvalidOperationException exactly as GetProperty did.
        if(!element.TryGetProperty(name, out JsonElement property))
        {
            throw new JsonException($"{label} must carry a '{name}' field.");
        }

        return property;
    }


    private static T Construct<T>(Func<T> construct)
    {
        //A value arriving from the wire has not been through a constructor, so every domain validator runs
        //here and none is duplicated: the codec adds no rule of its own. The argument exception a validator
        //raises is wrapped so the failure reaches a reader as a JSON fault, with the validator's own message
        //preserved as the inner exception.
        try
        {
            return construct();
        }
        catch(ArgumentException exception)
        {
            throw new JsonException("The payload carries values a QuePaxa message rejects.", exception);
        }
    }


    private static void WriteProposal<TValue>(Utf8JsonWriter writer, string name, PrioritizedProposal<TValue> proposal, Action<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();

        //The priority is the whole ulong range and is written as a bare number, matching the only other
        //unsigned-64 field on any Verisync wire. The remarks on this class state what that costs a consumer
        //that parses JSON numbers as doubles.
        writer.WriteNumber("priority", proposal.Key.Priority.Value);
        WriteLane(writer, "owner", proposal.Key.Owner);
        writer.WritePropertyName("value");
        writeValue(writer, proposal.Value);
        writer.WriteEndObject();
    }


    private static PrioritizedProposal<TValue> ReadProposal<TValue>(JsonElement element, Func<JsonElement, TValue> readValue)
    {
        ulong priority = RequireProperty(element, "priority", ProposalLabel).GetUInt64();
        ProposerLane owner = ReadLane(RequireProperty(element, "owner", ProposalLabel));
        TValue value = readValue(RequireProperty(element, "value", ProposalLabel));

        return new PrioritizedProposal<TValue>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    private static void WriteLane(Utf8JsonWriter writer, string name, ProposerLane lane)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("replica", Convert.ToHexStringLower(lane.Replica.AsSpan()));
        writer.WriteNumber("lane", lane.Lane);
        writer.WriteEndObject();
    }


    private static ProposerLane ReadLane(JsonElement element)
    {
        string replica = RequireProperty(element, "replica", LaneLabel).GetString()!;
        int lane = RequireProperty(element, "lane", LaneLabel).GetInt32();

        //The all-zero identity at lane zero is degenerate rather than illegal, so nothing here refuses it; the
        //identity width is the only rule, and it lives in the domain factory.
        return Construct(() => new ProposerLane(ReplicaId.FromSpan(Convert.FromHexString(replica)), lane));
    }
}
