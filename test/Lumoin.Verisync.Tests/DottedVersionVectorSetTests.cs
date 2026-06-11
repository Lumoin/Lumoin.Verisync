using Lumoin.Verisync.Core;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class DottedVersionVectorSetTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoValues()
    {
        Assert.AreEqual(0, DottedVersionVectorSet<string>.Empty.Count);
        Assert.HasCount(0, DottedVersionVectorSet<string>.Empty.Values);
    }


    [TestMethod]
    public void AddAccumulatesValues()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a").Add(R2, "b");

        Assert.AreEqual(2, set.Count);
        Assert.HasCount(2, set.Values);
        Assert.Contains("a", set.Values);
        Assert.Contains("b", set.Values);
    }


    [TestMethod]
    public void AddAdvancesContext()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a");

        Assert.AreEqual(1, set.Context[R1]);
    }


    [TestMethod]
    public void ClearValuesRemovesEntriesButKeepsContext()
    {
        DottedVersionVectorSet<string> added = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> cleared = added.ClearValues();

        Assert.AreEqual(0, cleared.Count);
        Assert.AreEqual(1, cleared.Context[R1]);
    }


    [TestMethod]
    public void ClearValuesOnEmptyReturnsSameInstance()
    {
        Assert.AreSame(DottedVersionVectorSet<string>.Empty, DottedVersionVectorSet<string>.Empty.ClearValues());
    }


    [TestMethod]
    public void MergeRetainsConcurrentValues()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> b = DottedVersionVectorSet<string>.Empty.Add(R2, "b");

        DottedVersionVectorSet<string> merged = a.Merge(b);

        Assert.AreEqual(2, merged.Count);
        Assert.Contains("a", merged.Values);
        Assert.Contains("b", merged.Values);
    }


    [TestMethod]
    public void MergeDropsSupersededValues()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> b = a.ClearValues().Add(R1, "b");

        DottedVersionVectorSet<string> merged = a.Merge(b);

        Assert.AreEqual(1, merged.Count);
        Assert.Contains("b", merged.Values);
    }


    [TestMethod]
    public void MergeIsIdempotent()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a").Add(R2, "b");

        Assert.AreEqual(set, set.Merge(set));
    }


    [TestMethod]
    public void EqualityHoldsForSameDotsAndValues()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> b = DottedVersionVectorSet<string>.Empty.Add(R1, "a");

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void FromStateRejectsDotAboveItsContextEntry()
    {
        //Context observes R1 up to 1, but a dot claims counter 2: the context cannot dominate it.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var state = new DottedVersionVectorSetState<string>(context, [new DottedEntry<string>(Bytes(R1), 2, "a")]);

        Assert.ThrowsExactly<ArgumentException>(() => DottedVersionVectorSet<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateRejectsZeroCounterDot()
    {
        //A dot is minted by advancing the context past zero, so a zero counter never occurs honestly.
        var context = new VectorClockState([new ReplicaCounterEntry(Bytes(R1), 1)]);
        var state = new DottedVersionVectorSetState<string>(context, [new DottedEntry<string>(Bytes(R1), 0, "a")]);

        Assert.ThrowsExactly<ArgumentException>(() => DottedVersionVectorSet<string>.FromState(state));
    }


    [TestMethod]
    public void FromStateAcceptsHonestRoundTrip()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a").Add(R2, "b");

        DottedVersionVectorSet<string> back = DottedVersionVectorSet<string>.FromState(set.ToState());

        Assert.AreEqual(set, back);
    }


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
