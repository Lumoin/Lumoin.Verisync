using System.Buffers;

namespace Lumoin.Verisync.Tests;

/// <summary>A pool that counts rentals and returns and observes dirty bytes before each return.</summary>
internal sealed class ReturnObservingMemoryPool: MemoryPool<byte>
{
    /// <summary>The caller-owned pool that supplies each rental.</summary>
    private MemoryPool<byte> Inner { get; }


    /// <summary>Initializes a wrapper that leaves the inner pool under the caller's ownership.</summary>
    /// <param name="inner">The pool that supplies each rental.</param>
    public ReturnObservingMemoryPool(MemoryPool<byte> inner)
    {
        Inner = inner;
    }


    /// <summary>The number of rentals handed out.</summary>
    public int Rented { get; private set; }


    /// <summary>The number of inner owners actually disposed.</summary>
    public int Returned { get; private set; }


    /// <summary>The number of returns whose requested region contained a non-zero byte.</summary>
    public int DirtyReturns { get; private set; }


    /// <summary>The maximum buffer size supported by the inner pool.</summary>
    public override int MaxBufferSize => Inner.MaxBufferSize;


    /// <summary>Rents from the inner pool and records the requested size for inspection before release.</summary>
    /// <param name="minBufferSize">The minimum buffer size forwarded to the inner pool.</param>
    /// <returns>The wrapped rental owner.</returns>
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        IMemoryOwner<byte> owner = Inner.Rent(minBufferSize);
        Rented++;

        return new RentalOwner(this, owner, minBufferSize);
    }


    /// <summary>Leaves the inner pool under the caller's ownership.</summary>
    /// <param name="disposing">Whether disposal was requested explicitly.</param>
    protected override void Dispose(bool disposing)
    {
    }


    /// <summary>An owner that inspects its requested region before releasing its inner owner once.</summary>
    /// <param name="pool">The pool whose return counters are updated.</param>
    /// <param name="inner">The owner supplying the rental memory.</param>
    /// <param name="requestedSize">The number of requested bytes to inspect, or negative for the pool's default size.</param>
    private sealed class RentalOwner(ReturnObservingMemoryPool pool, IMemoryOwner<byte> inner, int requestedSize): IMemoryOwner<byte>
    {
        /// <summary>Whether a release has already been attempted.</summary>
        private bool disposed;


        /// <summary>The memory supplied by the inner owner.</summary>
        public Memory<byte> Memory => inner.Memory;


        /// <summary>Counts dirty requested bytes before releasing the inner owner once; retries do nothing.</summary>
        public void Dispose()
        {
            if(disposed)
            {
                return;
            }

            disposed = true;
            ReadOnlySpan<byte> memory = inner.Memory.Span;
            int length = requestedSize < 0 ? memory.Length : requestedSize;
            foreach(byte value in memory[..length])
            {
                if(value != 0)
                {
                    pool.DirtyReturns++;

                    break;
                }
            }

            inner.Dispose();
            pool.Returned++;
        }
    }
}
