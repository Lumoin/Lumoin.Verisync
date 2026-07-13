using Lumoin.Verisync.Core;
using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;

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
        return JsonMessageGuard.FailClosed<GCounterState>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadGCounterState(document.RootElement);
        });
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
        return JsonMessageGuard.FailClosed<VectorClockState>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadVectorClockState(document.RootElement);
        });
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
        return JsonMessageGuard.FailClosed<PNCounterState>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            return new PNCounterState(
                ReadGCounterState(RequireProperty(root, "increments", "A PN-counter")),
                ReadGCounterState(RequireProperty(root, "decrements", "A PN-counter")));
        });
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

        return JsonMessageGuard.FailClosed<LwwRegisterState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            JsonElement value = RequireProperty(root, "value", "An LWW register");
            JsonElement writerElement = RequireProperty(root, "writer", "An LWW register");

            return new LwwRegisterState<TValue>(
                RequireProperty(root, "hasValue", "An LWW register").GetBoolean(),
                value.ValueKind == JsonValueKind.Null ? default : readValue(value),
                RequireProperty(root, "utcTicks", "An LWW register").GetInt64(),
                writerElement.ValueKind == JsonValueKind.Null
                    ? ImmutableArray<byte>.Empty
                    : ReadReplicaBytes(writerElement.GetString()!));
        });
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

        return JsonMessageGuard.FailClosed<DottedVersionVectorSetState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return ReadDottedVersionVectorSetState(document.RootElement, readValue);
        });
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

        return JsonMessageGuard.FailClosed<OrSetState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return new OrSetState<TValue>(ReadDottedVersionVectorSetState(RequireProperty(document.RootElement, "set", "An OR-set"), readValue));
        });
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

        return JsonMessageGuard.FailClosed<MvRegisterState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return new MvRegisterState<TValue>(ReadDottedVersionVectorSetState(RequireProperty(document.RootElement, "entries", "An MV-register"), readValue));
        });
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
            foreach(RgaTombstoneEntry tombstone in state.Tombstones)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("target");
                WriteDotState(writer, tombstone.Target);
                writer.WriteStartArray("removeDots");
                foreach(DotState removeDot in tombstone.RemoveDots)
                {
                    WriteDotState(writer, removeDot);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
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

        return JsonMessageGuard.FailClosed<RgaState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            JsonElement verticesElement = RequireProperty(root, "vertices", "An RGA");
            ImmutableArray<RgaVertexEntry<TValue>>.Builder vertices = ImmutableArray.CreateBuilder<RgaVertexEntry<TValue>>(verticesElement.GetArrayLength());
            foreach(JsonElement vertex in verticesElement.EnumerateArray())
            {
                JsonElement predecessor = RequireProperty(vertex, "predecessor", "An RGA vertex");
                vertices.Add(new RgaVertexEntry<TValue>(
                    ReadDotState(RequireProperty(vertex, "id", "An RGA vertex")),
                    predecessor.ValueKind == JsonValueKind.Null ? null : ReadDotState(predecessor),
                    readValue(RequireProperty(vertex, "value", "An RGA vertex"))));
            }

            JsonElement tombstonesElement = RequireProperty(root, "tombstones", "An RGA");
            ImmutableArray<RgaTombstoneEntry>.Builder tombstones = ImmutableArray.CreateBuilder<RgaTombstoneEntry>(tombstonesElement.GetArrayLength());
            foreach(JsonElement tombstone in tombstonesElement.EnumerateArray())
            {
                JsonElement removeDotsElement = RequireProperty(tombstone, "removeDots", "An RGA tombstone");
                ImmutableArray<DotState>.Builder removeDots = ImmutableArray.CreateBuilder<DotState>(removeDotsElement.GetArrayLength());
                foreach(JsonElement removeDot in removeDotsElement.EnumerateArray())
                {
                    removeDots.Add(ReadDotState(removeDot));
                }

                tombstones.Add(new RgaTombstoneEntry(
                    ReadDotState(RequireProperty(tombstone, "target", "An RGA tombstone")),
                    removeDots.ToImmutable()));
            }

            return new RgaState<TValue>(
                ReadVectorClockState(RequireProperty(root, "context", "An RGA")),
                vertices.ToImmutable(),
                tombstones.ToImmutable());
        });
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

            writer.WritePropertyName("baseFrontier");
            WriteVectorClockState(writer, state.BaseFrontier);

            writer.WriteNumber("baseGeneration", state.BaseGeneration);

            writer.WriteStartArray("removedBaseOffsets");
            foreach(OffsetBaseRemovalEntry removal in state.RemovedBaseOffsets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("offset", removal.Offset);
                writer.WriteStartArray("removeDots");
                foreach(DotState removeDot in removal.RemoveDots)
                {
                    WriteDotState(writer, removeDot);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
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
            foreach(OffsetTombstoneEntry tombstone in state.Tombstones)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("target");
                WriteDotState(writer, tombstone.Target);
                writer.WriteStartArray("removeDots");
                foreach(DotState removeDot in tombstone.RemoveDots)
                {
                    WriteDotState(writer, removeDot);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
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
            foreach(OffsetBaseAnchorEntry entry in state.CompactedBaseOffsets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("previous", entry.PreviousOffset);
                writer.WritePropertyName("target");
                WriteOffsetAnchorState(writer, entry.Target);
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

        return JsonMessageGuard.FailClosed<OffsetAnchoredSequenceState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            JsonElement baseElement = RequireProperty(root, "base", "An offset-anchored sequence");
            ImmutableArray<TValue>.Builder baseValues = ImmutableArray.CreateBuilder<TValue>(baseElement.GetArrayLength());
            foreach(JsonElement value in baseElement.EnumerateArray())
            {
                baseValues.Add(readValue(value));
            }

            JsonElement removedElement = RequireProperty(root, "removedBaseOffsets", "An offset-anchored sequence");
            ImmutableArray<OffsetBaseRemovalEntry>.Builder removed = ImmutableArray.CreateBuilder<OffsetBaseRemovalEntry>(removedElement.GetArrayLength());
            foreach(JsonElement removal in removedElement.EnumerateArray())
            {
                JsonElement removalDotsElement = RequireProperty(removal, "removeDots", "A removed base offset");
                ImmutableArray<DotState>.Builder removalDots = ImmutableArray.CreateBuilder<DotState>(removalDotsElement.GetArrayLength());
                foreach(JsonElement removeDot in removalDotsElement.EnumerateArray())
                {
                    removalDots.Add(ReadDotState(removeDot));
                }

                removed.Add(new OffsetBaseRemovalEntry(
                    ReadNonNegative(RequireProperty(removal, "offset", "A removed base offset"), "A removed base offset"),
                    removalDots.ToImmutable()));
            }

            JsonElement verticesElement = RequireProperty(root, "vertices", "An offset-anchored sequence");
            ImmutableArray<OffsetVertexEntry<TValue>>.Builder vertices = ImmutableArray.CreateBuilder<OffsetVertexEntry<TValue>>(verticesElement.GetArrayLength());
            foreach(JsonElement vertex in verticesElement.EnumerateArray())
            {
                vertices.Add(new OffsetVertexEntry<TValue>(
                    ReadDotState(RequireProperty(vertex, "id", "An offset-anchored sequence vertex")),
                    ReadOffsetAnchorState(RequireProperty(vertex, "anchor", "An offset-anchored sequence vertex")),
                    readValue(RequireProperty(vertex, "value", "An offset-anchored sequence vertex"))));
            }

            JsonElement tombstonesElement = RequireProperty(root, "tombstones", "An offset-anchored sequence");
            ImmutableArray<OffsetTombstoneEntry>.Builder tombstones = ImmutableArray.CreateBuilder<OffsetTombstoneEntry>(tombstonesElement.GetArrayLength());
            foreach(JsonElement tombstone in tombstonesElement.EnumerateArray())
            {
                JsonElement tombstoneDotsElement = RequireProperty(tombstone, "removeDots", "An offset-anchored sequence tombstone");
                ImmutableArray<DotState>.Builder tombstoneDots = ImmutableArray.CreateBuilder<DotState>(tombstoneDotsElement.GetArrayLength());
                foreach(JsonElement removeDot in tombstoneDotsElement.EnumerateArray())
                {
                    tombstoneDots.Add(ReadDotState(removeDot));
                }

                tombstones.Add(new OffsetTombstoneEntry(
                    ReadDotState(RequireProperty(tombstone, "target", "An offset-anchored sequence tombstone")),
                    tombstoneDots.ToImmutable()));
            }

            JsonElement dotAnchorsElement = RequireProperty(root, "compactedDotAnchors", "An offset-anchored sequence");
            ImmutableArray<OffsetTranslationEntry>.Builder dotAnchors = ImmutableArray.CreateBuilder<OffsetTranslationEntry>(dotAnchorsElement.GetArrayLength());
            foreach(JsonElement entry in dotAnchorsElement.EnumerateArray())
            {
                dotAnchors.Add(new OffsetTranslationEntry(
                    ReadDotState(RequireProperty(entry, "dropped", "A compacted dot anchor")),
                    ReadOffsetAnchorState(RequireProperty(entry, "target", "A compacted dot anchor"))));
            }

            JsonElement baseOffsetsElement = RequireProperty(root, "compactedBaseOffsets", "An offset-anchored sequence");
            ImmutableArray<OffsetBaseAnchorEntry>.Builder baseOffsets = ImmutableArray.CreateBuilder<OffsetBaseAnchorEntry>(baseOffsetsElement.GetArrayLength());
            foreach(JsonElement entry in baseOffsetsElement.EnumerateArray())
            {
                baseOffsets.Add(new OffsetBaseAnchorEntry(
                    ReadNonNegative(RequireProperty(entry, "previous", "A compacted base offset"), "A compacted base offset's previous offset"),
                    ReadOffsetAnchorState(RequireProperty(entry, "target", "A compacted base offset"))));
            }

            return new OffsetAnchoredSequenceState<TValue>(
                baseValues.ToImmutable(),
                ReadVectorClockState(RequireProperty(root, "baseFrontier", "An offset-anchored sequence")),
                ReadNonNegative(RequireProperty(root, "baseGeneration", "An offset-anchored sequence"), "A base generation"),
                removed.ToImmutable(),
                ReadVectorClockState(RequireProperty(root, "context", "An offset-anchored sequence")),
                vertices.ToImmutable(),
                tombstones.ToImmutable(),
                dotAnchors.ToImmutable(),
                baseOffsets.ToImmutable());
        });
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
                writer.WriteString("targetReplica", Convert.ToHexStringLower(span.TargetReplica.AsSpan()));
                writer.WriteNumber("targetFrom", span.TargetFrom);
                writer.WriteNumber("targetTo", span.TargetTo);
                writer.WriteString("removeReplica", Convert.ToHexStringLower(span.RemoveReplica.AsSpan()));
                writer.WriteNumber("removeFrom", span.RemoveFrom);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("irregularTombstones");
            foreach(RgaConcurrentTombstone tombstone in state.IrregularTombstones)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("target");
                WriteDotState(writer, tombstone.Target);
                writer.WriteStartArray("removeDots");
                foreach(DotState removeDot in tombstone.RemoveDots)
                {
                    WriteDotState(writer, removeDot);
                }

                writer.WriteEndArray();
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

            writer.WriteStartArray("translationSpans");
            foreach(RgaTranslationSpan span in state.TranslationSpans)
            {
                writer.WriteStartObject();
                writer.WriteString("replica", Convert.ToHexStringLower(span.DroppedReplica.AsSpan()));
                writer.WriteNumber("from", span.FromCounter);
                writer.WriteNumber("to", span.ToCounter);
                writer.WritePropertyName("target");
                WriteDotState(writer, span.Target);
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

        return JsonMessageGuard.FailClosed<RgaRunState<TValue>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            JsonElement runsElement = RequireProperty(root, "runs", "An RGA run state");
            ImmutableArray<RgaRunEntry<TValue>>.Builder runs = ImmutableArray.CreateBuilder<RgaRunEntry<TValue>>(runsElement.GetArrayLength());
            foreach(JsonElement run in runsElement.EnumerateArray())
            {
                JsonElement predecessor = RequireProperty(run, "predecessor", "An RGA run");
                JsonElement valuesElement = RequireProperty(run, "values", "An RGA run");
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
                    ReadDotState(RequireProperty(run, "first", "An RGA run")),
                    predecessor.ValueKind == JsonValueKind.Null ? null : ReadDotState(predecessor),
                    values.ToImmutable()));
            }

            JsonElement spansElement = RequireProperty(root, "tombstoneSpans", "An RGA run state");
            ImmutableArray<RgaTombstoneSpan>.Builder spans = ImmutableArray.CreateBuilder<RgaTombstoneSpan>(spansElement.GetArrayLength());
            foreach(JsonElement span in spansElement.EnumerateArray())
            {
                int targetFrom = RequireProperty(span, "targetFrom", "A tombstone span").GetInt32();
                int targetTo = RequireProperty(span, "targetTo", "A tombstone span").GetInt32();
                int removeFrom = RequireProperty(span, "removeFrom", "A tombstone span").GetInt32();
                if(targetFrom < 1 || targetTo < targetFrom || removeFrom < 1)
                {
                    throw new JsonException($"A tombstone span must satisfy 1 <= targetFrom <= targetTo and removeFrom >= 1, got targetFrom {targetFrom}, targetTo {targetTo}, removeFrom {removeFrom}.");
                }

                spans.Add(new RgaTombstoneSpan(
                    ReadReplicaBytes(RequireProperty(span, "targetReplica", "A tombstone span").GetString()!),
                    targetFrom,
                    targetTo,
                    ReadReplicaBytes(RequireProperty(span, "removeReplica", "A tombstone span").GetString()!),
                    removeFrom));
            }

            JsonElement irregularsElement = RequireProperty(root, "irregularTombstones", "An RGA run state");
            ImmutableArray<RgaConcurrentTombstone>.Builder irregulars = ImmutableArray.CreateBuilder<RgaConcurrentTombstone>(irregularsElement.GetArrayLength());
            foreach(JsonElement tombstone in irregularsElement.EnumerateArray())
            {
                JsonElement removeDotsElement = RequireProperty(tombstone, "removeDots", "An irregular tombstone");
                ImmutableArray<DotState>.Builder removeDots = ImmutableArray.CreateBuilder<DotState>(removeDotsElement.GetArrayLength());
                foreach(JsonElement removeDot in removeDotsElement.EnumerateArray())
                {
                    removeDots.Add(ReadDotState(removeDot));
                }

                irregulars.Add(new RgaConcurrentTombstone(
                    ReadDotState(RequireProperty(tombstone, "target", "An irregular tombstone")),
                    removeDots.ToImmutable()));
            }

            JsonElement translationsElement = RequireProperty(root, "translations", "An RGA run state");
            ImmutableArray<RgaTranslationEntry>.Builder translations = ImmutableArray.CreateBuilder<RgaTranslationEntry>(translationsElement.GetArrayLength());
            foreach(JsonElement translation in translationsElement.EnumerateArray())
            {
                translations.Add(new RgaTranslationEntry(
                    ReadDotState(RequireProperty(translation, "dropped", "A translation")),
                    ReadDotState(RequireProperty(translation, "target", "A translation"))));
            }

            JsonElement translationSpansElement = RequireProperty(root, "translationSpans", "An RGA run state");
            ImmutableArray<RgaTranslationSpan>.Builder translationSpans = ImmutableArray.CreateBuilder<RgaTranslationSpan>(translationSpansElement.GetArrayLength());
            foreach(JsonElement span in translationSpansElement.EnumerateArray())
            {
                int from = RequireProperty(span, "from", "A translation span").GetInt32();
                int to = RequireProperty(span, "to", "A translation span").GetInt32();
                if(from < 1 || to < from)
                {
                    throw new JsonException($"A translation span must satisfy 1 <= from <= to, got from {from}, to {to}.");
                }

                translationSpans.Add(new RgaTranslationSpan(
                    ReadReplicaBytes(RequireProperty(span, "replica", "A translation span").GetString()!),
                    from,
                    to,
                    ReadDotState(RequireProperty(span, "target", "A translation span"))));
            }

            return new RgaRunState<TValue>(
                ReadVectorClockState(RequireProperty(root, "context", "An RGA run state")),
                runs.ToImmutable(),
                spans.ToImmutable(),
                irregulars.ToImmutable(),
                translations.ToImmutable(),
                translationSpans.ToImmutable());
        });
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
        int baseOffset = RequireProperty(element, "baseOffset", "An offset anchor").GetInt32();
        if(baseOffset < -1)
        {
            throw new JsonException($"An anchor base offset is at least -1, got {baseOffset}.");
        }

        JsonElement liveId = RequireProperty(element, "liveId", "An offset anchor");
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
        JsonElement entriesElement = RequireProperty(element, "entries", "A replica-counter set");
        ImmutableArray<ReplicaCounterEntry>.Builder entries = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(entriesElement.GetArrayLength());
        foreach(JsonElement entry in entriesElement.EnumerateArray())
        {
            int count = RequireProperty(entry, "count", "A replica-counter entry").GetInt32();
            if(count < 0)
            {
                throw new JsonException($"A replica counter cannot be negative, got {count}.");
            }

            entries.Add(new ReplicaCounterEntry(ReadReplicaBytes(RequireProperty(entry, "replica", "A replica-counter entry").GetString()!), count));
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
        JsonElement entriesElement = RequireProperty(element, "entries", "A dotted version-vector set");
        ImmutableArray<DottedEntry<TValue>>.Builder entries = ImmutableArray.CreateBuilder<DottedEntry<TValue>>(entriesElement.GetArrayLength());
        foreach(JsonElement entry in entriesElement.EnumerateArray())
        {
            entries.Add(new DottedEntry<TValue>(
                ReadReplicaBytes(RequireProperty(entry, "replica", "A dotted entry").GetString()!),
                ReadDotCounter(entry),
                readValue(RequireProperty(entry, "value", "A dotted entry"))));
        }

        return new DottedVersionVectorSetState<TValue>(
            ReadVectorClockState(RequireProperty(element, "context", "A dotted version-vector set")),
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
            ReadReplicaBytes(RequireProperty(element, "replica", "A dot").GetString()!),
            ReadDotCounter(element));
    }


    private static int ReadDotCounter(JsonElement element)
    {
        int counter = RequireProperty(element, "counter", "A dot").GetInt32();
        if(counter < 1)
        {
            throw new JsonException($"A dot counter is at least one, got {counter}.");
        }

        return counter;
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
