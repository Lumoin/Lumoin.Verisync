using Lumoin.Verisync.Core;
using System;
using System.Collections.Immutable;
using System.Text.Json;

namespace Lumoin.Verisync.Json;

/// <summary>
/// Builds JSON <see cref="SerializeMessageDelegate{TMessage}"/> and
/// <see cref="DeserializeMessageDelegate{TMessage}"/> implementations for the reconciliation wire envelope, so
/// the five session messages can cross a Verisync message channel (in-memory pipe or socket).
/// </summary>
/// <remarks>
/// The encoding mirrors <see cref="RaftJson"/> and <see cref="LogCommitmentJson"/>: hand-written and explicit
/// — no reflection, AOT- and trim-safe — with a <c>type</c> discriminator on the envelope, a nested
/// <c>payload</c> object, and lowercase-hex byte fields. The caller supplies how to read and write the
/// element value, since the element is application-defined. The decoder validates fail-closed
/// (<see cref="JsonException"/>) on anything no honest sender produces — an unknown type, malformed or
/// wrong-length hex, a negative start index, a non-positive absorbed count, an empty or duplicate-bearing
/// array. The deserializer is verifying: it pins the LOCAL contract and rejects an offer that does not match
/// it and any hex field whose decoded width is wrong, so a contract mismatch throws before any symbol is
/// absorbed.
/// </remarks>
public static class ReconciliationJson
{
    private const string OfferType = "offer";
    private const string SymbolsType = "symbols";
    private const string DoneType = "done";
    private const string FetchType = "fetch";
    private const string ElementsType = "elements";
    private const string ContextType = "context";
    private const string DropType = "drop";

    private const string ContentHashDomain = "contentHash";
    private const string StructuralDomain = "structural";


    /// <summary>Creates a serializer for <see cref="ReconciliationEnvelope{TElement}"/>.</summary>
    /// <typeparam name="TElement">The application element type.</typeparam>
    /// <param name="writeElement">Writes an element value to the JSON writer.</param>
    /// <returns>A serialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="writeElement"/> is <see langword="null"/>.</exception>
    public static SerializeMessageDelegate<ReconciliationEnvelope<TElement>> CreateEnvelopeSerializer<TElement>(Action<Utf8JsonWriter, TElement> writeElement)
    {
        ArgumentNullException.ThrowIfNull(writeElement);

        return (envelope, output) =>
        {
            ArgumentNullException.ThrowIfNull(envelope);

            //The wire format has exactly one payload slot, so an envelope carrying any other number is
            //unrepresentable and fails closed before a byte is written.
            int payloadCount = (envelope.Offer is null ? 0 : 1)
                + (envelope.Symbols is null ? 0 : 1)
                + (envelope.Done is null ? 0 : 1)
                + (envelope.Fetch is null ? 0 : 1)
                + (envelope.Elements is null ? 0 : 1)
                + (envelope.Context is null ? 0 : 1)
                + (envelope.Drop is null ? 0 : 1);
            if(payloadCount != 1)
            {
                throw new ArgumentException($"A reconciliation envelope must carry exactly one payload, but it carries {payloadCount}.", nameof(envelope));
            }

            using var writer = new Utf8JsonWriter(output);
            writer.WriteStartObject();

            if(envelope.Offer is { } offer)
            {
                writer.WriteString("type", OfferType);
                writer.WritePropertyName("payload");
                WriteOffer(writer, offer);
            }
            else if(envelope.Symbols is { } symbols)
            {
                writer.WriteString("type", SymbolsType);
                writer.WritePropertyName("payload");
                WriteSymbols(writer, symbols);
            }
            else if(envelope.Done is { } done)
            {
                writer.WriteString("type", DoneType);
                writer.WritePropertyName("payload");
                WriteDone(writer, done);
            }
            else if(envelope.Fetch is { } fetch)
            {
                writer.WriteString("type", FetchType);
                writer.WritePropertyName("payload");
                WriteFetch(writer, fetch);
            }
            else if(envelope.Elements is { } elements)
            {
                writer.WriteString("type", ElementsType);
                writer.WritePropertyName("payload");
                WriteElements(writer, elements, writeElement);
            }
            else if(envelope.Context is { } context)
            {
                writer.WriteString("type", ContextType);
                writer.WritePropertyName("payload");
                WriteContext(writer, context);
            }
            else
            {
                writer.WriteString("type", DropType);
                writer.WritePropertyName("payload");
                WriteDrop(writer, envelope.Drop!);
            }

            writer.WriteEndObject();
        };
    }


    /// <summary>Creates a verifying deserializer for <see cref="ReconciliationEnvelope{TElement}"/>.</summary>
    /// <typeparam name="TElement">The application element type.</typeparam>
    /// <param name="contract">The local contract the deserializer pins; a non-matching offer is rejected.</param>
    /// <param name="readElement">
    /// Reads an element value from a JSON element. The codec hands this the raw <see cref="JsonElement"/> of
    /// the element value without asserting its kind, so a hostile peer can present any kind there; an
    /// implementation that wants the codec's fail-closed posture must validate the kind itself and throw
    /// <see cref="JsonException"/> on anything it does not expect.
    /// </param>
    /// <returns>A deserialize delegate.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="contract"/> or <paramref name="readElement"/> is <see langword="null"/>.</exception>
    public static DeserializeMessageDelegate<ReconciliationEnvelope<TElement>> CreateEnvelopeDeserializer<TElement>(ReconciliationContract contract, Func<JsonElement, TElement> readElement)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(readElement);

        return JsonMessageGuard.FailClosed<ReconciliationEnvelope<TElement>>(payload =>
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = RequireObject(document.RootElement, "A reconciliation envelope");
            string type = ReadString(RequireProperty(root, "type", "A reconciliation envelope"), "The envelope type");
            JsonElement payloadElement = RequireObject(RequireProperty(root, "payload", "A reconciliation envelope"), "The envelope payload");

            return type switch
            {
                OfferType => ReconciliationEnvelope<TElement>.ForOffer(ReadOffer(payloadElement, contract)),
                SymbolsType => ReconciliationEnvelope<TElement>.ForSymbols(ReadSymbols(payloadElement, contract)),
                DoneType => ReconciliationEnvelope<TElement>.ForDone(ReadDone(payloadElement)),
                FetchType => ReconciliationEnvelope<TElement>.ForFetch(ReadFetch(payloadElement, contract)),
                ElementsType => ReconciliationEnvelope<TElement>.ForElements(ReadElements(payloadElement, contract, readElement)),
                ContextType => ReconciliationEnvelope<TElement>.ForContext(ReadContext(payloadElement)),
                DropType => ReconciliationEnvelope<TElement>.ForDrop(ReadDrop(payloadElement)),
                _ => throw new JsonException($"Unknown reconciliation envelope type '{type}'.")
            };
        });
    }


    private static void WriteOffer(Utf8JsonWriter writer, ReconciliationOffer offer)
    {
        writer.WriteStartObject();
        writer.WriteString("itemDomain", DomainToString(offer.ItemDomain));
        writer.WriteNumber("itemWidth", offer.ItemWidth);
        writer.WriteNumber("checksumWidth", offer.ChecksumWidth);
        writer.WriteString("keyCheck", Convert.ToHexStringLower(offer.KeyCheck.Span));
        writer.WriteEndObject();
    }


    private static ReconciliationOffer ReadOffer(JsonElement element, ReconciliationContract contract)
    {
        ReconciliationItemDomain itemDomain = ReadDomain(ReadString(RequireProperty(element, "itemDomain", "An offer"), "An item domain"));
        int itemWidth = ReadInt32(RequireProperty(element, "itemWidth", "An offer"), "An item width");
        int checksumWidth = ReadInt32(RequireProperty(element, "checksumWidth", "An offer"), "A checksum width");
        ReadOnlyMemory<byte> keyCheck = ReadHex(RequireProperty(element, "keyCheck", "An offer"), "A key check");
        if(keyCheck.Length != 8)
        {
            throw new JsonException($"A key check must decode to eight bytes, got {keyCheck.Length}.");
        }

        ReconciliationOffer offer = Construct(() => new ReconciliationOffer(itemDomain, itemWidth, checksumWidth, keyCheck));
        if(!offer.Matches(contract))
        {
            throw new JsonException("The offer does not match the local reconciliation contract; the session must abort before any symbol flows.");
        }

        return offer;
    }


    private static void WriteSymbols(Utf8JsonWriter writer, ReconciliationSymbolBatch batch)
    {
        writer.WriteStartObject();
        writer.WriteNumber("startIndex", batch.StartIndex);
        writer.WriteStartArray("symbols");
        foreach(ReconciliationSymbol symbol in batch.Symbols)
        {
            writer.WriteStartObject();
            writer.WriteString("sum", Convert.ToHexStringLower(symbol.Sum.Span));
            writer.WriteString("checksum", Convert.ToHexStringLower(symbol.Checksum.Span));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static ReconciliationSymbolBatch ReadSymbols(JsonElement element, ReconciliationContract contract)
    {
        int startIndex = ReadNonNegativeInt(RequireProperty(element, "startIndex", "A symbol batch"), "A start index");

        JsonElement symbolsElement = RequireArray(RequireProperty(element, "symbols", "A symbol batch"), "A symbols field");
        if(symbolsElement.GetArrayLength() == 0)
        {
            throw new JsonException("A symbol batch must carry at least one symbol.");
        }

        ImmutableArray<ReconciliationSymbol>.Builder symbols = ImmutableArray.CreateBuilder<ReconciliationSymbol>(symbolsElement.GetArrayLength());
        foreach(JsonElement rawSymbol in symbolsElement.EnumerateArray())
        {
            JsonElement symbolElement = RequireObject(rawSymbol, "A symbol");
            ReadOnlyMemory<byte> sum = ReadHex(RequireProperty(symbolElement, "sum", "A symbol"), "A symbol sum");
            if(sum.Length != contract.ItemWidth)
            {
                throw new JsonException($"A symbol sum must decode to {contract.ItemWidth} bytes, got {sum.Length}.");
            }

            ReadOnlyMemory<byte> checksum = ReadHex(RequireProperty(symbolElement, "checksum", "A symbol"), "A symbol checksum");
            if(checksum.Length != contract.ChecksumWidth)
            {
                throw new JsonException($"A symbol checksum must decode to {contract.ChecksumWidth} bytes, got {checksum.Length}.");
            }

            symbols.Add(Construct(() => new ReconciliationSymbol(sum, checksum)));
        }

        ImmutableArray<ReconciliationSymbol> built = symbols.MoveToImmutable();

        return Construct(() => new ReconciliationSymbolBatch(startIndex, built));
    }


    private static void WriteDone(Utf8JsonWriter writer, ReconciliationDone done)
    {
        writer.WriteStartObject();
        writer.WriteNumber("absorbedCount", done.AbsorbedCount);
        writer.WriteEndObject();
    }


    private static ReconciliationDone ReadDone(JsonElement element)
    {
        int absorbedCount = ReadInt32(RequireProperty(element, "absorbedCount", "A done"), "An absorbed count");
        if(absorbedCount < 1)
        {
            throw new JsonException($"An absorbed count must be at least one, got {absorbedCount}.");
        }

        return Construct(() => new ReconciliationDone(absorbedCount));
    }


    private static void WriteFetch(Utf8JsonWriter writer, ReconciliationFetch fetch)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("items");
        foreach(ReadOnlyMemory<byte> item in fetch.Items)
        {
            writer.WriteStringValue(Convert.ToHexStringLower(item.Span));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static ReconciliationFetch ReadFetch(JsonElement element, ReconciliationContract contract)
    {
        JsonElement itemsElement = RequireArray(RequireProperty(element, "items", "A fetch"), "An items field");
        if(itemsElement.GetArrayLength() == 0)
        {
            throw new JsonException("A fetch must carry at least one item.");
        }

        ImmutableArray<ReadOnlyMemory<byte>>.Builder items = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>(itemsElement.GetArrayLength());
        foreach(JsonElement itemElement in itemsElement.EnumerateArray())
        {
            ReadOnlyMemory<byte> item = ReadHex(itemElement, "A fetch item");
            if(item.Length != contract.ItemWidth)
            {
                throw new JsonException($"A fetch item must decode to {contract.ItemWidth} bytes, got {item.Length}.");
            }

            items.Add(item);
        }

        ImmutableArray<ReadOnlyMemory<byte>> built = items.MoveToImmutable();

        return Construct(() => new ReconciliationFetch(built));
    }


    private static void WriteElements<TElement>(Utf8JsonWriter writer, ReconciliationElements<TElement> elements, Action<Utf8JsonWriter, TElement> writeElement)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("entries");
        foreach(ReconciliationElementEntry<TElement> entry in elements.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("item", Convert.ToHexStringLower(entry.Item.Span));
            writer.WritePropertyName("element");
            writeElement(writer, entry.Element);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static ReconciliationElements<TElement> ReadElements<TElement>(JsonElement element, ReconciliationContract contract, Func<JsonElement, TElement> readElement)
    {
        JsonElement entriesElement = RequireArray(RequireProperty(element, "entries", "An elements message"), "An entries field");
        if(entriesElement.GetArrayLength() == 0)
        {
            throw new JsonException("An elements message must carry at least one entry.");
        }

        ImmutableArray<ReconciliationElementEntry<TElement>>.Builder entries = ImmutableArray.CreateBuilder<ReconciliationElementEntry<TElement>>(entriesElement.GetArrayLength());
        foreach(JsonElement rawEntry in entriesElement.EnumerateArray())
        {
            JsonElement entryElement = RequireObject(rawEntry, "An element entry");
            ReadOnlyMemory<byte> item = ReadHex(RequireProperty(entryElement, "item", "An element entry"), "An element entry item");
            if(item.Length != contract.ItemWidth)
            {
                throw new JsonException($"An element entry's item must decode to {contract.ItemWidth} bytes, got {item.Length}.");
            }

            TElement value = readElement(RequireProperty(entryElement, "element", "An element entry"));
            entries.Add(Construct(() => new ReconciliationElementEntry<TElement>(item, value)));
        }

        ImmutableArray<ReconciliationElementEntry<TElement>> built = entries.MoveToImmutable();

        return Construct(() => new ReconciliationElements<TElement>(built));
    }


    private static void WriteContext(Utf8JsonWriter writer, ReconciliationContext context)
    {
        //The causal context is the vector clock encoded as CrdtStateJson encodes one: a count entry per
        //replica, the replica lower-hex and the count a non-negative number.
        writer.WriteStartObject();
        writer.WriteStartArray("entries");
        foreach(ReplicaCounterEntry entry in context.Clock.Entries)
        {
            writer.WriteStartObject();
            writer.WriteString("replica", Convert.ToHexStringLower(entry.Replica.AsSpan()));
            writer.WriteNumber("count", entry.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static ReconciliationContext ReadContext(JsonElement element)
    {
        JsonElement entriesElement = RequireArray(RequireProperty(element, "entries", "A context"), "A context entries field");
        ImmutableArray<ReplicaCounterEntry>.Builder entries = ImmutableArray.CreateBuilder<ReplicaCounterEntry>(entriesElement.GetArrayLength());
        foreach(JsonElement rawEntry in entriesElement.EnumerateArray())
        {
            JsonElement entryElement = RequireObject(rawEntry, "A context entry");
            ImmutableArray<byte> replica = ReadReplica(RequireProperty(entryElement, "replica", "A context entry"), "A context entry replica");
            int count = ReadInt32(RequireProperty(entryElement, "count", "A context entry"), "A context entry count");
            if(count < 0)
            {
                throw new JsonException($"A context entry count cannot be negative, got {count}.");
            }

            entries.Add(new ReplicaCounterEntry(replica, count));
        }

        return new ReconciliationContext(new VectorClockState(entries.ToImmutable()));
    }


    private static void WriteDrop(Utf8JsonWriter writer, ReconciliationDrop drop)
    {
        //Each dot is the replica lower-hex and its counter, matching the dot encoding CrdtStateJson uses.
        writer.WriteStartObject();
        writer.WriteStartArray("dots");
        foreach(DotState dot in drop.Dots)
        {
            writer.WriteStartObject();
            writer.WriteString("replica", Convert.ToHexStringLower(dot.Replica.AsSpan()));
            writer.WriteNumber("counter", dot.Counter);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }


    private static ReconciliationDrop ReadDrop(JsonElement element)
    {
        JsonElement dotsElement = RequireArray(RequireProperty(element, "dots", "A drop"), "A dots field");
        if(dotsElement.GetArrayLength() == 0)
        {
            throw new JsonException("A drop must carry at least one dot.");
        }

        ImmutableArray<DotState>.Builder dots = ImmutableArray.CreateBuilder<DotState>(dotsElement.GetArrayLength());
        foreach(JsonElement rawDot in dotsElement.EnumerateArray())
        {
            JsonElement dotElement = RequireObject(rawDot, "A drop dot");
            ImmutableArray<byte> replica = ReadReplica(RequireProperty(dotElement, "replica", "A drop dot"), "A drop dot replica");
            int counter = ReadInt32(RequireProperty(dotElement, "counter", "A drop dot"), "A drop dot counter");
            dots.Add(new DotState(replica, counter));
        }

        ImmutableArray<DotState> built = dots.MoveToImmutable();

        return Construct(() => new ReconciliationDrop(built));
    }


    private static ImmutableArray<byte> ReadReplica(JsonElement element, string label)
    {
        ReadOnlyMemory<byte> bytes = ReadHex(element, label);
        if(bytes.Length != ReplicaId.Size)
        {
            throw new JsonException($"{label} must decode to {ReplicaId.Size} bytes, got {bytes.Length}.");
        }

        return ImmutableArray.Create(bytes.Span);
    }


    private static string DomainToString(ReconciliationItemDomain domain)
    {
        return domain switch
        {
            ReconciliationItemDomain.ContentHash => ContentHashDomain,
            ReconciliationItemDomain.Structural => StructuralDomain,
            _ => throw new JsonException($"Unknown reconciliation item domain '{domain}'.")
        };
    }


    private static ReconciliationItemDomain ReadDomain(string domain)
    {
        return domain switch
        {
            ContentHashDomain => ReconciliationItemDomain.ContentHash,
            StructuralDomain => ReconciliationItemDomain.Structural,
            _ => throw new JsonException($"Unknown reconciliation item domain '{domain}'.")
        };
    }


    private static int ReadNonNegativeInt(JsonElement element, string label)
    {
        int value = ReadInt32(element, label);
        if(value < 0)
        {
            throw new JsonException($"{label} cannot be negative, got {value}.");
        }

        return value;
    }


    private static ReadOnlyMemory<byte> ReadHex(JsonElement element, string label)
    {
        //The payload may come from an untrusted peer, so the value's kind and hex content are both checked
        //before the bytes are allowed to stand in for an item, a symbol field, or a key check. A wrong-kind
        //or null value fails closed as JsonException, never as a raw accessor exception.
        string hex = ReadString(element, label);
        try
        {
            return Convert.FromHexString(hex);
        }
        catch(FormatException exception)
        {
            throw new JsonException($"{label} must be hex-encoded.", exception);
        }
    }


    private static string ReadString(JsonElement element, string label)
    {
        //GetString returns null for a JSON null and throws for a non-string kind, so the kind is asserted
        //first and the result is therefore never null.
        if(element.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"{label} must be a JSON string.");
        }

        return element.GetString()!;
    }


    private static int ReadInt32(JsonElement element, string label)
    {
        //A non-number kind, a fractional value, or a value outside the Int32 range all fail closed here
        //rather than surfacing a raw FormatException or InvalidOperationException from the accessor.
        if(element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
        {
            throw new JsonException($"{label} must be a 32-bit integer.");
        }

        return value;
    }


    private static JsonElement RequireProperty(JsonElement element, string name, string label)
    {
        //A required field absent from an otherwise well-formed object is malformed input, so it fails closed
        //as JsonException rather than the KeyNotFoundException the raw GetProperty accessor throws. The
        //element is always one the caller has already asserted is an object, so TryGetProperty cannot itself
        //surface the wrong-kind InvalidOperationException here.
        if(!element.TryGetProperty(name, out JsonElement property))
        {
            throw new JsonException($"{label} must carry a '{name}' field.");
        }

        return property;
    }


    private static JsonElement RequireObject(JsonElement element, string label)
    {
        if(element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"{label} must be a JSON object.");
        }

        return element;
    }


    private static JsonElement RequireArray(JsonElement element, string label)
    {
        if(element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{label} must be a JSON array.");
        }

        return element;
    }


    private static T Construct<T>(Func<T> construct)
    {
        //The typed fields come from an untrusted payload; the domain constructors reject hostile values with
        //argument exceptions, which are wrapped so the raw argument exception never surfaces from the codec.
        try
        {
            return construct();
        }
        catch(ArgumentException exception)
        {
            throw new JsonException("The payload carries values a reconciliation message rejects.", exception);
        }
    }
}
