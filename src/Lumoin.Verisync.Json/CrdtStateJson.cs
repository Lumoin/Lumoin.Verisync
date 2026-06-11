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
                    : ReadReplicaBytes(writerElement.GetString()!));
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


    /// <summary>Creates a serializer for <see cref="OffsetAnchoredSequenceState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<OffsetAnchoredSequenceState<TValue>> CreateOffsetAnchoredSequenceStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();

            writer.WriteStartArray("base");
            foreach(TValue value in state.Base)
            {
                writeValue(writer, value);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("removedBaseOffsets");
            foreach(int offset in state.RemovedBaseOffsets)
            {
                writer.WriteNumberValue(offset);
            }

            writer.WriteEndArray();

            writer.WritePropertyName("context");
            WriteVectorClockState(writer, state.Context);

            writer.WriteStartArray("vertices");
            foreach(OffsetVertexEntry<TValue> vertex in state.Vertices)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("id");
                WriteDotState(writer, vertex.Id);
                writer.WritePropertyName("anchor");
                WriteOffsetAnchorState(writer, vertex.Anchor);
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

            writer.WriteStartArray("compactedDotAnchors");
            foreach(OffsetTranslationEntry entry in state.CompactedDotAnchors)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("dropped");
                WriteDotState(writer, entry.Dropped);
                writer.WritePropertyName("target");
                WriteOffsetAnchorState(writer, entry.Target);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("compactedBaseOffsets");
            foreach(OffsetRebaseEntry entry in state.CompactedBaseOffsets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("previous", entry.PreviousOffset);
                writer.WriteNumber("current", entry.CurrentOffset);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="OffsetAnchoredSequenceState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<OffsetAnchoredSequenceState<TValue>> CreateOffsetAnchoredSequenceStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            JsonElement baseElement = root.GetProperty("base");
            ImmutableArray<TValue>.Builder baseValues = ImmutableArray.CreateBuilder<TValue>(baseElement.GetArrayLength());
            foreach(JsonElement value in baseElement.EnumerateArray())
            {
                baseValues.Add(readValue(value));
            }

            JsonElement removedElement = root.GetProperty("removedBaseOffsets");
            ImmutableArray<int>.Builder removed = ImmutableArray.CreateBuilder<int>(removedElement.GetArrayLength());
            foreach(JsonElement offset in removedElement.EnumerateArray())
            {
                removed.Add(ReadNonNegative(offset, "A removed base offset"));
            }

            JsonElement verticesElement = root.GetProperty("vertices");
            ImmutableArray<OffsetVertexEntry<TValue>>.Builder vertices = ImmutableArray.CreateBuilder<OffsetVertexEntry<TValue>>(verticesElement.GetArrayLength());
            foreach(JsonElement vertex in verticesElement.EnumerateArray())
            {
                vertices.Add(new OffsetVertexEntry<TValue>(
                    ReadDotState(vertex.GetProperty("id")),
                    ReadOffsetAnchorState(vertex.GetProperty("anchor")),
                    readValue(vertex.GetProperty("value"))));
            }

            JsonElement tombstonesElement = root.GetProperty("tombstones");
            ImmutableArray<DotState>.Builder tombstones = ImmutableArray.CreateBuilder<DotState>(tombstonesElement.GetArrayLength());
            foreach(JsonElement tombstone in tombstonesElement.EnumerateArray())
            {
                tombstones.Add(ReadDotState(tombstone));
            }

            JsonElement dotAnchorsElement = root.GetProperty("compactedDotAnchors");
            ImmutableArray<OffsetTranslationEntry>.Builder dotAnchors = ImmutableArray.CreateBuilder<OffsetTranslationEntry>(dotAnchorsElement.GetArrayLength());
            foreach(JsonElement entry in dotAnchorsElement.EnumerateArray())
            {
                dotAnchors.Add(new OffsetTranslationEntry(
                    ReadDotState(entry.GetProperty("dropped")),
                    ReadOffsetAnchorState(entry.GetProperty("target"))));
            }

            JsonElement baseOffsetsElement = root.GetProperty("compactedBaseOffsets");
            ImmutableArray<OffsetRebaseEntry>.Builder baseOffsets = ImmutableArray.CreateBuilder<OffsetRebaseEntry>(baseOffsetsElement.GetArrayLength());
            foreach(JsonElement entry in baseOffsetsElement.EnumerateArray())
            {
                baseOffsets.Add(new OffsetRebaseEntry(
                    ReadNonNegative(entry.GetProperty("previous"), "A compacted base offset's previous offset"),
                    ReadNonNegative(entry.GetProperty("current"), "A compacted base offset's current offset")));
            }

            return new OffsetAnchoredSequenceState<TValue>(
                baseValues.ToImmutable(),
                removed.ToImmutable(),
                ReadVectorClockState(root.GetProperty("context")),
                vertices.ToImmutable(),
                tombstones.ToImmutable(),
                dotAnchors.ToImmutable(),
                baseOffsets.ToImmutable());
        };
    }


    /// <summary>Creates a serializer for <see cref="RgaRunState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="writeValue">Writes a value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeValue"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<RgaRunState<TValue>> CreateRgaRunStateSerializer<TValue>(Action<Utf8JsonWriter, TValue> writeValue)
    {
        ArgumentNullException.ThrowIfNull(writeValue);

        return (state, output) =>
        {
            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();
            writer.WritePropertyName("context");
            WriteVectorClockState(writer, state.Context);

            writer.WriteStartArray("runs");
            foreach(RgaRunEntry<TValue> run in state.Runs)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("first");
                WriteDotState(writer, run.First);
                writer.WritePropertyName("predecessor");
                if(run.Predecessor is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    WriteDotState(writer, run.Predecessor);
                }

                writer.WriteStartArray("values");
                foreach(TValue value in run.Values)
                {
                    writeValue(writer, value);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("tombstoneSpans");
            foreach(RgaTombstoneSpan span in state.TombstoneSpans)
            {
                writer.WriteStartObject();
                writer.WriteString("replica", Convert.ToHexStringLower(span.Replica.AsSpan()));
                writer.WriteNumber("from", span.FromCounter);
                writer.WriteNumber("to", span.ToCounter);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("translations");
            foreach(RgaTranslationEntry translation in state.Translations)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("dropped");
                WriteDotState(writer, translation.Dropped);
                writer.WritePropertyName("target");
                WriteDotState(writer, translation.Target);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a deserializer for <see cref="RgaRunState{TValue}"/>.</summary>
    /// <typeparam name="TValue">The element type.</typeparam>
    /// <param name="readValue">Reads a value from a JSON element.</param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="readValue"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<RgaRunState<TValue>> CreateRgaRunStateDeserializer<TValue>(Func<JsonElement, TValue> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        return payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            JsonElement runsElement = root.GetProperty("runs");
            ImmutableArray<RgaRunEntry<TValue>>.Builder runs = ImmutableArray.CreateBuilder<RgaRunEntry<TValue>>(runsElement.GetArrayLength());
            foreach(JsonElement run in runsElement.EnumerateArray())
            {
                JsonElement predecessor = run.GetProperty("predecessor");
                JsonElement valuesElement = run.GetProperty("values");
                if(valuesElement.GetArrayLength() == 0)
                {
                    throw new JsonException("A run must carry at least one value.");
                }

                ImmutableArray<TValue>.Builder values = ImmutableArray.CreateBuilder<TValue>(valuesElement.GetArrayLength());
                foreach(JsonElement value in valuesElement.EnumerateArray())
                {
                    values.Add(readValue(value));
                }

                runs.Add(new RgaRunEntry<TValue>(
                    ReadDotState(run.GetProperty("first")),
                    predecessor.ValueKind == JsonValueKind.Null ? null : ReadDotState(predecessor),
                    values.ToImmutable()));
            }

            JsonElement spansElement = root.GetProperty("tombstoneSpans");
            ImmutableArray<RgaTombstoneSpan>.Builder spans = ImmutableArray.CreateBuilder<RgaTombstoneSpan>(spansElement.GetArrayLength());
            foreach(JsonElement span in spansElement.EnumerateArray())
            {
                int from = span.GetProperty("from").GetInt32();
                int to = span.GetProperty("to").GetInt32();
                if(from < 1 || to < from)
                {
                    throw new JsonException($"A tombstone span must satisfy 1 <= from <= to, got from {from}, to {to}.");
                }

                spans.Add(new RgaTombstoneSpan(ReadReplicaBytes(span.GetProperty("replica").GetString()!), from, to));
            }

            JsonElement translationsElement = root.GetProperty("translations");
            ImmutableArray<RgaTranslationEntry>.Builder translations = ImmutableArray.CreateBuilder<RgaTranslationEntry>(translationsElement.GetArrayLength());
            foreach(JsonElement translation in translationsElement.EnumerateArray())
            {
                translations.Add(new RgaTranslationEntry(
                    ReadDotState(translation.GetProperty("dropped")),
                    ReadDotState(translation.GetProperty("target"))));
            }

            return new RgaRunState<TValue>(
                ReadVectorClockState(root.GetProperty("context")),
                runs.ToImmutable(),
                spans.ToImmutable(),
                translations.ToImmutable());
        };
    }


    private static void WriteOffsetAnchorState(Utf8JsonWriter writer, OffsetAnchorState anchor)
    {
        writer.WriteStartObject();
        writer.WriteNumber("baseOffset", anchor.BaseOffset);
        writer.WritePropertyName("liveId");
        if(anchor.LiveId is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WriteDotState(writer, anchor.LiveId);
        }

        writer.WriteEndObject();
    }


    private static OffsetAnchorState ReadOffsetAnchorState(JsonElement element)
    {
        int baseOffset = element.GetProperty("baseOffset").GetInt32();
        if(baseOffset < -1)
        {
            throw new JsonException($"An anchor base offset is at least -1, got {baseOffset}.");
        }

        JsonElement liveId = element.GetProperty("liveId");
        if(liveId.ValueKind == JsonValueKind.Null)
        {
            return new OffsetAnchorState(baseOffset, null);
        }

        if(baseOffset != -1)
        {
            throw new JsonException($"A live anchor must carry base offset -1, got {baseOffset}.");
        }

        return new OffsetAnchorState(baseOffset, ReadDotState(liveId));
    }


    private static int ReadNonNegative(JsonElement element, string label)
    {
        int value = element.GetInt32();
        if(value < 0)
        {
            throw new JsonException($"{label} cannot be negative, got {value}.");
        }

        return value;
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
            int count = entry.GetProperty("count").GetInt32();
            if(count < 0)
            {
                throw new JsonException($"A replica counter cannot be negative, got {count}.");
            }

            entries.Add(new ReplicaCounterEntry(ReadReplicaBytes(entry.GetProperty("replica").GetString()!), count));
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
                ReadReplicaBytes(entry.GetProperty("replica").GetString()!),
                ReadDotCounter(entry),
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
            ReadReplicaBytes(element.GetProperty("replica").GetString()!),
            ReadDotCounter(element));
    }


    private static int ReadDotCounter(JsonElement element)
    {
        int counter = element.GetProperty("counter").GetInt32();
        if(counter < 1)
        {
            throw new JsonException($"A dot counter is at least one, got {counter}.");
        }

        return counter;
    }


    private static ImmutableArray<byte> ReadReplicaBytes(string hex)
    {
        //The payload may come from an untrusted peer; the hex and the decoded length are validated
        //before the bytes are allowed to act as a replica identity.
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
