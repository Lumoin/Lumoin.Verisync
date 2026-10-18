using System.Buffers;

namespace Lumoin.Verisync.Tests;

/// <summary>A pool that counts rentals and returns while one selected owner refuses its first release.</summary>
internal sealed class ThrowingOwnerMemoryPool: MemoryPool<byte>
{
    /// <summary>The caller-owned pool that supplies each rental.</summary>
    private MemoryPool<byte> Inner { get; }


    /// <summary>The one-based rental number whose owner throws on its first disposal.</summary>
    private int ThrowingRent { get; }


    /// <summary>Initializes a wrapper that leaves the inner pool under the caller's ownership.</summary>
    /// <param name="inner">The pool that supplies each rental.</param>
    /// <param name="throwingRent">The one-based rental number whose owner refuses release.</param>
    public ThrowingOwnerMemoryPool(MemoryPool<byte> inner, int throwingRent)
    {
        Inner = inner;
        ThrowingRent = throwingRent;
    }


    /// <summary>The number of rentals handed out.</summary>
    public int Rented { get; private set; }


    /// <summary>The number of inner owners actually disposed.</summary>
    public int Returned { get; private set; }


    /// <summary>The number of owner releases that raised the configured failure.</summary>
    public int FaultedReleases { get; private set; }


    /// <summary>The maximum buffer size supported by the inner pool.</summary>
    public override int MaxBufferSize => Inner.MaxBufferSize;


    /// <summary>Rents from the inner pool and wraps the owner with an idempotent release.</summary>
    /// <param name="minBufferSize">The minimum buffer size forwarded to the inner pool.</param>
    /// <returns>The wrapped rental owner.</returns>
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        IMemoryOwner<byte> owner = Inner.Rent(minBufferSize);
        Rented++;

        return new RentalOwner(this, owner, Rented == ThrowingRent);
    }


    /// <summary>Leaves the inner pool under the caller's ownership.</summary>
    /// <param name="disposing">Whether disposal was requested explicitly.</param>
    protected override void Dispose(bool disposing)
    {
    }


    /// <summary>An owner that releases once or throws once without releasing its selected inner owner.</summary>
    /// <param name="pool">The pool whose release counters are updated.</param>
    /// <param name="inner">The owner supplying the rental memory.</param>
    /// <param name="throws">Whether the first release throws instead of disposing the inner owner.</param>
    private sealed class RentalOwner(ThrowingOwnerMemoryPool pool, IMemoryOwner<byte> inner, bool throws): IMemoryOwner<byte>
    {
        /// <summary>Whether a release has already been attempted.</summary>
        private bool disposed;


        /// <summary>The memory supplied by the inner owner.</summary>
        public Memory<byte> Memory => inner.Memory;


        /// <summary>Releases the inner owner once or raises the configured failure once; retries do nothing.</summary>
        /// <exception cref="InvalidOperationException">Thrown on the selected owner's first release.</exception>
        public void Dispose()
        {
            if(disposed)
            {
                return;
            }

            disposed = true;
            if(throws)
            {
                pool.FaultedReleases++;

                throw new InvalidOperationException("The rental cannot be released.");
            }

            inner.Dispose();
            pool.Returned++;
        }
    }
}
