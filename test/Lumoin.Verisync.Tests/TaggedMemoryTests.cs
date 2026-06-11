using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
[DoNotParallelize]
internal sealed class TaggedMemoryTests
{
    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The constructor throws on the null-owner check before any disposable instance is fully constructed, so there is nothing to dispose.")]
    public void ConstructorRejectsNullMemoryOwner()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new TestTaggedMemory(null!, Tag.Empty));
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The constructor throws on the null-tag check before taking ownership of the rented owner; the owner is disposed explicitly after the assertion.")]
    public void ConstructorRejectsNullTag()
    {
        ExactSizeOwner owner = ExactSizeRent(4);

        Assert.ThrowsExactly<ArgumentNullException>(() => new TestTaggedMemory(owner, null!));

        owner.Dispose();
    }


    [TestMethod]
    public void TagIsExposed()
    {
        using TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        Assert.AreSame(VerisyncTags.ReplicaId, instance.Tag);
    }


    [TestMethod]
    public void AsReadOnlySpanReturnsContent()
    {
        byte[] expected = [1, 2, 3, 4];
        using TestTaggedMemory instance = CreateInstance(expected, VerisyncTags.ReplicaId);

        CollectionAssert.AreEqual(expected, instance.AsReadOnlySpan().ToArray());
    }


    [TestMethod]
    public void AsReadOnlyMemoryReturnsContent()
    {
        byte[] expected = [5, 6, 7, 8];
        using TestTaggedMemory instance = CreateInstance(expected, VerisyncTags.ReplicaId);

        CollectionAssert.AreEqual(expected, instance.AsReadOnlyMemory().ToArray());
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the rented owner transfers to the instance, which is disposed explicitly to exercise the byte-clearing path.")]
    public void DisposeClearsBytes()
    {
        byte[] data = [1, 2, 3, 4];
        ExactSizeOwner owner = ExactSizeRent(data.Length);
        data.CopyTo(owner.Memory.Span);
        Memory<byte> underlying = owner.Memory;

        TestTaggedMemory instance = new(owner, VerisyncTags.ReplicaId);
        instance.Dispose();

        Assert.AreEqual(-1, underlying.Span.IndexOfAnyExcept((byte)0));
    }


    [TestMethod]
    public void DisposeIsIdempotent()
    {
        TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        instance.Dispose();
        instance.Dispose();
    }


    [TestMethod]
    public void AsReadOnlySpanThrowsAfterDispose()
    {
        TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        instance.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => instance.AsReadOnlySpan());
    }


    [TestMethod]
    public void AsReadOnlyMemoryThrowsAfterDispose()
    {
        TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        instance.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => instance.AsReadOnlyMemory());
    }


    [TestMethod]
    public void EqualityHoldsForIdenticalBytes()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        using TestTaggedMemory right = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        Assert.IsTrue(left.Equals(right));
        Assert.IsTrue(left == right);
        Assert.IsFalse(left != right);
    }


    [TestMethod]
    public void EqualityIgnoresTagDifferences()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        using TestTaggedMemory right = CreateInstance([1, 2, 3], VerisyncTags.OperationId);

        Assert.IsTrue(left.Equals(right));
    }


    [TestMethod]
    public void EqualityFailsForDifferentBytes()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        using TestTaggedMemory right = CreateInstance([1, 2, 4], VerisyncTags.ReplicaId);

        Assert.IsFalse(left.Equals(right));
        Assert.IsTrue(left != right);
    }


    [TestMethod]
    public void HashCodeMatchesForEqualInstances()
    {
        using TestTaggedMemory left = CreateInstance([9, 8, 7], VerisyncTags.ReplicaId);
        using TestTaggedMemory right = CreateInstance([9, 8, 7], VerisyncTags.OperationId);

        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
    }


    [TestMethod]
    public void EqualsThrowsWhenThisIsDisposed()
    {
        using TestTaggedMemory other = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        TestTaggedMemory disposedLeft = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        disposedLeft.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => disposedLeft.Equals(other));
    }


    [TestMethod]
    public void EqualsThrowsWhenOtherIsDisposed()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        TestTaggedMemory disposedRight = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        disposedRight.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => left.Equals(disposedRight));
    }


    [TestMethod]
    public void EqualsReturnsFalseForNullEvenWhenNotDisposed()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        Assert.IsFalse(left.Equals(null));
    }


    [TestMethod]
    public void ObjectEqualsThrowsWhenThisIsDisposed()
    {
        using TestTaggedMemory other = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        TestTaggedMemory disposedLeft = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        disposedLeft.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => disposedLeft.Equals((object)other));
    }


    [TestMethod]
    public void GetHashCodeThrowsAfterDispose()
    {
        TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        instance.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => instance.GetHashCode());
    }


    [TestMethod]
    public void EqualityOperatorThrowsWhenLeftIsDisposed()
    {
        using TestTaggedMemory right = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        TestTaggedMemory disposedLeft = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        disposedLeft.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => disposedLeft == right);
    }


    [TestMethod]
    public void EqualityOperatorThrowsWhenRightIsDisposed()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        TestTaggedMemory disposedRight = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        disposedRight.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => left == disposedRight);
    }


    [TestMethod]
    public void InequalityOperatorThrowsWhenRightIsDisposed()
    {
        using TestTaggedMemory left = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        TestTaggedMemory disposedRight = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        disposedRight.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => left != disposedRight);
    }


    [TestMethod]
    public void EqualityOperatorTreatsTwoNullsAsEqualWithoutDisposalGuard()
    {
        Assert.IsTrue((TaggedMemory?)null == (TaggedMemory?)null);
        Assert.IsFalse((TaggedMemory?)null != (TaggedMemory?)null);
    }


    [TestMethod]
    public void LifetimeActivityIsNullWithoutListener()
    {
        Activity? currentBefore = Activity.Current;

        using TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        Assert.AreSame(currentBefore, Activity.Current);
    }


    [TestMethod]
    public void LifetimeActivityIsCreatedWithListener()
    {
        Activity? captured = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == VerisyncActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        Assert.IsNotNull(captured);
        Assert.AreEqual(VerisyncTelemetry.ActivityNameMemoryLifetime, captured.OperationName);
    }


    [TestMethod]
    public void LifetimeActivityCarriesBufferSizeAndKind()
    {
        Activity? captured = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == VerisyncActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);

        Assert.IsNotNull(captured);
        Assert.AreEqual(3, captured.GetTagItem(VerisyncTelemetry.TagBufferSize));
        Assert.AreEqual(VerisyncKind.ReplicaId, captured.GetTagItem(VerisyncTelemetry.TagKind));
    }


    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the rented owner transfers to the returned TestTaggedMemory, which disposes it on Dispose.")]
    private static TestTaggedMemory CreateInstance(ReadOnlySpan<byte> bytes, Tag tag)
    {
        ExactSizeOwner owner = ExactSizeRent(bytes.Length);
        bytes.CopyTo(owner.Memory.Span);

        return new TestTaggedMemory(owner, tag);
    }


    private static ExactSizeOwner ExactSizeRent(int length)
    {
        return new ExactSizeOwner(length);
    }


    private sealed class TestTaggedMemory: TaggedMemory
    {
        public TestTaggedMemory(IMemoryOwner<byte> memoryOwner, Tag tag) : base(memoryOwner, tag)
        {
        }
    }


    private sealed class ExactSizeOwner: IMemoryOwner<byte>
    {
        private IMemoryOwner<byte> Inner { get; }
        private int Length { get; }

        public ExactSizeOwner(int length)
        {
            Length = length;
            Inner = MemoryPool<byte>.Shared.Rent(length);
        }

        public Memory<byte> Memory => Inner.Memory[..Length];

        public void Dispose()
        {
            Inner.Dispose();
        }
    }
}
