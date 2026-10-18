using System.Buffers;

namespace Lumoin.Verisync.Tests;

/// <summary>A pool that refuses one selected rent and counts successful rentals and returns.</summary>
internal sealed class RefusingRentMemoryPool: MemoryPool<byte>
{
    /// <summary>The caller-owned pool that supplies each rental.</summary>
    private MemoryPool<byte> Inner { get; }


    /// <summary>The one-based rent call number that is refused.</summary>
    private int RefusedRent { get; }


    /// <summary>Initializes a wrapper that leaves the inner pool under the caller's ownership.</summary>
    /// <param name="inner">The pool that supplies each successful rental.</param>
    /// <param name="refusedRent">The one-based rent call number to refuse.</param>
    public RefusingRentMemoryPool(MemoryPool<byte> inner, int refusedRent)
    {
        Inner = inner;
        RefusedRent = refusedRent;
    }


    /// <summary>The number of rentals handed out.</summary>
    public int Rented { get; private set; }


    /// <summary>The number of inner owners actually disposed.</summary>
    public int Returned { get; private set; }


    /// <summary>The number of rent calls that raised the configured failure.</summary>
    public int RefusedRents { get; private set; }


    /// <summary>The number of rent calls attempted.</summary>
    private int RentCalls { get; set; }


    /// <summary>The maximum buffer size supported by the inner pool.</summary>
    public override int MaxBufferSize => Inner.MaxBufferSize;


    /// <summary>Refuses the selected call or rents from the inner pool with an idempotent release.</summary>
    /// <param name="minBufferSize">The minimum buffer size forwarded to the inner pool.</param>
    /// <returns>The wrapped rental owner.</returns>
    /// <exception cref="InvalidOperationException">Thrown on the selected rent call.</exception>
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        RentCalls++;
        if(RentCalls == RefusedRent)
        {
            RefusedRents++;

            throw new InvalidOperationException("The rental cannot be made.");
        }

        IMemoryOwner<byte> owner = Inner.Rent(minBufferSize);
        Rented++;

        return new RentalOwner(this, owner);
    }


    /// <summary>Leaves the inner pool under the caller's ownership.</summary>
    /// <param name="disposing">Whether disposal was requested explicitly.</param>
    protected override void Dispose(bool disposing)
    {
    }


    /// <summary>An owner that releases its inner owner once and counts the return.</summary>
    /// <param name="pool">The pool whose return counter is updated.</param>
    /// <param name="inner">The owner supplying the rental memory.</param>
    private sealed class RentalOwner(RefusingRentMemoryPool pool, IMemoryOwner<byte> inner): IMemoryOwner<byte>
    {
        /// <summary>Whether a release has already been attempted.</summary>
        private bool disposed;


        /// <summary>The memory supplied by the inner owner.</summary>
        public Memory<byte> Memory => inner.Memory;


        /// <summary>Releases the inner owner once and counts the return; retries do nothing.</summary>
        public void Dispose()
        {
            if(disposed)
            {
                return;
            }

            disposed = true;
            inner.Dispose();
            pool.Returned++;
        }
    }
}
