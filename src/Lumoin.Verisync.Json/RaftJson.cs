using Lumoin.Verisync.Core;
using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the Raft wire envelope and the
/// node's durable state, so they can cross a Verisync message channel (in-memory pipe or socket) or be
/// persisted by a host.
/// </summary>
/// <remarks>
/// The encoding mirrors <see cref="ConsensusMessageJson"/>: hand-written and explicit — no reflection, AOT-
/// and trim-safe — with a <c>type</c> discriminator on the envelope and hex-encoded replica ids. The caller
/// supplies how to read and write <c>TCommand</c>, since the command is application-defined. The decoders
/// validate fail-closed (<see cref="JsonException"/>) on anything no honest sender produces — an unknown
/// type, malformed or wrong-length replica ids, negative terms or indices, log entry terms below one, or a
/// negative match index; relational rules (decreasing log terms, a vote that is not a member) stay in
/// <see cref="RaftNode{TCommand}.FromState"/>, the single place that owns them.
/// </remarks>
public static class RaftJson
{
    /// <summary>Creates a serializer for <see cref="RaftEnvelope{TCommand}"/>.</summary>
    /// <typeparam name="TCommand">The application command type.</typeparam>
    /// <param name="writeCommand">Writes a command to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeCommand"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RaftEnvelope<TCommand>> CreateEnvelopeSerializer<TCommand>(Action<Utf8JsonWriter, TCommand> writeCommand)
    {
        ArgumentNullException.ThrowIfNull(writeCommand);

        return (envelope, output) =>
        {
            //The wire format has exactly one payload slot, so an envelope carrying any other number is
            //unrepresentable and fails closed before a byte is written.
            int payloadCount = (envelope.VoteRequest is null ? 0 : 1)
                + (envelope.VoteReply is null ? 0 : 1)
                + (envelope.AppendRequest is null ? 0 : 1)
                + (envelope.AppendReply is null ? 0 : 1);
            if(payloadCount != 1)
            {
                throw new JsonException($"A Raft envelope must carry exactly one payload, but it carries {payloadCount}.");
            }

            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteString("from", Convert.ToHexStringLower(envelope.From.AsSpan()));

            if(envelope.VoteRequest is { } voteRequest)
            {
                writer.WriteString("type", "voteRequest");
                writer.WritePropertyName("payload");
                WriteVoteRequest(writer, voteRequest);
            }
            else if(envelope.VoteReply is { } voteReply)
            {
                writer.WriteString("type", "voteReply");
                writer.WritePropertyName("payload");
                WriteVoteReply(writer, voteReply);
            }
            else if(envelope.AppendRequest is { } appendRequest)
            {
                writer.WriteString("type", "appendRequest");
                writer.WritePropertyName("payload");
                WriteAppendRequest(writer, appendRequest, writeCommand);
            }
            else if(envelope.AppendReply is { } appendReply)
            {
                writer.WriteString("type", "appendReply");
                writer.WritePropertyName("payload");
                WriteAppendReply(writer, appendReply);
            }
            else
            {
                throw new JsonException("A Raft envelope must carry exactly one payload, but it carries none.");
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="RaftEnvelope{TCommand}"/>.</summary>
    /// <typeparam name="TCommand">The application command type.</typeparam>
    /// <param name="readCommand">Reads a command from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readCommand"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RaftEnvelope<TCommand>> CreateEnvelopeDeserializer<TCommand>(Func<JsonElement, TCommand> readCommand)
    {
        ArgumentNullException.ThrowIfNull(readCommand);

        return JsonMessageGuard.FailClosed<RaftEnvelope<TCommand>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            ReplicaId from = ReadReplica(RequireProperty(root, "from", "A Raft envelope"));
            string type = RequireProperty(root, "type", "A Raft envelope").GetString()!;
            JsonElement payloadElement = RequireProperty(root, "payload", "A Raft envelope");

            return type switch
            {
                "voteRequest" => RaftEnvelope<TCommand>.ForVoteRequest(from, ReadVoteRequest(payloadElement)),
                "voteReply" => RaftEnvelope<TCommand>.ForVoteReply(from, ReadVoteReply(payloadElement)),
                "appendRequest" => RaftEnvelope<TCommand>.ForAppendRequest(from, ReadAppendRequest(payloadElement, readCommand)),
                "appendReply" => RaftEnvelope<TCommand>.ForAppendReply(from, ReadAppendReply(payloadElement)),
                _ => throw new JsonException($"Unknown Raft envelope type '{type}'.")
            };
        });
    }


    /// <summary>Creates a serializer for <see cref="RaftNodeState{TCommand}"/>.</summary>
    /// <typeparam name="TCommand">The application command type.</typeparam>
    /// <param name="writeCommand">Writes a command to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeCommand"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RaftNodeState<TCommand>> CreateNodeStateSerializer<TCommand>(Action<Utf8JsonWriter, TCommand> writeCommand)
    {
        ArgumentNullException.ThrowIfNull(writeCommand);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("currentTerm", state.CurrentTerm);

            if(state.VotedFor.IsDefaultOrEmpty)
            {
                writer.WriteNull("votedFor");
            }
            else
            {
                writer.WriteString("votedFor", Convert.ToHexStringLower(state.VotedFor.AsSpan()));
            }

            WriteLog(writer, state.Log, writeCommand);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="RaftNodeState{TCommand}"/>.</summary>
    /// <typeparam name="TCommand">The application command type.</typeparam>
    /// <param name="readCommand">Reads a command from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readCommand"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RaftNodeState<TCommand>> CreateNodeStateDeserializer<TCommand>(Func<JsonElement, TCommand> readCommand)
    {
        ArgumentNullException.ThrowIfNull(readCommand);

        return JsonMessageGuard.FailClosed<RaftNodeState<TCommand>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            long currentTerm = ReadNonNegativeLong(RequireProperty(root, "currentTerm", "A Raft node state"), "A current term");

            JsonElement votedForElement = RequireProperty(root, "votedFor", "A Raft node state");
            ImmutableArray<byte> votedFor = votedForElement.ValueKind == JsonValueKind.Null
                ? ImmutableArray<byte>.Empty
                : ReadReplicaBytes(votedForElement.GetString()!);

            return new RaftNodeState<TCommand>(currentTerm, votedFor, ReadEntries(RequireProperty(root, "log", "A Raft node state"), readCommand));
        });
    }


    private static void WriteVoteRequest(Utf8JsonWriter writer, RequestVoteRequest request)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", request.Term);
        writer.WriteString("candidateId", Convert.ToHexStringLower(request.CandidateId.AsSpan()));
        writer.WriteNumber("lastLogIndex", request.LastLogIndex);
        writer.WriteNumber("lastLogTerm", request.LastLogTerm);
        writer.WriteEndObject();
    }


    private static RequestVoteRequest ReadVoteRequest(JsonElement element)
    {
        return new RequestVoteRequest(
            ReadNonNegativeLong(RequireProperty(element, "term", "A vote request"), "A term"),
            ReadReplica(RequireProperty(element, "candidateId", "A vote request")),
            ReadNonNegativeLong(RequireProperty(element, "lastLogIndex", "A vote request"), "A last log index"),
            ReadNonNegativeLong(RequireProperty(element, "lastLogTerm", "A vote request"), "A last log term"));
    }


    private static void WriteVoteReply(Utf8JsonWriter writer, RequestVoteReply reply)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", reply.Term);
        writer.WriteBoolean("voteGranted", reply.VoteGranted);
        writer.WriteEndObject();
    }


    private static RequestVoteReply ReadVoteReply(JsonElement element)
    {
        return new RequestVoteReply(
            ReadNonNegativeLong(RequireProperty(element, "term", "A vote reply"), "A term"),
            RequireProperty(element, "voteGranted", "A vote reply").GetBoolean());
    }


    private static void WriteAppendRequest<TCommand>(Utf8JsonWriter writer, AppendEntriesRequest<TCommand> request, Action<Utf8JsonWriter, TCommand> writeCommand)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", request.Term);
        writer.WriteString("leaderId", Convert.ToHexStringLower(request.LeaderId.AsSpan()));
        writer.WriteNumber("prevLogIndex", request.PrevLogIndex);
        writer.WriteNumber("prevLogTerm", request.PrevLogTerm);

        writer.WriteStartArray("entries");
        foreach(RaftLogEntry<TCommand> entry in request.Entries)
        {
            WriteLogEntry(writer, entry, writeCommand);
        }

        writer.WriteEndArray();

        writer.WriteNumber("leaderCommit", request.LeaderCommit);
        writer.WriteEndObject();
    }


    private static AppendEntriesRequest<TCommand> ReadAppendRequest<TCommand>(JsonElement element, Func<JsonElement, TCommand> readCommand)
    {
        return new AppendEntriesRequest<TCommand>(
            ReadNonNegativeLong(RequireProperty(element, "term", "An append request"), "A term"),
            ReadReplica(RequireProperty(element, "leaderId", "An append request")),
            ReadNonNegativeLong(RequireProperty(element, "prevLogIndex", "An append request"), "A previous log index"),
            ReadNonNegativeLong(RequireProperty(element, "prevLogTerm", "An append request"), "A previous log term"),
            ReadEntries(RequireProperty(element, "entries", "An append request"), readCommand),
            ReadNonNegativeLong(RequireProperty(element, "leaderCommit", "An append request"), "A leader commit index"));
    }


    private static void WriteAppendReply(Utf8JsonWriter writer, AppendEntriesReply reply)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", reply.Term);
        writer.WriteBoolean("success", reply.Success);
        writer.WriteNumber("matchIndex", reply.MatchIndex);
        writer.WriteEndObject();
    }


    private static AppendEntriesReply ReadAppendReply(JsonElement element)
    {
        return new AppendEntriesReply(
            ReadNonNegativeLong(RequireProperty(element, "term", "An append reply"), "A term"),
            RequireProperty(element, "success", "An append reply").GetBoolean(),
            ReadNonNegativeLong(RequireProperty(element, "matchIndex", "An append reply"), "A match index"));
    }


    private static void WriteLog<TCommand>(Utf8JsonWriter writer, ImmutableArray<RaftLogEntry<TCommand>> log, Action<Utf8JsonWriter, TCommand> writeCommand)
    {
        writer.WriteStartArray("log");
        if(!log.IsDefault)
        {
            foreach(RaftLogEntry<TCommand> entry in log)
            {
                WriteLogEntry(writer, entry, writeCommand);
            }
        }

        writer.WriteEndArray();
    }


    private static void WriteLogEntry<TCommand>(Utf8JsonWriter writer, RaftLogEntry<TCommand> entry, Action<Utf8JsonWriter, TCommand> writeCommand)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", entry.Term);
        writer.WritePropertyName("command");
        writeCommand(writer, entry.Command);
        writer.WriteEndObject();
    }


    private static ImmutableArray<RaftLogEntry<TCommand>> ReadEntries<TCommand>(JsonElement element, Func<JsonElement, TCommand> readCommand)
    {
        ImmutableArray<RaftLogEntry<TCommand>>.Builder entries = ImmutableArray.CreateBuilder<RaftLogEntry<TCommand>>(element.GetArrayLength());
        foreach(JsonElement entry in element.EnumerateArray())
        {
            long term = RequireProperty(entry, "term", "A log entry").GetInt64();
            if(term < 1)
            {
                throw new JsonException($"A log entry term is at least one, got {term}.");
            }

            entries.Add(new RaftLogEntry<TCommand>(term, readCommand(RequireProperty(entry, "command", "A log entry"))));
        }

        return entries.MoveToImmutable();
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


    private static long ReadNonNegativeLong(JsonElement element, string label)
    {
        long value = element.GetInt64();
        if(value < 0)
        {
            throw new JsonException($"{label} cannot be negative, got {value}.");
        }

        return value;
    }


    private static ReplicaId ReadReplica(JsonElement element)
    {
        return ReplicaId.FromSpan(ReadReplicaBytes(element.GetString()!).AsSpan());
    }


    private static ImmutableArray<byte> ReadReplicaBytes(string hex)
    {
        //The payload may come from an untrusted peer; the hex and the decoded length are validated before the
        //bytes are allowed to act as a replica identity.
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch(FormatException exception)
        {
            throw new JsonException("A replica id must be hex-encoded.", exception);
        }

        if(bytes.Length != ReplicaId.Size)
        {
            throw new JsonException($"A replica id must be {ReplicaId.Size} bytes, got {bytes.Length}.");
        }

        //The decoded array is fresh and never aliased, so wrapping it without a copy is safe.
        return ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    }
}
