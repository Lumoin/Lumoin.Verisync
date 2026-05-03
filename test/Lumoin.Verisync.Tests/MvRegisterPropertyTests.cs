using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class MvRegisterPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    //The value written is a function of the writing replica so that a given dot maps to one value
    //globally, mirroring real usage where a replica produces a single event per counter. Without this,
    //two independently generated registers could reuse the same dot with different values, which is not
    //a state any real history can reach.
    private static Gen<MvRegister<int>> GenRegister { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Bool, (writer, present) => Build(writer, present));


    [TestMethod]
    public void MergeIsCommutative()
    {
        Gen.Select(GenRegister, GenRegister, (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(pair.a.Merge(pair.b), pair.b.Merge(pair.a));
        });
    }


    [TestMethod]
    public void MergeIsAssociative()
    {
        Gen.Select(GenRegister, GenRegister, GenRegister, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            Assert.AreEqual(triple.a.Merge(triple.b).Merge(triple.c), triple.a.Merge(triple.b.Merge(triple.c)));
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenRegister.Sample(register =>
        {
            Assert.AreEqual(register, register.Merge(register));
        });
    }


    [TestMethod]
    public void ConvergesRegardlessOfMergeOrder()
    {
        Gen.Select(GenRegister, GenRegister, GenRegister, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            MvRegister<int> order1 = triple.a.Merge(triple.b).Merge(triple.c);
            MvRegister<int> order2 = triple.c.Merge(triple.a).Merge(triple.b);

            Assert.AreEqual(order1, order2);
            Assert.HasCount(order1.Values.Count, order2.Values);
        });
    }


    private static MvRegister<int> Build(int writer, bool present)
    {
        return present
            ? MvRegister<int>.Empty.Write(writer, Replicas[writer])
            : MvRegister<int>.Empty;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
