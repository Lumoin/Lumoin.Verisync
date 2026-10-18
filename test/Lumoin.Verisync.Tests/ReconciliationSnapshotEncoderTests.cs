using System.Buffers.Binary;
using CsCheck;
using Lumoin.Base;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>Checks snapshot stream conformance, pass faults, visitor reuse, and rental ownership.</summary>
[TestClass]
[DoNotParallelize]
internal sealed class ReconciliationSnapshotEncoderTests
{
    /// <summary>The default-width structural contract with the well-known checksum key.</summary>
    private static ReconciliationContract Contract { get; } =
        new(ReconciliationItemDomain.Structural, 16, 8, ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);

    /// <summary>The context receiving property-test diagnostics.</summary>
    public TestContext TestContext { get; set; } = null!;


    /// <summary>Every partition of the produced prefix matches the incremental stream for generated sets.</summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(17)]
    [DataRow(500)]
    public void GeneratedSetsMatchEveryRangePartition(int itemCount)
    {
        Gen.Byte.Array[12].Array[itemCount].Sample(tails =>
        {
            byte[][] items = new byte[itemCount][];
            for(int i = 0; i < itemCount; i++)
            {
                items[i] = new byte[16];
                BinaryPrimitives.WriteInt32LittleEndian(items[i], i);
                tails[i].CopyTo(items[i], 4);
            }

            AssertPartitions(Contract, items);
        }, writeLine: message => TestContext.WriteLine(message), iter: 3, threads: 1);
    }


    /// <summary>The pinned eight-byte stream vector's set matches every range partition through 300 symbols.</summary>
    [TestMethod]
    public void PinnedVectorSetMatchesEveryRangePartition()
    {
        ReconciliationContract contract = new(ReconciliationItemDomain.Structural, 8, 8,
            ReconciliationContract.WellKnownChecksumKeyLow, ReconciliationContract.WellKnownChecksumKeyHigh);
        byte[][] items =
        [
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08],
            [0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18],
            [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28],
        ];
        AssertPartitions(contract, items);
    }


    /// <summary>A wrong-width item faults production and every later symbol read while allowing disposal.</summary>
    [TestMethod]
    public void WrongWidthFaultsTheEncoder()
    {
        using ReconciliationSnapshotEncoder encoder = new(Contract, Enumeration([new byte[15]]), BaseMemoryPool.Shared, 0);

        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => encoder.Produce(1, CancellationToken.None));

        Assert.AreEqual("An enumerated item must be exactly 16 bytes.", exception.Message);
        AssertFaulted(encoder);
        encoder.Dispose();
    }


    /// <summary>A pre-cancelled pass faults before the source is called.</summary>
    [TestMethod]
    public void PreCancelledPassFaultsTheEncoder()
    {
        bool called = false;
        using ReconciliationSnapshotEncoder encoder = new(Contract, (_, _) => called = true, BaseMemoryPool.Shared, 0);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => encoder.Produce(1, cancellation.Token));

        Assert.IsFalse(called);
        AssertFaulted(encoder);
    }


    /// <summary>Cancellation observed by the source after an item faults a partially folded range.</summary>
    [TestMethod]
    public void CancellationDuringEnumerationFaultsTheEncoder()
    {
        using CancellationTokenSource cancellation = new();
        using ReconciliationSnapshotEncoder encoder = new(Contract, (visit, token) =>
        {
            token.ThrowIfCancellationRequested();
            visit(new byte[16]);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }, BaseMemoryPool.Shared, 0);

        Assert.ThrowsExactly<OperationCanceledException>(() => encoder.Produce(7, cancellation.Token));

        AssertFaulted(encoder);
    }


    /// <summary>A nested Produce invoked by the enumeration's visitor wrapper propagates and faults the outer pass.</summary>
    [TestMethod]
    public void ReentrantProductionFaultsTheEncoder()
    {
        ReconciliationSnapshotEncoder? active = null;
        using ReconciliationSnapshotEncoder encoder = new(Contract, (visit, token) =>
        {
            VisitReconciliationItemDelegate nestedVisit = item =>
            {
                visit(item);
                active!.Produce(1, token);
            };
            token.ThrowIfCancellationRequested();
            nestedVisit(new byte[16]);
        }, BaseMemoryPool.Shared, 0);
        active = encoder;

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Produce(1, CancellationToken.None));

        Assert.AreEqual("A snapshot encoder cannot produce while a pass is running.", exception.Message);
        Assert.AreEqual(1, encoder.ProducedCount);
        AssertFaulted(encoder);
    }


    /// <summary>A single nested production is refused and permanently faults the outer pass after its first symbol.</summary>
    [TestMethod]
    public void ASingleNestedProductionIsRefused()
    {
        ReconciliationSnapshotEncoder? active = null;
        bool nested = false;
        using ReconciliationSnapshotEncoder encoder = new(Contract, (visit, token) =>
        {
            token.ThrowIfCancellationRequested();
            visit(new byte[16]);
            if(!nested)
            {
                nested = true;
                active!.Produce(1, token);
            }
        }, BaseMemoryPool.Shared, 0);
        active = encoder;

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Produce(1, CancellationToken.None));

        Assert.AreEqual("A snapshot encoder cannot produce while a pass is running.", exception.Message);
        Assert.AreEqual(1, encoder.ProducedCount);
        AssertFaulted(encoder);
    }


    /// <summary>A visitor call outside a pass leaves every produced symbol intact and permits further production.</summary>
    [TestMethod]
    public void AVisitorCallOutsideAPassThrowsAndLeavesTheEncoderIntact()
    {
        byte[] item = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        EnumerateReconciliationItemsDelegate enumerate = Enumeration([item]);
        VisitReconciliationItemDelegate? storedVisit = null;
        using ReconciliationSnapshotEncoder encoder = new(Contract, (visit, token) =>
        {
            storedVisit = visit;
            enumerate(visit, token);
        }, BaseMemoryPool.Shared, 0);
        encoder.Produce(3, CancellationToken.None);
        byte[][] sums = new byte[3][];
        byte[][] checksums = new byte[3][];
        for(int i = 0; i < 3; i++)
        {
            ReconciliationSymbol symbol = encoder.SymbolAt(i);
            sums[i] = symbol.Sum.ToArray();
            checksums[i] = symbol.Checksum.ToArray();
        }

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => storedVisit!(item));

        Assert.AreEqual("A snapshot encoder folds items only while its own pass is running.", exception.Message);
        for(int i = 0; i < 3; i++)
        {
            ReconciliationSymbol symbol = encoder.SymbolAt(i);
            Assert.AreSequenceEqual(sums[i], symbol.Sum.ToArray());
            Assert.AreSequenceEqual(checksums[i], symbol.Checksum.ToArray());
        }

        Assert.AreEqual(3, encoder.ProducedCount);
        encoder.Produce(1, CancellationToken.None);
        Assert.AreEqual(4, encoder.ProducedCount);
    }


    /// <summary>A symbol read during a pass propagates and permanently faults production and symbol reads.</summary>
    [TestMethod]
    public void ASymbolReadDuringAPassFaultsTheEncoder()
    {
        ReconciliationSnapshotEncoder? reading = null;
        EnumerateReconciliationItemsDelegate enumerate = Enumeration([new byte[16]]);
        using ReconciliationSnapshotEncoder encoder = new(Contract, (visit, token) =>
        {
            enumerate(item =>
            {
                visit(item);
                reading!.SymbolAt(0);
            }, token);
        }, BaseMemoryPool.Shared, 0);
        reading = encoder;

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Produce(2, CancellationToken.None));

        Assert.AreEqual("A snapshot encoder cannot read its symbols while a pass is running.", exception.Message);
        Assert.AreEqual(2, encoder.ProducedCount);
        AssertFaulted(encoder);
    }


    /// <summary>A source exception propagates unchanged and prevents reading even an earlier completed range.</summary>
    [TestMethod]
    public void SourceFailureInvalidatesAllSymbols()
    {
        int passes = 0;
        var failure = new FormatException("The snapshot source failed.");
        using ReconciliationSnapshotEncoder encoder = new(Contract, (_, _) =>
        {
            if(++passes == 2)
            {
                throw failure;
            }
        }, BaseMemoryPool.Shared, 0);
        encoder.Produce(1, CancellationToken.None);

        Assert.AreSame(failure, Assert.ThrowsExactly<FormatException>(() => encoder.Produce(1, CancellationToken.None)));
        AssertFaulted(encoder);
    }


    /// <summary>Invalid construction arguments identify their parameter.</summary>
    [TestMethod]
    public void ConstructorRejectsInvalidArguments()
    {
        EnumerateReconciliationItemsDelegate enumerate = Enumeration([]);
        Assert.AreEqual("contract", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReconciliationSnapshotEncoder(null!, enumerate, BaseMemoryPool.Shared, 0)).ParamName);
        Assert.AreEqual("enumerate", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReconciliationSnapshotEncoder(Contract, null!, BaseMemoryPool.Shared, 0)).ParamName);
        Assert.AreEqual("pool", Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReconciliationSnapshotEncoder(Contract, enumerate, null!, 0)).ParamName);
        Assert.AreEqual("cellCapacityHint", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ReconciliationSnapshotEncoder(Contract, enumerate, BaseMemoryPool.Shared, -1)).ParamName);
    }


    /// <summary>A null pool is reported before a negative cell capacity hint.</summary>
    [TestMethod]
    public void ANullPoolIsReportedBeforeANegativeHint()
    {
        EnumerateReconciliationItemsDelegate enumerate = Enumeration([]);

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() =>
            new ReconciliationSnapshotEncoder(Contract, enumerate, null!, -1));

        Assert.AreEqual("pool", exception.ParamName);
    }


    /// <summary>Invalid counts and indices leave the encoder usable and preserve parameter names and index diagnostics.</summary>
    [TestMethod]
    public void InvalidCountsAndIndicesDoNotFaultTheEncoder()
    {
        using ReconciliationSnapshotEncoder encoder = new(Contract, Enumeration([]), BaseMemoryPool.Shared, 0);
        Assert.AreSame(Contract, encoder.Contract);
        Assert.AreEqual(0, encoder.ProducedCount);
        Assert.AreEqual("count", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => encoder.Produce(0, CancellationToken.None)).ParamName);
        Assert.AreEqual("count", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => encoder.Produce(-1, CancellationToken.None)).ParamName);
        encoder.Produce(1, CancellationToken.None);
        Assert.ThrowsExactly<OverflowException>(() => encoder.Produce(int.MaxValue, CancellationToken.None));
        foreach(int index in new[] { -1, 1 })
        {
            ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => encoder.SymbolAt(index));
            Assert.AreEqual("index", exception.ParamName);
            Assert.StartsWith("The symbol index must be within the produced range.", exception.Message);
        }

        encoder.Produce(2, CancellationToken.None);
        Assert.AreEqual(3, encoder.ProducedCount);
        Assert.IsTrue(encoder.SymbolAt(0).IsNeutral);
    }


    /// <summary>Construction does not enumerate, and every pass receives the same visitor instance and caller token.</summary>
    [TestMethod]
    public void PassesReuseTheVisitorAndAccumulateCounts()
    {
        List<VisitReconciliationItemDelegate> visitors = [];
        using CancellationTokenSource cancellation = new();
        using ReconciliationSnapshotEncoder encoder = new(Contract, (visit, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            visitors.Add(visit);
        }, BaseMemoryPool.Shared, 0);
        Assert.IsEmpty(visitors);

        encoder.Produce(1, cancellation.Token);
        encoder.Produce(2, cancellation.Token);
        encoder.Produce(7, cancellation.Token);

        Assert.HasCount(3, visitors);
        Assert.AreSame(visitors[0], visitors[1]);
        Assert.AreSame(visitors[0], visitors[2]);
        Assert.AreEqual(10, encoder.ProducedCount);
    }


    /// <summary>Repeated items cancel silently in both fields.</summary>
    [TestMethod]
    public void DuplicateItemsCancelSilently()
    {
        byte[] item = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        using ReconciliationSnapshotEncoder encoder = new(Contract, Enumeration([item, item]), BaseMemoryPool.Shared, 0);
        encoder.Produce(300, CancellationToken.None);
        for(int i = 0; i < encoder.ProducedCount; i++)
        {
            Assert.IsTrue(encoder.SymbolAt(i).IsNeutral);
        }
    }


    /// <summary>Disposal returns every rental after growth or a failed pass and is idempotent.</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DisposalReturnsEveryRental(bool fail)
    {
        using RentalAccountant accountant = new();
        using BaseMemoryPool pool = new();
        using ReconciliationSnapshotEncoder encoder = new(Contract, Enumeration([new byte[fail ? 15 : 16]]), pool, 0);
        if(fail)
        {
            Assert.ThrowsExactly<ArgumentException>(() => encoder.Produce(300, CancellationToken.None));
        }
        else
        {
            encoder.Produce(3, CancellationToken.None);
            encoder.Produce(297, CancellationToken.None);
        }

        Assert.AreEqual(300, encoder.ProducedCount);
        Assert.IsGreaterThan(2L, accountant.Rented);
        Assert.AreEqual(2L, accountant.NetActive);

        encoder.Dispose();
        encoder.Dispose();

        Assert.AreEqual(0L, accountant.NetActive);
        Assert.AreEqual(accountant.Rented, accountant.Returned);
        Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.Produce(1, CancellationToken.None));
        Assert.ThrowsExactly<ObjectDisposedException>(() => encoder.SymbolAt(0));
        GC.KeepAlive(encoder);
    }


    /// <summary>Compares both symbol fields across every requested partition and checks one enumeration per range.</summary>
    private static void AssertPartitions(ReconciliationContract contract, byte[][] items)
    {
        const int SymbolCount = 300;
        using ReconciliationEncoder expected = new(contract, ReconciliationInjectivityEnforcement.Strict,
            BaseMemoryPool.Shared, ReconciliationCapacityHint.None);
        foreach(byte[] item in items)
        {
            expected.Add(item);
        }

        for(int i = 0; i < SymbolCount; i++)
        {
            _ = expected.ProduceNext();
        }

        foreach(int rangeSize in new[] { 1, 2, 3, 7, 64, 300 })
        {
            int passes = 0;
            EnumerateReconciliationItemsDelegate enumerate = Enumeration(items);
            using ReconciliationSnapshotEncoder actual = new(contract, (visit, token) =>
            {
                passes++;
                enumerate(visit, token);
            }, BaseMemoryPool.Shared, 0);
            while(actual.ProducedCount < SymbolCount)
            {
                actual.Produce(Math.Min(rangeSize, SymbolCount - actual.ProducedCount), CancellationToken.None);
            }

            Assert.AreEqual(SymbolCount, actual.ProducedCount);
            Assert.AreEqual((SymbolCount + rangeSize - 1) / rangeSize, passes);
            for(int i = 0; i < SymbolCount; i++)
            {
                ReconciliationSymbol expectedSymbol = expected.SymbolAt(i);
                ReconciliationSymbol actualSymbol = actual.SymbolAt(i);
                Assert.AreSequenceEqual(expectedSymbol.Sum.ToArray(), actualSymbol.Sum.ToArray());
                Assert.AreSequenceEqual(expectedSymbol.Checksum.ToArray(), actualSymbol.Checksum.ToArray());
            }
        }
    }


    /// <summary>Enumerates the same borrowed item arrays in the same order on each call, observing cancellation per item.</summary>
    private static EnumerateReconciliationItemsDelegate Enumeration(byte[][] items)
    {
        return (visit, token) =>
        {
            foreach(byte[] item in items)
            {
                token.ThrowIfCancellationRequested();
                visit(item);
            }
        };
    }


    /// <summary>Asserts the permanent fault diagnostics for production and symbol reads.</summary>
    private static void AssertFaulted(ReconciliationSnapshotEncoder encoder)
    {
        Assert.AreEqual("A snapshot encoder whose pass faulted cannot produce.",
            Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Produce(1, CancellationToken.None)).Message);
        Assert.AreEqual("A snapshot encoder whose pass faulted cannot read its symbols.",
            Assert.ThrowsExactly<InvalidOperationException>(() => encoder.SymbolAt(0)).Message);
    }
}
