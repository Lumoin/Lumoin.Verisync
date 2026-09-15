using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class ReplicaIdTests
{
    [TestMethod]
    public void FromSpanRejectsWrongLength()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ReplicaId.FromSpan([1, 2, 3]));
    }


    /// <summary>
    /// The bytes round-trip through both read forms: the span view and the array form built directly.
    /// </summary>
    [TestMethod]
    public void FromSpanRoundTripsBytes()
    {
        byte[] bytes = new byte[ReplicaId.Size];
        for(int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        ReplicaId id = ReplicaId.FromSpan(bytes);

        Assert.AreSequenceEqual(bytes, id.AsSpan().ToArray());
        Assert.AreSequenceEqual(bytes, id.ToArray());
    }


    [TestMethod]
    public void EqualityByByteContent()
    {
        ReplicaId left = Replica(7, 7, 7);
        ReplicaId right = Replica(7, 7, 7);

        Assert.IsTrue(left.Equals(right));
        Assert.IsTrue(left == right);
        Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
    }


    [TestMethod]
    public void InequalityByByteContent()
    {
        Assert.AreNotEqual(Replica(1), Replica(2));
        Assert.IsTrue(Replica(1) != Replica(2));
    }


    [TestMethod]
    public void OrderingIsLexicographic()
    {
        ReplicaId a = Replica(0, 1);
        ReplicaId b = Replica(0, 2);
        ReplicaId c = Replica(1, 0);

        Assert.IsLessThan(0, a.CompareTo(b));
        Assert.IsLessThan(0, b.CompareTo(c));
        Assert.IsLessThan(0, a.CompareTo(c));
    }


    [TestMethod]
    public void ComparisonOperatorsAreConsistent()
    {
        ReplicaId a = Replica(0, 1);
        ReplicaId b = Replica(0, 2);

        Assert.IsTrue(a < b);
        Assert.IsTrue(a <= b);
        Assert.IsFalse(a > b);
        Assert.IsFalse(a >= b);

        Assert.AreEqual(a.CompareTo(b) < 0, a < b);
        Assert.AreEqual(a.CompareTo(b) >= 0, a >= b);
    }


    [TestMethod]
    public void CopyToWritesAllBytes()
    {
        ReplicaId id = Replica(9, 8, 7);
        Span<byte> destination = stackalloc byte[ReplicaId.Size];

        id.CopyTo(destination);

        Assert.IsTrue(destination.SequenceEqual(id.AsSpan()));
    }


    [TestMethod]
    public void GenerateRejectsNullFillEntropy()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ReplicaId.Generate(null!));
    }


    [TestMethod]
    public void GenerateFillsTheWholeBuffer()
    {
        ReplicaId id = ReplicaId.Generate(FillSequential);

        Assert.HasCount(ReplicaId.Size, id.AsSpan());
        Assert.AreEqual(0, id.AsSpan()[0]);
        Assert.AreEqual(1, id.AsSpan()[1]);
    }


    [TestMethod]
    public void GenerateCallsFillEntropyExactlyOnce()
    {
        int calls = 0;
        void CountingFill(Span<byte> destination)
        {
            calls++;
        }

        _ = ReplicaId.Generate(CountingFill);

        Assert.AreEqual(1, calls);
    }


    [TestMethod]
    public void GenerateWithDefaultFillUsesRandomNumberGenerator()
    {
        ReplicaId first = ReplicaId.Generate();
        ReplicaId second = ReplicaId.Generate();

        Assert.AreNotEqual(first, second);
    }


    [TestMethod]
    public void ToStringShowsSizeAndHexPreview()
    {
        ReplicaId id = Replica(0x01, 0x02, 0x03);

        string text = id.ToString();

        Assert.Contains("32 bytes", text);
        Assert.Contains("010203", text);
    }


    /// <summary>
    /// Identifiers whose leading bytes differ hash differently, because the leading word of the random bytes is
    /// the hash. A constant hash keeps equality exact while putting every replica a vector clock, a counter or a
    /// stability frontier keys by into one bucket, which the equality vectors cannot see.
    /// </summary>
    [TestMethod]
    public void IdentifiersWithDifferentLeadingBytesHashDifferently()
    {
        Assert.AreNotEqual(Replica(1).GetHashCode(), Replica(2).GetHashCode());
    }


    /// <summary>
    /// Two identifiers holding the same bytes compare equal, so the non-strict comparisons hold between them and
    /// the strict ones do not. The equal arm is the only input on which each operator differs from its strict or
    /// non-strict sibling.
    /// </summary>
    [TestMethod]
    public void TheNonStrictComparisonsAreReflexiveAndTheStrictOnesAreNot()
    {
        ReplicaId left = Replica(5, 6, 7);
        ReplicaId right = Replica(5, 6, 7);

        Assert.AreEqual(0, left.CompareTo(right));

        Assert.IsTrue(left <= right);
        Assert.IsTrue(left >= right);
        Assert.IsFalse(left < right);
        Assert.IsFalse(left > right);
    }


    private static ReplicaId Replica(params byte[] prefix)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        prefix.AsSpan().CopyTo(buffer);

        return ReplicaId.FromSpan(buffer);
    }


    private static void FillSequential(Span<byte> destination)
    {
        for(int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)i;
        }
    }
}
