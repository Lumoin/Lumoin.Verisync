using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class MvRegisterTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoValues()
    {
        Assert.HasCount(0, MvRegister<string>.Empty.Values);
    }


    [TestMethod]
    public void WriteSetsSingleValue()
    {
        MvRegister<string> register = MvRegister<string>.Empty.Write("a", R1);

        Assert.HasCount(1, register.Values);
        Assert.Contains("a", register.Values);
    }


    [TestMethod]
    public void WriteReplacesObservedValue()
    {
        MvRegister<string> register = MvRegister<string>.Empty.Write("a", R1).Write("b", R1);

        Assert.HasCount(1, register.Values);
        Assert.Contains("b", register.Values);
    }


    [TestMethod]
    public void ConcurrentWritesAreRetainedAfterMerge()
    {
        MvRegister<string> a = MvRegister<string>.Empty.Write("a", R1);
        MvRegister<string> b = MvRegister<string>.Empty.Write("b", R2);

        MvRegister<string> merged = a.Merge(b);

        Assert.HasCount(2, merged.Values);
        Assert.Contains("a", merged.Values);
        Assert.Contains("b", merged.Values);
    }


    [TestMethod]
    public void WriteAfterMergeSupersedesConcurrentValues()
    {
        MvRegister<string> a = MvRegister<string>.Empty.Write("a", R1);
        MvRegister<string> b = MvRegister<string>.Empty.Write("b", R2);
        MvRegister<string> merged = a.Merge(b);

        MvRegister<string> resolved = merged.Write("c", R1);

        Assert.HasCount(1, resolved.Values);
        Assert.Contains("c", resolved.Values);
    }


    [TestMethod]
    public void WriteDoesNotMutateOriginal()
    {
        MvRegister<string> original = MvRegister<string>.Empty.Write("a", R1);
        _ = original.Write("b", R1);

        Assert.HasCount(1, original.Values);
        Assert.Contains("a", original.Values);
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        MvRegister<string> a = MvRegister<string>.Empty.Write("a", R1);
        MvRegister<string> b = MvRegister<string>.Empty.Write("b", R2);
        MvRegister<string> merged = a.Merge(b);

        Assert.AreEqual(merged, merged.Merge(merged));
    }


    [TestMethod]
    public void EqualityHoldsForSameWrites()
    {
        MvRegister<string> a = MvRegister<string>.Empty.Write("a", R1);
        MvRegister<string> b = MvRegister<string>.Empty.Write("a", R1);

        Assert.AreEqual(a, b);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
