using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The 512-bit vector backend for the reconciliation XOR fold. Full <see cref="Vector512{T}"/> chunks are
/// XORed in one operation each and the trailing bytes fall through to
/// <see cref="ReconciliationXorScalarBackend"/>, so the result is byte-identical to the scalar reference. The
/// width maps to a host's 512-bit instruction set and is the highest tier the facade dispatches to.
/// </summary>
/// <remarks>
/// Loads and stores use the safe <see cref="Vector512.LoadUnsafe{T}(ref readonly T, nuint)"/> and
/// <see cref="Vector512.StoreUnsafe{T}(Vector512{T}, ref T, nuint)"/> APIs over the span references, so no
/// unsafe block is needed. Each operation guards <see cref="IsSupported"/> first and validates lengths
/// second; the order is contractual so a mis-wired call on an unsupported host fails as a platform fault.
/// </remarks>
public static class ReconciliationXorVector512Backend
{
    /// <summary>Whether 512-bit vectors are hardware-accelerated on the current host.</summary>
    public static bool IsSupported => Vector512.IsHardwareAccelerated;


    /// <summary>
    /// Folds <paramref name="source"/> into <paramref name="destination"/> in place, setting
    /// <c>destination[i] ^= source[i]</c> for every index.
    /// </summary>
    /// <param name="destination">The buffer accumulating the fold; mutated in place.</param>
    /// <param name="source">The bytes to fold in. Its length must equal <paramref name="destination"/>'s.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown when <see cref="IsSupported"/> is <see langword="false"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the two spans differ in length.</exception>
    public static void Fold(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if(!IsSupported)
        {
            throw new PlatformNotSupportedException("The 512-bit vector backend is not supported on this host.");
        }

        if(destination.Length != source.Length)
        {
            throw new ArgumentException("A fold requires the destination and source to have equal length.", nameof(source));
        }

        int width = Vector512<byte>.Count;
        int offset = 0;
        int blockEnd = destination.Length - (destination.Length % width);
        while(offset < blockEnd)
        {
            Vector512<byte> left = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)offset);
            Vector512<byte> right = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(source), (nuint)offset);
            (left ^ right).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)offset);

            offset += width;
        }

        ReconciliationXorScalarBackend.Fold(destination[blockEnd..], source[blockEnd..]);
    }


    /// <summary>
    /// Writes the element-wise XOR of <paramref name="left"/> and <paramref name="right"/> into
    /// <paramref name="destination"/>, setting <c>destination[i] = left[i] ^ right[i]</c> for every index.
    /// </summary>
    /// <param name="left">The first operand.</param>
    /// <param name="right">The second operand. Its length must equal <paramref name="left"/>'s.</param>
    /// <param name="destination">The buffer the result is written into; may alias either input. Its length must equal the operands'.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown when <see cref="IsSupported"/> is <see langword="false"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the three spans are not all the same length.</exception>
    public static void Combine(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        if(!IsSupported)
        {
            throw new PlatformNotSupportedException("The 512-bit vector backend is not supported on this host.");
        }

        if(left.Length != right.Length || left.Length != destination.Length)
        {
            throw new ArgumentException("A combine requires the left, right, and destination spans to have equal length.", nameof(right));
        }

        int width = Vector512<byte>.Count;
        int offset = 0;
        int blockEnd = destination.Length - (destination.Length % width);
        while(offset < blockEnd)
        {
            Vector512<byte> a = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(left), (nuint)offset);
            Vector512<byte> b = Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(right), (nuint)offset);
            (a ^ b).StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)offset);

            offset += width;
        }

        ReconciliationXorScalarBackend.Combine(left[blockEnd..], right[blockEnd..], destination[blockEnd..]);
    }


    /// <summary>
    /// Determines whether every byte of <paramref name="bytes"/> is zero. An empty span is neutral.
    /// </summary>
    /// <param name="bytes">The bytes to scan.</param>
    /// <returns><see langword="true"/> when no byte is non-zero.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when <see cref="IsSupported"/> is <see langword="false"/>.</exception>
    public static bool IsNeutral(ReadOnlySpan<byte> bytes)
    {
        if(!IsSupported)
        {
            throw new PlatformNotSupportedException("The 512-bit vector backend is not supported on this host.");
        }

        int width = Vector512<byte>.Count;
        int offset = 0;
        int blockEnd = bytes.Length - (bytes.Length % width);
        Vector512<byte> accumulator = Vector512<byte>.Zero;
        while(offset < blockEnd)
        {
            accumulator |= Vector512.LoadUnsafe(ref MemoryMarshal.GetReference(bytes), (nuint)offset);

            offset += width;
        }

        if(accumulator != Vector512<byte>.Zero)
        {
            return false;
        }

        return ReconciliationXorScalarBackend.IsNeutral(bytes[blockEnd..]);
    }
}
