using System;
using System.Numerics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The keyed checksum primitive of the reconciliation kernel: SipHash-2-4 over arbitrary bytes under a
/// 128-bit key, plus a little-endian truncated writer. Each coded cell carries this checksum so the peeling
/// decoder can recognize a cell that has collapsed to a single item — a degree-one cell whose checksum
/// matches the checksum of its accumulated sum bytes is read out as a decoded item.
/// </summary>
/// <remarks>
/// SipHash-2-4 is a keyed pseudo-random function: two message-mixing rounds per word and four finalization
/// rounds. The construction is fixed and the kernel pins it byte-for-byte so independently authored encoders
/// and decoders agree on every cell. The key separates trust domains — replicas in one domain share the
/// well-known key, while peers across domains supply a secret key so a crafted checksum collision (the
/// masquerade an adversary would use to forge a peel) is rejected rather than silently accepted.
/// </remarks>
public static class ReconciliationChecksum
{
    /// <summary>
    /// Computes the 64-bit SipHash-2-4 of <paramref name="bytes"/> under the key halves
    /// <paramref name="keyLow"/> and <paramref name="keyHigh"/>.
    /// </summary>
    /// <param name="keyLow">The low 64 bits of the 128-bit key, the little-endian first eight key bytes.</param>
    /// <param name="keyHigh">The high 64 bits of the 128-bit key, the little-endian last eight key bytes.</param>
    /// <param name="bytes">The message to hash. An empty span is legal and produces the key-only digest.</param>
    /// <returns>The 64-bit digest.</returns>
    public static ulong Compute(ulong keyLow, ulong keyHigh, ReadOnlySpan<byte> bytes)
    {
        unchecked
        {
            ulong v0 = 0x736f6d6570736575UL ^ keyLow;
            ulong v1 = 0x646f72616e646f6dUL ^ keyHigh;
            ulong v2 = 0x6c7967656e657261UL ^ keyLow;
            ulong v3 = 0x7465646279746573UL ^ keyHigh;

            int length = bytes.Length;
            int wholeWords = length & ~7;
            for(int offset = 0; offset < wholeWords; offset += 8)
            {
                ulong m = ReadLittleEndianWord(bytes.Slice(offset, 8));
                v3 ^= m;
                SipRound(ref v0, ref v1, ref v2, ref v3);
                SipRound(ref v0, ref v1, ref v2, ref v3);
                v0 ^= m;
            }

            //The final word is the remaining tail bytes in the low positions plus the total length in the
            //top byte; it is always processed, even when the tail is empty.
            ulong tail = (ulong)(byte)length << 56;
            int remaining = length - wholeWords;
            for(int i = 0; i < remaining; i++)
            {
                tail |= (ulong)bytes[wholeWords + i] << (8 * i);
            }

            v3 ^= tail;
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            v0 ^= tail;

            v2 ^= 0xffUL;
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);

            return v0 ^ v1 ^ v2 ^ v3;
        }
    }


    /// <summary>
    /// Writes the low <c>destination.Length</c> bytes of <paramref name="checksum"/> into
    /// <paramref name="destination"/> in little-endian order, truncating to the destination width.
    /// </summary>
    /// <param name="checksum">The 64-bit checksum to truncate and write.</param>
    /// <param name="destination">The buffer to write into; its length is the checksum width.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/>'s length is outside the inclusive range one through eight.</exception>
    public static void Write(ulong checksum, Span<byte> destination)
    {
        if(destination.Length is < 1 or > 8)
        {
            throw new ArgumentException("A checksum width must be between one and eight bytes.", nameof(destination));
        }

        for(int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(checksum >> (8 * i));
        }
    }


    private static ulong ReadLittleEndianWord(ReadOnlySpan<byte> word)
    {
        return word[0]
            | ((ulong)word[1] << 8)
            | ((ulong)word[2] << 16)
            | ((ulong)word[3] << 24)
            | ((ulong)word[4] << 32)
            | ((ulong)word[5] << 40)
            | ((ulong)word[6] << 48)
            | ((ulong)word[7] << 56);
    }


    private static void SipRound(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        unchecked
        {
            v0 += v1;
            v1 = BitOperations.RotateLeft(v1, 13);
            v1 ^= v0;
            v0 = BitOperations.RotateLeft(v0, 32);
            v2 += v3;
            v3 = BitOperations.RotateLeft(v3, 16);
            v3 ^= v2;
            v0 += v3;
            v3 = BitOperations.RotateLeft(v3, 21);
            v3 ^= v0;
            v2 += v1;
            v1 = BitOperations.RotateLeft(v1, 17);
            v1 ^= v2;
            v2 = BitOperations.RotateLeft(v2, 32);
        }
    }
}
