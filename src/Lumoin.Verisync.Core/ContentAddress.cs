using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A content-keyed view over item bytes used as a set key in the reconciliation kernel: two addresses are
/// equal when their wrapped bytes are byte-for-byte equal, and the hash is derived from those bytes. It wraps
/// a view the caller already owns — the encoder's copied item bytes or the decoder's decoded item bytes — so
/// it owns nothing and allocates nothing.
/// </summary>
/// <remarks>
/// The synthesized record equality is replaced because <see cref="ReadOnlyMemory{T}"/> compares by reference,
/// not by content, which would make two distinct buffers holding identical bytes unequal. The wrapped bytes
/// must outlive the address; they do, because they live in the encoder's membership set or the decoder's
/// decoded list for as long as the address is held against them.
/// </remarks>
internal readonly record struct ContentAddress
{
    /// <summary>
    /// Initializes a content address wrapping <paramref name="bytes"/> as a view, taking no copy.
    /// </summary>
    /// <param name="bytes">The item bytes to key by. The caller retains ownership and must keep them alive.</param>
    public ContentAddress(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
    }


    /// <summary>The wrapped item bytes this address is keyed by.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }


    /// <summary>Determines whether <paramref name="other"/> wraps byte-for-byte identical content.</summary>
    /// <param name="other">The address to compare with.</param>
    /// <returns><see langword="true"/> when both wrapped byte sequences are equal.</returns>
    public bool Equals(ContentAddress other)
    {
        return Bytes.Span.SequenceEqual(other.Bytes.Span);
    }


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes.Span);

        return hash.ToHashCode();
    }
}
