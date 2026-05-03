using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LwwRegisterPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    //Value is derived from (writer, timestamp) so an equal (timestamp, writer) pair always carries an
    //equal value; that is the well-definedness assumption the register's tie-break relies on.
    private static Gen<LwwRegister<int>> GenRegister { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 5], Gen.Bool, (writer, ticks, present) => Build(writer, ticks, present));


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
    public void MergeResultIsOneOfInputs()
    {
        Gen.Select(GenRegister, GenRegister, (a, b) => (a, b)).Sample(pair =>
        {
            LwwRegister<int> merged = pair.a.Merge(pair.b);
            Assert.IsTrue(merged.Equals(pair.a) || merged.Equals(pair.b));
        });
    }


    private static LwwRegister<int> Build(int writer, int ticks, bool present)
    {
        if(!present)
        {
            return LwwRegister<int>.Empty;
        }

        return LwwRegister<int>.Empty.Write((writer * 100) + ticks, new Timestamp(ticks), Replicas[writer]);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
