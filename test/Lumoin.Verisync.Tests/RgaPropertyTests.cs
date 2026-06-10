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


    [TestMethod]
    public void InsertAfterIsIntentionPreservingOverMergedState()
    {
        //A replica that merges arbitrary remote state and inserts after any visible element must see
        //the new element immediately after that element, regardless of what siblings already hang there.
        Gen.Select(GenRgaUsing(R0), GenRgaUsing(R1), Gen.Int[0, 100], (a, b, pick) => (a, b, pick)).Sample(input =>
        {
            Rga<int> merged = input.a.Merge(input.b);
            if(merged.Count == 0)
            {
                return;
            }

            IReadOnlyList<int> before = merged.Values;
            int targetIndex = input.pick % before.Count;
            Dot target = FindIdAt(merged, targetIndex);
            (Rga<int> inserted, _) = merged.InsertAfter(target, -1, R2);

            Assert.AreEqual(-1, inserted.Values[targetIndex + 1]);
        });
    }


    private static Dot FindIdAt(Rga<int> rga, int index)
    {
        //Values are globally unique by construction (per-replica ranges in BuildChain), so the visible
        //element's identity is recoverable from the serialized vertices by value.
        int value = rga.Values[index];
        foreach(RgaVertexEntry<int> entry in rga.ToState().Vertices)
        {
            if(entry.Value == value)
            {
                return new Dot(ReplicaId.FromSpan(entry.Id.Replica.AsSpan()), entry.Id.Counter);
            }
        }

        throw new InvalidOperationException("The visible element was not found.");
    }


    private static Gen<Rga<int>> GenRgaUsing(ReplicaId replica)
    {
        return Gen.Int[0, 100].Array[0, 5].Select(seeds => BuildChain(replica, seeds));
    }


    private static Rga<int> BuildChain(ReplicaId replica, int[] seeds)
    {
        Rga<int> rga = Rga<int>.Empty;
        var ids = new List<Dot>();

        //Per-replica value ranges keep values globally unique across merged operands.
        int value = replica.AsSpan()[0] * 1000;

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
