using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class PNCounterPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    private static Gen<(int, int, int)> GenTriple { get; } =
        Gen.Select(Gen.Int[0, 4], Gen.Int[0, 4], Gen.Int[0, 4], (a, b, c) => (a, b, c));

    private static Gen<PNCounter> GenCounter { get; } =
        Gen.Select(GenTriple, GenTriple, (increments, decrements) => Build(increments, decrements));


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
            PNCounter left = triple.a.Merge(triple.b).Merge(triple.c);
            PNCounter right = triple.a.Merge(triple.b.Merge(triple.c));
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
            PNCounter order1 = triple.a.Merge(triple.b).Merge(triple.c);
            PNCounter order2 = triple.c.Merge(triple.a).Merge(triple.b);

            Assert.AreEqual(order1, order2);
            Assert.AreEqual(order1.Value, order2.Value);
        });
    }


    private static PNCounter Build((int, int, int) increments, (int, int, int) decrements)
    {
        int[] incs = [increments.Item1, increments.Item2, increments.Item3];
        int[] decs = [decrements.Item1, decrements.Item2, decrements.Item3];

        PNCounter counter = PNCounter.Empty;
        for(int i = 0; i < incs.Length; i++)
        {
            if(incs[i] > 0)
            {
                counter = counter.Increment(Replicas[i], incs[i]);
            }
        }

        for(int i = 0; i < decs.Length; i++)
        {
            if(decs[i] > 0)
            {
                counter = counter.Decrement(Replicas[i], decs[i]);
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
