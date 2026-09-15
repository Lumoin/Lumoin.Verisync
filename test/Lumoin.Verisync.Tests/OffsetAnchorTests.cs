using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Focused coverage of <see cref="OffsetAnchor"/> equality against a null other anchor.
/// </summary>
[TestClass]
internal sealed class OffsetAnchorTests
{
    /// <summary>Pins that equality against a null other anchor reports false.</summary>
    [TestMethod]
    public void EqualsReturnsFalseAgainstANullOtherAnchor()
    {
        OffsetAnchor anchor = OffsetAnchor.Head;

        Assert.IsFalse(anchor.Equals(NullOfT()));
    }


    /// <summary>A typed null anchor, so the Equals overload under test is the IEquatable one.</summary>
    private static OffsetAnchor? NullOfT() => null;
}
