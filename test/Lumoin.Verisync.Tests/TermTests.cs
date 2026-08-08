using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The Raft term's unit suite. A term is non-negative, the bound is enforced on the constructor and on a
/// <c>with</c> expression alike, and the successor fails closed at the top of the range rather than wrapping
/// into a value the validator would then reject for the wrong reason.
/// </summary>
[TestClass]
internal sealed class TermTests
{
    [TestMethod]
    public void TheNamedTermsAreTheProtocolsOwnBoundaries()
    {
        //Zero is the term a node occupies before any election, and it is what an empty log reports for its
        //last entry; one is the first term an election can produce and the lowest a log entry can carry.
        Assert.AreEqual(0L, Term.Zero.Value);
        Assert.AreEqual(1L, Term.First.Value);
        Assert.AreEqual(Term.Zero, default(Term));
    }


    [TestMethod]
    public void ANegativeTermIsRefusedOnTheConstructor()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new Term(-1));

        //The exception names the public property rather than the validator's own parameter, so a caller sees
        //the member it supplied.
        Assert.AreEqual("Value", thrown.ParamName);
    }


    //A WITH EXPRESSION IS A SECOND CONSTRUCTION PATH AND NOT A COPY. The initializer writes the backing field
    //directly, so a validator placed only there would leave the record mutable into an illegal state through
    //one line of caller code.
    [TestMethod]
    public void ANegativeTermIsRefusedOnAWithExpression()
    {
        Term term = new(4);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = term with { Value = -1 });
    }


    [TestMethod]
    public void TheSuccessorIsTheNextTermAndLeavesTheOriginalAlone()
    {
        Term term = new(7);

        Assert.AreEqual(new Term(8), term.Next());
        Assert.AreEqual(new Term(7), term);
        Assert.AreEqual(Term.First, Term.Zero.Next());
    }


    //THE SUCCESSOR FAILS CLOSED RATHER THAN WRAPPING. An unchecked increment at the top of the range yields
    //long.MinValue, which the non-negativity validator would then reject with a message about a negative term
    //and hide the exhaustion that actually happened.
    [TestMethod]
    public void TheSuccessorOfTheLastRepresentableTermOverflowsRatherThanWrapping()
    {
        Term last = new(long.MaxValue);

        Assert.ThrowsExactly<OverflowException>(() => _ = last.Next());
    }


    [TestMethod]
    public void TermsOrderByNumber()
    {
        Term lower = new(3);
        Term higher = new(9);

        Assert.IsTrue(lower < higher);
        Assert.IsTrue(lower <= higher);
        Assert.IsTrue(higher > lower);
        Assert.IsTrue(higher >= lower);
        Assert.IsTrue(lower <= new Term(3));
        Assert.IsTrue(lower >= new Term(3));
        Assert.IsLessThan(0, lower.CompareTo(higher));
        Assert.IsGreaterThan(0, higher.CompareTo(lower));
        Assert.AreEqual(0, lower.CompareTo(new Term(3)));
    }
}
