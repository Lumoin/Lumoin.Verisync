using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class DottedVersionVectorTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoDot()
    {
        Assert.IsNull(DottedVersionVector.Empty.Dot);
    }


    [TestMethod]
    public void EmptyHasEmptyContext()
    {
        Assert.AreEqual(VectorClock.Empty, DottedVersionVector.Empty.Context);
    }


    [TestMethod]
    public void AdvanceDotSetsContainedDot()
    {
        DottedVersionVector dvv = DottedVersionVector.Empty.AdvanceDot(R1);

        Assert.AreEqual(new Dot(R1, 1), dvv.Dot);
        Assert.AreEqual(1, dvv.Context[R1]);
        Assert.AreEqual(dvv.Context[dvv.Dot!.Replica], dvv.Dot!.Counter);
    }


    [TestMethod]
    public void AdvanceDotIncrementsCounter()
    {
        DottedVersionVector dvv = DottedVersionVector.Empty.AdvanceDot(R1).AdvanceDot(R1);

        Assert.AreEqual(new Dot(R1, 2), dvv.Dot);
        Assert.AreEqual(2, dvv.Context[R1]);
    }


    [TestMethod]
    public void AdvanceDotDoesNotMutateOriginal()
    {
        DottedVersionVector original = DottedVersionVector.Empty;
        _ = original.AdvanceDot(R1);

        Assert.IsNull(original.Dot);
        Assert.AreEqual(0, original.Context[R1]);
    }


    [TestMethod]
    public void MergeTakesElementWiseMaxContext()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R2);

        DottedVersionVector merged = a.Merge(b);

        Assert.AreEqual(1, merged.Context[R1]);
        Assert.AreEqual(1, merged.Context[R2]);
    }


    [TestMethod]
    public void MergeKeepsDominatingDot()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = a.AdvanceDot(R1);

        DottedVersionVector merged = a.Merge(b);

        Assert.AreEqual(new Dot(R1, 2), merged.Dot);
        Assert.AreEqual(2, merged.Context[R1]);
    }


    [TestMethod]
    public void MergeClearsDotWhenConcurrent()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R2);

        DottedVersionVector merged = a.Merge(b);

        Assert.IsNull(merged.Dot);
        Assert.AreEqual(1, merged.Context[R1]);
        Assert.AreEqual(1, merged.Context[R2]);
    }


    [TestMethod]
    public void MergeKeepsLoneDotWhenNotSuperseded()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty;

        DottedVersionVector merged = a.Merge(b);

        Assert.AreEqual(new Dot(R1, 1), merged.Dot);
    }


    [TestMethod]
    public void MergeDoesNotMutateOperands()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R2);

        _ = a.Merge(b);

        Assert.AreEqual(new Dot(R1, 1), a.Dot);
        Assert.AreEqual(0, a.Context[R2]);
        Assert.AreEqual(new Dot(R2, 1), b.Dot);
    }


    [TestMethod]
    public void CompareReturnsEqualForIdenticalContexts()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R1);

        Assert.AreEqual(Causality.Equal, a.Compare(b));
    }


    [TestMethod]
    public void CompareReturnsBeforeAndAfter()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = a.AdvanceDot(R1);

        Assert.AreEqual(Causality.Before, a.Compare(b));
        Assert.AreEqual(Causality.After, b.Compare(a));
    }


    [TestMethod]
    public void CompareReturnsConcurrent()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R2);

        Assert.AreEqual(Causality.Concurrent, a.Compare(b));
    }


    [TestMethod]
    public void EqualityHoldsForSameContextAndDot()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R1);

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void EqualityFailsForDifferentDot()
    {
        DottedVersionVector withDot = DottedVersionVector.Empty.AdvanceDot(R1).AdvanceDot(R2);
        DottedVersionVector withoutDot = DottedVersionVector.Empty.AdvanceDot(R1)
            .Merge(DottedVersionVector.Empty.AdvanceDot(R2));

        Assert.AreEqual(withDot.Context, withoutDot.Context);
        Assert.AreNotEqual(withDot, withoutDot);
    }


    [TestMethod]
    public void EqualityFailsForDifferentContext()
    {
        DottedVersionVector a = DottedVersionVector.Empty.AdvanceDot(R1);
        DottedVersionVector b = DottedVersionVector.Empty.AdvanceDot(R1).AdvanceDot(R1);

        Assert.AreNotEqual(a, b);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
