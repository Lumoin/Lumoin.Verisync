using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class GossipDigestTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void ConstructorRejectsNullSummary()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new GossipDigest(R1, null!));
    }


    [TestMethod]
    public void ExposesOriginAndSummary()
    {
        VectorClock summary = VectorClock.Empty.Increment(R1);
        GossipDigest digest = new(R1, summary);

        Assert.AreEqual(R1, digest.Origin);
        Assert.AreEqual(summary, digest.Summary);
    }


    [TestMethod]
    public void CompareReflectsSummaryComparison()
    {
        GossipDigest behind = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest ahead = new(R1, VectorClock.Empty.Increment(R1).Increment(R1));

        Assert.AreEqual(Causality.Before, behind.Compare(ahead));
        Assert.AreEqual(Causality.After, ahead.Compare(behind));
    }


    [TestMethod]
    public void IsBehindWhenPeerHasMore()
    {
        GossipDigest behind = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest ahead = new(R2, VectorClock.Empty.Increment(R1).Increment(R1));

        Assert.IsTrue(behind.IsBehind(ahead));
        Assert.IsFalse(ahead.IsBehind(behind));
    }


    [TestMethod]
    public void IsAheadWhenThisHasMore()
    {
        GossipDigest behind = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest ahead = new(R2, VectorClock.Empty.Increment(R1).Increment(R1));

        Assert.IsTrue(ahead.IsAheadOf(behind));
        Assert.IsFalse(behind.IsAheadOf(ahead));
    }


    [TestMethod]
    public void IsUpToDateWhenEqualOrAhead()
    {
        GossipDigest a = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest equal = new(R2, VectorClock.Empty.Increment(R1));
        GossipDigest behind = new(R2, VectorClock.Empty);

        Assert.IsTrue(a.IsUpToDateWith(equal));
        Assert.IsTrue(a.IsUpToDateWith(behind));
    }


    [TestMethod]
    public void ConcurrentSummariesAreBothBehindAndAhead()
    {
        GossipDigest a = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest b = new(R2, VectorClock.Empty.Increment(R2));

        Assert.AreEqual(Causality.Concurrent, a.Compare(b));
        Assert.IsTrue(a.IsBehind(b));
        Assert.IsTrue(a.IsAheadOf(b));
    }


    [TestMethod]
    public void EqualityHoldsForSameOriginAndSummary()
    {
        GossipDigest a = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest b = new(R1, VectorClock.Empty.Increment(R1));

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void EqualityFailsForDifferentOrigin()
    {
        GossipDigest a = new(R1, VectorClock.Empty.Increment(R1));
        GossipDigest b = new(R2, VectorClock.Empty.Increment(R1));

        Assert.AreNotEqual(a, b);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
