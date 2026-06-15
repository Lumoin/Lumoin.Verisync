using System;
using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The standalone, untracked heap-backed <see cref="IMemoryOwner{T}"/> the cell buffer falls back to when no
/// <see cref="MemoryPool{T}"/> is injected: a plain managed <c>byte[]</c> of exactly the requested length,
/// exposed through <see cref="Memory"/> and cleared on <see cref="Dispose"/> so a recycled reference cannot
/// observe stale bytes (defence in depth, matching the tagged-memory pattern).
/// </summary>
/// <remarks>
/// This owner emits no telemetry by design — accountable, tracked memory is the pool's job, and the no-pool
/// fallback keeps the reconciliation kernel standalone-usable without a pool dependency. The array is exactly
/// the requested length, so a consumer that slices by its own logical width never reads past the buffer, and
/// after <see cref="Dispose"/> the array carries no live bytes and becomes collectable.
/// </remarks>
internal sealed class ReconciliationHeapMemoryOwner: IMemoryOwner<byte>
{
    private byte[] Buffer { get; }


    /// <summary>
    /// Allocates an exact <c>byte[<paramref name="length"/>]</c> backing.
    /// </summary>
    /// <param name="length">The exact number of bytes to allocate; must be greater than zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is less than or equal to zero.</exception>
    public ReconciliationHeapMemoryOwner(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(length, 0);

        Buffer = new byte[length];
    }


    /// <summary>The owned bytes, of exactly the requested length.</summary>
    public Memory<byte> Memory => Buffer;


    /// <summary>Clears the bytes so no stale content survives the owner; the array is then collectable.</summary>
    public void Dispose()
    {
        Array.Clear(Buffer);
    }
}
