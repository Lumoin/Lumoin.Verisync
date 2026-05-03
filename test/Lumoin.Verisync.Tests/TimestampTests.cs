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
}
