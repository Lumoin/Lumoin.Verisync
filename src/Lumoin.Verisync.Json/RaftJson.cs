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
/// validate fail-closed (<see cref="JsonException"/>) on the two things only the encoding can be wrong
/// about, an unknown envelope type and a malformed or wrong-length replica id, and add no rule of their own.
/// Every other value is handed to its domain constructor here, so an out-of-range term or index is refused by
/// <see cref="Term"/> and <see cref="LogIndex"/> and a log entry tagged below <see cref="Term.First"/> by
/// <see cref="RaftLogEntry{TCommand}"/>, each surfacing with the validator's own message as the inner
/// exception. Relational rules (decreasing log terms, a vote that is not a member) stay in
/// <see cref="RaftNode{TCommand}.FromState"/>, the single place that owns them.
/// </remarks>
public static class RaftJson
{
    /// <summary>Creates a serializer for <see cref="RaftEnvelope{TCommand}"/>.</summary>
    /// <typeparam name="TCommand">The application command type.</typeparam>
    /// <param name="writeCommand">Writes a command to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeCommand"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RaftEnvelope<TCommand>> CreateEnvelopeSerializer<TCommand>(WriteValueDelegate<Utf8JsonWriter, TCommand> writeCommand)
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
    public static DeserializeMessageDelegate<RaftEnvelope<TCommand>> CreateEnvelopeDeserializer<TCommand>(ReadValueDelegate<JsonElement, TCommand> readCommand)
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
    public static SerializeMessageDelegate<RaftNodeState<TCommand>> CreateNodeStateSerializer<TCommand>(WriteValueDelegate<Utf8JsonWriter, TCommand> writeCommand)
    {
        ArgumentNullException.ThrowIfNull(writeCommand);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteNumber("currentTerm", state.CurrentTerm.Value);

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
    public static DeserializeMessageDelegate<RaftNodeState<TCommand>> CreateNodeStateDeserializer<TCommand>(ReadValueDelegate<JsonElement, TCommand> readCommand)
    {
        ArgumentNullException.ThrowIfNull(readCommand);

        return JsonMessageGuard.FailClosed<RaftNodeState<TCommand>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            Term currentTerm = ReadTerm(RequireProperty(root, "currentTerm", "A Raft node state"));

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
        writer.WriteNumber("term", request.Term.Value);
        writer.WriteString("candidateId", Convert.ToHexStringLower(request.CandidateId.AsSpan()));
        writer.WriteNumber("lastLogIndex", request.LastLogIndex.Value);
        writer.WriteNumber("lastLogTerm", request.LastLogTerm.Value);
        writer.WriteEndObject();
    }


    private static RequestVoteRequest ReadVoteRequest(JsonElement element)
    {
        return new RequestVoteRequest(
            ReadTerm(RequireProperty(element, "term", "A vote request")),
            ReadReplica(RequireProperty(element, "candidateId", "A vote request")),
            ReadLogIndex(RequireProperty(element, "lastLogIndex", "A vote request")),
            ReadTerm(RequireProperty(element, "lastLogTerm", "A vote request")));
    }


    private static void WriteVoteReply(Utf8JsonWriter writer, RequestVoteReply reply)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", reply.Term.Value);
        writer.WriteBoolean("voteGranted", reply.VoteGranted);
        writer.WriteEndObject();
    }


    private static RequestVoteReply ReadVoteReply(JsonElement element)
    {
        return new RequestVoteReply(
            ReadTerm(RequireProperty(element, "term", "A vote reply")),
            RequireProperty(element, "voteGranted", "A vote reply").GetBoolean());
    }


    private static void WriteAppendRequest<TCommand>(Utf8JsonWriter writer, AppendEntriesRequest<TCommand> request, WriteValueDelegate<Utf8JsonWriter, TCommand> writeCommand)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", request.Term.Value);
        writer.WriteString("leaderId", Convert.ToHexStringLower(request.LeaderId.AsSpan()));
        writer.WriteNumber("prevLogIndex", request.PrevLogIndex.Value);
        writer.WriteNumber("prevLogTerm", request.PrevLogTerm.Value);

        writer.WriteStartArray("entries");
        foreach(RaftLogEntry<TCommand> entry in request.Entries)
        {
            WriteLogEntry(writer, entry, writeCommand);
        }

        writer.WriteEndArray();

        writer.WriteNumber("leaderCommit", request.LeaderCommit.Value);
        writer.WriteEndObject();
    }


    private static AppendEntriesRequest<TCommand> ReadAppendRequest<TCommand>(JsonElement element, ReadValueDelegate<JsonElement, TCommand> readCommand)
    {
        return new AppendEntriesRequest<TCommand>(
            ReadTerm(RequireProperty(element, "term", "An append request")),
            ReadReplica(RequireProperty(element, "leaderId", "An append request")),
            ReadLogIndex(RequireProperty(element, "prevLogIndex", "An append request")),
            ReadTerm(RequireProperty(element, "prevLogTerm", "An append request")),
            ReadEntries(RequireProperty(element, "entries", "An append request"), readCommand),
            ReadLogIndex(RequireProperty(element, "leaderCommit", "An append request")));
    }


    private static void WriteAppendReply(Utf8JsonWriter writer, AppendEntriesReply reply)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", reply.Term.Value);
        writer.WriteBoolean("success", reply.Success);
        writer.WriteNumber("matchIndex", reply.MatchIndex.Value);
        writer.WriteEndObject();
    }


    private static AppendEntriesReply ReadAppendReply(JsonElement element)
    {
        return new AppendEntriesReply(
            ReadTerm(RequireProperty(element, "term", "An append reply")),
            RequireProperty(element, "success", "An append reply").GetBoolean(),
            ReadLogIndex(RequireProperty(element, "matchIndex", "An append reply")));
    }


    private static void WriteLog<TCommand>(Utf8JsonWriter writer, ImmutableArray<RaftLogEntry<TCommand>> log, WriteValueDelegate<Utf8JsonWriter, TCommand> writeCommand)
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


    private static void WriteLogEntry<TCommand>(Utf8JsonWriter writer, RaftLogEntry<TCommand> entry, WriteValueDelegate<Utf8JsonWriter, TCommand> writeCommand)
    {
        writer.WriteStartObject();
        writer.WriteNumber("term", entry.Term.Value);
        writer.WritePropertyName("command");
        writeCommand(writer, entry.Command);
        writer.WriteEndObject();
    }


    private static ImmutableArray<RaftLogEntry<TCommand>> ReadEntries<TCommand>(JsonElement element, ReadValueDelegate<JsonElement, TCommand> readCommand)
    {
        ImmutableArray<RaftLogEntry<TCommand>>.Builder entries = ImmutableArray.CreateBuilder<RaftLogEntry<TCommand>>(element.GetArrayLength());
        foreach(JsonElement entry in element.EnumerateArray())
        {
            long term = RequireProperty(entry, "term", "A log entry").GetInt64();
            TCommand command = readCommand(RequireProperty(entry, "command", "A log entry"));

            entries.Add(Construct(() => new RaftLogEntry<TCommand>(new Term(term), command)));
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


    private static Term ReadTerm(JsonElement element)
    {
        long value = element.GetInt64();

        return Construct(() => new Term(value));
    }


    private static LogIndex ReadLogIndex(JsonElement element)
    {
        long value = element.GetInt64();

        return Construct(() => new LogIndex(value));
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
            throw new JsonException("The payload carries values a Raft message rejects.", exception);
        }
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
