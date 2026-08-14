using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Deterministic coverage of the wire-free dotted-entry projection and reverse index: the round-trip that
/// turns a pinned <see cref="DottedVersionVectorSetState{T}"/> into a present-entry digest set with a
/// digest-to-entry lookup, the cross-replica purity that makes a shared entry project to a byte-identical
/// item, the foundational symmetric-difference law that an item differs between two projections exactly when
/// the entry's presence differs, the injectivity that distinct dots yield distinct items and a digest
/// collision fails closed, the causal-context passthrough, the construction validation surface, and the
/// pooled memory accountability that the framing scratch is rented and returned within the constructor so the
/// rental ledger balances. The value type is <see cref="string"/>, canonicalized to UTF-8 bytes; the digest
/// is SHA-256 over the pinned replica-counter-value frame; the contract is the 32-byte content-hash default.
/// </summary>
/// <remarks>
/// The accountability test observes the library's process-global rental instruments, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> to keep its measurement totals free of rentals emitted by other
/// pool-using tests running concurrently — the same isolation the other metric-observing suites use.
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class DottedReconciliationProjectionTests
{
    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    /// <summary>
    /// The pinned canonical value frame is UTF-8 of the string; the dot alone still distinguishes two entries
    /// sharing a value, so the value bytes may even be empty.
    /// </summary>
    private static CanonicalizeReconciliationValueDelegate<string> CanonicalizeUtf8 { get; } =
        static value => Encoding.UTF8.GetBytes(value);

    /// <summary>
    /// The production digest: SHA-256 of the frame, exactly 32 bytes, matching the content-hash contract width.
    /// </summary>
    private static ComputeDigestDelegate Sha256Digest { get; } =
        static frame => SHA256.HashData(frame.Span).AsMemory();

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);


    [TestMethod]
    public void ProjectionRoundTripsEveryEntryAndRejectsAnUnknownItem()
    {
        //A state with several entries minted under fixed replica ids over advancing counters.
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty
            .Add(R1, "alpha")
            .Add(R2, "beta")
            .Add(R1, "gamma")
            .Add(R3, "delta")
            .ToState();

        DottedReconciliationProjection<string> projection = new(state, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);

        //One item per present entry, each exactly the contract width.
        Assert.AreEqual(state.Entries.Length, projection.Count);
        Assert.HasCount(projection.Count, projection.Items);
        foreach(ReadOnlyMemory<byte> item in projection.Items)
        {
            Assert.HasCount(ContentHashContract.ItemWidth, item);
        }

        //Every produced item resolves back to a present entry; the resolved entry is one of the state's own.
        HashSet<string> entryKeys = [.. state.Entries.Select(EntryKey)];
        foreach(ReadOnlyMemory<byte> item in projection.Items)
        {
            Assert.IsTrue(projection.TryResolve(item, out DottedEntry<string>? entry));
            Assert.IsNotNull(entry);
            Assert.Contains(EntryKey(entry), entryKeys);
        }

        //An unknown item — a 32-byte digest of bytes no entry frames — resolves to false and null, and a
        //default (empty) item never throws.
        ReadOnlyMemory<byte> unknown = SHA256.HashData(Encoding.UTF8.GetBytes("absent")).AsMemory();
        Assert.IsFalse(projection.TryResolve(unknown, out DottedEntry<string>? missing));
        Assert.IsNull(missing);

        Assert.IsFalse(projection.TryResolve(default, out DottedEntry<string>? empty));
        Assert.IsNull(empty);
    }


    [TestMethod]
    public void TheSameEntryProjectsToAByteIdenticalItemFromTwoIndependentStates()
    {
        //Two independently built states that both contain the SAME (replica, counter, value) entry: a single
        //add of "shared" under R1 mints the dot (R1, 1) in each, plus a disjoint extra entry per side so the
        //states are otherwise different.
        DottedVersionVectorSetState<string> left = DottedVersionVectorSet<string>.Empty
            .Add(R1, "shared")
            .Add(R2, "left-only")
            .ToState();

        DottedVersionVectorSetState<string> right = DottedVersionVectorSet<string>.Empty
            .Add(R1, "shared")
            .Add(R3, "right-only")
            .ToState();

        DottedReconciliationProjection<string> leftProjection = new(left, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);
        DottedReconciliationProjection<string> rightProjection = new(right, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);

        //Locate the item each side produced for the shared dot by resolving back to the (R1, 1, "shared") entry.
        ReadOnlyMemory<byte> leftShared = ItemFor(leftProjection, R1, 1, "shared");
        ReadOnlyMemory<byte> rightShared = ItemFor(rightProjection, R1, 1, "shared");

        //Purity: the shared entry frames identically on both sides, so the digest — and the item — is
        //byte-for-byte equal. This is the property that makes the symmetric difference meaningful.
        Assert.IsTrue(leftShared.Span.SequenceEqual(rightShared.Span));
    }


    [TestMethod]
    public void ItemSetSymmetricDifferenceMatchesEntryPresenceDifference()
    {
        //Build A and B with a known shared / leftOnly / rightOnly split, every dot distinct by construction:
        //the shared entry is the same single add under R1; A additionally adds two entries under R2, B
        //additionally adds two entries under R3. No dot is shared except the deliberate one.
        DottedVersionVectorSet<string> shared = DottedVersionVectorSet<string>.Empty.Add(R1, "shared");

        DottedVersionVectorSetState<string> stateA = shared
            .Add(R2, "a-one")
            .Add(R2, "a-two")
            .ToState();

        DottedVersionVectorSetState<string> stateB = shared
            .Add(R3, "b-one")
            .Add(R3, "b-two")
            .ToState();

        DottedReconciliationProjection<string> a = new(stateA, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);
        DottedReconciliationProjection<string> b = new(stateB, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);

        HashSet<string> itemsA = [.. a.Items.Select(HexOf)];
        HashSet<string> itemsB = [.. b.Items.Select(HexOf)];

        //The entry-presence difference, known by construction: a-one and a-two are in A only, b-one and b-two
        //are in B only, and the shared dot is in both. The presence difference is therefore the four per-side
        //entries; the shared one is in neither side of the difference.
        HashSet<string> entryKeysA = [.. stateA.Entries.Select(EntryKey)];
        HashSet<string> entryKeysB = [.. stateB.Entries.Select(EntryKey)];

        //The foundational law: an item is in Items(A) and not Items(B) exactly when its entry is present in A
        //and absent in B. Assert both inclusions over the full item-set symmetric difference.
        HashSet<string> leftOnlyItems = [.. itemsA];
        leftOnlyItems.ExceptWith(itemsB);
        HashSet<string> rightOnlyItems = [.. itemsB];
        rightOnlyItems.ExceptWith(itemsA);

        //Every left-only item resolves to an entry present in A and absent from B.
        foreach(string hex in leftOnlyItems)
        {
            ReadOnlyMemory<byte> item = FromHex(hex);
            Assert.IsTrue(a.TryResolve(item, out DottedEntry<string>? entry));
            Assert.IsNotNull(entry);
            Assert.Contains(EntryKey(entry), entryKeysA);
            Assert.DoesNotContain(EntryKey(entry), entryKeysB);
        }

        //Every right-only item resolves to an entry present in B and absent from A.
        foreach(string hex in rightOnlyItems)
        {
            ReadOnlyMemory<byte> item = FromHex(hex);
            Assert.IsTrue(b.TryResolve(item, out DottedEntry<string>? entry));
            Assert.IsNotNull(entry);
            Assert.Contains(EntryKey(entry), entryKeysB);
            Assert.DoesNotContain(EntryKey(entry), entryKeysA);
        }

        //And the difference is exactly the four per-side entries — the shared dot's item is in both item sets,
        //so it appears in neither side of the symmetric difference.
        Assert.HasCount(2, leftOnlyItems);
        Assert.HasCount(2, rightOnlyItems);

        HashSet<string> sharedItems = [.. itemsA];
        sharedItems.IntersectWith(itemsB);
        Assert.HasCount(1, sharedItems);
    }


    [TestMethod]
    public void DistinctDotsForTheSameValueProjectToDistinctItems()
    {
        //The same value added twice under R1 mints two distinct dots, (R1, 1) and (R1, 2); the counter is part
        //of the frame, so the two entries project to distinct items even though their value bytes are equal.
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty
            .Add(R1, "same")
            .Add(R1, "same")
            .ToState();

        DottedReconciliationProjection<string> projection = new(state, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);

        Assert.AreEqual(2, projection.Count);
        HashSet<string> distinct = [.. projection.Items.Select(HexOf)];
        Assert.HasCount(2, distinct);
    }


    [TestMethod]
    public void TwoEntriesCollidingUnderAStubDigestThrowArgumentException()
    {
        //A hand-built valid-for-projection state with two distinct dots; both honest (32-byte replica, counter
        //at least one, context dominating each dot) so only the collision can be the cause of the throw.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 2)]);
        var state = new DottedVersionVectorSetState<string>(
            context,
            [
                new DottedEntry<string>(Bytes(R1), 1, "first"),
                new DottedEntry<string>(Bytes(R1), 2, "second")
            ]);

        //A stub digest that maps every frame to the same 32-byte constant: the second entry produces an
        //already-present item, which violates injectivity and would XOR-cancel two distinct entries silently.
        ComputeDigestDelegate collidingDigest = static _ => new byte[32];

        Assert.ThrowsExactly<ArgumentException>(() => new DottedReconciliationProjection<string>(state, ContentHashContract, collidingDigest, CanonicalizeUtf8, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public void ContextPassesThroughAsTheSameInstance()
    {
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty
            .Add(R1, "alpha")
            .Add(R2, "beta")
            .ToState();

        DottedReconciliationProjection<string> projection = new(state, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared);

        //The context is a passthrough of the same instance the state carries, not a copy.
        Assert.AreSame(state.Context, projection.Context);
        Assert.AreSame(ContentHashContract, projection.Contract);
    }


    [TestMethod]
    public void NullArgumentsAreRejected()
    {
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty.Add(R1, "alpha").ToState();

        Assert.ThrowsExactly<ArgumentNullException>(() => new DottedReconciliationProjection<string>(null!, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DottedReconciliationProjection<string>(state, null!, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DottedReconciliationProjection<string>(state, ContentHashContract, null!, CanonicalizeUtf8, BaseMemoryPool.Shared));
        Assert.ThrowsExactly<ArgumentNullException>(() => new DottedReconciliationProjection<string>(state, ContentHashContract, Sha256Digest, null!, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public void AStructuralDomainContractIsRejected()
    {
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty.Add(R1, "alpha").ToState();

        //The dotted projection digests a variable-length frame; the structural fixed-width dotted item is out
        //of scope, so a structural contract is rejected.
        var structural = new ReconciliationContract(ReconciliationItemDomain.Structural, 32, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

        Assert.ThrowsExactly<ArgumentException>(() => new DottedReconciliationProjection<string>(state, structural, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public void AnEntryWithAThirtyOneByteReplicaIsRejected()
    {
        //A hand-crafted state whose single entry carries a 31-byte replica — invalid for projection because a
        //replica must be exactly ReplicaId.Size (32) bytes. The state is built directly, bypassing the DVVSet
        //FromState validation, so the projection's own guard is what must fire.
        ImmutableArray<byte> shortReplica = [.. Enumerable.Repeat((byte)0x01, ReplicaId.Size - 1)];
        var context = new VectorClockState([new ReplicaCounterEntry(shortReplica, 1)]);
        var state = new DottedVersionVectorSetState<string>(context, [new DottedEntry<string>(shortReplica, 1, "alpha")]);

        Assert.ThrowsExactly<ArgumentException>(() => new DottedReconciliationProjection<string>(state, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public void AnEntryWithACounterOfZeroIsRejected()
    {
        //A hand-crafted state whose single entry has a zero counter — invalid for projection because a dot is
        //minted by advancing the context past zero, so a counter of at least one is required. Built directly
        //so the projection's own guard is what must fire.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var state = new DottedVersionVectorSetState<string>(context, [new DottedEntry<string>(Bytes(R1), 0, "alpha")]);

        Assert.ThrowsExactly<ArgumentException>(() => new DottedReconciliationProjection<string>(state, ContentHashContract, Sha256Digest, CanonicalizeUtf8, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public void ADigestOfTheWrongWidthIsRejected()
    {
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty.Add(R1, "alpha").ToState();

        //A stub digest returning 16 bytes instead of the contract's 32 violates the contract width.
        ComputeDigestDelegate narrowDigest = static _ => new byte[16];

        Assert.ThrowsExactly<ArgumentException>(() => new DottedReconciliationProjection<string>(state, ContentHashContract, narrowDigest, CanonicalizeUtf8, BaseMemoryPool.Shared));
    }


    [TestMethod]
    public void ConstructionOverAPooledPoolLeavesNoActiveRentalsAndRentsAtLeastOnce()
    {
        //Several present entries so the framing scratch is rented at least once during construction.
        DottedVersionVectorSetState<string> state = DottedVersionVectorSet<string>.Empty
            .Add(R1, "alpha")
            .Add(R2, "beta")
            .Add(R1, "gamma")
            .Add(R3, "delta")
            .ToState();

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            //The projection is NOT IDisposable: every rental it takes for the framing scratch is returned
            //before the constructor returns, only the produced items escaping as owned arrays. Constructing it
            //over the pool must therefore leave the rental ledger balanced with no live rentals afterwards.
            DottedReconciliationProjection<string> projection = new(state, ContentHashContract, Sha256Digest, CanonicalizeUtf8, pool);

            Assert.AreEqual(state.Entries.Length, projection.Count);
        }

        //The framing scratch is rented and returned within the constructor, so the net active gauge balances to
        //zero, and at least one rental occurred because entries were present to frame.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    /// <summary>
    /// Resolves the projection's item for the given dot and value by framing the same pinned layout the
    /// projection commits to — replica(32) || counterU64LE(8) || UTF-8 value — and SHA-256-ing it, then
    /// confirming the projection produced exactly that item and resolves it back to the matching entry.
    /// </summary>
    private static ReadOnlyMemory<byte> ItemFor(DottedReconciliationProjection<string> projection, ReplicaId replica, int counter, string value)
    {
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        byte[] frame = new byte[ReplicaId.Size + sizeof(ulong) + valueBytes.Length];
        replica.CopyTo(frame.AsSpan(0, ReplicaId.Size));
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(ReplicaId.Size, sizeof(ulong)), (ulong)counter);
        valueBytes.CopyTo(frame.AsSpan(ReplicaId.Size + sizeof(ulong)));

        ReadOnlyMemory<byte> item = SHA256.HashData(frame).AsMemory();

        Assert.IsTrue(projection.TryResolve(item, out DottedEntry<string>? entry));
        Assert.IsNotNull(entry);
        Assert.AreEqual(value, entry.Value);

        return item;
    }


    /// <summary>
    /// A content key for an entry independent of how the projection digests it: replica hex, counter, and
    /// value.
    /// </summary>
    private static string EntryKey(DottedEntry<string> entry)
    {
        return $"{Convert.ToHexString(entry.Replica.AsSpan())}:{entry.Counter}:{entry.Value}";
    }


    private static string HexOf(ReadOnlyMemory<byte> item)
    {
        return Convert.ToHexString(item.Span);
    }


    private static ReadOnlyMemory<byte> FromHex(string hex)
    {
        return Convert.FromHexString(hex);
    }


    private static ImmutableArray<byte> Bytes(ReplicaId replica)
    {
        return ImmutableArray.Create(replica.AsSpan());
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


}
