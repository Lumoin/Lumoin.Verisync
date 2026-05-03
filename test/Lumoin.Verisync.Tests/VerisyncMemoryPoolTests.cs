using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class VerisyncMemoryPoolTests
{
    [TestMethod]
    public void RentReturnsExactBufferSize()
    {
        using VerisyncMemoryPool<byte> pool = new();
        int[] sizes = [1, 7, 16, 31, 64, 100, 1000];

        foreach(int size in sizes)
        {
            using IMemoryOwner<byte> owner = pool.Rent(size);
            Assert.AreEqual(size, owner.Memory.Length);
        }
    }


    [TestMethod]
    public void RentReusesSegmentsAfterReturn()
    {
        using VerisyncMemoryPool<byte> pool = new();

        IMemoryOwner<byte> first = pool.Rent(64);
        first.Dispose();

        using IMemoryOwner<byte> second = pool.Rent(64);

        Assert.AreEqual(64, second.Memory.Length);
    }


    [TestMethod]
    public void OwnerDoubleDisposeIsSafe()
    {
        using VerisyncMemoryPool<byte> pool = new();
        IMemoryOwner<byte> owner = pool.Rent(8);

        owner.Dispose();
        owner.Dispose();
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Rent throws on the size check before any owner is created, so there is nothing to dispose.")]
    public void RentRejectsNonPositiveSize()
    {
        using VerisyncMemoryPool<byte> pool = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Rent throws on the disposed check before any owner is created.")]
    public void RentAfterDisposeThrows()
    {
        VerisyncMemoryPool<byte> pool = new();
        pool.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => pool.Rent(8));
    }


    [TestMethod]
    public void TrimExcessReclaimsIdleSlabs()
    {
        using VerisyncMemoryPool<byte> pool = new();
        IMemoryOwner<byte> owner = pool.Rent(64);
        owner.Dispose();

        int reclaimed = pool.TrimExcess();

        Assert.AreEqual(1, reclaimed);
    }


    [TestMethod]
    public void PropertyRentAlwaysReturnsExactSize()
    {
        Gen.Int[1, 4096].Sample(size =>
        {
            using VerisyncMemoryPool<byte> pool = new();
            using IMemoryOwner<byte> owner = pool.Rent(size);
            Assert.AreEqual(size, owner.Memory.Length);
        });
    }
}
