using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The normative byte-precise vectors from the reconciliation contract: SipHash-2-4 outputs, checksum
/// truncation widths, index walks, the encoder stream, and the difference-and-decode progression
/// including the built-in near-miss whose <c>Sum</c> collides with a non-member item.
/// </summary>
[TestClass]
internal sealed class ReconciliationVectorTests
{
    //The canonical SipHash key bytes 00 01 .. 0f split into little-endian halves.
    private const ulong KeyLow = 0x0706050403020100UL;
    private const ulong KeyHigh = 0x0F0E0D0C0B0A0908UL;


    //W3 = 01 02 03 04 05 06 07 08, used as a1 in the stream and difference vectors.
    private static byte[] W3 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] W1 { get; } = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];

    private static byte[] W2 { get; } = [.. Enumerable.Repeat((byte)0xFF, 32)];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

    private static byte[] B1 { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);


    [TestMethod]
    public void SipHashMatchesOfficialVectors()
    {
        ulong[] expected =
        [
            0x726fdb47dd0e0e31UL,
            0x74f839c593dc67fdUL,
            0x0d6c8009d9a94f5aUL,
            0x85676696d7fb7e2dUL,
            0xcf2794e0277187b7UL,
            0x18765564cd99a68dUL,
            0xcbc9466e58fee3ceUL,
            0xab0200f58b01d137UL,
            0x93f5f5799a932462UL
        ];

        for(int n = 0; n < expected.Length; n++)
        {
            byte[] input = [.. Enumerable.Range(0, n).Select(i => (byte)i)];
            Assert.AreEqual(expected[n], ReconciliationChecksum.Compute(KeyLow, KeyHigh, input));
        }
    }


    [TestMethod]
    public void WriteTruncatesToTheRequestedWidth()
    {
        //W3's checksum under the well-known default key.
        ulong checksum = ReconciliationChecksum.Compute(ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh, W3);
        Assert.AreEqual(0xDF849B943F9867CCUL, checksum);

        Span<byte> width8 = stackalloc byte[8];
        ReconciliationChecksum.Write(checksum, width8);
        byte[] expected8 = [0xCC, 0x67, 0x98, 0x3F, 0x94, 0x9B, 0x84, 0xDF];
        CollectionAssert.AreEqual(expected8, width8.ToArray());

        Span<byte> width4 = stackalloc byte[4];
        ReconciliationChecksum.Write(checksum, width4);
        byte[] expected4 = [0xCC, 0x67, 0x98, 0x3F];
        CollectionAssert.AreEqual(expected4, width4.ToArray());

        Span<byte> width1 = stackalloc byte[1];
        ReconciliationChecksum.Write(checksum, width1);
        byte[] expected1 = [0xCC];
        CollectionAssert.AreEqual(expected1, width1.ToArray());
    }


    [TestMethod]
    public void WalkSeedsAndIndicesMatchTheVectors()
    {
        long[] indicesW1 = [0, 1, 2, 3, 4, 7, 9, 44, 47, 73, 158, 286];
        long[] indicesW2 = [0, 2, 3, 5, 10, 12, 17, 39, 76, 143, 332, 368];
        long[] indicesW3 = [0, 1, 2, 3, 13, 27, 47, 49, 251, 667, 939, 1066];

        AssertWalk(W1, 0xA20DCC8CC2DA0DC9UL, indicesW1);
        AssertWalk(W2, 0x7C63691EB579E2E6UL, indicesW2);
        AssertWalk(W3, 0x35D15A6DAA5B9180UL, indicesW3);
    }


    [TestMethod]
    public void EncoderStreamMatchesTheVector()
    {
        using ReconciliationEncoder encoder = new(StructuralContract);
        encoder.Add(W3);
        encoder.Add(A2);
        encoder.Add(A3);

        (string Sum, string Checksum)[] expected =
        [
            ("3132333435363738", "A57A71E920BF57A9"),
            ("2020202020202020", "7D042E94AE36B153"),
            ("3132333435363738", "A57A71E920BF57A9"),
            ("3132333435363738", "A57A71E920BF57A9"),
            ("0000000000000000", "0000000000000000"),
            ("2122232425262728", "B163B6AB3AAD358C"),
            ("0000000000000000", "0000000000000000"),
            ("0000000000000000", "0000000000000000")
        ];

        ReconciliationSymbol[] produced = new ReconciliationSymbol[expected.Length];
        for(int n = 0; n < expected.Length; n++)
        {
            produced[n] = encoder.ProduceNext();
            AssertSymbol(expected[n].Sum, expected[n].Checksum, produced[n]);
        }

        //SymbolAt re-reads the same produced cells.
        for(int n = 0; n < expected.Length; n++)
        {
            AssertSymbol(expected[n].Sum, expected[n].Checksum, encoder.SymbolAt(n));
        }
    }


    [TestMethod]
    public void DifferenceStreamDecodesToTheTrueDifference()
    {
        using ReconciliationEncoder left = new(StructuralContract);
        left.Add(W3);
        left.Add(A2);
        left.Add(A3);

        using ReconciliationEncoder right = new(StructuralContract);
        right.Add(W3);
        right.Add(B1);

        (string Sum, string Checksum)[] expected =
        [
            ("0102030405060708", "BF123749482234A4"),
            ("1010101010101010", "676C6834C6ABD25E"),
            ("3030303030303030", "691DE9D6B424D376"),
            ("3030303030303030", "691DE9D6B424D376"),
            ("0000000000000000", "0000000000000000"),
            ("2122232425262728", "B163B6AB3AAD358C"),
            ("3132333435363738", "D60FDE9FFC06E7D2"),
            ("0000000000000000", "0000000000000000")
        ];

        using ReconciliationDecoder decoder = new(StructuralContract);
        for(int n = 0; n < expected.Length; n++)
        {
            ReconciliationSymbol difference = left.ProduceNext().Combine(right.ProduceNext());
            AssertSymbol(expected[n].Sum, expected[n].Checksum, difference);
            decoder.Absorb(difference);

            //Not complete through symbols 1-5; complete immediately after absorbing symbol index 5.
            if(n < 5)
            {
                Assert.IsFalse(decoder.IsComplete);
            }
            else
            {
                Assert.IsTrue(decoder.IsComplete);
            }
        }

        string[] expectedSet = [.. new[] { A2, A3, B1 }.Select(Convert.ToHexString).Order()];
        string[] decodedSet = [.. decoder.DecodedItems.Select(item => Convert.ToHexString(item.Span)).Order()];
        CollectionAssert.AreEqual(expectedSet, decodedSet);
    }


    [TestMethod]
    public void DifferenceCellZeroIsAnImpureNearMiss()
    {
        using ReconciliationEncoder left = new(StructuralContract);
        left.Add(W3);
        left.Add(A2);
        left.Add(A3);

        using ReconciliationEncoder right = new(StructuralContract);
        right.Add(W3);
        right.Add(B1);

        ReconciliationSymbol cellZero = left.ProduceNext().Combine(right.ProduceNext());

        //The cell is not neutral and its Sum coincides with a1's bytes, yet the cell is impure: the
        //checksum field is the XOR of three item checksums, so a correct purity test rejects it.
        Assert.IsFalse(cellZero.IsNeutral);
        CollectionAssert.AreEqual(W3, cellZero.Sum.ToArray());

        using ReconciliationDecoder decoder = new(StructuralContract);
        decoder.Absorb(cellZero);

        Assert.HasCount(0, decoder.DecodedItems);
    }


    private static void AssertWalk(byte[] item, ulong expectedState, long[] expectedIndices)
    {
        ReconciliationWalkPosition position = ReconciliationIndexWalk.Start(item);
        Assert.AreEqual(0L, position.Index);
        Assert.AreEqual(expectedState, position.State);

        long[] indices = new long[expectedIndices.Length];
        indices[0] = position.Index;
        for(int i = 1; i < expectedIndices.Length; i++)
        {
            position = ReconciliationIndexWalk.Next(position);
            indices[i] = position.Index;
        }

        CollectionAssert.AreEqual(expectedIndices, indices);
    }


    private static void AssertSymbol(string expectedSumHex, string expectedChecksumHex, ReconciliationSymbol symbol)
    {
        CollectionAssert.AreEqual(Convert.FromHexString(expectedSumHex), symbol.Sum.ToArray());
        CollectionAssert.AreEqual(Convert.FromHexString(expectedChecksumHex), symbol.Checksum.ToArray());
    }
}
