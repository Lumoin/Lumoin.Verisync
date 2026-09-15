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


    /// <summary>
    /// Pins the item-width bounds documented on the public constructor: one and 1024 bytes are the exact
    /// inclusive extremes and must construct rather than throw, with the width flowing through unchanged.
    /// </summary>
    [TestMethod]
    public void ContractConstructionAcceptsTheItemWidthBoundaryValues()
    {
        ReconciliationContract narrowest = new(ReconciliationItemDomain.ContentHash, 1, 8, 0, 0);
        Assert.AreEqual(1, narrowest.ItemWidth);

        ReconciliationContract widest = new(ReconciliationItemDomain.ContentHash, 1024, 8, 0, 0);
        Assert.AreEqual(1024, widest.ItemWidth);
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
        Assert.IsEmpty(decoder.DecodedItems);

        decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
        Assert.IsTrue(decoder.IsComplete);
        Assert.IsEmpty(decoder.DecodedItems);
    }


    /// <summary>
    /// A pure cell whose item is already decoded is dropped rather than decoded again. The same
    /// (W2, checksum(W2)) cell is absorbed at index zero and index one; W2's index walk covers zero but not
    /// one, so the first decode's peel never reaches the second cell, which stays pure with an already-seen
    /// key. Exactly one item is decoded and a further absorb still returns, so the drop neither loses the item
    /// nor spins the worklist.
    /// </summary>
    [TestMethod]
    public void ADuplicatePureCellIsDroppedRatherThanRedecoded()
    {
        ReconciliationContract contract = ReconciliationContract.ContentHashDefault;
        byte[] w2 = new byte[32];
        Array.Fill(w2, (byte)0xFF);

        ulong checksum = ReconciliationChecksum.Compute(contract.ChecksumKeyLow, contract.ChecksumKeyHigh, w2);
        byte[] checksumBytes = new byte[contract.ChecksumWidth];
        ReconciliationChecksum.Write(checksum, checksumBytes);

        using ReconciliationDecoder decoder = new(contract, BaseMemoryPool.Shared);
        decoder.Absorb(new ReconciliationSymbol((byte[])w2.Clone(), (byte[])checksumBytes.Clone()));
        decoder.Absorb(new ReconciliationSymbol((byte[])w2.Clone(), (byte[])checksumBytes.Clone()));

        Assert.HasCount(1, decoder.DecodedItems);
        Assert.IsTrue(decoder.IsComplete);

        //The duplicate did not spin the worklist: a further absorb returns rather than hanging.
        decoder.Absorb(new ReconciliationSymbol((byte[])w2.Clone(), (byte[])checksumBytes.Clone()));
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
        Assert.IsEmpty(decoder.DecodedItems);
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


    /// <summary>
    /// SymbolAt on a disposed encoder that never produced anything is refused for disposal, not for the index
    /// being out of the (empty) produced range.
    /// </summary>
    [TestMethod]
    public void DisposedEncoderSymbolAtReportsDisposalBeforeTheRangeCheck()
    {
        ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        encoder.Dispose();

        //ProducedCount is zero, so index 0 is also out of the produced range; disposal must be checked first,
        //or this would surface as ArgumentOutOfRangeException instead.
        ObjectDisposedException thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.SymbolAt(0));
        Assert.AreEqual(typeof(ReconciliationEncoder).FullName, thrown.ObjectName);
    }


    /// <summary>
    /// Remove on a disposed encoder is refused as the encoder's own disposal, not silently deferred to the
    /// item arena it owns internally.
    /// </summary>
    [TestMethod]
    public void DisposedEncoderRemoveReportsItselfAsTheDisposedObject()
    {
        ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        encoder.Dispose();

        ObjectDisposedException thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.Remove(A1));
        Assert.AreEqual(typeof(ReconciliationEncoder).FullName, thrown.ObjectName);
    }


    /// <summary>Pins that <see cref="ReconciliationSymbol.Combine(ReconciliationSymbol)"/> rejects a null
    /// <paramref name="other"/> with <see cref="ArgumentNullException"/> rather than surfacing a raw
    /// null-reference failure from touching the argument's fields.</summary>
    [TestMethod]
    public void CombineRejectsNullOther()
    {
        ReconciliationSymbol symbol = new(new byte[8], new byte[8]);
        Assert.ThrowsExactly<ArgumentNullException>(() => symbol.Combine(null!));
    }


    /// <summary>
    /// An enforcement value outside the three defined members is rejected at construction, before any cell
    /// or item storage is rented.
    /// </summary>
    [TestMethod]
    public void ConstructorRejectsUndefinedEnforcementValue()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReconciliationEncoder(StructuralContract, (ReconciliationInjectivityEnforcement)99, BaseMemoryPool.Shared));
        Assert.AreEqual("enforcement", thrown.ParamName);
    }


    /// <summary>
    /// A null contract is refused with ArgumentNullException before any of its members are dereferenced; an
    /// unguarded null contract would instead surface as a NullReferenceException from the ItemWidth access
    /// used to size the cell store.
    /// </summary>
    [TestMethod]
    public void ConstructorRejectsNullContract()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(() => new ReconciliationEncoder(null!, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared));
        Assert.AreEqual("contract", thrown.ParamName);
    }


    /// <summary>
    /// A null pool is refused with ArgumentNullException naming "pool" ahead of the enforcement guard, so an
    /// out-of-range enforcement value cannot mask a missing pool guard by throwing the enforcement's own
    /// ArgumentOutOfRangeException first.
    /// </summary>
    [TestMethod]
    public void ConstructorRejectsNullPool()
    {
        ArgumentNullException thrown = Assert.ThrowsExactly<ArgumentNullException>(
            () => new ReconciliationEncoder(StructuralContract, (ReconciliationInjectivityEnforcement)99, null!));
        Assert.AreEqual("pool", thrown.ParamName);
    }


    /// <summary>
    /// Add on a disposed encoder is refused as the encoder's own disposal, not silently deferred to the item
    /// arena it owns internally.
    /// </summary>
    [TestMethod]
    public void DisposedEncoderAddReportsItselfAsTheDisposedObject()
    {
        ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        encoder.Dispose();

        ObjectDisposedException thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.Add(A1));
        Assert.AreEqual(typeof(ReconciliationEncoder).FullName, thrown.ObjectName);
    }


    /// <summary>Pins that Absorb after Dispose reports the decoder itself as the disposed object, via the
    /// decoder's own guard, rather than the ObjectDisposedException that would surface two calls deeper from
    /// the already-disposed cell store once the decoder's own check is skipped or its flag never set.</summary>
    [TestMethod]
    public void AbsorbAfterDisposeThrowsTheDecodersOwnObjectDisposedException()
    {
        ReconciliationDecoder decoder = new(StructuralContract, BaseMemoryPool.Shared);
        decoder.Dispose();

        ObjectDisposedException refusal = Assert.ThrowsExactly<ObjectDisposedException>(
            () => decoder.Absorb(new ReconciliationSymbol(new byte[8], new byte[8])));

        Assert.AreEqual(typeof(ReconciliationDecoder).ToString(), refusal.ObjectName);
    }


    /// <summary>Pins that <see cref="ReconciliationSymbol.Combine(ReconciliationSymbol)"/>'s own width guard —
    /// not the XOR primitive's unrelated length check — is what rejects a mismatch, naming
    /// <paramref name="other"/> even when only one of the two fields differs in width.</summary>
    [TestMethod]
    public void CombineNamesOtherWhenOnlyOneFieldWidthDiffers()
    {
        ReconciliationSymbol symbol = new(new byte[8], new byte[8]);
        ReconciliationSymbol mismatchedSumOnly = new(new byte[4], new byte[8]);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => symbol.Combine(mismatchedSumOnly));
        Assert.AreEqual("other", exception.ParamName);
    }


    /// <summary>Pins that <see cref="ReconciliationSymbol.GetHashCode"/> folds in both fields: varying only the
    /// sum, or only the checksum, changes the hash.</summary>
    [TestMethod]
    public void GetHashCodeIncorporatesBothFieldsIndependently()
    {
        byte[] sharedSum = [1, 1, 1, 1, 1, 1, 1, 1];
        byte[] sharedChecksum = [2, 2, 2, 2, 2, 2, 2, 2];

        ReconciliationSymbol baseline = new(sharedSum, sharedChecksum);
        ReconciliationSymbol differentSum = new([9, 9, 9, 9, 9, 9, 9, 9], sharedChecksum);
        ReconciliationSymbol differentChecksum = new(sharedSum, [9, 9, 9, 9, 9, 9, 9, 9]);

        Assert.AreNotEqual(baseline.GetHashCode(), differentSum.GetHashCode());
        Assert.AreNotEqual(baseline.GetHashCode(), differentChecksum.GetHashCode());
    }


    /// <summary>
    /// ProduceNext on a disposed encoder is refused as the encoder's own disposal, not silently deferred to
    /// the cell store it owns internally.
    /// </summary>
    [TestMethod]
    public void DisposedEncoderProduceNextReportsItselfAsTheDisposedObject()
    {
        ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared);
        encoder.Dispose();

        ObjectDisposedException thrown = Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.ProduceNext());
        Assert.AreEqual(typeof(ReconciliationEncoder).FullName, thrown.ObjectName);
    }


    /// <summary>Pins that a null contract is rejected by the decoder's own constructor guard, not by a
    /// NullReferenceException from the first line inside it that dereferences the contract.</summary>
    [TestMethod]
    public void DecoderConstructorRejectsNullContract()
    {
        ArgumentNullException refusal = Assert.ThrowsExactly<ArgumentNullException>(
            () => new ReconciliationDecoder(null!, BaseMemoryPool.Shared));

        Assert.AreEqual("contract", refusal.ParamName);
    }


    /// <summary>Pins <see cref="ReconciliationSymbol.Equals(ReconciliationSymbol?)"/>'s full contract: a null
    /// other is never equal, a self-reference is always equal, and content equality requires both fields to
    /// match — a match on only one field is not equality.</summary>
    [TestMethod]
    public void EqualsHonorsNullSelfReferenceAndBothFieldsContract()
    {
        ReconciliationSymbol symbol = new(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[] { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF, 0x10, 0x11 });

        //A null other is never equal; obtained from an opaque helper so CA1508 cannot fold the comparison.
        Assert.IsFalse(symbol.Equals(NullSymbol()));

        //A symbol is equal to itself by reference.
        Assert.IsTrue(symbol.Equals(symbol));

        //Matching sum but a different checksum is not equality...
        ReconciliationSymbol sameSumDifferentChecksum = new(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[] { 1, 1, 1, 1, 1, 1, 1, 1 });
        Assert.IsFalse(symbol.Equals(sameSumDifferentChecksum));

        //...and matching checksum but a different sum is likewise not equality.
        ReconciliationSymbol differentSumSameChecksum = new(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }, new byte[] { 0xA, 0xB, 0xC, 0xD, 0xE, 0xF, 0x10, 0x11 });
        Assert.IsFalse(symbol.Equals(differentSumSameChecksum));
    }


    /// <summary>Returns a null symbol through an opaque call so the null-argument comparison in <see cref="EqualsHonorsNullSelfReferenceAndBothFieldsContract"/> is not folded away by the analyzer.</summary>
    private static ReconciliationSymbol? NullSymbol() => null;


    /// <summary>Pins that the <see cref="ReadOnlyMemory{Byte}"/> constructor overload validates the checksum
    /// field's length against the same one-through-eight inclusive range as the span overload: both boundaries
    /// construct successfully, and one below or above the range throws.</summary>
    [TestMethod]
    public void MemoryConstructorValidatesChecksumLengthAcrossTheFullRange()
    {
        ReadOnlyMemory<byte> sum = A1;
        ReadOnlyMemory<byte> lowerBound = new byte[1];
        ReadOnlyMemory<byte> upperBound = new byte[8];

        ReconciliationSymbol atLowerBound = new(sum, lowerBound);
        ReconciliationSymbol atUpperBound = new(sum, upperBound);
        Assert.HasCount(1, atLowerBound.Checksum.ToArray());
        Assert.HasCount(8, atUpperBound.Checksum.ToArray());

        ReadOnlyMemory<byte> belowRange = ReadOnlyMemory<byte>.Empty;
        ReadOnlyMemory<byte> aboveRange = new byte[9];
        Assert.ThrowsExactly<ArgumentException>(() => _ = new ReconciliationSymbol(sum, belowRange));
        Assert.ThrowsExactly<ArgumentException>(() => _ = new ReconciliationSymbol(sum, aboveRange));
    }


    /// <summary>Pins that <see cref="ReconciliationSymbol.IsNeutral"/> requires both fields to be all zero, not
    /// just one — a cell with a zero sum and a non-zero checksum (or vice versa) still carries net contribution
    /// and must not read as neutral.</summary>
    [TestMethod]
    public void IsNeutralRequiresBothFieldsToBeZero()
    {
        ReconciliationSymbol zeroSumOnly = new(new byte[8], new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        ReconciliationSymbol zeroChecksumOnly = new(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, new byte[8]);

        Assert.IsFalse(zeroSumOnly.IsNeutral);
        Assert.IsFalse(zeroChecksumOnly.IsNeutral);
    }
}
