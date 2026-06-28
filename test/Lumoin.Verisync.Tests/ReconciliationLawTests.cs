using CsCheck;
using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The reconciliation kernel's algebraic laws as CsCheck properties over randomly constructed
/// symmetric differences: the GF(2) homomorphism, history erasure, decode completeness and exactness,
/// quiescence, monotone knowledge, soundness under bit-flips, and the index-walk invariants. The
/// default contract is structural, width 8, well-known key.
/// </summary>
[TestClass]
internal sealed class ReconciliationLawTests
{
    private const int K = 16;

    private static ReconciliationContract Contract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    //Disjoint seed ranges per bucket guarantee the three item lists are pairwise disjoint by construction;
    //items are the 8 little-endian bytes of each seed, distinct within a bucket (dedupe by seed).
    private static Gen<(byte[][] Shared, byte[][] LeftOnly, byte[][] RightOnly)> GenDifference { get; } =
        Gen.Select(
            GenBucket(1L),
            GenBucket(1_000_001L),
            GenBucket(2_000_001L),
            static (shared, leftOnly, rightOnly) => (shared, leftOnly, rightOnly));


    [TestMethod]
    public void CombineOfEncodersEqualsEncoderOfTheDifference()
    {
        GenDifference.Sample(difference =>
        {
            ReconciliationEncoder left = Encoder([.. difference.Shared, .. difference.LeftOnly]);
            ReconciliationEncoder right = Encoder([.. difference.Shared, .. difference.RightOnly]);
            ReconciliationEncoder symmetric = Encoder([.. difference.LeftOnly, .. difference.RightOnly]);

            for(int n = 0; n < K; n++)
            {
                Assert.AreEqual(symmetric.ProduceNext(), left.ProduceNext().Combine(right.ProduceNext()));
            }
        });
    }


    [TestMethod]
    public void HistoryIsErasedRegardlessOfConstruction()
    {
        Gen.Select(GenDifference, Gen.Int[0, 1_000_000], static (difference, noise) => (difference, noise)).Sample(input =>
        {
            byte[][] netItems = [.. input.difference.Shared, .. input.difference.LeftOnly];

            //Transient items added then removed, net items added by odd repetition (add, remove, add), and
            //ProduceNext calls interleaved at points driven by the case seed.
            ReconciliationEncoder encoder = new(Contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
            byte[] transient = ItemOf(3_000_001L + (input.noise % 1000));
            int produced = 0;

            encoder.Add(transient);
            for(int i = 0; i < netItems.Length; i++)
            {
                encoder.Add(netItems[i]);
                if(((input.noise >> i) & 1) == 1 && produced < K)
                {
                    _ = encoder.ProduceNext();
                    produced++;
                }
            }

            encoder.Remove(transient);
            if(netItems.Length > 0)
            {
                //Odd repetition keeps the first net item present: remove then add again.
                encoder.Remove(netItems[0]);
                encoder.Add(netItems[0]);
            }

            while(produced < K)
            {
                _ = encoder.ProduceNext();
                produced++;
            }

            ReconciliationEncoder fresh = Encoder(netItems);
            for(int i = 0; i < K; i++)
            {
                Assert.AreEqual(fresh.ProduceNext(), encoder.SymbolAt(i));
            }
        });
    }


    [TestMethod]
    public void DecodeCompletesWithinTheCapAndIsExact()
    {
        GenDifference.Sample(difference =>
        {
            ReconciliationEncoder left = Encoder([.. difference.Shared, .. difference.LeftOnly]);
            ReconciliationEncoder right = Encoder([.. difference.Shared, .. difference.RightOnly]);

            int d = difference.LeftOnly.Length + difference.RightOnly.Length;
            int cap = 100 + (20 * d);

            ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
            int absorbed = 0;
            while(!decoder.IsComplete && absorbed < cap)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
                absorbed++;
            }

            Assert.IsTrue(decoder.IsComplete);

            //A zero-size difference completes on the very first symbol with nothing decoded.
            if(d == 0)
            {
                Assert.AreEqual(1, absorbed);
                Assert.HasCount(0, decoder.DecodedItems);
            }

            string[] expected = [.. HexSet([.. difference.LeftOnly, .. difference.RightOnly])];
            string[] decoded = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
            CollectionAssert.AreEqual(expected, decoded);
        });
    }


    [TestMethod]
    public void EqualSetsAreQuiescentAfterOneSymbol()
    {
        GenDifference.Sample(difference =>
        {
            byte[][] items = [.. difference.Shared, .. difference.LeftOnly];
            ReconciliationEncoder left = Encoder(items);
            ReconciliationEncoder right = Encoder(items);

            ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
            decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));

            Assert.IsTrue(decoder.IsComplete);
            Assert.HasCount(0, decoder.DecodedItems);
        });
    }


    [TestMethod]
    public void KnowledgeIsMonotoneAfterCompletion()
    {
        GenDifference.Sample(difference =>
        {
            ReconciliationEncoder left = Encoder([.. difference.Shared, .. difference.LeftOnly]);
            ReconciliationEncoder right = Encoder([.. difference.Shared, .. difference.RightOnly]);

            int d = difference.LeftOnly.Length + difference.RightOnly.Length;
            int cap = 100 + (20 * d);

            ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
            int absorbed = 0;
            while(!decoder.IsComplete && absorbed < cap)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
                absorbed++;
            }

            Assert.IsTrue(decoder.IsComplete);

            string[] before = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
            for(int i = 0; i < 5; i++)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            }

            Assert.IsTrue(decoder.IsComplete);
            string[] after = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
            CollectionAssert.AreEqual(before, after);
        });
    }


    [TestMethod]
    public void FlippingOneBitNeverYieldsAWrongSet()
    {
        Gen.Select(
            GenDifference,
            Gen.Int[0, 1_000_000],
            Gen.Int[0, 1],
            static (difference, position, field) => (difference, position, field)).Sample(input =>
        {
            ReconciliationEncoder left = Encoder([.. input.difference.Shared, .. input.difference.LeftOnly]);
            ReconciliationEncoder right = Encoder([.. input.difference.Shared, .. input.difference.RightOnly]);

            int d = input.difference.LeftOnly.Length + input.difference.RightOnly.Length;
            int cap = 100 + (20 * d);

            int corruptIndex = input.position % cap;
            int fieldWidth = input.field == 0 ? Contract.ItemWidth : Contract.ChecksumWidth;
            int corruptByte = (input.position / cap) % fieldWidth;
            int corruptBit = input.position % 8;

            ReconciliationDecoder decoder = new(Contract, BaseMemoryPool.Shared);
            int absorbed = 0;
            while(!decoder.IsComplete && absorbed < cap)
            {
                ReconciliationSymbol symbol = left.ProduceNext().Combine(right.ProduceNext());
                if(absorbed == corruptIndex)
                {
                    symbol = Flip(symbol, input.field, corruptByte, corruptBit);
                }

                decoder.Absorb(symbol);
                absorbed++;
            }

            //Soundness: either it never completes, or it completes with exactly the true difference.
            if(decoder.IsComplete)
            {
                string[] expected = [.. HexSet([.. input.difference.LeftOnly, .. input.difference.RightOnly])];
                string[] decoded = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
                CollectionAssert.AreEqual(expected, decoded);
            }
        });
    }


    [TestMethod]
    public void WalkStartsAtZeroIncreasesAndIsPure()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, int.MaxValue], static (width, seed) => (width, seed)).Sample(input =>
        {
            byte[] item = new byte[input.width];
            ulong fill = (ulong)input.seed;
            for(int i = 0; i < item.Length; i++)
            {
                fill = unchecked((fill * 6364136223846793005UL) + 1442695040888963407UL);
                item[i] = (byte)(fill >> 56);
            }

            ReconciliationWalkPosition start = ReconciliationIndexWalk.Start(item);
            Assert.AreEqual(0L, start.Index);

            long[] first = WalkIndices(item, 12);
            for(int i = 1; i < first.Length; i++)
            {
                Assert.IsGreaterThan(first[i - 1], first[i]);
            }

            //Restarting from the same bytes reproduces the identical sequence (pure function of item bytes).
            long[] second = WalkIndices(item, 12);
            CollectionAssert.AreEqual(first, second);
        });
    }


    private static long[] WalkIndices(byte[] item, int count)
    {
        long[] indices = new long[count];
        ReconciliationWalkPosition position = ReconciliationIndexWalk.Start(item);
        indices[0] = position.Index;
        for(int i = 1; i < count; i++)
        {
            position = ReconciliationIndexWalk.Next(position);
            indices[i] = position.Index;
        }

        return indices;
    }


    private static ReconciliationSymbol Flip(ReconciliationSymbol symbol, int field, int byteIndex, int bit)
    {
        byte[] sum = symbol.Sum.ToArray();
        byte[] checksum = symbol.Checksum.ToArray();
        if(field == 0)
        {
            sum[byteIndex] ^= (byte)(1 << bit);
        }
        else
        {
            checksum[byteIndex] ^= (byte)(1 << bit);
        }

        return new ReconciliationSymbol(sum, checksum);
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


    private static byte[] ItemOf(long seed) => BitConverter.GetBytes(seed);


    private static Gen<byte[][]> GenBucket(long baseSeed)
    {
        return Gen.Int[0, 999].Array[0, 8].Select(offsets =>
        {
            long[] seeds = [.. offsets.Select(offset => baseSeed + offset).Distinct()];

            return seeds.Select(ItemOf).ToArray();
        });
    }
}
