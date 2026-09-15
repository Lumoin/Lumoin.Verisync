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
        Assert.IsEmpty(DottedVersionVectorSet<string>.Empty.Values);
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


    /// <summary>
    /// Pins that two sets sharing the same dot but holding different values are unequal.
    /// </summary>
    [TestMethod]
    public void EqualityFailsForDifferentValues()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> b = DottedVersionVectorSet<string>.Empty.Add(R1, "b");

        Assert.AreNotEqual(a, b);
    }


    /// <summary>
    /// Pins that two sets with matching entry counts but different observed contexts are unequal.
    /// </summary>
    [TestMethod]
    public void EqualityFailsWhenContextsDifferButEntryCountsMatch()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty;
        DottedVersionVectorSet<string> b = DottedVersionVectorSet<string>.Empty.Add(R1, "x").ClearValues();

        Assert.AreNotEqual(a, b);
    }


    /// <summary>
    /// Pins that <see cref="DottedVersionVectorSet{T}.FromState"/> rejects a null state.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DottedVersionVectorSet<string>.FromState(null!));
    }


    /// <summary>
    /// Pins that <see cref="DottedVersionVectorSet{T}.RemoveValue"/> retains entries whose value differs
    /// from the one being removed.
    /// </summary>
    [TestMethod]
    public void RemoveValueRetainsEntriesWithOtherValues()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a").Add(R2, "b");

        DottedVersionVectorSet<string> result = set.RemoveValue("a");

        Assert.AreEqual(1, result.Count);
        Assert.Contains("b", result.Values);
    }


    /// <summary>
    /// Pins that <see cref="DottedVersionVectorSet{T}.RemoveValue"/> returns the same instance when no
    /// entry holds the given value, matching <see cref="DottedVersionVectorSet{T}.ClearValues"/>'s fast path.
    /// </summary>
    [TestMethod]
    public void RemoveValueOnAbsentValueReturnsSameInstance()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a");

        Assert.AreSame(set, set.RemoveValue("missing"));
    }


    /// <summary>
    /// Pins that two sets sharing a context but holding different values hash differently, so the entry fold
    /// contributes to GetHashCode rather than collapsing to a constant.
    /// </summary>
    [TestMethod]
    public void HashCodeReflectsEntryContent()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> b = DottedVersionVectorSet<string>.Empty.Add(R1, "b");

        Assert.AreNotEqual(a.GetHashCode(), b.GetHashCode());
    }


    /// <summary>
    /// Pins that <see cref="DottedVersionVectorSet{T}.Merge"/> rejects a null other operand.
    /// </summary>
    [TestMethod]
    public void MergeRejectsNull()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a");

        Assert.ThrowsExactly<ArgumentNullException>(() => set.Merge(null!));
    }


    /// <summary>
    /// Pins that <see cref="DottedVersionVectorSet{T}.Equals(DottedVersionVectorSet{T})"/> returns false
    /// for a null other operand.
    /// </summary>
    [TestMethod]
    public void EqualsReturnsFalseForNullOther()
    {
        DottedVersionVectorSet<string> set = DottedVersionVectorSet<string>.Empty.Add(R1, "a");

        Assert.IsFalse(set.Equals(NullSet()));
    }


    /// <summary>Returns a null DottedVersionVectorSet for the equality vector.</summary>
    private static DottedVersionVectorSet<string>? NullSet() => null;


    /// <summary>
    /// Pins that two sets with the same observed context but different entry counts are unequal.
    /// </summary>
    [TestMethod]
    public void EqualityFailsWhenEntryCountsDifferButContextsMatch()
    {
        DottedVersionVectorSet<string> a = DottedVersionVectorSet<string>.Empty.Add(R1, "a");
        DottedVersionVectorSet<string> b = a.RemoveValue("a");

        Assert.AreNotEqual(a, b);
    }


    private static ImmutableArray<byte> Bytes(ReplicaId replica) => ImmutableArray.Create(replica.AsSpan());


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
