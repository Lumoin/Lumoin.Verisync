using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The leader-side replication position's unit suite.
/// </summary>
[TestClass]
internal sealed class FollowerProgressTests
{
    /// <summary>
    /// A NextIndex below LogIndex.First is refused on construction, because the empty prefix is a position every
    /// log matches rather than one a leader can send from.
    /// </summary>
    [TestMethod]
    public void ANextIndexBelowFirstIsRefusedOnConstruction()
    {
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new FollowerProgress(LogIndex.BeforeFirst, LogIndex.BeforeFirst));

        Assert.AreEqual("NextIndex", thrown.ParamName);
    }


    /// <summary>
    /// The retreat stops at LogIndex.First: the empty prefix below it is a position every log matches, not a
    /// send-from point, so retreating from the first index holds there rather than stepping below it.
    /// </summary>
    [TestMethod]
    public void RetreatingFromTheFirstIndexFloorsThereRatherThanSteppingBelowIt()
    {
        FollowerProgress progress = new(LogIndex.First, LogIndex.BeforeFirst);

        FollowerProgress retreated = progress.Retreated();

        Assert.AreEqual(LogIndex.First, retreated.NextIndex);
    }
}
