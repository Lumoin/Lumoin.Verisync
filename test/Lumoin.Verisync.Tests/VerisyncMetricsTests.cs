using Lumoin.Verisync.Core;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
[DoNotParallelize]
internal sealed class VerisyncMetricsTests
{
    [TestMethod]
    public void AllocatedBytesRecordedOnConstruction()
    {
        using MetricCollector<long> collector = new(VerisyncMetrics.MemoryAllocatedBytes);

        using(TestTaggedMemory instance = CreateInstance([1, 2, 3, 4], VerisyncTags.ReplicaId))
        {
            IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();

            Assert.HasCount(1, measurements);
            Assert.AreEqual(4L, measurements[0].Value);
            Assert.AreEqual(VerisyncKind.ReplicaId, measurements[0].Tags[VerisyncTelemetry.TagKind]);
        }
    }


    [TestMethod]
    public void LifetimeRecordedOnDisposal()
    {
        using MetricCollector<double> collector = new(VerisyncMetrics.MemoryLifetimeMs);

        TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.OperationId);
        instance.Dispose();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();

        Assert.HasCount(1, measurements);
        Assert.AreEqual(VerisyncKind.OperationId, measurements[0].Tags[VerisyncTelemetry.TagKind]);
    }


    [TestMethod]
    public void LifetimeRecordedEvenWithoutActivityListener()
    {
        using MetricCollector<double> collector = new(VerisyncMetrics.MemoryLifetimeMs);

        TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
        instance.Dispose();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();

        Assert.HasCount(1, measurements);
        Assert.AreEqual(0d, measurements[0].Value);
    }


    [TestMethod]
    public void EachConstructionAndDisposalProducesOneMeasurement()
    {
        using MetricCollector<long> allocCollector = new(VerisyncMetrics.MemoryAllocatedBytes);
        using MetricCollector<double> lifetimeCollector = new(VerisyncMetrics.MemoryLifetimeMs);

        for(int i = 0; i < 5; i++)
        {
            TestTaggedMemory instance = CreateInstance([1, 2, 3], VerisyncTags.ReplicaId);
            instance.Dispose();
        }

        Assert.HasCount(5, allocCollector.GetMeasurementSnapshot());
        Assert.HasCount(5, lifetimeCollector.GetMeasurementSnapshot());
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
