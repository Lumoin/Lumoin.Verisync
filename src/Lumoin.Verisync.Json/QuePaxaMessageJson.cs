using Lumoin.Verisync.Core;
using System;
using System.Collections.Immutable;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the QuePaxa protocol messages,
/// <see cref="RecordRequest{TValue}"/> and <see cref="RecordReply{TValue}"/>, for the versioned envelopes
/// that address them to one consensus instance, for the decided record a versioned register carries, and for
/// the two durable states a host persists, the recorder's own and the versioned host's, so they can cross a
/// Verisync message channel or be persisted by a host.
/// </summary>
/// <remarks>
/// <para>
/// There is no discriminator field, unlike <see cref="ConsensusMessageJson"/>. QuePaxa has one message in
/// each direction rather than a family to discriminate, so each direction gets its own factory pair and the
/// caller picks by type.
/// </para>
/// <para>
/// The versioned factories work the same way. <see cref="VersionedRecordRequest{TValue}"/> wraps a request
/// with the register version of the consensus instance it belongs to, and a channel carrying the wrapper is
/// monotyped exactly as one carrying the bare message is. The version is a guard, so that a recorder host
/// serving one instance can refuse a request meant for another, and the inner encoding inside the wrapper is
/// byte for byte the bare one.
/// </para>
/// <para>
/// The reserved priority is a bare JSON number, and a double-parsing consumer silently demotes it.
/// <see cref="ProposalPriority.Reserved"/> is <see cref="ulong.MaxValue"/>, which is above two to the
/// fifty-third, and it is indistinguishable from the priority one below it once either has passed through an
/// IEEE double. These factories read the field with <see cref="JsonElement.GetUInt64"/> and are therefore
/// exact, but a JavaScript consumer, a JSON-Schema validator or any pipeline that reparses and re-emits the
/// payload collapses the two, and a demoted reserved priority costs the round-one leader its fast path. A
/// deployment that carries QuePaxa messages through a non-.NET JSON reader needs an exact encoding, and this
/// codec is not one.
/// </para>
/// <para>
/// The message pair is not self-correlating, and no encoding here can make it so. A reply carries the
/// recorder's own step rather than the step of the request it answers, and
/// <see cref="RecorderEndpointDelegate{TValue}"/> states that calls for consecutive steps overlap, so a
/// transport holding one slot per recorder hands an older call's reply to a newer call and nothing above it
/// can tell. Correlating a reply to the call it answers is the transport's obligation.
/// <see cref="QuePaxaProposer{TValue}"/>'s step floor is a one-sided backstop that drops a reply from below
/// the current step, and it cannot detect the opposite mis-correlation.
/// </para>
/// <para>
/// <see cref="QuePaxaRecorderState{TValue}"/> is durable state rather than a message, and its pair follows the
/// same validation split the message pairs follow. The decoder refuses only what the encoding can be wrong
/// about, which for a recorder state is a missing field alone, and hands every value to its domain
/// constructor, so a step outside its range is refused by <see cref="RecorderStep"/> and a negative lane by
/// <see cref="ProposerLane"/>, each surfacing with the validator's own message as the inner exception. The
/// relational rules over a whole state, among them that a step above <see cref="RecorderStep.Zero"/> carries a
/// first proposal and that a reserved priority at <see cref="RecorderStep.RoundOnePhaseZero"/> belongs to the
/// configured leader, stay in <see cref="QuePaxaRecorder{TValue}.FromState"/>, the single place that owns them.
/// A decoded state is therefore a faithful reading of the payload and not yet a state a recorder accepts, and
/// a host restores by handing what it decoded to that factory.
/// </para>
/// <para>
/// <see cref="QuePaxaVersionedNodeState{TValue}"/> follows the same split one layer up. Its decoder refuses a
/// missing field alone and hands every value to its domain constructor, while the rules that read its stored
/// leader and its stored version against the committed record beside them stay in
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/>. The recorder nested inside it is encoded exactly as a
/// standalone recorder state is, over <see cref="VersionedValue{TValue}"/> as the register's value type, so
/// there is one encoding of a recorder state and not two.
/// </para>
/// <para>
/// Every optional slot is written as an explicit null when absent — all three proposal slots of a recorder
/// state, and a versioned node state's committed record and configured leader — so an absent slot round-trips
/// as null and stays distinguishable from a field the payload never carried.
/// </para>
/// <para>
/// A versioned reply's <c>recorder</c> is a required slot on the same rule, written as the answering host's
/// identity in lower-case hexadecimal. A writer counts a quorum over distinct members of the membership it
/// addressed, so the identity is what lets it check that the endpoint it aimed at one member was answered by
/// that member; a payload that omits the field is refused by name and the missing-field tests sweep it. The
/// field is a claim the sender makes about itself and the codec verifies nothing about it, exactly as
/// <see cref="VersionedRecordReply{TValue}"/> states.
/// </para>
/// <para>
/// A decided record's configuration is a required slot rather than an optional one, so a payload that omits it
/// is refused by name and the missing-field tests sweep it. It is written as a nested object carrying the
/// chain identity and the ordered member list, each identity in lower-case hexadecimal, and the member order
/// is part of the value rather than an arrangement of a set: a payload listing the same replicas in another
/// order decodes into a configuration that is not equal to the one encoded.
/// </para>
/// <para>
/// A versioned node state's <c>activeConfiguration</c> is the same nested object under its own required slot,
/// and it is stored rather than recomputed for the same reason the stored leader and the stored version are:
/// the restore compares it against what the record beside it implies, and a value the restore recomputed
/// would be compared with itself. The decoder still adds no rule — the comparison lives in
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/>.
/// </para>
/// <para>
/// The caller supplies how to read and write <c>TValue</c>, since the value is
/// application-defined.
/// </para>
/// </remarks>
public static class QuePaxaMessageJson
{
    private const string RequestLabel = "A record request";
    private const string ReplyLabel = "A record reply";
    private const string VersionedRequestLabel = "A versioned record request";
    private const string VersionedReplyLabel = "A versioned record reply";
    private const string RecordLabel = "A versioned value";
    private const string ConfigurationLabel = "A configuration";
    private const string RecorderStateLabel = "A recorder state";
    private const string VersionedNodeStateLabel = "A versioned node state";
    private const string ProposalLabel = "A proposal";
    private const string LaneLabel = "A proposer lane";


    /// <summary>Creates a serializer for <see cref="RecordRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RecordRequest<TValue>> CreateRequestSerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (request, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            WriteRequest(writer, request, writeValue);
        };
    }


    /// <summary>Creates a deserializer for <see cref="RecordRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RecordRequest<TValue>> CreateRequestDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<RecordRequest<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadRequest(document.RootElement, readValue);
        });
    }


    /// <summary>Creates a serializer for <see cref="RecordReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RecordReply<TValue>> CreateReplySerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (reply, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            WriteReply(writer, reply, writeValue);
        };
    }


    /// <summary>Creates a deserializer for <see cref="RecordReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RecordReply<TValue>> CreateReplyDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<RecordReply<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadReply(document.RootElement, readValue);
        });
    }


    /// <summary>Creates a serializer for <see cref="VersionedRecordRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The consensus value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<VersionedRecordRequest<TValue>> CreateVersionedRequestSerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (request, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("version", request.Version.Value);
            writer.WritePropertyName("request");
            WriteRequest(writer, request.Request, writeValue);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="VersionedRecordRequest{TValue}"/>.</summary>
    /// <typeparam name="TValue">The consensus value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<VersionedRecordRequest<TValue>> CreateVersionedRequestDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<VersionedRecordRequest<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            ulong version = RequireProperty(root, "version", VersionedRequestLabel).GetUInt64();
            RecordRequest<TValue> request = ReadRequest(RequireProperty(root, "request", VersionedRequestLabel), readValue);

            return Construct(() => new VersionedRecordRequest<TValue>(new RegisterVersion(version), request));
        });
    }


    /// <summary>Creates a serializer for <see cref="VersionedRecordReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The consensus value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<VersionedRecordReply<TValue>> CreateVersionedReplySerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (reply, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("version", reply.Version.Value);
            writer.WriteString("recorder", Convert.ToHexStringLower(reply.Recorder.AsSpan()));
            writer.WritePropertyName("reply");
            WriteReply(writer, reply.Reply, writeValue);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="VersionedRecordReply{TValue}"/>.</summary>
    /// <typeparam name="TValue">The consensus value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<VersionedRecordReply<TValue>> CreateVersionedReplyDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<VersionedRecordReply<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            ulong version = RequireProperty(root, "version", VersionedReplyLabel).GetUInt64();
            string recorder = RequireProperty(root, "recorder", VersionedReplyLabel).GetString()!;
            RecordReply<TValue> reply = ReadReply(RequireProperty(root, "reply", VersionedReplyLabel), readValue);

            return Construct(() => new VersionedRecordReply<TValue>(new RegisterVersion(version), ReplicaId.FromSpan(Convert.FromHexString(recorder)), reply));
        });
    }


    /// <summary>Creates a serializer for <see cref="QuePaxaRecorderState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The state is what a host makes stable before a dependent reply leaves the process, so a payload this
    /// writes is the payload <see cref="CreateRecorderStateDeserializer{TValue}"/> reads back on a restart.
    /// </remarks>
    public static SerializeMessageDelegate<QuePaxaRecorderState<TValue>> CreateRecorderStateSerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            WriteRecorderState(writer, state, writeValue);
        };
    }


    /// <summary>Creates a deserializer for <see cref="QuePaxaRecorderState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A state this returns is decoded and not validated as a whole. The relational rules belong to
    /// <see cref="QuePaxaRecorder{TValue}.FromState"/>, so a host restores by passing the decoded state there
    /// and lets that factory refuse a snapshot no recorder-driven register can hold.
    /// </remarks>
    public static DeserializeMessageDelegate<QuePaxaRecorderState<TValue>> CreateRecorderStateDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return JsonMessageGuard.FailClosed<QuePaxaRecorderState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadRecorderState(document.RootElement, readValue);
        });
    }


    /// <summary>Creates a serializer for <see cref="QuePaxaVersionedNodeState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The application value type.</typeparam>
    /// <param name="writeValue">Writes an application value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The recorder nested inside the state is encoded exactly as
    /// <see cref="CreateRecorderStateSerializer{TValue}"/> encodes a standalone one, over the decided record as
    /// its value type, so the two payloads agree field for field where they overlap.
    /// </remarks>
    public static SerializeMessageDelegate<QuePaxaVersionedNodeState<TValue>> CreateVersionedNodeStateSerializer<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        //The record writer is built once here rather than per call, exactly as the value writer the other
        //factories close over is.
        WriteValueDelegate<Utf8JsonWriter, VersionedValue<TValue>> writeRecord = (writer, record) => WriteRecord(writer, record, writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            WriteRecordOrNull(writer, "committed", state.Committed, writeValue);
            writer.WriteNumber("recorderVersion", state.RecorderVersion.Value);
            WriteLaneOrNull(writer, "configuredLeader", state.ConfiguredLeader);
            WriteConfiguration(writer, "activeConfiguration", state.ActiveConfiguration);
            writer.WritePropertyName("recorder");
            WriteRecorderState(writer, state.Recorder, writeRecord);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="QuePaxaVersionedNodeState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The application value type.</typeparam>
    /// <param name="readValue">Reads an application value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A state this returns is decoded and not cross-checked. The rules that read the stored leader and the
    /// stored version against the record beside them belong to
    /// <see cref="QuePaxaVersionedNode{TValue}.FromState"/>, so a host restores by passing the decoded state
    /// there and lets that factory refuse a snapshot whose parts disagree.
    /// </remarks>
    public static DeserializeMessageDelegate<QuePaxaVersionedNodeState<TValue>> CreateVersionedNodeStateDeserializer<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        ReadValueDelegate<JsonElement, VersionedValue<TValue>> readRecord = element => ReadRecord(element, readValue);

        return JsonMessageGuard.FailClosed<QuePaxaVersionedNodeState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            VersionedValue<TValue>? committed = ReadRecordOrNull(root, "committed", VersionedNodeStateLabel, readValue);
            ulong recorderVersion = RequireProperty(root, "recorderVersion", VersionedNodeStateLabel).GetUInt64();
            ProposerLane? configuredLeader = ReadLaneOrNull(root, "configuredLeader", VersionedNodeStateLabel);
            QuePaxaConfiguration activeConfiguration = ReadConfiguration(RequireProperty(root, "activeConfiguration", VersionedNodeStateLabel));
            QuePaxaRecorderState<VersionedValue<TValue>> recorder = ReadRecorderState(RequireProperty(root, "recorder", VersionedNodeStateLabel), readRecord);

            return Construct(() => new QuePaxaVersionedNodeState<TValue>(committed, new RegisterVersion(recorderVersion), configuredLeader, activeConfiguration, recorder));
        });
    }


    /// <summary>
    /// Builds the value seam for a <see cref="VersionedValue{TValue}"/>, which is what a versioned register
    /// puts in a proposal's value slot.
    /// </summary>
    /// <typeparam name="TValue">The application value type.</typeparam>
    /// <param name="writeValue">Writes an application value to the JSON writer.</param>
    /// <returns>A writer for the record wrapping that value.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The record is a Verisync type and its writer field is what the next instance's leader is derived from,
    /// so its encoding lives here. Only the application value inside it stays caller-supplied.
    /// </remarks>
    public static WriteValueDelegate<Utf8JsonWriter, VersionedValue<TValue>> CreateVersionedValueWriter<TValue>(WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (writer, record) => WriteRecord(writer, record, writeValue);
    }


    /// <summary>
    /// Builds the value seam that reads a <see cref="VersionedValue{TValue}"/> back.
    /// </summary>
    /// <typeparam name="TValue">The application value type.</typeparam>
    /// <param name="readValue">Reads an application value from a JSON element.</param>
    /// <returns>A reader for the record wrapping that value.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static ReadValueDelegate<JsonElement, VersionedValue<TValue>> CreateVersionedValueReader<TValue>(ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return element => ReadRecord(element, readValue);
    }


    private static void WriteRecord<TValue>(Utf8JsonWriter writer, VersionedValue<TValue> record, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", record.Version.Value);
        writer.WriteString("writer", Convert.ToHexStringLower(record.Writer.AsSpan()));
        WriteConfiguration(writer, "configuration", record.NextConfiguration);
        writer.WritePropertyName("value");
        writeValue(writer, record.Value);
        writer.WriteEndObject();
    }


    private static VersionedValue<TValue> ReadRecord<TValue>(JsonElement element, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ulong version = RequireProperty(element, "version", RecordLabel).GetUInt64();
        string writer = RequireProperty(element, "writer", RecordLabel).GetString()!;
        QuePaxaConfiguration configuration = ReadConfiguration(RequireProperty(element, "configuration", RecordLabel));
        TValue value = readValue(RequireProperty(element, "value", RecordLabel));

        return Construct(() => new VersionedValue<TValue>(new RegisterVersion(version), ReplicaId.FromSpan(Convert.FromHexString(writer)), configuration, value));
    }


    private static void WriteConfiguration(Utf8JsonWriter writer, string name, QuePaxaConfiguration configuration)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("cluster", Convert.ToHexStringLower(configuration.Cluster.AsSpan()));
        writer.WriteStartArray("members");
        foreach(ReplicaId member in configuration.Members)
        {
            writer.WriteStringValue(Convert.ToHexStringLower(member.AsSpan()));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static QuePaxaConfiguration ReadConfiguration(JsonElement element)
    {
        JsonElement cluster = RequireProperty(element, "cluster", ConfigurationLabel);
        JsonElement members = RequireProperty(element, "members", ConfigurationLabel);

        //The chain identity's width, the members' widths and the list's own rules all belong to the domain
        //factories, so one construction carries every one of them and the codec adds none.
        return Construct(() =>
        {
            ImmutableArray<ReplicaId>.Builder listed = ImmutableArray.CreateBuilder<ReplicaId>();
            foreach(JsonElement member in members.EnumerateArray())
            {
                listed.Add(ReplicaId.FromSpan(Convert.FromHexString(member.GetString()!)));
            }

            return QuePaxaConfiguration.Create(ClusterId.FromSpan(Convert.FromHexString(cluster.GetString()!)), listed.ToImmutable());
        });
    }


    private static void WriteRecordOrNull<TValue>(Utf8JsonWriter writer, string name, VersionedValue<TValue>? record, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        //An absent record is written as an explicit null rather than omitted, on the same rule the proposal
        //slots follow: a host that has learned nothing is a state the protocol reaches, and a required field
        //can be swept for by a missing-field test where an optional one cannot.
        if(record is { } present)
        {
            writer.WritePropertyName(name);
            WriteRecord(writer, present, writeValue);
        }
        else
        {
            writer.WriteNull(name);
        }
    }


    private static VersionedValue<TValue>? ReadRecordOrNull<TValue>(JsonElement element, string name, string label, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        JsonElement slot = RequireProperty(element, name, label);

        return slot.ValueKind == JsonValueKind.Null ? null : ReadRecord(slot, readValue);
    }


    private static void WriteRecorderState<TValue>(Utf8JsonWriter writer, QuePaxaRecorderState<TValue> state, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartObject();
        writer.WriteNumber("step", state.Step.Value);
        WriteProposalOrNull(writer, "first", state.First, writeValue);
        WriteProposalOrNull(writer, "currentAggregate", state.CurrentAggregate, writeValue);
        WriteProposalOrNull(writer, "priorAggregate", state.PriorAggregate, writeValue);
        writer.WriteEndObject();
    }


    private static QuePaxaRecorderState<TValue> ReadRecorderState<TValue>(JsonElement element, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        int step = RequireProperty(element, "step", RecorderStateLabel).GetInt32();
        PrioritizedProposal<TValue>? first = ReadProposalOrNull(element, "first", RecorderStateLabel, readValue);
        PrioritizedProposal<TValue>? currentAggregate = ReadProposalOrNull(element, "currentAggregate", RecorderStateLabel, readValue);
        PrioritizedProposal<TValue>? priorAggregate = ReadProposalOrNull(element, "priorAggregate", RecorderStateLabel, readValue);

        return Construct(() => new QuePaxaRecorderState<TValue>(new RecorderStep(step), first, currentAggregate, priorAggregate));
    }


    private static void WriteRequest<TValue>(Utf8JsonWriter writer, RecordRequest<TValue> request, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartObject();
        writer.WriteNumber("step", request.Step.Value);
        WriteProposal(writer, "proposal", request.Proposal, writeValue);
        writer.WriteEndObject();
    }


    private static RecordRequest<TValue> ReadRequest<TValue>(JsonElement element, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        int step = RequireProperty(element, "step", RequestLabel).GetInt32();
        PrioritizedProposal<TValue> proposal = ReadProposal(RequireProperty(element, "proposal", RequestLabel), readValue);

        return Construct(() => new RecordRequest<TValue>(new RecorderStep(step), proposal));
    }


    private static void WriteReply<TValue>(Utf8JsonWriter writer, RecordReply<TValue> reply, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartObject();
        writer.WriteNumber("step", reply.Step.Value);
        WriteProposal(writer, "first", reply.First, writeValue);
        WriteProposalOrNull(writer, "priorAggregate", reply.PriorAggregate, writeValue);
        writer.WriteEndObject();
    }


    private static RecordReply<TValue> ReadReply<TValue>(JsonElement element, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        int step = RequireProperty(element, "step", ReplyLabel).GetInt32();
        PrioritizedProposal<TValue> first = ReadProposal(RequireProperty(element, "first", ReplyLabel), readValue);
        PrioritizedProposal<TValue>? prior = ReadProposalOrNull(element, "priorAggregate", ReplyLabel, readValue);

        return Construct(() => new RecordReply<TValue>(new RecorderStep(step), first, prior));
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


    private static void WriteProposal<TValue>(Utf8JsonWriter writer, string name, PrioritizedProposal<TValue> proposal, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
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


    private static PrioritizedProposal<TValue> ReadProposal<TValue>(JsonElement element, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        ulong priority = RequireProperty(element, "priority", ProposalLabel).GetUInt64();
        ProposerLane owner = ReadLane(RequireProperty(element, "owner", ProposalLabel));
        TValue value = readValue(RequireProperty(element, "value", ProposalLabel));

        return new PrioritizedProposal<TValue>(new ProposalKey(new ProposalPriority(priority), owner), value);
    }


    private static void WriteProposalOrNull<TValue>(Utf8JsonWriter writer, string name, PrioritizedProposal<TValue>? proposal, WriteValueDelegate<Utf8JsonWriter, TValue> writeValue)
    {
        //An absent slot is written as an explicit null rather than omitted, because the protocol reaches an
        //absent slot legitimately, a skipped step clearing the prior aggregate among the ways, and a required
        //field can be swept for by a missing-field test where an optional one cannot.
        if(proposal is { } present)
        {
            WriteProposal(writer, name, present, writeValue);
        }
        else
        {
            writer.WriteNull(name);
        }
    }


    private static PrioritizedProposal<TValue>? ReadProposalOrNull<TValue>(JsonElement element, string name, string label, ReadValueDelegate<JsonElement, TValue> readValue)
    {
        JsonElement slot = RequireProperty(element, name, label);

        return slot.ValueKind == JsonValueKind.Null ? null : ReadProposal(slot, readValue);
    }


    private static void WriteLane(Utf8JsonWriter writer, string name, ProposerLane lane)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteString("replica", Convert.ToHexStringLower(lane.Replica.AsSpan()));
        writer.WriteNumber("lane", lane.Lane);
        writer.WriteEndObject();
    }


    private static void WriteLaneOrNull(Utf8JsonWriter writer, string name, ProposerLane? lane)
    {
        //A leaderless instance is a derived fact rather than a missing one, so it is written as an explicit
        //null and stays distinguishable from a field the payload never carried.
        if(lane is { } present)
        {
            WriteLane(writer, name, present);
        }
        else
        {
            writer.WriteNull(name);
        }
    }


    private static ProposerLane? ReadLaneOrNull(JsonElement element, string name, string label)
    {
        JsonElement slot = RequireProperty(element, name, label);

        return slot.ValueKind == JsonValueKind.Null ? null : ReadLane(slot);
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
