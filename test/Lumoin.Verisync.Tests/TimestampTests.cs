using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class TimestampTests
{
    [TestMethod]
    public void EqualityByTicks()
    {
        Assert.AreEqual(new Timestamp(42), new Timestamp(42));
        Assert.AreNotEqual(new Timestamp(42), new Timestamp(43));
    }


    [TestMethod]
    public void CompareToOrdersByTicks()
    {
        Assert.IsLessThan(0, new Timestamp(1).CompareTo(new Timestamp(2)));
        Assert.IsGreaterThan(0, new Timestamp(2).CompareTo(new Timestamp(1)));
        Assert.AreEqual(0, new Timestamp(7).CompareTo(new Timestamp(7)));
    }


    [TestMethod]
    public void ComparisonOperatorsAreConsistent()
    {
        Timestamp earlier = new(1);
        Timestamp later = new(2);

        Assert.IsTrue(earlier < later);
        Assert.IsTrue(earlier <= later);
        Assert.IsFalse(earlier > later);
        Assert.IsFalse(earlier >= later);
        Assert.IsTrue(later >= new Timestamp(2));
    }


    /// <summary>Pins that the less-than operator is strict: two <see cref="Timestamp"/> values with equal ticks never compare as less-than.</summary>
    [TestMethod]
    public void LessThanIsFalseForEqualTimestamps()
    {
        Timestamp a = new(5);
        Timestamp b = new(5);

        Assert.IsFalse(a < b);
    }


    /// <summary>Pins that the greater-than operator is strict: two <see cref="Timestamp"/> values with equal ticks never compare as greater-than.</summary>
    [TestMethod]
    public void GreaterThanIsFalseForEqualTimestamps()
    {
        Timestamp a = new(5);
        Timestamp b = new(5);

        Assert.IsFalse(a > b);
    }


    /// <summary>Pins that the less-than-or-equal operator is inclusive: two <see cref="Timestamp"/> values with equal ticks compare as less-than-or-equal.</summary>
    [TestMethod]
    public void LessThanOrEqualIsTrueForEqualTimestamps()
    {
        Timestamp a = new(5);
        Timestamp b = new(5);

        Assert.IsTrue(a <= b);
    }
}
