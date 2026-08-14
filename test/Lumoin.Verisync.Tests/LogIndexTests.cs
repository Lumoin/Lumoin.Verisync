using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The log position's unit suite. An index is non-negative, the empty prefix names no entry and every
/// operation that would read one through it fails closed, and the conversion to a zero-based position is the
/// single place the 1-based protocol index meets the backing store.
/// </summary>
[TestClass]
internal sealed class LogIndexTests
{
    [TestMethod]
    public void TheNamedIndicesAreTheProtocolsOwnBoundaries()
    {
        //The empty prefix is the consistency check's base case and the value a leader's matchIndex starts at;
        //one is the first entry a log can hold.
        Assert.AreEqual(0L, LogIndex.BeforeFirst.Value);
        Assert.AreEqual(1L, LogIndex.First.Value);
        Assert.AreEqual(LogIndex.BeforeFirst, default(LogIndex));
        Assert.IsTrue(LogIndex.BeforeFirst.IsBeforeFirst);
        Assert.IsFalse(LogIndex.First.IsBeforeFirst);
    }


    [TestMethod]
    public void ANegativeIndexIsRefusedOnTheConstructor()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new LogIndex(-1));

        Assert.AreEqual("Value", thrown.ParamName);
    }


    /// <summary>
    /// A WITH EXPRESSION IS A SECOND CONSTRUCTION PATH AND NOT A COPY, exactly as it is for a term.
    /// </summary>
    [TestMethod]
    public void ANegativeIndexIsRefusedOnAWithExpression()
    {
        LogIndex index = new(4);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = index with { Value = -1 });
    }


    [TestMethod]
    public void ThePositionIsTheOneBasedIndexReadAgainstAZeroBasedStore()
    {
        Assert.AreEqual(0, LogIndex.First.Position);
        Assert.AreEqual(4, new LogIndex(5).Position);
    }


    /// <summary>
    /// THE EMPTY PREFIX NAMES NO ENTRY, so both ways of reaching one through it are refused rather than
    /// returning the position below the store or the index below the base.
    /// </summary>
    [TestMethod]
    public void TheEmptyPrefixHasNeitherAPositionNorAPredecessor()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = LogIndex.BeforeFirst.Position);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = LogIndex.BeforeFirst.Previous());
    }


    /// <summary>
    /// A POSITION THAT DOES NOT FIT AN INT FAILS CLOSED.
    /// </summary>
    /// <remarks>
    /// An unchecked narrowing would hand a negative or wrapped offset to the backing list, which either throws
    /// somewhere unrelated or reads the wrong entry.
    /// </remarks>
    [TestMethod]
    public void APositionBeyondWhatAnInMemoryLogCanAddressOverflowsRatherThanWrapping()
    {
        LogIndex beyond = new((long)int.MaxValue + 2);

        Assert.ThrowsExactly<OverflowException>(() => _ = beyond.Position);
    }


    [TestMethod]
    public void TheNeighboursAreTheAdjacentEntriesAndTheFirstStepsDownToTheEmptyPrefix()
    {
        LogIndex index = new(7);

        Assert.AreEqual(new LogIndex(8), index.Next());
        Assert.AreEqual(new LogIndex(6), index.Previous());
        Assert.AreEqual(new LogIndex(7), index);
        Assert.AreEqual(LogIndex.First, LogIndex.BeforeFirst.Next());
        Assert.AreEqual(LogIndex.BeforeFirst, LogIndex.First.Previous());
    }


    [TestMethod]
    public void AdvancingByACountLandsOnTheEntryThatManyPast()
    {
        LogIndex index = new(3);

        Assert.AreEqual(new LogIndex(3), index.Advance(0));
        Assert.AreEqual(new LogIndex(7), index.Advance(4));

        //Advancing is how an append request derives the index of its last delivered entry, so a backwards
        //count is a caller error rather than a retreat.
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = index.Advance(-1));

        Assert.AreEqual("count", thrown.ParamName);
    }


    /// <summary>
    /// THE BOUND IS THE ONE A TERM TAKES AND FOR THE SAME REASON, an index being a bare JSON number too.
    /// </summary>
    /// <remarks>
    /// Here the consequence is the consistency check's: a previous log index that arrives as its neighbour
    /// matches the wrong entry.
    /// </remarks>
    [TestMethod]
    public void TheIndexRangeStopsAtTheLargestValueAJsonReaderReadsExactly()
    {
        Assert.AreEqual(9007199254740991L, LogIndex.MaxValue.Value);
        Assert.IsTrue(LogIndex.MaxValue.IsExhausted);
        Assert.IsFalse(new LogIndex(9007199254740990L).IsExhausted);

        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new LogIndex(9007199254740992L));

        Assert.AreEqual("Value", thrown.ParamName);
    }


    [TestMethod]
    public void TheSuccessorOfTheLastRepresentableIndexFailsClosed()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = LogIndex.MaxValue.Next());

        //Advancing attributes the overrun to the resulting index rather than to the count, because the count
        //itself was legal and it is the sum that leaves the range.
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = LogIndex.MaxValue.Advance(1));

        Assert.AreEqual("Value", thrown.ParamName);

        //A count large enough to leave the underlying range at all is caught before the validator sees a
        //wrapped negative it would report as a negative index.
        Assert.ThrowsExactly<OverflowException>(() => _ = LogIndex.MaxValue.Advance(long.MaxValue));
    }


    [TestMethod]
    public void IndicesOrderByPositionAndSelectTheirExtremes()
    {
        LogIndex lower = new(3);
        LogIndex higher = new(9);

        Assert.IsTrue(lower < higher);
        Assert.IsTrue(lower <= higher);
        Assert.IsTrue(higher > lower);
        Assert.IsTrue(higher >= lower);
        Assert.IsTrue(lower <= new LogIndex(3));
        Assert.IsTrue(lower >= new LogIndex(3));
        Assert.IsLessThan(0, lower.CompareTo(higher));
        Assert.AreEqual(0, lower.CompareTo(new LogIndex(3)));

        Assert.AreEqual(lower, LogIndex.Min(lower, higher));
        Assert.AreEqual(lower, LogIndex.Min(higher, lower));
        Assert.AreEqual(higher, LogIndex.Max(lower, higher));
        Assert.AreEqual(higher, LogIndex.Max(higher, lower));
    }
}
