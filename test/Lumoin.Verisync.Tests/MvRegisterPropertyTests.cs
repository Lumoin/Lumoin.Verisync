using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class MvRegisterPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    //The value written is a function of (writer, per-writer write ordinal). In a linear history the
    //ordinal equals the writer's dot counter, so a given dot maps to one value globally even across
    //independently generated registers — two samples can both contain dot (r, k), but always carrying
    //the same value, which is the invariant every real history maintains.
    private static Gen<MvRegister<int>> GenRegister { get; } =
        Gen.Int[0, 2].Array[0, 6].Select(writers => Build(writers));

    //Two replicas diverge from a generated shared ancestor and write independently — the core MV
    //register scenario the single-register generator cannot produce.
    private static Gen<(MvRegister<int> B, MvRegister<int> C, MvRegister<int> Ancestor, int LastB, int LastC)> GenDivergent { get; } =
        Gen.Select(Gen.Int[0, 4], Gen.Int[1, 3], Gen.Int[1, 3], (ancestorWrites, bWrites, cWrites) =>
        {
            MvRegister<int> ancestor = MvRegister<int>.Empty;
            for(int i = 1; i <= ancestorWrites; i++)
            {
                ancestor = ancestor.Write(Value(0, i), Replicas[0]);
            }

            MvRegister<int> b = ancestor;
            for(int i = 1; i <= bWrites; i++)
            {
                b = b.Write(Value(1, i), Replicas[1]);
            }

            MvRegister<int> c = ancestor;
            for(int i = 1; i <= cWrites; i++)
            {
                c = c.Write(Value(2, i), Replicas[2]);
            }

            return (b, c, ancestor, Value(1, bWrites), Value(2, cWrites));
        });


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


    [TestMethod]
    public void DivergentWritesFromACommonAncestorAreBothRetained()
    {
        GenDivergent.Sample(history =>
        {
            MvRegister<int> merged = history.B.Merge(history.C);

            //The two divergent writers are concurrent, so both final values survive; the shared
            //ancestor's value was observed by both and is superseded.
            Assert.HasCount(2, merged.Values);
            Assert.Contains(history.LastB, merged.Values);
            Assert.Contains(history.LastC, merged.Values);
            Assert.AreEqual(merged, history.C.Merge(history.B));

            //Re-delivering the already-incorporated ancestor must not resurrect its value.
            Assert.AreEqual(merged, merged.Merge(history.Ancestor));
        });
    }


    private static MvRegister<int> Build(int[] writers)
    {
        MvRegister<int> register = MvRegister<int>.Empty;
        var ordinals = new int[Replicas.Length];
        foreach(int writer in writers)
        {
            ordinals[writer]++;
            register = register.Write(Value(writer, ordinals[writer]), Replicas[writer]);
        }

        return register;
    }


    private static int Value(int writer, int ordinal) => (writer * 1000) + ordinal;


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
