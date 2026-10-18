using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The fail-closed governance gate for the reconciliation tier's pooled memory: a <see cref="MeterListener"/>
/// subscribed to <see cref="BaseMemoryPoolMetrics.MeterName"/> sums the pool's rent and return counters across
/// a full session, and after every pooled object is disposed the rented count must equal the returned count and
/// exceed zero, so every pooled backing was returned. One test drives a direct encode, stream, decode exchange
/// over a fresh <see cref="BaseMemoryPool"/>; the other drives the two-session in-memory reconcile with a pool
/// injected into both sessions. A rented/returned imbalance is a leaked rental -- a hard CI failure, never
/// tolerated.
/// </summary>
/// <remarks>
/// Both tests observe the pool's process-global rental instruments, so the class is marked
/// <see cref="DoNotParallelizeAttribute"/> to keep their measurement totals free of rentals emitted by other
/// pool-using tests running concurrently -- the same isolation the other metric-observing suites use.
/// </remarks>
[TestClass]
[DoNotParallelize]
internal sealed class ReconciliationMemoryAccountabilityTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int DefaultBatchSize = 4;

    private static ReconciliationContract StructuralContract { get; } =
        new(ReconciliationItemDomain.Structural, 8, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    private static ReconciliationContract ContentHashContract { get; } = ReconciliationContract.ContentHashDefault;

    private static byte[] A1 { get; } = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] A2 { get; } = [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18];

    private static byte[] A3 { get; } = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28];

    private static byte[] B1 { get; } = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38];

    private static string[] ExpectedConverged { get; } = [.. new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta" }.Order()];

    private static ReplicaId R1 { get; } = Replica(1);

    private static ReplicaId R2 { get; } = Replica(2);

    private static ReplicaId R3 { get; } = Replica(3);

    public TestContext TestContext { get; set; } = null!;


    /// <summary>A faulting cell owner still releases the other backing and a retried disposal does nothing.</summary>
    [TestMethod]
    public void AFaultingCellReleaseStillReturnsTheOtherCellBuffer()
    {
        using BaseMemoryPool inner = new();
        using ThrowingOwnerMemoryPool pool = new(inner, throwingRent: 1);
        ReconciliationCellBuffer buffer = new(8, 8, pool, 4);
        TestContext.WriteLine($"Rentals before disposal: {pool.Rented}.");
        Assert.AreEqual(2, pool.Rented);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => buffer.Dispose());

        Assert.AreEqual("The rental cannot be released.", failure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.IsGreaterThan(1, pool.Rented);
        Assert.AreEqual(pool.Rented - 1, pool.Returned);

        int rented = pool.Rented;
        int returned = pool.Returned;
        int faultedReleases = pool.FaultedReleases;

        buffer.Dispose();

        Assert.AreEqual(rented, pool.Rented);
        Assert.AreEqual(returned, pool.Returned);
        Assert.AreEqual(faultedReleases, pool.FaultedReleases);
        GC.KeepAlive(buffer);
    }


    /// <summary>A faulting block owner still releases the other blocks and leaves the arena disposed.</summary>
    [TestMethod]
    public void AFaultingBlockReleaseStillReturnsTheOtherBlocks()
    {
        using BaseMemoryPool inner = new();
        using ThrowingOwnerMemoryPool pool = new(inner, throwingRent: 1);
        ReconciliationItemArena arena = new(8, pool, itemCapacityHint: 0);
        Span<byte> item = stackalloc byte[8];
        for(int i = 0; i < 65_537; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(item, i);
            _ = arena.Append(item);
        }

        TestContext.WriteLine($"Rentals before disposal: {pool.Rented}.");
        Assert.AreEqual(2, pool.Rented);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => arena.Dispose());

        Assert.AreEqual("The rental cannot be released.", failure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.IsGreaterThan(1, pool.Rented);
        Assert.AreEqual(pool.Rented - 1, pool.Returned);
        Assert.ThrowsExactly<ObjectDisposedException>(() => arena.Append(A1));

        int rented = pool.Rented;
        int returned = pool.Returned;
        int faultedReleases = pool.FaultedReleases;

        arena.Dispose();

        Assert.AreEqual(rented, pool.Rented);
        Assert.AreEqual(returned, pool.Returned);
        Assert.AreEqual(faultedReleases, pool.FaultedReleases);
        GC.KeepAlive(arena);
    }


    /// <summary>A faulting cell owner still releases the encoder items, cursors, and membership storage.</summary>
    [TestMethod]
    public void AFaultingCellReleaseStillReleasesTheEncodersItemsAndCursors()
    {
        using BaseMemoryPool inner = new();
        using ThrowingOwnerMemoryPool pool = new(inner, throwingRent: 1);
        ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.Strict,
            pool, new ReconciliationCapacityHint(items: 4, cells: 4));
        encoder.Add(A1);
        encoder.Add(A2);
        _ = encoder.ProduceNext();
        TestContext.WriteLine($"Rentals before disposal: {pool.Rented}.");
        Assert.AreEqual(3, pool.Rented);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Dispose());

        Assert.AreEqual("The rental cannot be released.", failure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.IsGreaterThan(1, pool.Rented);
        Assert.AreEqual(pool.Rented - 1, pool.Returned);
        Assert.AreEqual(0, encoder.PendingCursorCount);
        Assert.AreEqual(0, encoder.MemberCount);

        int rented = pool.Rented;
        int returned = pool.Returned;
        int faultedReleases = pool.FaultedReleases;

        encoder.Dispose();

        Assert.AreEqual(rented, pool.Rented);
        Assert.AreEqual(returned, pool.Returned);
        Assert.AreEqual(faultedReleases, pool.FaultedReleases);
        GC.KeepAlive(encoder);
    }


    /// <summary>A faulting cell owner still releases the decoder items and a retried disposal does nothing.</summary>
    [TestMethod]
    public void AFaultingCellReleaseStillReleasesTheDecodersItems()
    {
        using BaseMemoryPool inner = new();
        using ThrowingOwnerMemoryPool pool = new(inner, throwingRent: 1);
        ReconciliationDecoder decoder = new(StructuralContract, pool, new ReconciliationCapacityHint(items: 4, cells: 4));
        using(ReconciliationEncoder source = new(StructuralContract, ReconciliationInjectivityEnforcement.None,
            BaseMemoryPool.Shared, ReconciliationCapacityHint.None))
        {
            source.Add(A1);
            decoder.Absorb(source.ProduceNext());
        }

        Assert.HasCount(1, decoder.DecodedItems);
        TestContext.WriteLine($"Rentals before disposal: {pool.Rented}.");
        Assert.AreEqual(3, pool.Rented);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => decoder.Dispose());

        Assert.AreEqual("The rental cannot be released.", failure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.IsGreaterThan(1, pool.Rented);
        Assert.AreEqual(pool.Rented - 1, pool.Returned);

        int rented = pool.Rented;
        int returned = pool.Returned;
        int faultedReleases = pool.FaultedReleases;

        decoder.Dispose();

        Assert.AreEqual(rented, pool.Rented);
        Assert.AreEqual(returned, pool.Returned);
        Assert.AreEqual(faultedReleases, pool.FaultedReleases);
        GC.KeepAlive(decoder);
    }


    /// <summary>A faulting encoder release still releases the session decoder and a retried disposal does nothing.</summary>
    [TestMethod]
    public void AFaultingEncoderReleaseStillReleasesTheSessionsDecoder()
    {
        using BaseMemoryPool inner = new();
        using ThrowingOwnerMemoryPool pool = new(inner, throwingRent: 1);
        AntiEntropySession<string> session = new(AntiEntropyRole.Initiator, StructuralContract, [A1, A2], DefaultBatchSize, pool);
        TestContext.WriteLine($"Rentals before disposal: {pool.Rented}.");
        Assert.AreEqual(5, pool.Rented);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => session.Dispose());

        Assert.AreEqual("The rental cannot be released.", failure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.IsGreaterThan(1, pool.Rented);
        Assert.AreEqual(pool.Rented - 1, pool.Returned);

        int rented = pool.Rented;
        int returned = pool.Returned;
        int faultedReleases = pool.FaultedReleases;

        session.Dispose();

        Assert.AreEqual(rented, pool.Rented);
        Assert.AreEqual(returned, pool.Returned);
        Assert.AreEqual(faultedReleases, pool.FaultedReleases);
        GC.KeepAlive(session);
    }


    /// <summary>A snapshot responder rents fewer buffers because only the initiator builds a decoder.</summary>
    [TestMethod]
    public void ASnapshotResponderSessionRentsFewerBuffersThanASnapshotInitiator()
    {
        EnumerateReconciliationItemsDelegate enumerate = (visit, _) =>
        {
            visit(A1);
            visit(A2);
        };

        long initiatorRented;
        using(RentalAccountant accountant = new())
        {
            using BaseMemoryPool pool = new();
            using(new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, enumerate, DefaultBatchSize, pool))
            {
            }

            initiatorRented = accountant.Rented;
        }

        long responderRented;
        using(RentalAccountant accountant = new())
        {
            using BaseMemoryPool pool = new();
            using(new AntiEntropySession<string>(AntiEntropyRole.Responder, StructuralContract, enumerate, DefaultBatchSize, pool))
            {
            }

            responderRented = accountant.Rented;
        }

        TestContext.WriteLine($"Initiator rentals: {initiatorRented}; responder rentals: {responderRented}.");
        Assert.IsGreaterThan(0L, responderRented);
        Assert.IsLessThan(initiatorRented, responderRented);
    }


    /// <summary>Disposing a still-referenced snapshot session returns every rental while its pool remains live.</summary>
    [TestMethod]
    public void DisposingASnapshotSessionReturnsEveryRentalWhileThePoolIsLive()
    {
        using RentalAccountant accountant = new();
        using BaseMemoryPool pool = new();
        EnumerateReconciliationItemsDelegate enumerate = (visit, _) => visit(A1);
        AntiEntropySession<string> session = new(AntiEntropyRole.Responder, StructuralContract, enumerate, DefaultBatchSize, pool);
        TestContext.WriteLine($"Active rentals before disposal: {accountant.NetActive}.");
        Assert.IsGreaterThan(0L, accountant.NetActive);

        session.Dispose();

        Assert.AreEqual(0L, accountant.NetActive);
        GC.KeepAlive(session);
    }


    /// <summary>A failed pending-queue allocation propagates before any cell rental is acquired.</summary>
    [TestMethod]
    public void AnItemHintPastTheQueueBoundFailsBeforeAnyRental()
    {
        using RentalAccountant accountant = new();
        using BaseMemoryPool pool = new();

        OutOfMemoryException fault = Assert.ThrowsExactly<OutOfMemoryException>(() =>
            new ReconciliationEncoder(StructuralContract, ReconciliationInjectivityEnforcement.None,
                pool, new ReconciliationCapacityHint(items: int.MaxValue, cells: 0)));

        TestContext.WriteLine($"{fault.GetType().FullName}: {fault.Message}");
        Assert.AreEqual(0L, accountant.Rented);
        Assert.AreEqual(0L, accountant.NetActive);
    }


    /// <summary>A faulting retired release preserves the grown pair and the live cells without appending a cell.</summary>
    [TestMethod]
    public void AFaultingRetiredReleaseOnGrowthKeepsTheBufferUsable()
    {
        using BaseMemoryPool inner = new();
        using ThrowingOwnerMemoryPool pool = new(inner, throwingRent: 1);
        ReconciliationCellBuffer buffer = new(8, 8, pool, cellCapacityHint: 0);
        for(int i = 0; i < 4; i++)
        {
            _ = buffer.Append();
        }

        A1.CopyTo(buffer.SumAt(0));

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => buffer.Append());

        Assert.AreEqual("The rental cannot be released.", failure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.AreEqual(4, pool.Rented);
        Assert.AreEqual(1, pool.Returned);
        Assert.AreEqual(4, buffer.Count);

        Assert.AreEqual(4, buffer.Append());
        Assert.AreEqual(4, pool.Rented);
        Assert.AreSequenceEqual(new byte[8], buffer.SumAt(4).ToArray());
        Assert.AreSequenceEqual(A1, buffer.SumAt(0).ToArray());

        buffer.Dispose();

        Assert.AreEqual(3, pool.Returned);
        Assert.AreEqual(1, pool.FaultedReleases);
        GC.KeepAlive(buffer);
    }


    /// <summary>Growth clears retired backings before returning them and preserves live cells in the grown pair.</summary>
    [TestMethod]
    public void ARetiredBackingIsClearedBeforeItReturnsToThePool()
    {
        using BaseMemoryPool inner = new();
        using ReturnObservingMemoryPool pool = new(inner);
        using ReconciliationCellBuffer buffer = new(8, 8, pool, cellCapacityHint: 0);
        for(int i = 0; i < 4; i++)
        {
            _ = buffer.Append();
            buffer.SumAt(i).Fill(0xFF);
            buffer.ChecksumAt(i).Fill(0xFF);
        }

        Assert.AreEqual(4, buffer.Append());

        Assert.AreEqual(2, pool.Returned);
        Assert.AreEqual(0, pool.DirtyReturns);
        foreach(byte value in buffer.SumAt(0))
        {
            Assert.AreEqual((byte)0xFF, value);
        }

        Assert.AreSequenceEqual(new byte[8], buffer.SumAt(4).ToArray());

        buffer.Dispose();

        Assert.AreEqual(4, pool.Returned);
        Assert.AreEqual(0, pool.DirtyReturns);
    }


    /// <summary>A refused first decoder rental releases every rental held by the session's item-list encoder.</summary>
    [TestMethod]
    public void AFailedDecoderRentReleasesTheSessionsEncoder()
    {
        using BaseMemoryPool inner = new();
        using RefusingRentMemoryPool pool = new(inner, refusedRent: 4);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, [A1, A2], DefaultBatchSize, pool));

        Assert.AreEqual("The rental cannot be made.", failure.Message);
        Assert.AreEqual(1, pool.RefusedRents);
        Assert.AreEqual(3, pool.Rented);
        Assert.AreEqual(3, pool.Returned);
    }


    /// <summary>A refused first decoder rental releases both rentals held by the session's snapshot encoder.</summary>
    [TestMethod]
    public void AFailedDecoderRentReleasesTheSessionsSnapshotEncoder()
    {
        using BaseMemoryPool inner = new();
        using RefusingRentMemoryPool pool = new(inner, refusedRent: 3);
        EnumerateReconciliationItemsDelegate enumerate = (visit, _) => visit(A1);

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, enumerate, DefaultBatchSize, pool));

        Assert.AreEqual("The rental cannot be made.", failure.Message);
        Assert.AreEqual(1, pool.RefusedRents);
        Assert.AreEqual(2, pool.Rented);
        Assert.AreEqual(2, pool.Returned);
    }


    /// <summary>A construction failure and a release failure propagate together while every other rental is returned.</summary>
    [TestMethod]
    public void AFailedConstructionWhoseReleaseAlsoFailsCarriesBothFailures()
    {
        using BaseMemoryPool inner = new();
        using RefusingRentMemoryPool refusing = new(inner, refusedRent: 4);
        using ThrowingOwnerMemoryPool pool = new(refusing, throwingRent: 1);

        AggregateException failure = Assert.ThrowsExactly<AggregateException>(() =>
            new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, [A1, A2], DefaultBatchSize, pool));

        Assert.HasCount(2, failure.InnerExceptions);
        InvalidOperationException constructionFailure = Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerExceptions[0]);
        InvalidOperationException releaseFailure = Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerExceptions[1]);
        Assert.AreEqual("The rental cannot be made.", constructionFailure.Message);
        Assert.AreEqual("The rental cannot be released.", releaseFailure.Message);
        Assert.AreEqual(1, pool.FaultedReleases);
        Assert.AreEqual(2, refusing.Returned);
    }


    /// <summary>A large item hint leaves the arena lazy with fixed blocks and sizes cell rentals only by the cell hint.</summary>
    [TestMethod]
    public void ALargeItemHintRentsOnlySmallCellsUntilTheFirstAdd()
    {
        using RentalAccountant accountant = new();
        using var meter = new Meter(BaseMemoryPoolMetrics.MeterName);
        List<int> segmentSizes = [];
        using BaseMemoryPool pool = new(meter, capacityStrategy: size =>
        {
            segmentSizes.Add(size);

            return 1;
        });
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.None,
            pool, new ReconciliationCapacityHint(items: 1 << 20, cells: 8));

        Assert.AreEqual(2L, accountant.Rented);
        Assert.AreEqual(2L, accountant.NetActive);
        Assert.AreSequenceEqual(new[] { 8 * StructuralContract.ItemWidth, 8 * StructuralContract.ChecksumWidth }, segmentSizes);

        encoder.Add(A1);

        Assert.AreEqual(3L, accountant.Rented);
        Assert.AreEqual(3L, accountant.NetActive);
        Assert.AreEqual(65_536 * StructuralContract.ItemWidth, segmentSizes[^1]);
    }


    /// <summary>Disposing a still-referenced encoder returns every rental while its pool remains live.</summary>
    [TestMethod]
    public void ADisposedReferencedEncoderLeavesNoOutstandingRentals()
    {
        using RentalAccountant accountant = new();
        using BaseMemoryPool pool = new();
        using ReconciliationEncoder encoder = new(StructuralContract, ReconciliationInjectivityEnforcement.Strict,
            pool, new ReconciliationCapacityHint(items: 16, cells: 8));
        encoder.Add(A1);
        encoder.Add(A2);
        _ = encoder.ProduceNext();
        Assert.IsGreaterThan(0L, accountant.NetActive);

        encoder.Dispose();

        Assert.AreEqual(0L, accountant.NetActive);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
        Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.ProduceNext());
        GC.KeepAlive(encoder);
    }


    /// <summary>An encode and decode exchange returns every pooled rental on disposal.</summary>
    [TestMethod]
    public void OneEncodeStreamDecodeSessionLeaksNoPooledRentals()
    {
        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            //A small structural difference over the well-known key: the left set holds A1, A2, B1, the right
            //holds A1, A2, A3, so the symmetric difference is the two items B1 and A3. Both encoders and the
            //decoder rent their cell backings from the same pool inside this scope.
            using ReconciliationEncoder left = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, ReconciliationCapacityHint.None);
            left.Add(A1);
            left.Add(A2);
            left.Add(B1);

            using ReconciliationEncoder right = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, ReconciliationCapacityHint.None);
            right.Add(A1);
            right.Add(A2);
            right.Add(A3);

            using ReconciliationDecoder decoder = new(StructuralContract, pool, ReconciliationCapacityHint.None);

            //Absorb the symbol-wise difference until the peel completes; the cap is generous for a difference of
            //two and the loop stops the moment cell zero clears.
            const int Cap = 200;
            for(int n = 0; n < Cap && !decoder.IsComplete; n++)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            }

            Assert.IsTrue(decoder.IsComplete);
            Assert.HasCount(2, decoder.DecodedItems);
        }

        //After the using-scope disposes both encoders, the decoder, and the pool, the rental ledger must
        //balance: the net active gauge returns to zero and the rented count equals the returned count and is
        //strictly positive, proving every pooled cell backing was returned.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public void AThrowingSecondRentReturnsTheFirstRentalRatherThanLeakingIt()
    {
        //The cell buffer rents its two backings as a pair. A capacity strategy that yields a valid slab for the
        //sum backing's rent size but zero segments for the checksum backing's size forces the SECOND rent to
        //throw after the first has already taken a live segment. The buffer must return that first rental on the
        //failure path rather than orphan it, or the accountability invariant the whole tier rests on is broken.
        const int SumWidth = 16;
        const int ChecksumWidth = 8;
        const int InitialCapacity = 4;
        const int ChecksumRentSize = InitialCapacity * ChecksumWidth;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using var meter = new Meter(BaseMemoryPoolMetrics.MeterName);
            using BaseMemoryPool pool = new(meter, capacityStrategy: size => size == ChecksumRentSize ? 0 : 4);

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            {
                using ReconciliationCellBuffer buffer = new(SumWidth, ChecksumWidth, pool, cellCapacityHint: 0);
            });
        }

        //The first (sum) rental succeeded and the second (checksum) rent threw during slab construction; the net
        //active gauge must still balance to zero and the rented count equal the returned count, proving the first
        //rental was returned on the exception path instead of leaking.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    /// <summary>Encoder and decoder growth returns every cell and arena rental on disposal.</summary>
    [TestMethod]
    public void EncoderAndDecoderArenaGrowthLeavesNoActiveRentals()
    {
        const int Stride = 8;

        //Each encoder holds 65,537 distinct items and the decoder recovers 65,538, so all three arenas
        //cross a fixed-block boundary while the cell buffers grow with the produced stream.
        const int SharedCount = 32_768;
        const int LeftOnlyCount = 32_769;
        const int RightOnlyCount = 32_769;
        const int ExpectedDecoded = LeftOnlyCount + RightOnlyCount;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            using ReconciliationEncoder left = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, ReconciliationCapacityHint.None);
            using ReconciliationEncoder right = new(StructuralContract, ReconciliationInjectivityEnforcement.None, pool, ReconciliationCapacityHint.None);

            for(int n = 0; n < SharedCount; n++)
            {
                byte[] shared = BuildArenaItem(Stride, 0, n);
                left.Add(shared);
                right.Add(shared);
            }

            for(int n = 0; n < LeftOnlyCount; n++)
            {
                left.Add(BuildArenaItem(Stride, 1, n));
            }

            for(int n = 0; n < RightOnlyCount; n++)
            {
                right.Add(BuildArenaItem(Stride, 2, n));
            }

            using ReconciliationDecoder decoder = new(StructuralContract, pool, ReconciliationCapacityHint.None);

            //The symbol cap bounds the exchange; the whole difference peels within it.
            const int Cap = 400_000;
            for(int n = 0; n < Cap && !decoder.IsComplete; n++)
            {
                decoder.Absorb(left.ProduceNext().Combine(right.ProduceNext()));
            }

            Assert.IsTrue(decoder.IsComplete);
            Assert.HasCount(ExpectedDecoded, decoder.DecodedItems);
        }

        //After the using-scope disposes both encoders, the decoder, and the pool, every block both the cell
        //buffers and the arenas grew through must have been returned: the net active gauge balances to zero and
        //the rented count equals the returned count and is strictly positive. This is the explicit arena-growth
        //balance check the two byte-vector tests only cover implicitly.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    [TestMethod]
    public async Task TwoSessionReconcileLeavesNoActiveRentalsAfterDisposal()
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeoutSource.CancelAfter(Timeout);
        CancellationToken cancellationToken = timeoutSource.Token;

        RentalAccountant accountant = new();
        using(accountant)
        {
            using BaseMemoryPool pool = new();

            OrSet<string> ancestor = OrSet<string>.Empty.Add("alpha", R1).Add("beta", R1).Add("gamma", R1);
            OrSet<string> initiatorSet = ancestor.Add("delta", R2).Add("epsilon", R2);
            OrSet<string> responderSet = ancestor.Add("zeta", R3);

            ReadOnlyMemory<byte>[] initiatorItems = ProjectHashes(initiatorSet);
            ReadOnlyMemory<byte>[] responderItems = ProjectHashes(responderSet);

            //Inject the pool into both sessions through the pool-bearing constructor; each session builds its
            //encoder (both roles) and decoder (initiator) over the pool and disposes them when the session is
            //disposed at the end of this scope.
            using AntiEntropySession<string> initiator = new(AntiEntropyRole.Initiator, ContentHashContract, initiatorItems, DefaultBatchSize, pool);
            using AntiEntropySession<string> responder = new(AntiEntropyRole.Responder, ContentHashContract, responderItems, DefaultBatchSize, pool);

            Dictionary<string, string> initiatorDirectory = BuildHashDirectory(initiatorSet);
            Dictionary<string, string> responderDirectory = BuildHashDirectory(responderSet);
            HashSet<string> initiatorHexes = [.. initiatorItems.Select(item => Convert.ToHexString(item.Span))];

            //The initiator partitions the decoded difference into a fetch for digests it lacks and a push for
            //digests it holds in surplus; the responder serves fetches and both sides apply received elements.
            ResolveReconciliationDifferenceDelegate<string> resolve = (decoded, _) =>
            {
                ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
                ImmutableArray<ReconciliationElementEntry<string>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<string>>();
                foreach(ReadOnlyMemory<byte> item in decoded)
                {
                    string hex = Convert.ToHexString(item.Span);
                    if(initiatorHexes.Contains(hex))
                    {
                        push.Add(new ReconciliationElementEntry<string>(item, initiatorDirectory[hex]));
                    }
                    else
                    {
                        fetch.Add(item);
                    }
                }

                return new ReconciliationDifferenceResolution<string>(fetch.ToImmutable(), push.ToImmutable());
            };

            ServeReconciliationFetchDelegate<string> serve = items =>
                [.. items.Select(item => new ReconciliationElementEntry<string>(item, responderDirectory[Convert.ToHexString(item.Span)]))];

            OrSet<string> initiatorResult = initiatorSet;
            ApplyReconciliationElementsDelegate<string> applyToInitiator = (entries, _, ct) =>
            {
                foreach(ReconciliationElementEntry<string> entry in entries)
                {
                    initiatorResult = initiatorResult.Add(entry.Element, R2);
                }

                return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
            };

            OrSet<string> responderResult = responderSet;
            ApplyReconciliationElementsDelegate<string> applyToResponder = (entries, _, ct) =>
            {
                foreach(ReconciliationElementEntry<string> entry in entries)
                {
                    responderResult = responderResult.Add(entry.Element, R3);
                }

                return new ValueTask<ImmutableArray<DotState>>(ImmutableArray<DotState>.Empty);
            };

            Task initiatorRun = initiator.RunAsync(Forward(responder), resolve, null, applyToInitiator, cancellationToken: cancellationToken);
            Task responderRun = responder.RunAsync(Forward(initiator), null, serve, applyToResponder, cancellationToken: cancellationToken);

            await DriveInitiatorToCompletionAsync(initiatorRun, responder, ContentHashContract, initiatorItems, responderItems, cancellationToken).ConfigureAwait(false);

            responder.Complete();
            await Task.WhenAll(initiatorRun, responderRun).ConfigureAwait(false);

            //The exchange converges, which proves the pooled cell backings carried the reconciliation correctly,
            //not merely that they balanced.
            Assert.AreSequenceEqual(ExpectedConverged, Sorted(initiatorResult));
            Assert.AreSequenceEqual(ExpectedConverged, Sorted(responderResult));
            Assert.HasCount(3, initiator.DecodedItems);
        }

        //Both sessions and the pool are disposed at the end of the scope, so every rental the sessions took for
        //their encoders and decoder is returned: the net active gauge balances to zero.
        Assert.AreEqual(0L, accountant.NetActive);
        Assert.IsGreaterThan(0L, accountant.Rented);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
    }


    /// <summary>
    /// Drives a two-session exchange to the initiator's completion without polling: a local mirror decoder over the
    /// same items and coded symbols names the exact symbol count the initiator's decoder needs, so the responder is
    /// triggered just enough batches to deliver them, and the ordered channel then carries the done, fetch, and
    /// answer round-trip through to the initiator's completion with no further trigger.
    /// </summary>
    private static async Task DriveInitiatorToCompletionAsync(
        Task initiatorRun,
        AntiEntropySession<string> responder,
        ReconciliationContract contract,
        IReadOnlyList<ReadOnlyMemory<byte>> initiatorItems,
        IReadOnlyList<ReadOnlyMemory<byte>> responderItems,
        CancellationToken cancellationToken)
    {
        using ReconciliationEncoder local = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(ReadOnlyMemory<byte> item in initiatorItems)
        {
            local.Add(item.Span);
        }

        using ReconciliationEncoder remote = new(contract, ReconciliationInjectivityEnforcement.None, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(ReadOnlyMemory<byte> item in responderItems)
        {
            remote.Add(item.Span);
        }

        using ReconciliationDecoder mirror = new(contract, BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        int symbols = 0;
        while(!mirror.IsComplete)
        {
            mirror.Absorb(local.ProduceNext().Combine(remote.ProduceNext()));
            symbols++;
        }

        int batches = (symbols + responder.BatchSize - 1) / responder.BatchSize;
        for(int batch = 0; batch < batches; batch++)
        {
            await responder.TriggerBatchAsync(cancellationToken).ConfigureAwait(false);
        }

        await initiatorRun.ConfigureAwait(false);
    }


    /// <summary>Creates a sender that forwards envelopes to the peer session.</summary>
    private static SendReconciliationEnvelopeDelegate<string> Forward(AntiEntropySession<string> peer)
    {
        return (envelope, cancellationToken) => ForwardTo(peer, envelope, cancellationToken);
    }


    /// <summary>Submits an envelope to the peer and discards it if the peer has already closed its channel.</summary>
    private static ValueTask ForwardTo(AntiEntropySession<string> peer, ReconciliationEnvelope<string> envelope, CancellationToken cancellationToken)
    {
        try
        {
            return peer.SubmitAsync(envelope, cancellationToken);
        }
        catch(ChannelClosedException)
        {
            //A completed peer is a wound-down session; dropping the late send is exactly the transport's behaviour.

            return ValueTask.CompletedTask;
        }
    }


    private static ReadOnlyMemory<byte>[] ProjectHashes(OrSet<string> set)
    {
        List<ReadOnlyMemory<byte>> items = [];
        foreach(string element in set.Elements)
        {
            items.Add(SHA256.HashData(Encoding.UTF8.GetBytes(element)));
        }

        return [.. items];
    }


    private static Dictionary<string, string> BuildHashDirectory(OrSet<string> set)
    {
        Dictionary<string, string> directory = [];
        foreach(string element in set.Elements)
        {
            directory[Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(element)))] = element;
        }

        return directory;
    }


    /// <summary>Returns the set elements in sorted order.</summary>
    private static string[] Sorted(OrSet<string> set)
    {
        return [.. set.Elements.Order()];
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>
    /// Builds a deterministic, distinct item of the given width without System.Random (CA5394).
    /// </summary>
    /// <remarks>
    /// The group tag in the leading byte separates the shared, left-only, and right-only corpora, and the
    /// little-endian index in the following bytes makes every item within a group distinct across the
    /// few-hundred-item range; the remaining stride carries a position-derived tail. Distinct (group, index)
    /// pairs therefore yield distinct full-width items, so no enforcement is needed to keep them apart.
    /// </remarks>
    private static byte[] BuildArenaItem(int stride, int group, int index)
    {
        byte[] item = new byte[stride];
        item[0] = (byte)(0xA0 + group);
        long value = index;
        for(int b = 1; b < stride; b++)
        {
            item[b] = (byte)((value & 0xFF) ^ (byte)(b * 13));
            value >>= 8;
        }

        return item;
    }


    /// <summary>A responder builds no decoder, so its construction rents strictly fewer buffers than an initiator's over the same contract and snapshot.</summary>
    [TestMethod]
    public void AResponderSessionRentsFewerBuffersThanAnInitiator()
    {
        //The decoder is documented as initiator-only (class remarks): an initiator builds one, a responder must
        //not, so the difference between the two rental counts is the decoder's own rentals.
        ReadOnlyMemory<byte>[] items = [A1, A2];

        long initiatorRented;
        using(RentalAccountant initiatorAccountant = new())
        {
            using BaseMemoryPool initiatorPool = new();
            using(new AntiEntropySession<string>(AntiEntropyRole.Initiator, StructuralContract, items, initiatorPool))
            {
            }

            initiatorRented = initiatorAccountant.Rented;
        }

        long responderRented;
        using(RentalAccountant responderAccountant = new())
        {
            using BaseMemoryPool responderPool = new();
            using(new AntiEntropySession<string>(AntiEntropyRole.Responder, StructuralContract, items, responderPool))
            {
            }

            responderRented = responderAccountant.Rented;
        }

        Assert.IsLessThan(initiatorRented, responderRented, "A responder must rent fewer buffers than an initiator, which alone builds the decoder.");
    }
}
