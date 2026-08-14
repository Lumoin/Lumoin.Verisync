using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Black-box proofs of the two end-to-end contracts the never-relocate arena introduces, exercised only
/// through the public encoder and decoder: that a <see cref="ReconciliationInjectivityEnforcement.Strict"/>
/// membership key seeded into the first arena block still matches after later appends grow the arena across
/// several blocks, and that the items handed out by <c>DecodedItems</c> are owned copies that stay readable
/// after the decoder (and its pool) are disposed. If the arena ever relocated a stored item the first proof
/// would let a duplicate add slip through; if <c>DecodedItems</c> returned the raw arena view the second proof
/// would read recycled pooled memory after dispose.
/// </summary>
/// <remarks>
/// The disposal proof constructs a pool and recycles its segments on dispose, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> alongside the other pool-using suites.
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class ReconciliationItemArenaInvariantTests
{
    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

    private static byte[] B1 { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];


    [TestMethod]
    public void StrictMembershipSurvivesArenaGrowth()
    {
        const int Stride = 8;
        const int Items = 1000;

        //A zero hint pins the small initial arena block, so adding a thousand distinct items forces the arena
        //across several block grows. The first item's membership key views the very first block; if any grow
        //relocated that block the re-add below would not be detected as a duplicate.
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.Strict, BaseMemoryPool.Shared, cellCapacityHint: 0);

        byte[] firstItem = BuildItem(Stride, 0);
        encoder.Add(firstItem);
        for(int n = 1; n < Items; n++)
        {
            encoder.Add(BuildItem(Stride, n));
        }

        //Re-adding the FIRST item, whose key views block zero, must still be rejected as a duplicate after all
        //the later blocks were appended — the no-move arena kept that key's viewed bytes valid.
        Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Add(BuildItem(Stride, 0)));

        //A brand-new item that was never added must not throw, proving the rejection above was a genuine
        //membership hit and not a blanket failure of the grown arena.
        encoder.Add(BuildItem(Stride, Items));
    }


    [TestMethod]
    public void DecodedItemsRemainReadableAfterDecoderDisposal()
    {
        //A small structural difference over the well-known key: the left set holds A1, A2, B1, the right holds
        //A1, A2, A3, so the symmetric difference the decoder recovers is the two items B1 and A3. A pool backs
        //the decoder so its arena segments are genuinely recycled when it is disposed below.
        IReadOnlyList<ReadOnlyMemory<byte>> captured;
        using(BaseMemoryPool pool = new())
        {
            using ReconciliationEncoder left = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, cellCapacityHint: 0);
            left.Add(A1);
            left.Add(A2);
            left.Add(B1);

            using ReconciliationEncoder right = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, cellCapacityHint: 0);
            right.Add(A1);
            right.Add(A2);
            right.Add(A3);

            using ReconciliationDecoder decoder = new(StructuralContract, pool, cellCapacityHint: 0);

            const int Cap = 200;
            for(int n = 0; n < Cap && !decoder.IsComplete; n++)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            }

            Assert.IsTrue(decoder.IsComplete);

            //Capture the decoded items into a local while the decoder (and its arena) are still alive.
            captured = decoder.DecodedItems;
            Assert.HasCount(2, captured);
        }

        //The decoder and the pool are now disposed and the arena segments recycled. The captured items must
        //still read their decoded bytes, proving DecodedItems handed out owned copies rather than arena views:
        //the recovered difference is exactly B1 and A3, in either order.
        byte[][] expected = [B1, A3];
        bool[] matched = new bool[expected.Length];
        foreach(ReadOnlyMemory<byte> item in captured)
        {
            byte[] bytes = item.ToArray();
            Assert.HasCount(8, bytes);

            for(int e = 0; e < expected.Length; e++)
            {
                if(!matched[e] && bytes.AsSpan().SequenceEqual(expected[e]))
                {
                    matched[e] = true;
                    break;
                }
            }
        }

        for(int e = 0; e < expected.Length; e++)
        {
            Assert.IsTrue(matched[e], "A decoded item read back as stale or missing after the decoder was disposed.");
        }
    }


    /// <summary>
    /// Builds a deterministic item of the given width without System.Random (CA5394).
    /// </summary>
    /// <remarks>
    /// The little-endian index occupies the leading bytes so distinct indices yield distinct full-width items
    /// across the whole tested range, then a position-derived tail fills the remaining stride; this avoids the
    /// spurious Strict duplicate a single-byte index pattern would hit once the index wraps modulo 256.
    /// </remarks>
    private static byte[] BuildItem(int stride, int index)
    {
        byte[] item = new byte[stride];
        long value = index;
        for(int b = 0; b < stride; b++)
        {
            item[b] = (byte)((value & 0xFF) ^ (byte)(b * 7));
            value >>= 8;
        }

        return item;
    }
}
