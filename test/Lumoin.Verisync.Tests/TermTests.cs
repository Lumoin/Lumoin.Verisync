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


    /// <summary>
    /// A WITH EXPRESSION IS A SECOND CONSTRUCTION PATH AND NOT A COPY.
    /// </summary>
    /// <remarks>
    /// The initializer writes the backing field directly, so a validator placed only there would leave the
    /// record mutable into an illegal state through one line of caller code.
    /// </remarks>
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


    /// <summary>
    /// THE BOUND IS THE WIRE'S AND NOT THE ARITHMETIC'S.
    /// </summary>
    /// <remarks>
    /// A term crosses JSON as a bare number, and two terms above two to the fifty-third reach a double-parsing
    /// consumer as one value; every term rule in Figure 2 is a comparison, so two terms that arrive equal let a
    /// stale request pass the staleness test.
    /// </remarks>
    [TestMethod]
    public void TheTermRangeStopsAtTheLargestValueAJsonReaderReadsExactly()
    {
        //The bound is read back through the term rather than off a constant, because an assertion whose
        //operands are both compile-time constants is folded and stops testing anything.
        Assert.AreEqual(9007199254740991L, Term.MaxValue.Value);
        Assert.IsTrue(Term.MaxValue.IsExhausted);
        Assert.IsFalse(new Term(9007199254740990L).IsExhausted);

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new Term(9007199254740992L));

        Assert.AreEqual("Value", thrown.ParamName);
    }


    /// <summary>
    /// THE SUCCESSOR FAILS CLOSED RATHER THAN WRAPPING.
    /// </summary>
    /// <remarks>
    /// Incrementing past the top of the range would otherwise be caught by the range validator, whose message
    /// names the value rather than the exhaustion that actually happened, and a wrapped term would name an
    /// epoch that has already elected a leader.
    /// </remarks>
    [TestMethod]
    public void TheSuccessorOfTheLastRepresentableTermFailsClosed()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = Term.MaxValue.Next());
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
