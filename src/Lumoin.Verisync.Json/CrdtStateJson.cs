using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the CRDT state records, so a
/// host can persist them (a database row, a message table) or carry them over a Verisync message channel.
/// </summary>
/// <remarks>
/// <para>
/// The encoding is hand-written and explicit — no reflection, AOT- and trim-safe — and replica ids are
/// hex-encoded, matching <see cref="ConsensusMessageJson"/>. For the generic states the caller supplies
/// how to read and write <c>TValue</c>, since the value is application-defined.
/// </para>
/// <para>
/// The serialize delegates write into any <see cref="System.Buffers.IBufferWriter{T}"/>: a
/// <see cref="System.IO.Pipelines.PipeWriter"/> to stream into a channel, or an
/// <see cref="System.Buffers.ArrayBufferWriter{T}"/> to produce bytes for a host store.
/// </para>
/// </remarks>
public static class CrdtStateJson
{
    /// <summary>Creates a serializer for <see cref="GCounterState"/>.</summary>
    /// <returns>A serialize delegate.</returns>
    public static SerializeMessageDelegate<GCounterState> CreateGCounterStateSerializer()
    {
        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            WriteGCounterState(writer, state);
        };
    }


    /// <summary>Creates a deserializer for <see cref="GCounterState"/>.</summary>
    /// <returns>A deserialize delegate.</returns>
    public static DeserializeMessageDelegate<GCounterState> CreateGCounterStateDeserializer()
    {
        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadGCounterState(document.RootElement);
        };
    }


    /// <summary>Creates a serializer for <see cref="VectorClockState"/>.</summary>
    /// <returns>A serialize delegate.</returns>
    public static SerializeMessageDelegate<VectorClockState> CreateVectorClockStateSerializer()
    {
        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            WriteVectorClockState(writer, state);
        };
    }


    /// <summary>Creates a deserializer for <see cref="VectorClockState"/>.</summary>
    /// <returns>A deserialize delegate.</returns>
    public static DeserializeMessageDelegate<VectorClockState> CreateVectorClockStateDeserializer()
    {
        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadVectorClockState(document.RootElement);
        };
    }


    /// <summary>Creates a serializer for <see cref="PNCounterState"/>.</summary>
    /// <returns>A serialize delegate.</returns>
    public static SerializeMessageDelegate<PNCounterState> CreatePNCounterStateSerializer()
    {
        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WritePropertyName("increments");
            WriteGCounterState(writer, state.Increments);
            writer.WritePropertyName("decrements");
            WriteGCounterState(writer, state.Decrements);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="PNCounterState"/>.</summary>
    /// <returns>A deserialize delegate.</returns>
    public static DeserializeMessageDelegate<PNCounterState> CreatePNCounterStateDeserializer()
    {
        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            return new PNCounterState(
                ReadGCounterState(root.GetProperty("increments")),
                ReadGCounterState(root.GetProperty("decrements")));
        };
    }


    /// <summary>Creates a serializer for <see cref="LwwRegisterState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<LwwRegisterState<TValue>> CreateLwwRegisterStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WriteBoolean("hasValue", state.HasValue);
            writer.WritePropertyName("value");
            if(state.Value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writeValue(writer, state.Value);
            }

            writer.WriteNumber("utcTicks", state.UtcTicks);
            if(state.Writer.IsEmpty)
            {
                writer.WriteNull("writer");
            }
            else
            {
                writer.WriteString("writer", Convert.ToHexStringLower(state.Writer.AsSpan()));
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="LwwRegisterState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<LwwRegisterState<TValue>> CreateLwwRegisterStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            JsonElement value = root.GetProperty("value");
            JsonElement writerElement = root.GetProperty("writer");

            return new LwwRegisterState<TValue>(
                root.GetProperty("hasValue").GetBoolean(),
                value.ValueKind == JsonValueKind.Null ? default : readValue(value),
                root.GetProperty("utcTicks").GetInt64(),
                writerElement.ValueKind == JsonValueKind.Null
                    ? ImmutableArray<byte>.Empty
                    : FromHex(writerElement.GetString()!));
        };
    }


    /// <summary>Creates a serializer for <see cref="DottedVersionVectorSetState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The tagged value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<DottedVersionVectorSetState<TValue>> CreateDottedVersionVectorSetStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            WriteDottedVersionVectorSetState(writer, state, writeValue);
        };
    }


    /// <summary>Creates a deserializer for <see cref="DottedVersionVectorSetState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The tagged value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<DottedVersionVectorSetState<TValue>> CreateDottedVersionVectorSetStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadDottedVersionVectorSetState(document.RootElement, readValue);
        };
    }


    /// <summary>Creates a serializer for <see cref="OrSetState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<OrSetState<TValue>> CreateOrSetStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WritePropertyName("set");
            WriteDottedVersionVectorSetState(writer, state.Set, writeValue);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="OrSetState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<OrSetState<TValue>> CreateOrSetStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return new OrSetState<TValue>(ReadDottedVersionVectorSetState(document.RootElement.GetProperty("set"), readValue));
        };
    }


    /// <summary>Creates a serializer for <see cref="MvRegisterState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<MvRegisterState<TValue>> CreateMvRegisterStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WritePropertyName("entries");
            WriteDottedVersionVectorSetState(writer, state.Entries, writeValue);
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="MvRegisterState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The register value type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<MvRegisterState<TValue>> CreateMvRegisterStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return new MvRegisterState<TValue>(ReadDottedVersionVectorSetState(document.RootElement.GetProperty("entries"), readValue));
        };
    }


    /// <summary>Creates a serializer for <see cref="RgaState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RgaState<TValue>> CreateRgaStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WritePropertyName("context");
            WriteVectorClockState(writer, state.Context);

            writer.WriteStartArray("vertices");
            foreach(RgaVertexEntry<TValue> vertex in state.Vertices)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("id");
                WriteDotState(writer, vertex.Id);
                writer.WritePropertyName("predecessor");
                if(vertex.Predecessor is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteDotState(writer, vertex.Predecessor);
                }

                writer.WritePropertyName("value");
                writeValue(writer, vertex.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("tombstones");
            foreach(DotState tombstone in state.Tombstones)
            {
                WriteDotState(writer, tombstone);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="RgaState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RgaState<TValue>> CreateRgaStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            JsonElement verticesElement = root.GetProperty("vertices");
            ImmutableArray<RgaVertexEntry<TValue>>.Builder vertices = ImmutableArray.CreateBuilder<RgaVertexEntry<TValue>>(verticesElement.GetArrayLength());
            foreach(JsonElement vertex in verticesElement.EnumerateArray())
            {
                JsonElement predecessor = vertex.GetProperty("predecessor");
                vertices.Add(new RgaVertexEntry<TValue>(
                    ReadDotState(vertex.GetProperty("id")),
                    predecessor.ValueKind == JsonValueKind.Null ? null : ReadDotState(predecessor),
                    readValue(vertex.GetProperty("value"))));
            }

            JsonElement tombstonesElement = root.GetProperty("tombstones");
            ImmutableArray<DotState>.Builder tombstones = ImmutableArray.CreateBuilder<DotState>(tombstonesElement.GetArrayLength());
            foreach(JsonElement tombstone in tombstonesElement.EnumerateArray())
            {
                tombstones.Add(ReadDotState(tombstone));
            }

            return new RgaState<TValue>(
                ReadVectorClockState(root.GetProperty("context")),
                vertices.ToImmutable(),
                tombstones.ToImmutable());
        };
    }


    private static void WriteGCounterState(Utf8JsonWriter writer, GCounterState state)
    {
        writer.WriteStartObject();
        WriteReplicaCounterEntries(writer, state.Entries);
        writer.WriteEndObject();
    }


    private static GCounterState ReadGCounterState(JsonElement element)
    {
        return new GCounterState(ReadReplicaCounterEntries(element));
    }


    private static void WriteVectorClockState(Utf8JsonWriter writer, VectorClockState state)
    {
        writer.WriteStartObject();
        WriteReplicaCounterEntries(writer, state.Entries);
        writer.WriteEndObject();
    }


    private static VectorClockState ReadVectorClockState(JsonElement element)
    {
        return new VectorClockState(ReadReplicaCounterEntries(element));
    }


    private static void WriteReplicaCounterEntries(Utf8JsonWriter writer, ImmutableArray<ReplicaCounterEntry> entries)
    {
        writer.WriteStartArray("entries");
        foreach(ReplicaCounterEntry entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteString("replica", Convert.ToHexStringLower(entry.Replica.AsSpan()));
            writer.WriteNumber("count", entry.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }


    private static ImmutableArray<ReplicaCounterEntry> ReadReplicaCounterEntries(JsonElement element)
    {
        JsonElement entriesElement = element.GetProperty("entries");
        ImmutableArray<ReplicaCounterEntry>.Builder entries = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(entriesElement.GetArrayLength());
        foreach(JsonElement entry in entriesElement.EnumerateArray())
        {
            entries.Add(new ReplicaCounterEntry(
                FromHex(entry.GetProperty("replica").GetString()!),
                entry.GetProperty("count").GetInt32()));
        }

        return entries.ToImmutable();
    }


    private static void WriteDottedVersionVectorSetState<TValue>(Utf8JsonWriter writer, DottedVersionVectorSetState<TValue> state, Action<Utf8JsonWriter, TValue> writeValue)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("context");
        WriteVectorClockState(writer, state.Context);

        writer.WriteStartArray("entries");
        foreach(DottedEntry<TValue> entry in state.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("replica", Convert.ToHexStringLower(entry.Replica.AsSpan()));
            writer.WriteNumber("counter", entry.Counter);
            writer.WritePropertyName("value");
            writeValue(writer, entry.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static DottedVersionVectorSetState<TValue> ReadDottedVersionVectorSetState<TValue>(JsonElement element, Func<JsonElement, TValue> readValue)
    {
        JsonElement entriesElement = element.GetProperty("entries");
        ImmutableArray<DottedEntry<TValue>>.Builder entries = ImmutableArray.CreateBuilder<DottedEntry<TValue>>(entriesElement.GetArrayLength());
        foreach(JsonElement entry in entriesElement.EnumerateArray())
        {
            entries.Add(new DottedEntry<TValue>(
                FromHex(entry.GetProperty("replica").GetString()!),
                entry.GetProperty("counter").GetInt32(),
                readValue(entry.GetProperty("value"))));
        }

        return new DottedVersionVectorSetState<TValue>(
            ReadVectorClockState(element.GetProperty("context")),
            entries.ToImmutable());
    }


    private static void WriteDotState(Utf8JsonWriter writer, DotState dot)
    {
        writer.WriteStartObject();
        writer.WriteString("replica", Convert.ToHexStringLower(dot.Replica.AsSpan()));
        writer.WriteNumber("counter", dot.Counter);
        writer.WriteEndObject();
    }


    private static DotState ReadDotState(JsonElement element)
    {
        return new DotState(
            FromHex(element.GetProperty("replica").GetString()!),
            element.GetProperty("counter").GetInt32());
    }


    private static ImmutableArray<byte> FromHex(string hex)
    {
        //The decoded array is fresh and never aliased, so wrapping it without a copy is safe.
        return ImmutableCollectionsMarshal.AsImmutableArray(Convert.FromHexString(hex));
    }
}
