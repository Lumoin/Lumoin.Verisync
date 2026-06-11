using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class GCounterPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    private static Gen<GCounter> GenCounter { get; } =
        Gen.Select(Gen.Int[0, 5], Gen.Int[0, 5], Gen.Int[0, 5], (a0, a1, a2) => Build(a0, a1, a2));


    [TestMethod]
    public void MergeIsCommutative()
    {
        Gen.Select(GenCounter, GenCounter, (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(pair.a.Merge(pair.b), pair.b.Merge(pair.a));
        });
    }


    [TestMethod]
    public void MergeIsAssociative()
    {
        Gen.Select(GenCounter, GenCounter, GenCounter, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            GCounter left = triple.a.Merge(triple.b).Merge(triple.c);
            GCounter right = triple.a.Merge(triple.b.Merge(triple.c));
            Assert.AreEqual(left, right);
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenCounter.Sample(counter =>
        {
            Assert.AreEqual(counter, counter.Merge(counter));
        });
    }


    [TestMethod]
    public void ConvergesRegardlessOfMergeOrder()
    {
        Gen.Select(GenCounter, GenCounter, GenCounter, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            GCounter order1 = triple.a.Merge(triple.b).Merge(triple.c);
            GCounter order2 = triple.c.Merge(triple.a).Merge(triple.b);

            Assert.AreEqual(order1, order2);
            Assert.AreEqual(order1.Value, order2.Value);
        });
    }


    private static GCounter Build(int a0, int a1, int a2)
    {
        int[] amounts = [a0, a1, a2];
        GCounter counter = GCounter.Empty;
        for(int i = 0; i < amounts.Length; i++)
        {
            if(amounts[i] > 0)
            {
                counter = counter.Increment(Replicas[i], amounts[i]);
            }
        }

        return counter;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
