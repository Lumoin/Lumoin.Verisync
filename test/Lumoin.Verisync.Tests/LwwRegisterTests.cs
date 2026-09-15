using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LwwRegisterTests
{
    private static ReplicaId R1 { get; } = Replica(1);
    private static ReplicaId R2 { get; } = Replica(2);


    [TestMethod]
    public void EmptyHasNoValue()
    {
        Assert.IsFalse(LwwRegister<string>.Empty.HasValue);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = LwwRegister<string>.Empty.Value);
    }


    [TestMethod]
    public void WriteSetsValue()
    {
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);

        Assert.IsTrue(register.HasValue);
        Assert.AreEqual("a", register.Value);
        Assert.AreEqual(new Timestamp(1), register.Timestamp);
        Assert.AreEqual(R1, register.Writer);
    }


    [TestMethod]
    public void WriteWithTimeProviderStampsFromClock()
    {
        FakeTimeProvider clock = new();
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("a", R1, clock);

        Assert.AreEqual(clock.GetUtcNow().UtcTicks, register.Timestamp.UtcTicks);
    }


    [TestMethod]
    public void WriteWithTimeProviderRejectsNullClock()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => LwwRegister<string>.Empty.Write("a", R1, null!));
    }


    [TestMethod]
    public void MergeHigherTimestampWins()
    {
        LwwRegister<string> earlier = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);
        LwwRegister<string> later = LwwRegister<string>.Empty.Write("b", new Timestamp(2), R1);

        Assert.AreEqual("b", earlier.Merge(later).Value);
        Assert.AreEqual("b", later.Merge(earlier).Value);
    }


    [TestMethod]
    public void MergeBreaksTimestampTieByWriter()
    {
        LwwRegister<string> fromR1 = LwwRegister<string>.Empty.Write("a", new Timestamp(5), R1);
        LwwRegister<string> fromR2 = LwwRegister<string>.Empty.Write("b", new Timestamp(5), R2);

        //R2 ([2]) sorts after R1 ([1]), so its write wins.
        Assert.AreEqual("b", fromR1.Merge(fromR2).Value);
        Assert.AreEqual("b", fromR2.Merge(fromR1).Value);
    }


    [TestMethod]
    public void MergeEmptyLosesToWritten()
    {
        LwwRegister<string> written = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);

        Assert.AreEqual("a", LwwRegister<string>.Empty.Merge(written).Value);
        Assert.AreEqual("a", written.Merge(LwwRegister<string>.Empty).Value);
    }


    [TestMethod]
    public void WriteDoesNotMutateOriginal()
    {
        LwwRegister<string> original = LwwRegister<string>.Empty;
        _ = original.Write("a", new Timestamp(1), R1);

        Assert.IsFalse(original.HasValue);
    }


    [TestMethod]
    public void EqualityHoldsForSameWrite()
    {
        LwwRegister<string> a = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);
        LwwRegister<string> b = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);

        Assert.AreEqual(a, b);
    }


    [TestMethod]
    public void EqualityFailsForDifferentValue()
    {
        LwwRegister<string> a = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);
        LwwRegister<string> b = LwwRegister<string>.Empty.Write("b", new Timestamp(1), R1);

        Assert.AreNotEqual(a, b);
    }


    /// <summary>Pins that <see cref="LwwRegister{TValue}.Equals(LwwRegister{TValue}?)"/> refuses operands whose <see cref="LwwRegister{TValue}.HasValue"/> differ.</summary>
    [TestMethod]
    public void EqualsWhenHasValueDiffersReturnsFalse()
    {
        LwwRegister<string> empty = LwwRegister<string>.Empty;
        LwwRegister<string> written = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);

        Assert.IsFalse(empty.Equals(written));
        Assert.IsFalse(written.Equals(empty));
    }


    /// <summary>Pins that <see cref="LwwRegister{TValue}.Merge"/> rejects a null operand.</summary>
    [TestMethod]
    public void MergeRejectsNullOther()
    {
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);

        Assert.ThrowsExactly<ArgumentNullException>(() => register.Merge(null!));
    }


    /// <summary>
    /// Pins that a register holding a value hashes differently from the empty register, so the value branch of
    /// GetHashCode is taken rather than folded to the empty register's zero.
    /// </summary>
    [TestMethod]
    public void GetHashCodeWithValueDiffersFromEmpty()
    {
        Timestamp timestamp = new(7);
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("a", timestamp, R1);

        Assert.AreNotEqual(LwwRegister<string>.Empty.GetHashCode(), register.GetHashCode());
    }


    /// <summary>Pins that <see cref="LwwRegister{TValue}.Equals(LwwRegister{TValue}?)"/> refuses a null operand.</summary>
    [TestMethod]
    public void EqualsWithNullOtherReturnsFalse()
    {
        LwwRegister<string> register = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);

        Assert.IsFalse(register.Equals(NullRegister()));
    }


    /// <summary>Returns a null LwwRegister for the equality vector.</summary>
    private static LwwRegister<string>? NullRegister() => null;


    /// <summary>Pins that <see cref="LwwRegister{TValue}.Equals(LwwRegister{TValue}?)"/> requires the timestamp to match even when the writer and value already do.</summary>
    [TestMethod]
    public void EqualsWhenTimestampsDifferButWriterAndValueMatchReturnsFalse()
    {
        LwwRegister<string> earlier = LwwRegister<string>.Empty.Write("a", new Timestamp(1), R1);
        LwwRegister<string> later = LwwRegister<string>.Empty.Write("a", new Timestamp(2), R1);

        Assert.IsFalse(earlier.Equals(later));
    }


    /// <summary>Pins that <see cref="LwwRegister{TValue}.Merge"/> keeps the receiver when timestamp and writer both tie.</summary>
    [TestMethod]
    public void MergeOnExactTiePrefersThis()
    {
        LwwRegister<string> left = LwwRegister<string>.Empty.Write("left", new Timestamp(5), R1);
        LwwRegister<string> right = LwwRegister<string>.Empty.Write("right", new Timestamp(5), R1);

        Assert.AreEqual("left", left.Merge(right).Value);
        Assert.AreEqual("right", right.Merge(left).Value);
    }


    /// <summary>Pins that <see cref="LwwRegister{TValue}.GetHashCode"/> is zero for the empty register.</summary>
    [TestMethod]
    public void GetHashCodeWhenEmptyIsZero()
    {
        Assert.AreEqual(0, LwwRegister<string>.Empty.GetHashCode());
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
