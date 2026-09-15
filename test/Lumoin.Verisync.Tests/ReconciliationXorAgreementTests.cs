using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Agreement coverage for the SIMD XOR fold facade and its per-width backends: scalar semantics (the
/// reference every backend must match), byte-for-byte agreement of each vector tier against the scalar
/// backend over pinned lengths and deterministic buffers, the platform re-guard posture on unsupported
/// hosts, length validation, and the wire-visible stream-level agreement where the encoder's emitted
/// symbols equal a scalar-backend reference fold over the pinned vector items.
/// </summary>
[TestClass]
internal sealed class ReconciliationXorAgreementTests
{
    /// <summary>
    /// The sibling batch lengths extended around the vector-width boundaries (128/256/512 bits map to 16/32/64
    /// byte chunks), so every tier exercises full chunks, partial chunks, and a scalar-only tail.
    /// </summary>
    private static readonly int[] Lengths = [0, 1, 2, 3, 7, 8, 31, 32, 33, 64, 257];

    /// <summary>
    /// Two pinned LCG seeds for the deterministic fill; small constants, never System.Random.
    /// </summary>
    private static readonly ulong[] FillSeeds = [0x1111111111111111UL, 0x2468ACE013579BDFUL];

    /// <summary>
    /// The stream vector contract: structural, item width 8, checksum width 8, well-known key.
    /// </summary>
    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    /// <summary>
    /// The phase 1 stream items a1 = W3, a2, a3 used to pin the encoder's emitted symbols.
    /// </summary>
    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];


    /// <summary>
    /// Backend-shaped delegates so the per-tier agreement assertion runs once against each vector backend's
    /// method group; the span parameters rule out Func/Action, which cannot bind ref-struct type arguments.
    /// </summary>
    private delegate void FoldDelegate(Span<byte> destination, ReadOnlySpan<byte> source);

    private delegate void CombineDelegate(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination);

    private delegate bool NeutralDelegate(ReadOnlySpan<byte> bytes);


    [TestMethod]
    public void ScalarSemanticsFoldCombineAndNeutralityHold()
    {
        //Fold matches a hand-written XOR loop at a representative non-trivial length.
        byte[] destination = Fill(33, FillSeeds[0]);
        byte[] source = Fill(33, FillSeeds[1]);
        byte[] expectedFold = new byte[33];
        for(int i = 0; i < expectedFold.Length; i++)
        {
            expectedFold[i] = (byte)(destination[i] ^ source[i]);
        }

        ReconciliationXorScalarBackend.Fold(destination, source);
        Assert.AreSequenceEqual(expectedFold, destination);

        //Combine writes left ^ right into a separate destination.
        byte[] left = Fill(33, FillSeeds[0]);
        byte[] right = Fill(33, FillSeeds[1]);
        byte[] combined = new byte[33];
        byte[] expectedCombine = new byte[33];
        for(int i = 0; i < expectedCombine.Length; i++)
        {
            expectedCombine[i] = (byte)(left[i] ^ right[i]);
        }

        ReconciliationXorScalarBackend.Combine(left, right, combined);
        Assert.AreSequenceEqual(expectedCombine, combined);

        //Combine may alias an input: Combine(x, y, x) leaves the same left ^ right bytes in x.
        byte[] aliasLeft = Fill(33, FillSeeds[0]);
        ReconciliationXorScalarBackend.Combine(aliasLeft, right, aliasLeft);
        Assert.AreSequenceEqual(expectedCombine, aliasLeft);

        //IsNeutral is true on an all-zero span and on an empty span.
        Assert.IsTrue(ReconciliationXorScalarBackend.IsNeutral(new byte[33]));
        Assert.IsTrue(ReconciliationXorScalarBackend.IsNeutral([]));

        //IsNeutral is false when any single byte index is nonzero: probe every index at length 33.
        for(int probe = 0; probe < 33; probe++)
        {
            byte[] bytes = new byte[33];
            bytes[probe] = 0xFF;
            Assert.IsFalse(ReconciliationXorScalarBackend.IsNeutral(bytes), $"Index {probe} must break neutrality.");
        }
    }


    [TestMethod]
    public void Vector128AgreesWithScalar()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        AssertTierAgreesWithScalar(
            ReconciliationXorVector128Backend.Fold,
            ReconciliationXorVector128Backend.Combine,
            ReconciliationXorVector128Backend.IsNeutral);
    }


    [TestMethod]
    public void Vector256AgreesWithScalar()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        AssertTierAgreesWithScalar(
            ReconciliationXorVector256Backend.Fold,
            ReconciliationXorVector256Backend.Combine,
            ReconciliationXorVector256Backend.IsNeutral);
    }


    [TestMethod]
    public void Vector512AgreesWithScalar()
    {
        if(!ReconciliationXorVector512Backend.IsSupported)
        {
            Assert.Inconclusive("Vector512 is not hardware-accelerated on this host.");
        }

        AssertTierAgreesWithScalar(
            ReconciliationXorVector512Backend.Fold,
            ReconciliationXorVector512Backend.Combine,
            ReconciliationXorVector512Backend.IsNeutral);
    }


    [TestMethod]
    public void Vector128ReGuardsUnsupportedHosts()
    {
        AssertPlatformReGuard(
            ReconciliationXorVector128Backend.IsSupported,
            static () => ReconciliationXorVector128Backend.Fold(new byte[8], new byte[8]));
    }


    [TestMethod]
    public void Vector256ReGuardsUnsupportedHosts()
    {
        AssertPlatformReGuard(
            ReconciliationXorVector256Backend.IsSupported,
            static () => ReconciliationXorVector256Backend.Fold(new byte[8], new byte[8]));
    }


    [TestMethod]
    public void Vector512ReGuardsUnsupportedHosts()
    {
        AssertPlatformReGuard(
            ReconciliationXorVector512Backend.IsSupported,
            static () => ReconciliationXorVector512Backend.Fold(new byte[8], new byte[8]));
    }


    [TestMethod]
    public void MismatchedLengthsThrowArgumentException()
    {
        //The facade adds no validation of its own, but the dispatched backend validates, so a mismatched
        //call through the facade still throws. The scalar backend always validates regardless of host.
        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXor.Fold(new byte[8], new byte[7]));
        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorScalarBackend.Fold(new byte[8], new byte[7]));

        //Combine throws when any of the three lengths disagrees.
        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXor.Combine(new byte[8], new byte[7], new byte[8]));
        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXor.Combine(new byte[8], new byte[8], new byte[7]));
        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorScalarBackend.Combine(new byte[8], new byte[7], new byte[8]));
        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorScalarBackend.Combine(new byte[7], new byte[8], new byte[8]));
    }


    [TestMethod]
    public void EncoderStreamEqualsTheScalarReferenceFold()
    {
        //Reference fold of the first 8 symbols over {a1, a2, a3} using the scalar backend plus the walk and
        //checksum primitives directly: for each item, fold (item, truncated checksum) into the cells at every
        //walk index below 8. This mirrors the kernel's encoder definition without dispatching through the facade.
        const int symbolCount = 8;
        byte[][] referenceSum = new byte[symbolCount][];
        byte[][] referenceChecksum = new byte[symbolCount][];
        for(int n = 0; n < symbolCount; n++)
        {
            referenceSum[n] = new byte[StructuralContract.ItemWidth];
            referenceChecksum[n] = new byte[StructuralContract.ChecksumWidth];
        }

        byte[][] items = [A1, A2, A3];
        foreach(byte[] item in items)
        {
            byte[] checksumBytes = new byte[StructuralContract.ChecksumWidth];
            ulong checksum = ReconciliationChecksum.Compute(StructuralContract.ChecksumKeyLow, StructuralContract.ChecksumKeyHigh, item);
            ReconciliationChecksum.Write(checksum, checksumBytes);

            ReconciliationWalkPosition position = ReconciliationIndexWalk.Start(item);
            while(position.Index < symbolCount)
            {
                int index = (int)position.Index;
                ReconciliationXorScalarBackend.Fold(referenceSum[index], item);
                ReconciliationXorScalarBackend.Fold(referenceChecksum[index], checksumBytes);
                position = ReconciliationIndexWalk.Next(position);
            }
        }

        //The encoder dispatches its folds through the facade; its emitted symbols must equal the reference.
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        encoder.Add(A1);
        encoder.Add(A2);
        encoder.Add(A3);

        for(int n = 0; n < symbolCount; n++)
        {
            ReconciliationSymbol symbol = encoder.ProduceNext();
            Assert.AreSequenceEqual(referenceSum[n], symbol.Sum.ToArray());
            Assert.AreSequenceEqual(referenceChecksum[n], symbol.Checksum.ToArray());
        }

        //Re-assert the four pinned non-neutral stream rows byte-for-byte: symbols 0, 2, 3 share a1's row,
        //symbol 1 is a2's lone contribution. The neutral rows (4, 6, 7) and symbol 5 are covered by the
        //reference fold above and by the phase 1 vector test.
        byte[] row0Sum = Convert.FromHexString("3132333435363738");
        byte[] row0Checksum = Convert.FromHexString("A57A71E920BF57A9");
        byte[] row1Sum = Convert.FromHexString("2020202020202020");
        byte[] row1Checksum = Convert.FromHexString("7D042E94AE36B153");

        Assert.AreSequenceEqual(row0Sum, referenceSum[0]);
        Assert.AreSequenceEqual(row0Checksum, referenceChecksum[0]);
        Assert.AreSequenceEqual(row1Sum, referenceSum[1]);
        Assert.AreSequenceEqual(row1Checksum, referenceChecksum[1]);
        Assert.AreSequenceEqual(row0Sum, referenceSum[2]);
        Assert.AreSequenceEqual(row0Checksum, referenceChecksum[2]);
        Assert.AreSequenceEqual(row0Sum, referenceSum[3]);
        Assert.AreSequenceEqual(row0Checksum, referenceChecksum[3]);
    }


    private static void AssertTierAgreesWithScalar(FoldDelegate fold, CombineDelegate combine, NeutralDelegate isNeutral)
    {
        foreach(int length in Lengths)
        {
            foreach(ulong seed in FillSeeds)
            {
                //Fold agrees byte-for-byte with the scalar backend on independent copies of the same buffers.
                byte[] tierDestination = Fill(length, seed);
                byte[] scalarDestination = Fill(length, seed);
                byte[] source = Fill(length, seed ^ 0xFFFFFFFFFFFFFFFFUL);

                fold(tierDestination, source);
                ReconciliationXorScalarBackend.Fold(scalarDestination, source);
                Assert.AreSequenceEqual(scalarDestination, tierDestination);

                //Combine agrees byte-for-byte with the scalar backend.
                byte[] left = Fill(length, seed);
                byte[] right = Fill(length, seed ^ 0x0F0F0F0F0F0F0F0FUL);
                byte[] tierCombine = new byte[length];
                byte[] scalarCombine = new byte[length];

                combine(left, right, tierCombine);
                ReconciliationXorScalarBackend.Combine(left, right, scalarCombine);
                Assert.AreSequenceEqual(scalarCombine, tierCombine);

                //IsNeutral agrees on the all-zero span.
                byte[] zero = new byte[length];
                Assert.AreEqual(ReconciliationXorScalarBackend.IsNeutral(zero), isNeutral(zero));

                //IsNeutral agrees on a single nonzero probe byte at each index (a nonempty buffer only).
                for(int probe = 0; probe < length; probe++)
                {
                    byte[] bytes = new byte[length];
                    bytes[probe] = 0xFF;
                    Assert.AreEqual(ReconciliationXorScalarBackend.IsNeutral(bytes), isNeutral(bytes), $"Length {length} probe {probe} disagrees.");
                }
            }
        }
    }


    private static void AssertPlatformReGuard(bool isSupported, Action unsupportedCall)
    {
        //The test asserts whichever branch the host allows and never fails on either kind of host: an
        //unsupported tier throws PlatformNotSupportedException; a supported tier cannot exercise that path.
        if(isSupported)
        {
            Assert.Inconclusive("The tier is hardware-accelerated, so the platform re-guard cannot be exercised here.");
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(unsupportedCall);
    }


    private static byte[] Fill(int length, ulong seed)
    {
        //The pinned LCG fill shared by benches and tests; deterministic and free of System.Random.
        byte[] bytes = new byte[length];
        ulong state = seed;
        for(int i = 0; i < bytes.Length; i++)
        {
            state = unchecked((state * 2862933555777941757UL) + 3037000493UL);
            bytes[i] = (byte)(state >> 56);
        }

        return bytes;
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector512Backend.IsNeutral"/> checks
    /// <see cref="ReconciliationXorVector512Backend.IsSupported"/> before scanning any bytes, mirroring the
    /// guard already pinned for <c>Fold</c> by <see cref="Vector512ReGuardsUnsupportedHosts"/>.
    /// </summary>
    [TestMethod]
    public void Vector512IsNeutralReGuardsUnsupportedHosts()
    {
        AssertPlatformReGuard(
            ReconciliationXorVector512Backend.IsSupported,
            static () => { _ = ReconciliationXorVector512Backend.IsNeutral(new byte[8]); });
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector256Backend.Fold"/> validates lengths before touching any
    /// vector-width block: a mismatch large enough to enter the vectorized loop must still throw
    /// <see cref="ArgumentException"/> without XORing any destination bytes, since skipping the guard would
    /// let the loop write a full block into <c>destination</c> before the scalar fallback's own mismatch
    /// check eventually throws the same exception type, masking the missing guard.
    /// </summary>
    [TestMethod]
    public void Vector256FoldGuardsLengthBeforeWritingAnyBlock()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        byte[] destination = Fill(32, FillSeeds[0]);
        byte[] destinationBeforeCall = (byte[])destination.Clone();
        byte[] source = Fill(64, FillSeeds[1]);

        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorVector256Backend.Fold(destination, source));
        Assert.AreSequenceEqual(destinationBeforeCall, destination);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector512Backend.Fold"/> validates lengths before touching any
    /// vector-width block, mirroring <see cref="Vector256FoldGuardsLengthBeforeWritingAnyBlock"/> one tier
    /// up: a mismatch whose destination still fills a 64-byte block must throw
    /// <see cref="ArgumentException"/> without XORing any destination bytes.
    /// </summary>
    [TestMethod]
    public void Vector512FoldGuardsLengthBeforeWritingAnyBlock()
    {
        if(!ReconciliationXorVector512Backend.IsSupported)
        {
            Assert.Inconclusive("Vector512 is not hardware-accelerated on this host.");
        }

        byte[] destination = Fill(64, FillSeeds[0]);
        byte[] destinationBeforeCall = (byte[])destination.Clone();
        byte[] source = Fill(128, FillSeeds[1]);

        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorVector512Backend.Fold(destination, source));
        Assert.AreSequenceEqual(destinationBeforeCall, destination);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector512Backend.Combine"/> validates all three lengths before
    /// writing any vector-width block, mirroring <see cref="Vector256CombineGuardFiresBeforeAnyBlockIsWritten"/>
    /// one tier up: a mismatch that trips only the destination-length half of the guard must throw
    /// <see cref="ArgumentException"/> before the first 64-byte block lands in <c>destination</c>.
    /// </summary>
    [TestMethod]
    public void Vector512CombineGuardFiresBeforeAnyBlockIsWritten()
    {
        if(!ReconciliationXorVector512Backend.IsSupported)
        {
            Assert.Inconclusive("Vector512 is not hardware-accelerated on this host.");
        }

        byte[] left = Fill(128, FillSeeds[0]);
        byte[] right = Fill(128, FillSeeds[1]);
        byte[] destination = Fill(64, FillSeeds[0] ^ 0x0F0F0F0F0F0F0F0FUL);
        byte[] destinationBeforeCall = (byte[])destination.Clone();

        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorVector512Backend.Combine(left, right, destination));
        Assert.AreSequenceEqual(destinationBeforeCall, destination);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector512Backend.IsNeutral"/> accumulates blocks with a bitwise
    /// OR, mirroring <see cref="Vector256IsNeutralOrsBlocksRatherThanCancellingThem"/> one tier up: two full
    /// 64-byte blocks that each set the same single byte position to a nonzero value must still report
    /// <see langword="false"/>, since an XOR-based accumulator would cancel them back to zero.
    /// </summary>
    [TestMethod]
    public void Vector512IsNeutralOrsBlocksRatherThanCancellingThem()
    {
        if(!ReconciliationXorVector512Backend.IsSupported)
        {
            Assert.Inconclusive("Vector512 is not hardware-accelerated on this host.");
        }

        byte[] bytes = new byte[128];
        bytes[5] = 0xFF;
        bytes[5 + 64] = 0xFF;

        Assert.IsFalse(ReconciliationXorVector512Backend.IsNeutral(bytes));
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector256Backend.Combine"/> validates all three lengths
    /// (via an OR of the two inequality checks) before writing any vector-width block into
    /// <c>destination</c>: a mismatch that trips only the destination-length half of the guard
    /// (<c>left.Length == right.Length</c> but <c>left.Length != destination.Length</c>) must still throw
    /// <see cref="ArgumentException"/> before the first block is written. The vectorized loop must
    /// not XOR a block into <c>destination</c> before the guard throws, because the scalar fallback's own
    /// mismatch check eventually throws the same exception type, so the exception type alone does not prove
    /// the guard fired ahead of any write.
    /// </summary>
    [TestMethod]
    public void Vector256CombineGuardFiresBeforeAnyBlockIsWritten()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        byte[] left = Fill(64, FillSeeds[0]);
        byte[] right = Fill(64, FillSeeds[1]);
        byte[] destination = Fill(32, FillSeeds[0] ^ 0x0F0F0F0F0F0F0F0FUL);
        byte[] destinationBeforeCall = (byte[])destination.Clone();

        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorVector256Backend.Combine(left, right, destination));
        Assert.AreSequenceEqual(destinationBeforeCall, destination);
    }


    /// <summary>
    /// Pins that Vector128Backend.Combine stops scanning at exactly <c>blockEnd</c>: slicing 16-byte operands out
    /// of larger backing arrays whose immediately following bytes are non-zero must leave the destination's
    /// backing bytes beyond the sliced length untouched, proving the accelerated loop never reads or writes one
    /// block past the given spans.
    /// </summary>
    [TestMethod]
    public void Vector128CombineStopsExactlyAtBlockEnd()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        const int width = 16;
        byte[] leftBacking = new byte[width * 2];
        byte[] rightBacking = new byte[width * 2];
        byte[] destinationBacking = new byte[width * 2];
        Array.Fill(leftBacking, (byte)0xAA, width, width);
        Array.Fill(rightBacking, (byte)0x55, width, width);

        ReconciliationXorVector128Backend.Combine(
            leftBacking.AsSpan(0, width),
            rightBacking.AsSpan(0, width),
            destinationBacking.AsSpan(0, width));

        byte[] untouchedTail = new byte[width];
        Assert.AreSequenceEqual(untouchedTail, destinationBacking.AsSpan(width, width).ToArray());
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector256Backend.IsNeutral"/> accumulates blocks with a
    /// bitwise OR: two full 32-byte blocks that each set the same single byte position to a nonzero value
    /// must still report <see langword="false"/> (not neutral), since an XOR-based accumulator would cancel
    /// the matching bytes back to zero across blocks and falsely report the span as neutral.
    /// </summary>
    [TestMethod]
    public void Vector256IsNeutralOrsBlocksRatherThanCancellingThem()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        byte[] bytes = new byte[64];
        bytes[5] = 0xFF;
        bytes[5 + 32] = 0xFF;

        Assert.IsFalse(ReconciliationXorVector256Backend.IsNeutral(bytes));
    }


    /// <summary>
    /// Pins that Vector128Backend.IsNeutral stops scanning at exactly <c>blockEnd</c> (the loop boundary must be a
    /// strict `&lt;`, not `&lt;=`): a 16-byte all-zero span sliced from a larger buffer whose immediately following
    /// bytes are non-zero must still report neutral, proving the accelerated loop never reads one block past the
    /// scanned span.
    /// </summary>
    [TestMethod]
    public void Vector128IsNeutralStopsExactlyAtBlockEnd()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        const int width = 16;
        byte[] backing = new byte[width * 2];
        Array.Fill(backing, (byte)0xFF, width, width);
        ReadOnlySpan<byte> zeroSlice = backing.AsSpan(0, width);

        Assert.IsTrue(ReconciliationXorVector128Backend.IsNeutral(zeroSlice));
    }


    /// <summary>
    /// Pins that Vector128Backend.IsNeutral accumulates full blocks with OR, not XOR: two full blocks that each
    /// carry the same non-zero byte at the same intra-block position must not cancel each other out.
    /// </summary>
    [TestMethod]
    public void Vector128IsNeutralOrsAcrossBlocksRatherThanXoring()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        const int width = 16;
        byte[] bytes = new byte[width * 2];
        bytes[5] = 0xFF;
        bytes[width + 5] = 0xFF;

        Assert.IsFalse(ReconciliationXorVector128Backend.IsNeutral(bytes));
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector128Backend.Fold"/> validates lengths before touching any
    /// vector-width block: a mismatch large enough to enter the vectorized loop must still throw
    /// <see cref="ArgumentException"/> without XORing any destination bytes, since skipping the guard would
    /// let the loop write a full block into <c>destination</c> before the scalar fallback's own mismatch
    /// check eventually throws the same exception type, masking the missing guard.
    /// </summary>
    [TestMethod]
    public void Vector128FoldGuardsLengthBeforeWritingAnyBlock()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        byte[] destination = Fill(16, FillSeeds[0]);
        byte[] destinationBeforeCall = (byte[])destination.Clone();
        byte[] source = Fill(32, FillSeeds[1]);

        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorVector128Backend.Fold(destination, source));
        Assert.AreSequenceEqual(destinationBeforeCall, destination);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector128Backend.Combine"/> validates all three lengths
    /// (via an OR of the two inequality checks) before writing any vector-width block into
    /// <c>destination</c>: a mismatch that trips only the destination-length half of the guard
    /// (<c>left.Length == right.Length</c> but <c>left.Length != destination.Length</c>) must still throw
    /// <see cref="ArgumentException"/> before the first block is written. The vectorized loop must
    /// not XOR a block into <c>destination</c> before the guard throws, because the scalar fallback's own
    /// mismatch check eventually throws the same exception type, so the exception type alone does not prove
    /// the guard fired ahead of any write.
    /// </summary>
    [TestMethod]
    public void Vector128CombineGuardFiresBeforeAnyBlockIsWritten()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        byte[] left = Fill(32, FillSeeds[0]);
        byte[] right = Fill(32, FillSeeds[1]);
        byte[] destination = Fill(16, FillSeeds[0] ^ 0x0F0F0F0F0F0F0F0FUL);
        byte[] destinationBeforeCall = (byte[])destination.Clone();

        Assert.ThrowsExactly<ArgumentException>(() => ReconciliationXorVector128Backend.Combine(left, right, destination));
        Assert.AreSequenceEqual(destinationBeforeCall, destination);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector128Backend.Fold"/> stops at exactly <c>blockEnd</c> (the loop
    /// boundary must be a strict <c>&lt;</c>, not <c>&lt;=</c>): folding a 16-byte source into a 16-byte
    /// destination sliced from larger backing arrays whose following bytes are non-zero must leave the
    /// destination's backing bytes beyond the sliced length untouched, proving the accelerated loop never reads
    /// or writes one block past the given spans.
    /// </summary>
    [TestMethod]
    public void Vector128FoldStopsExactlyAtBlockEnd()
    {
        if(!ReconciliationXorVector128Backend.IsSupported)
        {
            Assert.Inconclusive("Vector128 is not hardware-accelerated on this host.");
        }

        const int width = 16;
        byte[] destinationBacking = new byte[width * 2];
        byte[] sourceBacking = new byte[width * 2];
        Array.Fill(destinationBacking, (byte)0xAA, width, width);
        Array.Fill(sourceBacking, (byte)0x55, width, width);

        ReconciliationXorVector128Backend.Fold(destinationBacking.AsSpan(0, width), sourceBacking.AsSpan(0, width));

        byte[] expectedTail = new byte[width];
        Array.Fill(expectedTail, (byte)0xAA);
        Assert.AreSequenceEqual(expectedTail, destinationBacking.AsSpan(width, width).ToArray());
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector256Backend.Fold"/> stops at exactly <c>blockEnd</c> (the loop
    /// boundary must be a strict <c>&lt;</c>, not <c>&lt;=</c>): folding a 32-byte source into a 32-byte
    /// destination sliced from larger backing arrays whose following bytes are non-zero must leave the
    /// destination's backing bytes beyond the sliced length untouched, proving the accelerated loop never reads
    /// or writes one block past the given spans.
    /// </summary>
    [TestMethod]
    public void Vector256FoldStopsExactlyAtBlockEnd()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        const int width = 32;
        byte[] destinationBacking = new byte[width * 2];
        byte[] sourceBacking = new byte[width * 2];
        Array.Fill(destinationBacking, (byte)0xAA, width, width);
        Array.Fill(sourceBacking, (byte)0x55, width, width);

        ReconciliationXorVector256Backend.Fold(destinationBacking.AsSpan(0, width), sourceBacking.AsSpan(0, width));

        byte[] expectedTail = new byte[width];
        Array.Fill(expectedTail, (byte)0xAA);
        Assert.AreSequenceEqual(expectedTail, destinationBacking.AsSpan(width, width).ToArray());
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector256Backend.Combine"/> stops scanning at exactly <c>blockEnd</c>:
    /// slicing 32-byte operands out of larger backing arrays whose immediately following bytes are non-zero must
    /// leave the destination's backing bytes beyond the sliced length untouched, proving the accelerated loop
    /// never reads or writes one block past the given spans.
    /// </summary>
    [TestMethod]
    public void Vector256CombineStopsExactlyAtBlockEnd()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        const int width = 32;
        byte[] leftBacking = new byte[width * 2];
        byte[] rightBacking = new byte[width * 2];
        byte[] destinationBacking = new byte[width * 2];
        Array.Fill(leftBacking, (byte)0xAA, width, width);
        Array.Fill(rightBacking, (byte)0x55, width, width);

        ReconciliationXorVector256Backend.Combine(
            leftBacking.AsSpan(0, width),
            rightBacking.AsSpan(0, width),
            destinationBacking.AsSpan(0, width));

        byte[] untouchedTail = new byte[width];
        Assert.AreSequenceEqual(untouchedTail, destinationBacking.AsSpan(width, width).ToArray());
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector256Backend.IsNeutral"/> stops scanning at exactly <c>blockEnd</c>
    /// (the loop boundary must be a strict <c>&lt;</c>, not <c>&lt;=</c>): a 32-byte all-zero span sliced from a
    /// larger buffer whose immediately following bytes are non-zero must still report neutral, proving the
    /// accelerated loop never reads one block past the scanned span.
    /// </summary>
    [TestMethod]
    public void Vector256IsNeutralStopsExactlyAtBlockEnd()
    {
        if(!ReconciliationXorVector256Backend.IsSupported)
        {
            Assert.Inconclusive("Vector256 is not hardware-accelerated on this host.");
        }

        const int width = 32;
        byte[] backing = new byte[width * 2];
        Array.Fill(backing, (byte)0xFF, width, width);
        ReadOnlySpan<byte> zeroSlice = backing.AsSpan(0, width);

        Assert.IsTrue(ReconciliationXorVector256Backend.IsNeutral(zeroSlice));
    }


    /// <summary>
    /// The scalar backend has no platform dependency, so <see cref="ReconciliationXorScalarBackend.IsSupported"/>
    /// is always <see langword="true"/>, per the class remarks describing it as the arbiter every accelerated
    /// tier is compared against.
    /// </summary>
    [TestMethod]
    public void ScalarBackendIsAlwaysSupported()
    {
        Assert.IsTrue(ReconciliationXorScalarBackend.IsSupported);
    }


    /// <summary>
    /// Pins that <see cref="ReconciliationXorVector512Backend.Combine"/> checks
    /// <see cref="ReconciliationXorVector512Backend.IsSupported"/> before any length validation or vector
    /// work, mirroring the guard already pinned for <c>Fold</c> by <see cref="Vector512ReGuardsUnsupportedHosts"/>.
    /// </summary>
    [TestMethod]
    public void Vector512CombineReGuardsUnsupportedHosts()
    {
        AssertPlatformReGuard(
            ReconciliationXorVector512Backend.IsSupported,
            static () => ReconciliationXorVector512Backend.Combine(new byte[8], new byte[8], new byte[8]));
    }
}
