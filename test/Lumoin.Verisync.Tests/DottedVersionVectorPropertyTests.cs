using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class DottedVersionVectorPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    private static Gen<DottedVersionVector> GenDvv { get; } =
        Gen.Select(Gen.Int[0, 4], Gen.Int[0, 4], Gen.Int[0, 4], (c0, c1, c2) => Build(c0, c1, c2));


    [TestMethod]
    public void MergeIsCommutative()
    {
        Gen.Select(GenDvv, GenDvv, (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(pair.a.Merge(pair.b), pair.b.Merge(pair.a));
        });
    }


    [TestMethod]
    public void MergeContextIsAssociative()
    {
        Gen.Select(GenDvv, GenDvv, GenDvv, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            VectorClock left = triple.a.Merge(triple.b).Merge(triple.c).Context;
            VectorClock right = triple.a.Merge(triple.b.Merge(triple.c)).Context;
            Assert.AreEqual(left, right);
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenDvv.Sample(dvv =>
        {
            Assert.AreEqual(dvv, dvv.Merge(dvv));
        });
    }


    [TestMethod]
    public void AdvanceDotMakesContextAfter()
    {
        GenDvv.Sample(dvv =>
        {
            Assert.AreEqual(Causality.After, dvv.AdvanceDot(Replicas[0]).Compare(dvv));
        });
    }


    [TestMethod]
    public void AdvanceDotProducesContainedDot()
    {
        GenDvv.Sample(dvv =>
        {
            DottedVersionVector advanced = dvv.AdvanceDot(Replicas[1]);
            Assert.IsNotNull(advanced.Dot);
            Assert.AreEqual(advanced.Context[advanced.Dot!.Replica], advanced.Dot!.Counter);
        });
    }


    private static DottedVersionVector Build(int c0, int c1, int c2)
    {
        int[] counts = [c0, c1, c2];
        DottedVersionVector dvv = DottedVersionVector.Empty;
        for(int i = 0; i < counts.Length; i++)
        {
            for(int j = 0; j < counts[i]; j++)
            {
                dvv = dvv.AdvanceDot(Replicas[i]);
            }
        }

        return dvv;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
