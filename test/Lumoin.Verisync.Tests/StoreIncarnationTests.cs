using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Coverage for <see cref="StoreIncarnation"/>, the store instance a configuration admits to answer for a
/// member. The fixed-width shape is pinned as <see cref="ReplicaId"/>'s is: the width refusal, every read of
/// the bytes, both generation overloads, the inequality operator, and the leading-word hash.
/// </summary>
[TestClass]
internal sealed class StoreIncarnationTests
{
    /// <summary>
    /// A span shorter than the fixed width is refused, naming the source span, rather than zero-padded into an
    /// incarnation.
    /// </summary>
    [TestMethod]
    public void FromSpanRefusesASpanShorterThanTheFixedWidth()
    {
        //A short span copies cleanly into the sixteen-byte value, so the width check is the only rule that
        //refuses it. A long span would also fail inside the copy, which names the destination instead.
        ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => StoreIncarnation.FromSpan([1, 2, 3]));

        Assert.AreEqual("source", refused.ParamName);
    }


    /// <summary>
    /// Every read of an incarnation returns the bytes it was built from: the span view, a fresh array, and a
    /// copy into a caller's buffer.
    /// </summary>
    [TestMethod]
    public void EveryReadReturnsTheBytesTheIncarnationWasBuiltFrom()
    {
        byte[] bytes = Sequential();
        StoreIncarnation incarnation = StoreIncarnation.FromSpan(bytes);

        Assert.AreSequenceEqual(bytes, incarnation.AsSpan().ToArray());
        Assert.AreSequenceEqual(bytes, incarnation.ToArray());

        Span<byte> destination = stackalloc byte[StoreIncarnation.Size];
        incarnation.CopyTo(destination);

        Assert.AreSequenceEqual(bytes, destination.ToArray());
    }


    /// <summary>A null entropy source is refused with an argument-null exception naming the parameter.</summary>
    [TestMethod]
    public void GenerateRefusesANullEntropySource()
    {
        ArgumentNullException refused = Assert.ThrowsExactly<ArgumentNullException>(() => StoreIncarnation.Generate(null!));

        Assert.AreEqual("fillEntropy", refused.ParamName);
    }


    /// <summary>
    /// Generation hands the entropy source the whole incarnation exactly once, and the incarnation holds exactly
    /// the bytes the source wrote.
    /// </summary>
    [TestMethod]
    public void GenerateHoldsExactlyTheBytesTheEntropySourceWrote()
    {
        byte[] written = Sequential();
        int calls = 0;
        int handed = 0;
        void FillFromWritten(Span<byte> destination)
        {
            calls++;
            handed = destination.Length;
            written.AsSpan().CopyTo(destination);
        }

        StoreIncarnation incarnation = StoreIncarnation.Generate(FillFromWritten);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(StoreIncarnation.Size, handed);
        Assert.AreSequenceEqual(written, incarnation.AsSpan().ToArray());
    }


    /// <summary>
    /// The parameterless overload draws from the platform generator, so two incarnations minted in a row differ.
    /// </summary>
    [TestMethod]
    public void GenerateWithThePlatformGeneratorMintsDistinctIncarnations()
    {
        StoreIncarnation first = StoreIncarnation.Generate();
        StoreIncarnation second = StoreIncarnation.Generate();

        Assert.AreNotEqual(first, second);
    }


    /// <summary>The inequality operator answers true across different bytes and false across equal ones.</summary>
    [TestMethod]
    public void TheInequalityOperatorIsTheNegationOfByteEquality()
    {
        Assert.IsTrue(Membership.Incarnation(1) != Membership.Incarnation(2));
        Assert.IsFalse(Membership.Incarnation(7, 7, 7) != Membership.Incarnation(7, 7, 7));
    }


    /// <summary>
    /// Incarnations that differ in their leading bytes hash apart, and equal incarnations hash alike.
    /// </summary>
    /// <remarks>
    /// The hash reads the leading word, which the configuration suite's collision vector relies on. A constant
    /// hash keeps every collection correct and turns every lookup into a scan, so only the apart half can see
    /// it.
    /// </remarks>
    [TestMethod]
    public void IncarnationsDifferingInTheirLeadingBytesHashApart()
    {
        Assert.AreNotEqual(Membership.Incarnation(1).GetHashCode(), Membership.Incarnation(2).GetHashCode());
        Assert.AreEqual(Membership.Incarnation(7, 7, 7).GetHashCode(), Membership.Incarnation(7, 7, 7).GetHashCode());
    }


    /// <summary>
    /// Sixteen bytes counting up from one, so no byte is zero and a read that wrote nothing cannot pass for one
    /// that wrote the value.
    /// </summary>
    private static byte[] Sequential()
    {
        byte[] bytes = new byte[StoreIncarnation.Size];
        for(int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        return bytes;
    }
}
