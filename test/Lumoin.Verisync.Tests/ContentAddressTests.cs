using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The content address's unit suite.
/// </summary>
[TestClass]
internal sealed class ContentAddressTests
{
    /// <summary>Pins that <see cref="ContentAddress.GetHashCode"/> derives its result from the wrapped bytes: two
    /// addresses over distinct content must not collapse onto the same hash code.</summary>
    [TestMethod]
    public void HashCodeDependsOnWrappedBytes()
    {
        var a = new ContentAddress(new byte[] { 1, 2, 3, 4 });
        var b = new ContentAddress(new byte[] { 5, 6, 7, 8 });

        Assert.AreNotEqual(a.GetHashCode(), b.GetHashCode());
    }
}
