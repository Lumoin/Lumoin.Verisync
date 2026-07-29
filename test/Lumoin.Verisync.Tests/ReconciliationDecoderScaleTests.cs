using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Scale coverage for <see cref="ReconciliationDecoder"/> beyond the bounded law tests: large symmetric
/// differences decode exactly within the cap, independently built decoders are confluent over the same
/// difference stream, an equal-set difference is quiescent even over a large shared corpus, and the pinned
/// phase-1 difference vector still completes after exactly six absorbed symbols. The default contract is
/// structural, width 8, checksum 8, well-known key.
/// </summary>
[TestClass]
internal sealed class ReconciliationDecoderScaleTests
{
    //The structural difference sizes the large-d exactness sweep covers.
    private static int[] DifferenceSizes { get; } = [64, 257, 1000];

    //Pinned seeds selecting deterministic disjoint counter ranges per sweep; no System.Random anywhere.
    private static long[] Seeds { get; } = [1L, 7L, 23L, 101L];

    //Pinned phase-1 difference vector items: a1 lies only in the left set, the other three are the true
    //difference {a2, a3, b1}.
    private static byte[] A1 { get; } = Convert.FromHexString("0102030405060708");

    private static byte[] A2 { get; } = Convert.FromHexString("1112131415161718");

    private static byte[] A3 { get; } = Convert.FromHexString("2122232425262728");

    private static byte[] B1 { get; } = Convert.FromHexString("3132333435363738");

    private static ReconciliationContract Contract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);


    [TestMethod]
    public void LargeDifferencesDecodeExactlyWithinTheCap()
    {
        foreach(int d in DifferenceSizes)
        {
            foreach(long seed in Seeds)
            {
                (byte[][] shared, byte[][] leftOnly, byte[][] rightOnly) = BuildDifference(seed, d);

                //The encoders are built inline rather than through the Encoder helper so the disposable owner is
                //a directly-constructed local the dispose analysis recognizes across the nested sweep.
                using ReconciliationEncoder left = new(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
                foreach(byte[] item in (byte[][])[.. shared, .. leftOnly])
                {
                    left.Add(item);
                }

                using ReconciliationEncoder right = new(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
                foreach(byte[] item in (byte[][])[.. shared, .. rightOnly])
                {
                    right.Add(item);
                }

                int cap = 100 + (20 * d);
                using ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
                int absorbed = 0;
                while(!decoder.IsComplete && absorbed < cap)
                {
                    decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
                    absorbed++;
                }

                Assert.IsTrue(decoder.IsComplete);

                string[] expected = [.. HexSet([.. leftOnly, .. rightOnly])];
                string[] decoded = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
                Assert.AreSequenceEqual(expected, decoded);
            }
        }
    }


    [TestMethod]
    public void TwoDecodersOverTheSameStreamAreConfluent()
    {
        (byte[][] shared, byte[][] leftOnly, byte[][] rightOnly) = BuildDifference(43L, 257);

        using ReconciliationEncoder leftOne = Encoder([.. shared, .. leftOnly]);
        using ReconciliationEncoder rightOne = Encoder([.. shared, .. rightOnly]);
        using ReconciliationEncoder leftTwo = Encoder([.. shared, .. leftOnly]);
        using ReconciliationEncoder rightTwo = Encoder([.. shared, .. rightOnly]);

        int d = leftOnly.Length + rightOnly.Length;
        int cap = 100 + (20 * d);

        using ReconciliationDecoder decoderOne = new(Contract, BaseMemoryPool.Shared);
        using ReconciliationDecoder decoderTwo = new(Contract, BaseMemoryPool.Shared);
        int absorbed = 0;
        while((!decoderOne.IsComplete || !decoderTwo.IsComplete) && absorbed < cap)
        {
            decoderOne.Absorb(leftOne.ProduceNext().Combine(rightOne.ProduceNext()));
            decoderTwo.Absorb(leftTwo.ProduceNext().Combine(rightTwo.ProduceNext()));
            absorbed++;
        }

        Assert.IsTrue(decoderOne.IsComplete);
        Assert.IsTrue(decoderTwo.IsComplete);

        string[] decodedOne = [.. decoderOne.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
        string[] decodedTwo = [.. decoderTwo.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
        Assert.AreSequenceEqual(decodedOne, decodedTwo);
    }


    [TestMethod]
    public void EqualSetsAreQuiescentOverALargeCorpus()
    {
        (byte[][] shared, _, _) = BuildDifference(91L, 0);
        Assert.HasCount(1000, shared);

        using ReconciliationEncoder left = Encoder(shared);
        using ReconciliationEncoder right = Encoder(shared);

        using ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
        decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));

        //An equal set difference is complete on the very first absorbed symbol with nothing decoded, even
        //when the shared corpus is large.
        Assert.IsTrue(decoder.IsComplete);
        Assert.AreEqual(1, decoder.AbsorbedCount);
        Assert.HasCount(0, decoder.DecodedItems);
    }


    [TestMethod]
    public void PinnedDifferenceVectorCompletesAfterExactlySixSymbols()
    {
        using ReconciliationEncoder left = Encoder([A1, A2, A3]);
        using ReconciliationEncoder right = Encoder([A1, B1]);

        using ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
        int absorbed = 0;
        while(!decoder.IsComplete)
        {
            decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            absorbed++;
        }

        //The observable completion point is pinned: not complete through symbols 1-5, complete the instant
        //symbol index 5 (the sixth) is absorbed.
        Assert.AreEqual(6, absorbed);
        Assert.AreEqual(6, decoder.AbsorbedCount);

        string[] expected = [.. HexSet([A2, A3, B1])];
        string[] decoded = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
        Assert.AreSequenceEqual(expected, decoded);
    }


    //Builds a (shared, leftOnly, rightOnly) triple from disjoint counter ranges so the three lists are
    //pairwise disjoint by construction: the shared corpus is a fixed 1000 items, and the d difference items
    //split evenly between the two sides. Each item is the eight little-endian bytes of a distinct counter.
    private static (byte[][] Shared, byte[][] LeftOnly, byte[][] RightOnly) BuildDifference(long seed, int d)
    {
        long sharedBase = 1L + ((seed % 7) * 10_000_000L);
        long leftBase = 1_000_000_001L + ((seed % 7) * 10_000_000L);
        long rightBase = 2_000_000_001L + ((seed % 7) * 10_000_000L);

        byte[][] shared = Range(sharedBase, 1000);
        int leftCount = (d + 1) / 2;
        int rightCount = d - leftCount;
        byte[][] leftOnly = Range(leftBase, leftCount);
        byte[][] rightOnly = Range(rightBase, rightCount);

        return (shared, leftOnly, rightOnly);
    }


    private static byte[][] Range(long baseCounter, int count)
    {
        byte[][] items = new byte[count][];
        for(int i = 0; i < count; i++)
        {
            items[i] = ItemOf(baseCounter + i);
        }

        return items;
    }


    private static ReconciliationEncoder Encoder(byte[][] items)
    {
        ReconciliationEncoder encoder = new(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(byte[] item in items)
        {
            encoder.Add(item);
        }

        return encoder;
    }


    private static IEnumerable<string> HexSet(byte[][] items)
    {
        return items.Select(Convert.ToHexString).Order();
    }


    private static byte[] ItemOf(long counter) => BitConverter.GetBytes(counter);
}
