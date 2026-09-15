using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Validation and equality coverage for the phase 2 reconciliation wire records. Each record copies its byte
/// arguments and carries custom equality over its <see cref="ReadOnlyMemory{T}"/> and
/// <see cref="ImmutableArray{T}"/> members, so the tests pin construction-time validation, the defensive copy,
/// and that equality is by content across independently allocated buffers rather than by reference identity.
/// </summary>
[TestClass]
internal sealed class ReconciliationWireRecordTests
{
    private static ReconciliationContract WellKnownContract { get; } = ReconciliationContract.ContentHashDefault;

    private static ReconciliationContract MismatchKeyContract { get; } =
        new(ReconciliationItemDomain.ContentHash, 32, 8, 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);

    private static byte[] WellKnownKeyCheck { get; } = Convert.FromHexString("630c7d8175160642");

    private static byte[] SumEight { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    private static byte[] ChecksumEight { get; } = [0xa5, 0x7a, 0x71, 0xe9, 0x20, 0xbf, 0x57, 0xa9];


    [TestMethod]
    public void OfferFromContractCarriesThePinnedKeyCheckAndMatchesItsContract()
    {
        ReconciliationOffer offer = ReconciliationOffer.FromContract(WellKnownContract);

        //The key never travels; the offer carries only the eight-byte key-check tag pinned for the well-known key.
        Assert.AreSequenceEqual(WellKnownKeyCheck, offer.KeyCheck.ToArray());
        Assert.AreEqual(ReconciliationItemDomain.ContentHash, offer.ItemDomain);
        Assert.AreEqual(32, offer.ItemWidth);
        Assert.AreEqual(8, offer.ChecksumWidth);

        Assert.IsTrue(offer.Matches(WellKnownContract));

        //A different key, width, or domain is a hard mismatch the offer must reject before any symbol flows.
        Assert.IsFalse(offer.Matches(MismatchKeyContract));
        Assert.IsFalse(offer.Matches(new ReconciliationContract(ReconciliationItemDomain.ContentHash, 16, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh)));
        Assert.IsFalse(offer.Matches(new ReconciliationContract(ReconciliationItemDomain.Structural, 32, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh)));
    }


    [TestMethod]
    public void OfferValidationRejectsBadArgumentsAndEqualityIsByContent()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationOffer((ReconciliationItemDomain)0, 32, 8, WellKnownKeyCheck));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationOffer(ReconciliationItemDomain.ContentHash, 0, 8, WellKnownKeyCheck));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationOffer(ReconciliationItemDomain.ContentHash, 1025, 8, WellKnownKeyCheck));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationOffer(ReconciliationItemDomain.ContentHash, 32, 0, WellKnownKeyCheck));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationOffer(ReconciliationItemDomain.ContentHash, 32, 9, WellKnownKeyCheck));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationOffer(ReconciliationItemDomain.ContentHash, 32, 8, new byte[7]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationOffer(ReconciliationItemDomain.ContentHash, 32, 8, new byte[9]));

        //Independently allocated key-check buffers with equal bytes yield equal offers with equal hash codes.
        ReconciliationOffer first = new(ReconciliationItemDomain.ContentHash, 32, 8, Convert.FromHexString("630c7d8175160642"));
        ReconciliationOffer second = new(ReconciliationItemDomain.ContentHash, 32, 8, Convert.FromHexString("630c7d8175160642"));
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
    }


    [TestMethod]
    public void BatchValidationRejectsBadArgumentsAndEqualityIsElementWise()
    {
        ReconciliationSymbol symbol = new(SumEight, ChecksumEight);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationSymbolBatch(-1, [symbol]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbolBatch(0, default));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbolBatch(0, []));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbolBatch(0, [symbol, null!]));

        //A batch's symbols must share the first symbol's field widths; a mixed-width array fails closed.
        ReconciliationSymbol narrow = new(new byte[4], new byte[8]);
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbolBatch(0, [symbol, narrow]));

        ReconciliationSymbolBatch batch = new(4, [symbol]);
        ReconciliationSymbolBatch same = new(4, [new ReconciliationSymbol(SumEight.ToArray(), ChecksumEight.ToArray())]);
        Assert.AreEqual(batch, same);

        ReconciliationSymbolBatch differentIndex = new(0, [symbol]);
        Assert.AreNotEqual(batch, differentIndex);
    }


    [TestMethod]
    public void DoneValidationRejectsNonPositiveCounts()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationDone(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationDone(-1));

        ReconciliationDone done = new(6);
        Assert.AreEqual(6, done.AbsorbedCount);
    }


    [TestMethod]
    public void FetchValidationRejectsBadArgumentsCopiesAndComparesByContent()
    {
        byte[] one = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] two = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationFetch(default));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationFetch([]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationFetch([ReadOnlyMemory<byte>.Empty]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationFetch([one, new byte[4]]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationFetch([one, one.ToArray()]));

        //The constructor copies each item, so mutating the source buffer afterwards does not change the record.
        byte[] mutable = [.. one];
        ReconciliationFetch fetch = new([mutable, two]);
        mutable[0] = 0xFF;
        Assert.AreSequenceEqual(one, fetch.Items[0].ToArray());

        ReconciliationFetch same = new([one.ToArray(), two.ToArray()]);
        Assert.AreEqual(fetch, same);
    }


    [TestMethod]
    public void ElementEntryAndElementsValidationIncludeTheElementValue()
    {
        byte[] one = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] two = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElementEntry<string>(ReadOnlyMemory<byte>.Empty, "zeta"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ReconciliationElementEntry<string>(one, null!));

        ReconciliationElementEntry<string> entry = new(one, "zeta");
        ReconciliationElementEntry<string> sameEntry = new(one.ToArray(), "zeta");
        Assert.AreEqual(entry, sameEntry);

        //Equality includes the element value, so an entry with the same item but a different element differs.
        ReconciliationElementEntry<string> differentElement = new(one.ToArray(), "eta");
        Assert.AreNotEqual(entry, differentElement);

        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElements<string>(default));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElements<string>([]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElements<string>([entry, null!]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElements<string>([entry, new ReconciliationElementEntry<string>(one.ToArray(), "other")]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElements<string>([entry, new ReconciliationElementEntry<string>(new byte[4], "short")]));

        ReconciliationElements<string> elements = new([entry, new ReconciliationElementEntry<string>(two, "eta")]);
        ReconciliationElements<string> sameElements = new([new ReconciliationElementEntry<string>(one.ToArray(), "zeta"), new ReconciliationElementEntry<string>(two.ToArray(), "eta")]);
        Assert.AreEqual(elements, sameElements);
    }


    [TestMethod]
    public void EnvelopeFactoriesNullCheckAndSetExactlyOneSlot()
    {
        ReconciliationOffer offer = ReconciliationOffer.FromContract(WellKnownContract);
        ReconciliationSymbolBatch symbols = new(0, [new ReconciliationSymbol(SumEight, ChecksumEight)]);
        ReconciliationDone done = new(6);
        ReconciliationFetch fetch = new([new byte[8]]);
        ReconciliationElements<string> elements = new([new ReconciliationElementEntry<string>(new byte[8], "zeta")]);

        Assert.ThrowsExactly<ArgumentNullException>(() => ReconciliationEnvelope<string>.ForOffer(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ReconciliationEnvelope<string>.ForSymbols(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ReconciliationEnvelope<string>.ForDone(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ReconciliationEnvelope<string>.ForFetch(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => ReconciliationEnvelope<string>.ForElements(null!));

        ReconciliationEnvelope<string> offerEnvelope = ReconciliationEnvelope<string>.ForOffer(offer);
        Assert.AreEqual(offer, offerEnvelope.Offer);
        Assert.IsNull(offerEnvelope.Symbols);
        Assert.IsNull(offerEnvelope.Done);
        Assert.IsNull(offerEnvelope.Fetch);
        Assert.IsNull(offerEnvelope.Elements);

        ReconciliationEnvelope<string> symbolsEnvelope = ReconciliationEnvelope<string>.ForSymbols(symbols);
        Assert.AreEqual(symbols, symbolsEnvelope.Symbols);
        Assert.IsNull(symbolsEnvelope.Offer);
        Assert.IsNull(symbolsEnvelope.Done);
        Assert.IsNull(symbolsEnvelope.Fetch);
        Assert.IsNull(symbolsEnvelope.Elements);

        ReconciliationEnvelope<string> doneEnvelope = ReconciliationEnvelope<string>.ForDone(done);
        Assert.AreEqual(done, doneEnvelope.Done);
        Assert.IsNull(doneEnvelope.Offer);
        Assert.IsNull(doneEnvelope.Symbols);
        Assert.IsNull(doneEnvelope.Fetch);
        Assert.IsNull(doneEnvelope.Elements);

        ReconciliationEnvelope<string> fetchEnvelope = ReconciliationEnvelope<string>.ForFetch(fetch);
        Assert.AreEqual(fetch, fetchEnvelope.Fetch);
        Assert.IsNull(fetchEnvelope.Offer);
        Assert.IsNull(fetchEnvelope.Symbols);
        Assert.IsNull(fetchEnvelope.Done);
        Assert.IsNull(fetchEnvelope.Elements);

        ReconciliationEnvelope<string> elementsEnvelope = ReconciliationEnvelope<string>.ForElements(elements);
        Assert.AreEqual(elements, elementsEnvelope.Elements);
        Assert.IsNull(elementsEnvelope.Offer);
        Assert.IsNull(elementsEnvelope.Symbols);
        Assert.IsNull(elementsEnvelope.Done);
        Assert.IsNull(elementsEnvelope.Fetch);
    }


    /// <summary>Pins that GetHashCode folds in StartIndex, not only the symbol content.</summary>
    [TestMethod]
    public void BatchHashCodeIncorporatesStartIndex()
    {
        ReconciliationSymbol symbol = new(SumEight, ChecksumEight);
        ReconciliationSymbolBatch batch = new(4, [symbol]);
        ReconciliationSymbolBatch differentIndex = new(9, [new ReconciliationSymbol(SumEight.ToArray(), ChecksumEight.ToArray())]);

        Assert.AreNotEqual(batch.GetHashCode(), differentIndex.GetHashCode());
    }


    /// <summary>Pins that a null first symbol is rejected before its width fields are read.</summary>
    [TestMethod]
    public void BatchValidationRejectsNullFirstSymbol()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbolBatch(0, [null!]));
    }


    /// <summary>Pins that GetHashCode folds in each symbol's content, not only StartIndex.</summary>
    [TestMethod]
    public void BatchHashCodeIncorporatesSymbolContent()
    {
        ReconciliationSymbol symbol = new(SumEight, ChecksumEight);
        ReconciliationSymbolBatch batch = new(4, [symbol]);

        byte[] differentSum = [0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48];
        ReconciliationSymbolBatch differentSymbol = new(4, [new ReconciliationSymbol(differentSum, ChecksumEight)]);

        Assert.AreNotEqual(batch.GetHashCode(), differentSymbol.GetHashCode());
    }


    /// <summary>Returns a null batch through an opaque call so the null comparison in <see cref="BatchEqualsReturnsFalseForNullOther"/> is not folded away by the analyzer.</summary>
    private static ReconciliationSymbolBatch? NullBatch() => null;


    /// <summary>Pins that a batch compares unequal to a null batch rather than defaulting to true.</summary>
    [TestMethod]
    public void BatchEqualsReturnsFalseForNullOther()
    {
        ReconciliationSymbol symbol = new(SumEight, ChecksumEight);
        ReconciliationSymbolBatch batch = new(4, [symbol]);

        Assert.IsFalse(batch.Equals(NullBatch()));
    }


    /// <summary>Pins that batch equality compares symbols element-wise, not just start index and count.</summary>
    [TestMethod]
    public void BatchEqualityDistinguishesDifferingSymbolContentAtSameIndex()
    {
        ReconciliationSymbol symbol = new(SumEight, ChecksumEight);
        ReconciliationSymbolBatch batch = new(4, [symbol]);

        byte[] differentSum = [0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48];
        ReconciliationSymbolBatch differentContent = new(4, [new ReconciliationSymbol(differentSum, ChecksumEight)]);

        Assert.AreNotEqual(batch, differentContent);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationOffer.FromContract"/> and <see cref="ReconciliationOffer.Matches"/> both
    /// throw <see cref="ArgumentNullException"/> for a null contract, as their XML docs contract.
    /// </summary>
    [TestMethod]
    public void OfferFromContractAndMatchesRejectNullContract()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ReconciliationOffer.FromContract(null!));

        ReconciliationOffer offer = ReconciliationOffer.FromContract(WellKnownContract);
        Assert.ThrowsExactly<ArgumentNullException>(() => offer.Matches(null!));
    }


    /// <summary>Pins that <see cref="ReconciliationFetch.Equals(ReconciliationFetch?)"/> returns false, never true, when compared against a null fetch.</summary>
    [TestMethod]
    public void FetchEqualsReturnsFalseAgainstNullFetch()
    {
        ReconciliationFetch fetch = new([new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 }]);

        Assert.IsFalse(fetch.Equals(NullOfReconciliationFetch()));
    }


    /// <summary>Returns a null fetch from an opaque helper so the null comparison is not folded at compile time.</summary>
    private static ReconciliationFetch? NullOfReconciliationFetch() => null;


    /// <summary>Pins that two fetches whose single item shares its length but differs in content are unequal.</summary>
    [TestMethod]
    public void FetchEqualsIsFalseWhenSameLengthItemsDifferInContent()
    {
        byte[] one = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] two = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        ReconciliationFetch left = new([one]);
        ReconciliationFetch right = new([two]);

        Assert.AreNotEqual(left, right);
    }


    /// <summary>Pins that the hash code reflects every entry's content, not just the run's shape.</summary>
    [TestMethod]
    public void ElementsHashCodeReflectsEveryEntry()
    {
        byte[] itemOne = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] itemTwo = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        ReconciliationElements<string> first = new([new ReconciliationElementEntry<string>(itemOne, "alpha"), new ReconciliationElementEntry<string>(itemTwo, "beta")]);
        ReconciliationElements<string> differsAtSecondEntry = new([new ReconciliationElementEntry<string>(itemOne, "alpha"), new ReconciliationElementEntry<string>(itemTwo, "gamma")]);

        Assert.AreNotEqual(first.GetHashCode(), differsAtSecondEntry.GetHashCode());
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationOffer.GetHashCode"/> mixes every field: an offer that differs from a
    /// baseline in exactly one field must not collide with it.
    /// </summary>
    [TestMethod]
    public void OfferHashCodeMixesEveryField()
    {
        ReconciliationOffer baseline = new(ReconciliationItemDomain.ContentHash, 32, 8, WellKnownKeyCheck);

        ReconciliationOffer differentDomain = new(ReconciliationItemDomain.Structural, 32, 8, WellKnownKeyCheck);
        Assert.AreNotEqual(baseline.GetHashCode(), differentDomain.GetHashCode());

        ReconciliationOffer differentItemWidth = new(ReconciliationItemDomain.ContentHash, 64, 8, WellKnownKeyCheck);
        Assert.AreNotEqual(baseline.GetHashCode(), differentItemWidth.GetHashCode());

        ReconciliationOffer differentChecksumWidth = new(ReconciliationItemDomain.ContentHash, 32, 4, WellKnownKeyCheck);
        Assert.AreNotEqual(baseline.GetHashCode(), differentChecksumWidth.GetHashCode());

        ReconciliationOffer differentKeyCheck = new(ReconciliationItemDomain.ContentHash, 32, 8, Convert.FromHexString("0011223344556677"));
        Assert.AreNotEqual(baseline.GetHashCode(), differentKeyCheck.GetHashCode());
    }


    /// <summary>Pins that <see cref="ReconciliationFetch.GetHashCode"/> mixes each item's bytes, so fetches with differing item content hash differently.</summary>
    [TestMethod]
    public void FetchGetHashCodeDiffersWhenItemContentDiffers()
    {
        byte[] one = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] two = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        ReconciliationFetch left = new([one]);
        ReconciliationFetch right = new([two]);

        Assert.AreNotEqual(left.GetHashCode(), right.GetHashCode());
    }


    /// <summary>
    /// Pins that item width validation is inclusive at both ends of one through 1024, and that checksum width
    /// validation is inclusive at its lower end of one, by constructing successfully at each boundary.
    /// </summary>
    [TestMethod]
    public void OfferConstructionAcceptsInclusiveBoundaryWidths()
    {
        ReconciliationOffer minItemWidth = new(ReconciliationItemDomain.ContentHash, 1, 8, WellKnownKeyCheck);
        Assert.AreEqual(1, minItemWidth.ItemWidth);

        ReconciliationOffer maxItemWidth = new(ReconciliationItemDomain.ContentHash, 1024, 8, WellKnownKeyCheck);
        Assert.AreEqual(1024, maxItemWidth.ItemWidth);

        ReconciliationOffer minChecksumWidth = new(ReconciliationItemDomain.ContentHash, 32, 1, WellKnownKeyCheck);
        Assert.AreEqual(1, minChecksumWidth.ChecksumWidth);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationOffer.Equals(ReconciliationOffer?)"/> compares by field content for
    /// distinct instances rather than short-circuiting to true on anything but reference identity.
    /// </summary>
    [TestMethod]
    public void OfferEqualsComparesFieldsForDistinctInstances()
    {
        ReconciliationOffer first = new(ReconciliationItemDomain.ContentHash, 32, 8, WellKnownKeyCheck);
        ReconciliationOffer second = new(ReconciliationItemDomain.ContentHash, 16, 8, WellKnownKeyCheck);

        Assert.AreNotEqual(first, second);
    }


    /// <summary>Pins that equality is false whenever entry counts differ or any entry's content differs, even
    /// though the compared instances are always different references.</summary>
    [TestMethod]
    public void ElementsInequalityIsDetectedByLengthAndByEntryContent()
    {
        byte[] itemOne = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] itemTwo = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        ReconciliationElementEntry<string> entryOne = new(itemOne, "alpha");

        ReconciliationElements<string> baseline = new([entryOne, new ReconciliationElementEntry<string>(itemTwo, "beta")]);
        ReconciliationElements<string> shorter = new([entryOne]);
        ReconciliationElements<string> differentContent = new([entryOne, new ReconciliationElementEntry<string>(itemTwo, "gamma")]);

        //Fewer entries must never compare equal, even before any entry is inspected.
        Assert.AreNotEqual(baseline, shorter);

        //The same entry count with one differing element must still compare unequal.
        Assert.AreNotEqual(baseline, differentContent);
    }


    /// <summary>Pins that a resolution set with a null first entry is rejected before any other entry is examined.</summary>
    [TestMethod]
    public void ElementsRejectsANullFirstEntry()
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationElements<string>([null!]));

        Assert.AreEqual("entries", exception.ParamName);
    }


    /// <summary>Pins that a resolution set never reports equal to a null comparand.</summary>
    [TestMethod]
    public void ElementsIsNeverEqualToNull()
    {
        byte[] item = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        ReconciliationElements<string> elements = new([new ReconciliationElementEntry<string>(item, "zeta")]);

        Assert.IsFalse(elements.Equals(NullElements()));
    }


    /// <summary>Returns a null elements set from an opaque helper so the null comparison is not folded at compile time.</summary>
    private static ReconciliationElements<string>? NullElements() => null;


    /// <summary>Returns a null offer from an opaque helper so the null comparison is not folded at compile time.</summary>
    private static ReconciliationOffer? NullOffer() => null;


    /// <summary>
    /// Pins that <see cref="ReconciliationOffer.Equals(ReconciliationOffer?)"/> reports false, never true, when
    /// compared against a null instance.
    /// </summary>
    [TestMethod]
    public void OfferEqualsReturnsFalseForNull()
    {
        ReconciliationOffer offer = ReconciliationOffer.FromContract(WellKnownContract);

        Assert.IsFalse(offer.Equals(NullOffer()));
    }


    /// <summary>Pins that two fetches carrying a different number of items are unequal.</summary>
    [TestMethod]
    public void FetchEqualsIsFalseWhenItemCountsDiffer()
    {
        byte[] one = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];
        byte[] two = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

        ReconciliationFetch left = new([one]);
        ReconciliationFetch right = new([one, two]);

        Assert.AreNotEqual(left, right);
    }
}
