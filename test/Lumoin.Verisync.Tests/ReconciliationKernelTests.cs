using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Security.Cryptography;
using System.Text;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Focused deterministic coverage of the reconciliation kernel: contract and symbol validation,
/// enforcement modes, the incremental-update law on a hand-built set, the width-bounded masquerade
/// (toy checksum width 1) and its refusal under a secret key, and the in-memory OrSet quiescence
/// post-condition projecting elements through SHA-256.
/// </summary>
[TestClass]
internal sealed class ReconciliationKernelTests
{
    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

    private static byte[] B1 { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void ContractValidationRejectsBadArgumentsAndPinsTheDefault()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationContract((ReconciliationItemDomain)0, 32, 8, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationContract(ReconciliationItemDomain.ContentHash, 0, 8, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationContract(ReconciliationItemDomain.ContentHash, 1025, 8, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationContract(ReconciliationItemDomain.ContentHash, 32, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationContract(ReconciliationItemDomain.ContentHash, 32, 9, 0, 0));

        //The public constructor enforces the production floor: a width below MinimumProductionChecksumWidth is
        //rejected, so a below-floor width is constructible only through the adversarial-test factory.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationContract(ReconciliationItemDomain.ContentHash, 32, 3, 0, 0));

        //The factory lifts the floor to admit narrow widths for masquerade probes but still rejects zero and
        //widths above eight.
        ReconciliationContract narrow = ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 1, 0, 0);
        Assert.AreEqual(1, narrow.ChecksumWidth);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 9, 0, 0));

        ReconciliationContract contract = ReconciliationContract.ContentHashDefault;
        Assert.AreEqual(ReconciliationItemDomain.ContentHash, contract.ItemDomain);
        Assert.AreEqual(32, contract.ItemWidth);
        Assert.AreEqual(8, contract.ChecksumWidth);
        Assert.AreEqual(ReconciliationContract.WellKnownChecksumKeyLow, contract.ChecksumKeyLow);
        Assert.AreEqual(ReconciliationContract.WellKnownChecksumKeyHigh, contract.ChecksumKeyHigh);
    }


    [TestMethod]
    public void SymbolValidationAndEqualityHold()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbol(ReadOnlyMemory<byte>.Empty, new byte[8]));
        Assert.ThrowsExactly<ArgumentException>(() => new ReconciliationSymbol(new byte[8], new byte[9]));

        ReconciliationSymbol symbol = new(A1, new byte[8]);
        Assert.ThrowsExactly<ArgumentException>(() => symbol.Combine(new ReconciliationSymbol(new byte[4], new byte[8])));

        //Equal bytes from independent buffers are equal with equal hash codes regardless of buffer identity.
        byte[] sumCopy = [.. A1];
        ReconciliationSymbol same = new(sumCopy, new byte[8]);
        Assert.AreEqual(symbol, same);
        Assert.AreEqual(symbol.GetHashCode(), same.GetHashCode());

        //Combine of a symbol with itself is neutral (GF(2) self-inverse).
        Assert.IsTrue(symbol.Combine(symbol).IsNeutral);
    }


    [TestMethod]
    public void EncoderValidationRejectsWrongWidthsAndOutOfRange()
    {
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        byte[] tooShort = [0x01, 0x02, 0x03];

        Assert.ThrowsExactly<ArgumentException>(() => encoder.Add(tooShort));
        Assert.ThrowsExactly<ArgumentException>(() => encoder.Remove(tooShort));

        encoder.Add(A1);
        _ = encoder.ProduceNext();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => encoder.SymbolAt(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => encoder.SymbolAt(1));
    }


    [TestMethod]
    public void StrictEnforcementGuardsMembership()
    {
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.Strict, BaseMemoryPool.Shared);
        encoder.Add(A1);

        Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Add(A1));
        Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Remove(A2));

        //Add, remove, add of the same item is a legal history and the net stream contains it.
        encoder.Remove(A1);
        encoder.Add(A1);

        using ReconciliationEncoder expected = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        expected.Add(A1);

        AssertSameStream(expected, encoder, 8);
    }


    [TestMethod]
    public void NoneEnforcementHasSetSemantics()
    {
        //Double-add cancels under XOR: the stream equals the empty-set stream.
        using ReconciliationEncoder doubleAdd = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        doubleAdd.Add(A1);
        doubleAdd.Add(A1);

        using ReconciliationEncoder empty = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        AssertSameStream(empty, doubleAdd, 8);

        //Add then remove cancels likewise.
        using ReconciliationEncoder addRemove = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        addRemove.Add(A2);
        addRemove.Remove(A2);

        AssertSameStream(empty, addRemove, 8);
    }


    [TestMethod]
    public void IncrementalUpdateTracksTheLiveSet()
    {
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        encoder.Add(A1);
        encoder.Add(A2);
        encoder.Add(A3);
        for(int i = 0; i < 6; i++)
        {
            _ = encoder.ProduceNext();
        }

        encoder.Add(B1);
        encoder.Remove(A2);

        using ReconciliationEncoder fresh = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        fresh.Add(A1);
        fresh.Add(A3);
        fresh.Add(B1);

        for(int i = 0; i < encoder.ProducedCount; i++)
        {
            Assert.AreEqual(fresh.ProduceNext(), encoder.SymbolAt(i));
        }
    }


    [TestMethod]
    public void DecoderAbsorbValidationAndPostCompletionAbsorb()
    {
        using ReconciliationDecoder decoder = new(StructuralContract, BaseMemoryPool.Shared);

        Assert.ThrowsExactly<ArgumentNullException>(() => decoder.Absorb(null!));
        Assert.ThrowsExactly<ArgumentException>(() => decoder.Absorb(new ReconciliationSymbol(new byte[4], new byte[8])));
        Assert.ThrowsExactly<ArgumentException>(() => decoder.Absorb(new ReconciliationSymbol(new byte[8], new byte[4])));

        //Equal-set reconciliation: complete after the first absorbed symbol; a further absorb stays legal.
        using ReconciliationEncoder left = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        left.Add(A1);
        using ReconciliationEncoder right = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        right.Add(A1);

        decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
        Assert.IsTrue(decoder.IsComplete);
        Assert.HasCount(0, decoder.DecodedItems);

        decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
        Assert.IsTrue(decoder.IsComplete);
        Assert.HasCount(0, decoder.DecodedItems);
    }


    [TestMethod]
    public void MasqueradeIsWidthBoundedAndKeyRefuses()
    {
        //Toy width-1 checksum over the well-known key: brute-force a y whose width-1 checksum XORs
        //linearly with x's, so a degree-2 cell masquerades as pure.
        ReconciliationContract toyWellKnown = ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 1, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);
        byte[] x = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7];

        byte checksumX = Checksum1(toyWellKnown, x);
        byte[]? collidingY = null;
        for(long counter = 0; counter < 100_000 && collidingY is null; counter++)
        {
            byte[] y = BitConverter.GetBytes(counter);
            if(y.AsSpan().SequenceEqual(x))
            {
                continue;
            }

            byte[] xor = Xor(x, y);
            if((byte)(checksumX ^ Checksum1(toyWellKnown, y)) == Checksum1(toyWellKnown, xor))
            {
                collidingY = y;
            }
        }

        Assert.IsNotNull(collidingY);

        //Unkeyed decoder over {x, y} versus empty: difference symbol 0 masquerades as pure and the
        //decoder completes immediately with the single WRONG item x ^ y.
        using ReconciliationEncoder twoItems = new(toyWellKnown, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        twoItems.Add(x);
        twoItems.Add(collidingY);
        using ReconciliationEncoder emptyWellKnown = new(toyWellKnown, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);

        using ReconciliationDecoder unkeyed = new(toyWellKnown, BaseMemoryPool.Shared);
        unkeyed.Absorb(twoItems.ProduceNext().Combine(emptyWellKnown.ProduceNext()));

        Assert.IsTrue(unkeyed.IsComplete);
        Assert.HasCount(1, unkeyed.DecodedItems);
        Assert.AreSequenceEqual(Xor(x, collidingY), unkeyed.DecodedItems[0].ToArray());

        //The same construction under a secret key refuses the crafted collision: not complete after symbol 0.
        ReconciliationContract toySecret = ReconciliationContract.ForAdversarialTesting(ReconciliationItemDomain.Structural, 8, 1, 0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);
        using ReconciliationEncoder twoItemsSecret = new(toySecret, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        twoItemsSecret.Add(x);
        twoItemsSecret.Add(collidingY);
        using ReconciliationEncoder emptySecret = new(toySecret, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);

        using ReconciliationDecoder keyed = new(toySecret, BaseMemoryPool.Shared);
        keyed.Absorb(twoItemsSecret.ProduceNext().Combine(emptySecret.ProduceNext()));

        Assert.IsFalse(keyed.IsComplete);
    }


    [TestMethod]
    public void OrSetReconciliationReachesQuiescence()
    {
        ReconciliationContract contract = ReconciliationContract.ContentHashDefault;
        ProjectReconciliationItemsDelegate<OrSet<string>> project = static (set, _) =>
        {
            List<ReadOnlyMemory<byte>> items = [];
            foreach(string element in set.Elements)
            {
                items.Add(Digest(element));
            }

            return items;
        };

        //Two OrSets diverged from a common ancestor: shared adds, then disjoint adds and removes per side.
        OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
        OrSet<string> left = ancestor.Add("delta", R1).Remove("beta");
        OrSet<string> right = ancestor.Add("epsilon", R2).Remove("gamma");

        ReadOnlyMemory<byte>[] leftItems = [.. project(left, contract)];
        ReadOnlyMemory<byte>[] rightItems = [.. project(right, contract)];

        string[] expectedDifference = [.. SymmetricDifference(left.Elements, right.Elements).Select(e => Convert.ToHexString(Digest(e).Span)).Order()];
        string[] decodedDifference = [.. Reconcile(contract, leftItems, rightItems).Select(item => Convert.ToHexString(item.Span)).Order()];
        Assert.AreSequenceEqual(expectedDifference, decodedDifference);

        //Merge both ways, re-project, reconcile again: complete at the first symbol with zero decoded items.
        OrSet<string> mergedLeft = left.Merge(right);
        OrSet<string> mergedRight = right.Merge(left);

        ReadOnlyMemory<byte>[] mergedLeftItems = [.. project(mergedLeft, contract)];
        ReadOnlyMemory<byte>[] mergedRightItems = [.. project(mergedRight, contract)];

        using ReconciliationEncoder encoderLeft = LoadEncoder(contract, mergedLeftItems);
        using ReconciliationEncoder encoderRight = LoadEncoder(contract, mergedRightItems);
        using ReconciliationDecoder decoder = new(contract, BaseMemoryPool.Shared);
        decoder.Absorb(encoderLeft.ProduceNext().Combine(encoderRight.ProduceNext()));

        Assert.IsTrue(decoder.IsComplete);
        Assert.HasCount(0, decoder.DecodedItems);
    }


    private static IReadOnlyList<ReadOnlyMemory<byte>> Reconcile(ReconciliationContract contract, ReadOnlyMemory<byte>[] leftItems, ReadOnlyMemory<byte>[] rightItems)
    {
        using ReconciliationEncoder left = LoadEncoder(contract, leftItems);
        using ReconciliationEncoder right = LoadEncoder(contract, rightItems);
        using ReconciliationDecoder decoder = new(contract, BaseMemoryPool.Shared);

        int cap = 100 + (20 * (leftItems.Length + rightItems.Length));
        for(int n = 0; n < cap && !decoder.IsComplete; n++)
        {
            decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
        }

        Assert.IsTrue(decoder.IsComplete);

        return decoder.DecodedItems;
    }


    private static ReconciliationEncoder LoadEncoder(ReconciliationContract contract, ReadOnlyMemory<byte>[] items)
    {
        ReconciliationEncoder encoder = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            encoder.Add(item.Span);
        }

        return encoder;
    }


    private static ReadOnlyMemory<byte> Digest(string element)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(element));
    }


    private static HashSet<string> SymmetricDifference(IEnumerable<string> left, IEnumerable<string> right)
    {
        HashSet<string> leftSet = [.. left];
        HashSet<string> rightSet = [.. right];
        HashSet<string> symmetric = [.. leftSet];
        symmetric.SymmetricExceptWith(rightSet);

        return symmetric;
    }


    private static byte Checksum1(ReconciliationContract contract, ReadOnlySpan<byte> item)
    {
        ulong checksum = ReconciliationChecksum.Compute(contract.ChecksumKeyLow, contract.ChecksumKeyHigh, item);
        Span<byte> width1 = stackalloc byte[1];
        ReconciliationChecksum.Write(checksum, width1);

        return width1[0];
    }


    private static byte[] Xor(byte[] left, byte[] right)
    {
        byte[] result = new byte[left.Length];
        for(int i = 0; i < result.Length; i++)
        {
            result[i] = (byte)(left[i] ^ right[i]);
        }

        return result;
    }


    private static void AssertSameStream(ReconciliationEncoder expected, ReconciliationEncoder actual, int count)
    {
        for(int n = 0; n < count; n++)
        {
            Assert.AreEqual(expected.ProduceNext(), actual.ProduceNext());
        }
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
