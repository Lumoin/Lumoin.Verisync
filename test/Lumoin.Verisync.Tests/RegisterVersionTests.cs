using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The register version's unit suite. The subjects are the range bound and why it sits where it does, the
/// successor's refusal at the top of the range, and the ordering the instance identity depends on.
/// </summary>
[TestClass]
internal sealed class RegisterVersionTests
{
    /// <summary>
    /// A version crosses JSON as a bare number, so the bound is the largest integer a double holds exactly.
    /// </summary>
    /// <remarks>
    /// The literal is asserted rather than computed, so a mutation of that expression cannot move it.
    /// </remarks>
    [TestMethod]
    public void TheHighestVersionIsTheLargestIntegerADoubleRepresentsExactly()
    {
        ulong top = RegisterVersion.MaxValue.Value;

        Assert.AreEqual(9007199254740991UL, top);

        //The operands go through locals because an assertion whose sides are one expression is folded away.
        ulong topThroughADouble = (ulong)(double)top;
        ulong belowThroughADouble = (ulong)(double)(top - 1);

        Assert.AreEqual(top, topThroughADouble);
        Assert.AreEqual(top - 1, belowThroughADouble);

        double justAbove = top + 1;
        double twoAbove = top + 2;

        Assert.AreEqual(justAbove, twoAbove);
    }


    [TestMethod]
    public void AVersionAboveTheRangeIsRefusedOnConstructionAndOnAWithExpression()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RegisterVersion(RegisterVersion.MaxValue.Value + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RegisterVersion(ulong.MaxValue));

        //A validator written only for the constructor would miss the with expression.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = RegisterVersion.First with { Value = RegisterVersion.MaxValue.Value + 1 });
    }


    [TestMethod]
    public void TheUnwrittenVersionIsTheDefaultAndIsDistinctFromTheFirstWrite()
    {
        Assert.AreEqual(RegisterVersion.Unwritten, default(RegisterVersion));
        Assert.AreEqual(0UL, RegisterVersion.Unwritten.Value);
        Assert.AreEqual(1UL, RegisterVersion.First.Value);
        Assert.IsFalse(RegisterVersion.Unwritten.IsWritten);
        Assert.IsTrue(RegisterVersion.First.IsWritten);
    }


    [TestMethod]
    public void NextAdvancesByOneAndRefusesToWrapAtTheTopOfTheRange()
    {
        Assert.AreEqual(RegisterVersion.First, RegisterVersion.Unwritten.Next());
        Assert.AreEqual(new RegisterVersion(2UL), RegisterVersion.First.Next());

        Assert.IsFalse(RegisterVersion.First.IsExhausted);
        Assert.IsTrue(RegisterVersion.MaxValue.IsExhausted);

        //Wrapping would name a consensus instance that has already decided.
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = RegisterVersion.MaxValue.Next());
    }


    [TestMethod]
    public void VersionsOrderByNumber()
    {
        RegisterVersion low = new(4UL);
        RegisterVersion high = new(9UL);

        //The equal case needs a second value because the compiler refuses a self-comparison.
        RegisterVersion alsoLow = new(4UL);

        Assert.IsTrue(low < high);
        Assert.IsTrue(low <= high);
        Assert.IsTrue(high > low);
        Assert.IsTrue(high >= low);
        Assert.IsTrue(low <= alsoLow);
        Assert.IsTrue(low >= alsoLow);
        Assert.IsFalse(low < alsoLow);
        Assert.IsFalse(low > alsoLow);
        Assert.IsGreaterThan(0, high.CompareTo(low));
        Assert.IsLessThan(0, low.CompareTo(high));
        Assert.AreEqual(0, low.CompareTo(alsoLow));
    }
}
