using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class OrSetPropertyTests
{
    private static ReplicaId[] Replicas { get; } = [Replica(0), Replica(1), Replica(2)];

    //Each added element is a function of (replica, per-replica add ordinal). In a linear history the
    //ordinal equals the replica's dot counter, so a given dot maps to one element globally even across
    //independently generated sets — the invariant every real history maintains. Removes target this or
    //a neighbouring replica's latest element, so cross-replica observed-removes are exercised too.
    private static Gen<OrSet<int>> GenSet { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 7], (replica, action) => (replica, action))
            .Array[0, 8]
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


    [TestMethod]
    public void ConcurrentReAddWinsOverRemoveFromACommonAncestor()
    {
        //The add-wins core as a property: whatever the shared ancestor, a remove racing a re-add of
        //the same element loses, because the remove never observed the re-add's fresh dot.
        Gen.Select(Gen.Int[1, 4], Gen.Int[1, 4], (adds, pick) => (adds, pick)).Sample(input =>
        {
            OrSet<int> ancestor = AddChain(input.adds);
            int element = Element(0, ((input.pick - 1) % input.adds) + 1);

            OrSet<int> removed = ancestor.Remove(element);
            OrSet<int> readded = ancestor.Add(element, Replicas[1]);

            OrSet<int> merged = removed.Merge(readded);
            Assert.Contains(element, merged.Elements);
            Assert.AreEqual(merged, readded.Merge(removed));
        });
    }


    [TestMethod]
    public void ObservedRemoveDoesNotResurrectOnAncestorReMerge()
    {
        //Re-delivering the ancestor state whose dots the remove already observed must not bring the
        //element back — the retained causal context is what distinguishes "removed" from "never seen".
        Gen.Select(Gen.Int[1, 4], Gen.Int[1, 4], (adds, pick) => (adds, pick)).Sample(input =>
        {
            OrSet<int> ancestor = AddChain(input.adds);
            int element = Element(0, ((input.pick - 1) % input.adds) + 1);

            OrSet<int> removed = ancestor.Remove(element);

            OrSet<int> merged = removed.Merge(ancestor);
            Assert.DoesNotContain(element, merged.Elements);
            Assert.AreEqual(merged, ancestor.Merge(removed));
        });
    }


    private static OrSet<int> Build((int Replica, int Action)[] operations)
    {
        OrSet<int> set = OrSet<int>.Empty;
        var ordinals = new int[Replicas.Length];
        foreach((int replica, int action) in operations)
        {
            if(action < 5)
            {
                ordinals[replica]++;
                set = set.Add(Element(replica, ordinals[replica]), Replicas[replica]);
            }
            else
            {
                //Remove the latest element added by this or a neighbouring replica; removing an
                //element that was never added or is already gone is a valid no-op history.
                int target = (replica + action - 5) % Replicas.Length;
                if(ordinals[target] > 0)
                {
                    set = set.Remove(Element(target, ordinals[target]));
                }
            }
        }

        return set;
    }


    private static OrSet<int> AddChain(int count)
    {
        OrSet<int> set = OrSet<int>.Empty;
        for(int i = 1; i <= count; i++)
        {
            set = set.Add(Element(0, i), Replicas[0]);
        }

        return set;
    }


    private static int Element(int replica, int ordinal) => (replica * 1000) + ordinal;


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
