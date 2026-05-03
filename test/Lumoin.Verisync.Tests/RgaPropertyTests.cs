using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class RgaPropertyTests
{
    private static ReplicaId R0 { get; } = Replica(0);
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void MergeIsCommutative()
    {
        //Each operand is built by a distinct replica, so their dot spaces are disjoint, mirroring a real
        //distributed run where two replicas never mint the same identity.
        Gen.Select(GenRgaUsing(R0), GenRgaUsing(R1), (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(pair.a.Merge(pair.b), pair.b.Merge(pair.a));
        });
    }


    [TestMethod]
    public void MergeIsAssociative()
    {
        Gen.Select(GenRgaUsing(R0), GenRgaUsing(R1), GenRgaUsing(R2), (a, b, c) => (a, b, c)).Sample(triple =>
        {
            Assert.AreEqual(triple.a.Merge(triple.b).Merge(triple.c), triple.a.Merge(triple.b.Merge(triple.c)));
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenRgaUsing(R0).Sample(rga =>
        {
            Assert.AreEqual(rga, rga.Merge(rga));
        });
    }


    [TestMethod]
    public void ConvergesRegardlessOfMergeOrder()
    {
        Gen.Select(GenRgaUsing(R0), GenRgaUsing(R1), GenRgaUsing(R2), (a, b, c) => (a, b, c)).Sample(triple =>
        {
            Rga<int> order1 = triple.a.Merge(triple.b).Merge(triple.c);
            Rga<int> order2 = triple.c.Merge(triple.a).Merge(triple.b);

            Assert.AreEqual(order1, order2);
            CollectionAssert.AreEqual(order1.Values.ToArray(), order2.Values.ToArray());
        });
    }


    private static Gen<Rga<int>> GenRgaUsing(ReplicaId replica)
    {
        return Gen.Int[0, 100].Array[0, 5].Select(seeds => BuildChain(replica, seeds));
    }


    private static Rga<int> BuildChain(ReplicaId replica, int[] seeds)
    {
        Rga<int> rga = Rga<int>.Empty;
        var ids = new List<Dot>();
        int value = 0;

        foreach(int seed in seeds)
        {
            Dot inserted;
            if(ids.Count == 0)
            {
                (rga, inserted) = rga.InsertAtHead(value, replica);
            }
            else
            {
                Dot after = ids[seed % ids.Count];
                (rga, inserted) = rga.InsertAfter(after, value, replica);
            }

            ids.Add(inserted);
            value++;
        }

        return rga;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
