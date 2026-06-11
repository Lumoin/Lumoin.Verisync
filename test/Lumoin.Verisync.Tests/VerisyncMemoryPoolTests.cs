using CsCheck;
using Lumoin.Verisync.Core;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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
    public void OwnerConcurrentDoubleDisposeIsIdempotent()
    {
        using VerisyncMemoryPool<byte> pool = new();
        IMemoryOwner<byte> owner = pool.Rent(8);

        //A plain bool disposed flag let two threads both pass the guard, with the loser hitting the pool's
        //double-return validation and surfacing InvalidOperationException out of Dispose. The Interlocked
        //flag makes concurrent disposal a no-op for all but the first caller.
        Parallel.Invoke(owner.Dispose, owner.Dispose);
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Rent throws on the oversized-slab check before any owner or backing array is created, so there is nothing to dispose.")]
    public void RentRejectsOversizedBuffer()
    {
        using VerisyncMemoryPool<byte> pool = new();

        //The default strategy allocates a slab of bufferSize * 4 elements for large buffers; int.MaxValue * 4
        //overflows int range, so the slab constructor must reject it with a clear ArgumentOutOfRangeException
        //before attempting any allocation rather than wrapping into a confusing OverflowException or a later
        //ArraySegment ArgumentException.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(int.MaxValue));
    }


    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Rent throws on the oversized-slab check before any owner is created; the assertion verifies the activity left behind is stopped.")]
    public void OversizedRentLeavesNoUnstoppedActivity()
    {
        Activity? captured = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == VerisyncActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);

        using VerisyncMemoryPool<byte> pool = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => pool.Rent(int.MaxValue));

        //The rental span is started before slab construction, so a throwing rent must stop and dispose it
        //rather than leaking an open activity. A disposed activity is detached from Activity.Current and
        //carries the Error status the catch path stamped on it.
        Assert.IsNotNull(captured);
        Assert.IsNull(Activity.Current);
        Assert.AreEqual(ActivityStatusCode.Error, captured.Status);
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
