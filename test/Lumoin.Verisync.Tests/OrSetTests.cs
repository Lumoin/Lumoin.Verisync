using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class OrSetTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoElements()
    {
        Assert.HasCount(0, OrSet<string>.Empty.Elements);
        Assert.IsFalse(OrSet<string>.Empty.Contains("x"));
    }


    [TestMethod]
    public void AddMakesElementPresent()
    {
        OrSet<string> set = OrSet<string>.Empty.Add("x", R1);

        Assert.IsTrue(set.Contains("x"));
        Assert.HasCount(1, set.Elements);
    }


    [TestMethod]
    public void RemoveMakesElementAbsent()
    {
        OrSet<string> set = OrSet<string>.Empty.Add("x", R1).Remove("x");

        Assert.IsFalse(set.Contains("x"));
        Assert.HasCount(0, set.Elements);
    }


    [TestMethod]
    public void RemoveAbsentElementIsNoOp()
    {
        OrSet<string> set = OrSet<string>.Empty.Remove("x");

        Assert.IsFalse(set.Contains("x"));
        Assert.HasCount(0, set.Elements);
    }


    [TestMethod]
    public void AddDoesNotMutateOriginal()
    {
        OrSet<string> original = OrSet<string>.Empty;
        _ = original.Add("x", R1);

        Assert.IsFalse(original.Contains("x"));
    }


    [TestMethod]
    public void RemoveDoesNotMutateOriginal()
    {
        OrSet<string> original = OrSet<string>.Empty.Add("x", R1);
        _ = original.Remove("x");

        Assert.IsTrue(original.Contains("x"));
    }


    [TestMethod]
    public void ConcurrentAddWinsOverRemove()
    {
        OrSet<string> added = OrSet<string>.Empty.Add("x", R1);
        OrSet<string> removed = added.Remove("x");
        OrSet<string> concurrentAdd = OrSet<string>.Empty.Add("x", R2);

        OrSet<string> merged = removed.Merge(concurrentAdd);

        Assert.IsTrue(merged.Contains("x"));
    }


    [TestMethod]
    public void ObservedRemoveStaysRemovedAfterMerge()
    {
        OrSet<string> shared = OrSet<string>.Empty.Add("x", R1);
        OrSet<string> removed = shared.Remove("x");

        OrSet<string> merged = removed.Merge(shared);

        Assert.IsFalse(merged.Contains("x"));
    }


    [TestMethod]
    public void RemoveAffectsAllObservedAddsOfElement()
    {
        OrSet<string> a = OrSet<string>.Empty.Add("x", R1);
        OrSet<string> b = OrSet<string>.Empty.Add("x", R2);
        OrSet<string> merged = a.Merge(b);

        Assert.IsTrue(merged.Contains("x"));

        OrSet<string> afterRemove = merged.Remove("x");

        Assert.IsFalse(afterRemove.Contains("x"));
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        OrSet<string> a = OrSet<string>.Empty.Add("x", R1);
        OrSet<string> b = OrSet<string>.Empty.Add("y", R2);
        OrSet<string> merged = a.Merge(b);

        Assert.AreEqual(merged, merged.Merge(merged));
    }


    [TestMethod]
    public void EqualityHoldsForSameOperations()
    {
        OrSet<string> a = OrSet<string>.Empty.Add("x", R1);
        OrSet<string> b = OrSet<string>.Empty.Add("x", R1);

        Assert.AreEqual(a, b);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
