using System;
using System.Buffers;

namespace Lumoin.Verisync.Benchmarks;

/// <summary>Allocates only the requested logical bytes for the scale probe.</summary>
internal sealed class ReconciliationHeapPool: MemoryPool<byte>
{
    /// <summary>Gets the largest supported byte array length.</summary>
    public override int MaxBufferSize => Array.MaxLength;

    /// <summary>Allocates an exact-size array, using one byte for an unspecified size.</summary>
    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        long length = minBufferSize == -1 ? 1L : minBufferSize;
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Array.MaxLength);

        return new Owner((int)length);
    }

    /// <summary>Disposes the pool, which retains no rentals.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    /// <summary>Owns one exact-size array until disposal drops its reference.</summary>
    private sealed class Owner(int length): IMemoryOwner<byte>
    {
        /// <summary>Gets or clears the owned memory.</summary>
        public Memory<byte> Memory { get; private set; } = new byte[length];

        /// <summary>Drops the owned array without pooling or clearing it.</summary>
        public void Dispose()
        {
            Memory = default;
        }
    }
}
