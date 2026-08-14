using System.Buffers;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A <see cref="MemoryPool{T}"/> whose every rental hands out a buffer pre-filled with a non-zero poison byte
/// and that never clears on return, so a segment a consumer rents is genuinely dirty regardless of any
/// production pool's clear policy. It lets the cell-buffer and arena tests exercise the CONSUMER's own
/// zero-on-rent / overwrite-on-append contract directly, without depending on whether the real pool happens to
/// clear recycled memory — <c>BaseMemoryPool</c> clears on return, which would otherwise mask a lost
/// consumer-side clear and make those tests vacuous.
/// </summary>
internal sealed class DirtyMemoryPool: MemoryPool<byte>
{
    private const byte Poison = 0xFF;

    public override int MaxBufferSize => int.MaxValue;


    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        int size = minBufferSize < 0 ? 1 : minBufferSize;
        byte[] buffer = new byte[size];
        buffer.AsSpan().Fill(Poison);

        return new DirtyOwner(buffer);
    }


    protected override void Dispose(bool disposing)
    {
    }


    /// <summary>
    /// The owner hands out exactly its dirty buffer and does not clear on dispose; the point is that the buffer
    /// the consumer holds starts non-zero, so only the consumer's own clear or overwrite can make it read
    /// clean.
    /// </summary>
    private sealed class DirtyOwner(byte[] buffer): IMemoryOwner<byte>
    {
        public Memory<byte> Memory => buffer;

        public void Dispose()
        {
        }
    }
}
