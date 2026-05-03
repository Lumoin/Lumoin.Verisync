using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class OrSetPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    //Each operation's element equals its replica index, so a given dot maps to one element globally,
    //matching real histories where a replica produces a single event per counter.
    private static Gen<OrSet<int>> GenSet { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Bool, (replica, isAdd) => (replica, isAdd))
            .Array[0, 5]
            .Select(operations => Build(operations));


    [TestMethod]
    public void MergeIsCommutative()
    {
        Gen.Select(GenSet, GenSet, (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(pair.a.Merge(pair.b), pair.b.Merge(pair.a));
        });
    }


    [TestMethod]
    public void MergeIsAssociative()
    {
        Gen.Select(GenSet, GenSet, GenSet, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            Assert.AreEqual(triple.a.Merge(triple.b).Merge(triple.c), triple.a.Merge(triple.b.Merge(triple.c)));
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenSet.Sample(set =>
        {
            Assert.AreEqual(set, set.Merge(set));
        });
    }


    [TestMethod]
    public void ConvergesRegardlessOfMergeOrder()
    {
        Gen.Select(GenSet, GenSet, GenSet, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            OrSet<int> order1 = triple.a.Merge(triple.b).Merge(triple.c);
            OrSet<int> order2 = triple.c.Merge(triple.a).Merge(triple.b);

            Assert.AreEqual(order1, order2);
            Assert.HasCount(order1.Elements.Count, order2.Elements);
        });
    }


    private static OrSet<int> Build((int Replica, bool IsAdd)[] operations)
    {
        OrSet<int> set = OrSet<int>.Empty;
        foreach((int replica, bool isAdd) in operations)
        {
            set = isAdd ? set.Add(replica, Replicas[replica]) : set.Remove(replica);
        }

        return set;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
