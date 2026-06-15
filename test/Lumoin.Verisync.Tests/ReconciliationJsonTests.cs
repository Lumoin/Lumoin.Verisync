using Lumoin.Verisync.Core;
using Lumoin.Verisync.Json;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Round-trips and hostile-input checks for the reconciliation envelope JSON codec, mirroring
/// <see cref="RaftJsonTests"/> and <see cref="ConsensusMessageJsonTests"/>: every wire kind survives a
/// serialize/deserialize cycle and every malformed payload the spec enumerates fails closed. The deserializer
/// is verifying — it pins the local contract and rejects a non-matching offer or a wrongly sized hex field —
/// so the strictness vectors run against a fixed structural width-8 contract under the well-known key.
/// </summary>
[TestClass]
internal sealed class ReconciliationJsonTests
{
    private static ReconciliationContract LocalContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static byte[] SumEight { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    private static byte[] ChecksumEight { get; } = [0xa5, 0x7a, 0x71, 0xe9, 0x20, 0xbf, 0x57, 0xa9];

    private static byte[] ItemOne { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] ItemTwo { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];


    [TestMethod]
    public void EachWireKindRoundTrips()
    {
        //The offer carries the pinned well-known key-check tag, derived from the contract, never the key.
        ReconciliationOffer offer = ReconciliationOffer.FromContract(LocalContract);
        ReconciliationEnvelope<string> offerBack = RoundTrip(ReconciliationEnvelope<string>.ForOffer(offer));
        Assert.AreEqual(offer, offerBack.Offer);

        //The pinned phase 1 stream vector: symbol 0 of the well-known structural set, sum and checksum exact.
        ReconciliationSymbol pinned = new(SumEight, ChecksumEight);
        ReconciliationSymbolBatch singleBatch = new(0, [pinned]);
        ReconciliationEnvelope<string> singleBack = RoundTrip(ReconciliationEnvelope<string>.ForSymbols(singleBatch));
        Assert.AreEqual(singleBatch, singleBack.Symbols);

        ReconciliationSymbolBatch multiBatch = new(4, [pinned, new ReconciliationSymbol(ItemOne, ChecksumEight)]);
        ReconciliationEnvelope<string> multiBack = RoundTrip(ReconciliationEnvelope<string>.ForSymbols(multiBatch));
        Assert.AreEqual(multiBatch, multiBack.Symbols);

        ReconciliationDone done = new(6);
        ReconciliationEnvelope<string> doneBack = RoundTrip(ReconciliationEnvelope<string>.ForDone(done));
        Assert.AreEqual(done, doneBack.Done);

        ReconciliationFetch fetch = new([ItemOne, ItemTwo]);
        ReconciliationEnvelope<string> fetchBack = RoundTrip(ReconciliationEnvelope<string>.ForFetch(fetch));
        Assert.AreEqual(fetch, fetchBack.Fetch);

        ReconciliationElements<string> elements = new([new ReconciliationElementEntry<string>(ItemOne, "zeta"), new ReconciliationElementEntry<string>(ItemTwo, "eta")]);
        ReconciliationEnvelope<string> elementsBack = RoundTrip(ReconciliationEnvelope<string>.ForElements(elements));
        Assert.AreEqual(elements, elementsBack.Elements);
    }


    [TestMethod]
    public void OfferStrictnessRejectsEveryContractMismatch()
    {
        //A content-hash offer against the structural local contract is a domain mismatch.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"contentHash","itemWidth":8,"checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));

        //A width-32 offer does not match the width-8 local contract.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":32,"checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));

        //A checksum-width-4 offer does not match the checksum-width-8 local contract.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":4,"keyCheck":"630c7d8175160642"}}"""));

        //The pinned mismatch-vector key check is a different key and so a hard mismatch.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8,"keyCheck":"a3248bbf55272e4d"}}"""));

        //An unknown domain string is unmappable and fails closed.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"mystery","itemWidth":8,"checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));

        //A key check of the wrong byte length cannot be a valid eight-byte tag.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8,"keyCheck":"630c7d81751606"}}"""));

        //A non-hex key check cannot decode at all.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8,"keyCheck":"zzzzzzzzzzzzzzzz"}}"""));
    }


    [TestMethod]
    public void TheWellKnownOfferShapeDeserializesAndMatchesTheLocalContract()
    {
        //The exact pinned width-8 offer shape under the well-known key deserializes and matches the contract.
        ReconciliationEnvelope<string> back = Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8,"keyCheck":"630c7d8175160642"}}""");

        Assert.IsNotNull(back.Offer);
        Assert.IsTrue(back.Offer.Matches(LocalContract));
    }


    [TestMethod]
    public void SymbolsStrictnessRejectsBadStartIndexAndFieldWidths()
    {
        //A negative start index is never a legal stream position.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":-1,"symbols":[{"sum":"3132333435363738","checksum":"a57a71e920bf57a9"}]}}"""));

        //An empty symbols array carries no cell and is malformed.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[]}}"""));

        //A sum of seven bytes is below the contract's item width.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"sum":"31323334353637","checksum":"a57a71e920bf57a9"}]}}"""));

        //A sum of nine bytes is above the contract's item width.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"sum":"313233343536373839","checksum":"a57a71e920bf57a9"}]}}"""));

        //A checksum of the wrong width does not match the contract's checksum width.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"sum":"3132333435363738","checksum":"a57a71e9"}]}}"""));

        //A non-hex sum cannot decode.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"sum":"zzzzzzzzzzzzzzzz","checksum":"a57a71e920bf57a9"}]}}"""));
    }


    [TestMethod]
    public void DoneStrictnessRejectsNonPositiveCounts()
    {
        //A done that absorbed zero symbols is impossible; completion takes at least one.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":{"absorbedCount":0}}"""));

        //A negative absorbed count is forged.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":{"absorbedCount":-1}}"""));
    }


    [TestMethod]
    public void FetchAndElementsStrictnessRejectBadArraysWidthsAndDuplicates()
    {
        //Empty arrays carry nothing to fetch or resolve.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"fetch","payload":{"items":[]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":[]}}"""));

        //A fetch item of the wrong width violates the contract's item width.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"fetch","payload":{"items":["11121314"]}}"""));

        //An element entry's item of the wrong width violates the contract's item width.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":[{"item":"11121314","element":"zeta"}]}}"""));

        //Duplicate fetch items are unrepresentable: decoded items are distinct by construction.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"fetch","payload":{"items":["1112131415161718","1112131415161718"]}}"""));

        //Duplicate element items likewise fail closed.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":[{"item":"1112131415161718","element":"zeta"},{"item":"1112131415161718","element":"eta"}]}}"""));
    }


    [TestMethod]
    public void UnknownTypeAndTruncatedDocumentFailClosed()
    {
        //An unrecognized discriminator is a malformed envelope.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"evil","payload":{}}"""));

        //A cut-off document cannot parse; the reader exception is wrapped as MessageDeserializationException.
        Assert.Throws<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":{"absorbedCo"""));
    }


    [TestMethod]
    public void PresentButMalformedValuesFailClosed()
    {
        //A JSON-null hex field must fail closed, not surface a raw ArgumentNullException from the hex decode.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8,"keyCheck":null}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"sum":null,"checksum":"a57a71e920bf57a9"}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"fetch","payload":{"items":[null]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":[{"item":null,"element":"zeta"}]}}"""));

        //A wrong-kind value where a string or hex field is expected must fail closed, not surface a raw
        //InvalidOperationException from GetString.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8,"keyCheck":12345678}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":123,"payload":{}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":true,"itemWidth":8,"checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));

        //A non-integer number (fractional or Int32-overflowing) or a wrong-kind value where an integer is
        //expected must fail closed, not surface a raw FormatException or InvalidOperationException.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":{"absorbedCount":9999999999}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":{"absorbedCount":1.5}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":"0","symbols":[{"sum":"3132333435363738","checksum":"a57a71e920bf57a9"}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":"8","checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));
    }


    [TestMethod]
    public void WrongKindContainersFailClosed()
    {
        //A non-object envelope or payload must fail closed, not surface a raw InvalidOperationException from
        //the property access.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("123"));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("null"));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":42}"""));

        //A non-array where an array is expected must fail closed, not surface a raw InvalidOperationException
        //from GetArrayLength.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":null}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"fetch","payload":{"items":42}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":{}}}"""));

        //A non-object array element where an object is expected must fail closed.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[null]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":["zeta"]}}"""));
    }


    [TestMethod]
    public void MissingRequiredFieldsFailClosed()
    {
        //A required field absent from an otherwise well-formed object must fail closed as MessageDeserializationException, not
        //surface the framework's KeyNotFoundException from a raw property accessor. Each envelope arm is
        //exercised with one required field omitted.
        string replica = HexReplica();

        //The discriminator and the payload slot are both mandatory on the envelope itself.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"payload":{"absorbedCount":1}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done"}"""));

        //The offer omits each of its scalars in turn.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemWidth":8,"checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","checksumWidth":8,"keyCheck":"630c7d8175160642"}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"keyCheck":"630c7d8175160642"}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"offer","payload":{"itemDomain":"structural","itemWidth":8,"checksumWidth":8}}"""));

        //The symbols arm omits the start index, the array, then a field of a symbol.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"symbols":[{"sum":"3132333435363738","checksum":"a57a71e920bf57a9"}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"checksum":"a57a71e920bf57a9"}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"symbols","payload":{"startIndex":0,"symbols":[{"sum":"3132333435363738"}]}}"""));

        //The done arm omits its only field.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"done","payload":{}}"""));

        //The fetch arm omits its items array.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"fetch","payload":{}}"""));

        //The elements arm omits its array, then a field of an entry.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":[{"element":"zeta"}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"elements","payload":{"entries":[{"item":"1112131415161718"}]}}"""));

        //The context arm omits its entries array, then a field of an entry (the replica is read before the count).
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"context","payload":{}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"context","payload":{"entries":[{"count":1}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"type":"context","payload":{"entries":[{"replica":"{{{replica}}}"}]}}"""));

        //The drop arm omits its dots array, then a field of a dot.
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"drop","payload":{}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize("""{"type":"drop","payload":{"dots":[{"counter":1}]}}"""));
        Assert.ThrowsExactly<MessageDeserializationException>(() => Deserialize($$$"""{"type":"drop","payload":{"dots":[{"replica":"{{{replica}}}"}]}}"""));
    }


    [TestMethod]
    public void SerializerRefusesAnEnvelopeWithoutExactlyOnePayload()
    {
        SerializeMessageDelegate<ReconciliationEnvelope<string>> serialize =
            ReconciliationJson.CreateEnvelopeSerializer<string>((writer, value) => writer.WriteStringValue(value));

        //Zero payloads has no wire shape.
        ReconciliationEnvelope<string> empty = new(null, null, null, null, null, null, null);
        Assert.ThrowsExactly<ArgumentException>(() => serialize(empty, new ArrayBufferWriter<byte>()));

        //Two payloads is ambiguous on the wire and refused.
        ReconciliationEnvelope<string> two = new(ReconciliationOffer.FromContract(LocalContract), null, new ReconciliationDone(6), null, null, null, null);
        Assert.ThrowsExactly<ArgumentException>(() => serialize(two, new ArrayBufferWriter<byte>()));

        //A null envelope is a programming error surfaced as ArgumentNullException.
        Assert.ThrowsExactly<ArgumentNullException>(() => serialize(null!, new ArrayBufferWriter<byte>()));
    }


    private static ReconciliationEnvelope<string> RoundTrip(ReconciliationEnvelope<string> envelope)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ReconciliationJson.CreateEnvelopeSerializer<string>((writer, value) => writer.WriteStringValue(value))(envelope, buffer);

        return ReconciliationJson.CreateEnvelopeDeserializer<string>(LocalContract, element => element.GetString()!)(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }


    private static ReconciliationEnvelope<string> Deserialize(string json)
    {
        return ReconciliationJson.CreateEnvelopeDeserializer<string>(LocalContract, element => element.GetString()!)(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)));
    }


    private static string HexReplica()
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = 1;

        return Convert.ToHexStringLower(buffer);
    }
}
