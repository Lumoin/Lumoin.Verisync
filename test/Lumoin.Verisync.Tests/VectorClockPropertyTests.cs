using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class VectorClockPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    private static Gen<VectorClock> GenClock { get; } =
        Gen.Select(Gen.Int[0, 4], Gen.Int[0, 4], Gen.Int[0, 4], (c0, c1, c2) => Build(c0, c1, c2));


    [TestMethod]
    public void MergeIsCommutative()
    {
        Gen.Select(GenClock, GenClock, (a, b) => (a, b)).Sample(pair =>
        {
            Assert.AreEqual(pair.a.Merge(pair.b), pair.b.Merge(pair.a));
        });
    }


    [TestMethod]
    public void MergeIsAssociative()
    {
        Gen.Select(GenClock, GenClock, GenClock, (a, b, c) => (a, b, c)).Sample(triple =>
        {
            VectorClock left = triple.a.Merge(triple.b).Merge(triple.c);
            VectorClock right = triple.a.Merge(triple.b.Merge(triple.c));
            Assert.AreEqual(left, right);
        });
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        GenClock.Sample(clock =>
        {
            Assert.AreEqual(clock, clock.Merge(clock));
        });
    }


    [TestMethod]
    public void IncrementMakesClockGreater()
    {
        GenClock.Sample(clock =>
        {
            Assert.AreEqual(Causality.After, clock.Increment(Replicas[0]).Compare(clock));
        });
    }


    [TestMethod]
    public void MergeUpperBound()
    {
        Gen.Select(GenClock, GenClock, (a, b) => (a, b)).Sample(pair =>
        {
            VectorClock merged = pair.a.Merge(pair.b);
            Assert.IsTrue(merged.Compare(pair.a) is Causality.After or Causality.Equal);
            Assert.IsTrue(merged.Compare(pair.b) is Causality.After or Causality.Equal);
        });
    }


    private static VectorClock Build(int c0, int c1, int c2)
    {
        int[] counts = [c0, c1, c2];
        VectorClock clock = VectorClock.Empty;
        for(int i = 0; i < counts.Length; i++)
        {
            for(int j = 0; j < counts[i]; j++)
            {
                clock = clock.Increment(Replicas[i]);
            }
        }

        return clock;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
