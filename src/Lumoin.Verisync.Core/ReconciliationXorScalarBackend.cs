using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The byte-by-byte reference backend for the reconciliation XOR fold. Every vector backend matches this
/// implementation byte-for-byte, and its scalar tail delegates here, so this class pins the exact semantics
/// the kernel's hot loops carry — element-wise XOR over equal-length spans and an all-zero neutrality scan.
/// </summary>
/// <remarks>
/// The operations are plain index loops with no platform dependency, so <see cref="IsSupported"/> is always
/// <see langword="true"/>. This is the arbiter the agreement tests compare every accelerated tier against.
/// </remarks>
public static class ReconciliationXorScalarBackend
{
    /// <summary>Whether this backend can run on the current host. The scalar backend is always supported.</summary>
    public static bool IsSupported => true;


    /// <summary>
    /// Folds <paramref name="source"/> into <paramref name="destination"/> in place, setting
    /// <c>destination[i] ^= source[i]</c> for every index.
    /// </summary>
    /// <param name="destination">The buffer accumulating the fold; mutated in place.</param>
    /// <param name="source">The bytes to fold in. Its length must equal <paramref name="destination"/>'s.</param>
    /// <exception cref="ArgumentException">Thrown when the two spans differ in length.</exception>
    public static void Fold(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if(destination.Length != source.Length)
        {
            throw new ArgumentException("A fold requires the destination and source to have equal length.", nameof(source));
        }

        for(int i = 0; i < destination.Length; i++)
        {
            destination[i] ^= source[i];
        }
    }


    /// <summary>
    /// Writes the element-wise XOR of <paramref name="left"/> and <paramref name="right"/> into
    /// <paramref name="destination"/>, setting <c>destination[i] = left[i] ^ right[i]</c> for every index.
    /// </summary>
    /// <param name="left">The first operand.</param>
    /// <param name="right">The second operand. Its length must equal <paramref name="left"/>'s.</param>
    /// <param name="destination">The buffer the result is written into; may alias either input. Its length must equal the operands'.</param>
    /// <exception cref="ArgumentException">Thrown when the three spans are not all the same length.</exception>
    public static void Combine(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        if(left.Length != right.Length || left.Length != destination.Length)
        {
            throw new ArgumentException("A combine requires the left, right, and destination spans to have equal length.", nameof(right));
        }

        for(int i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(left[i] ^ right[i]);
        }
    }


    /// <summary>
    /// Determines whether every byte of <paramref name="bytes"/> is zero. An empty span is neutral.
    /// </summary>
    /// <param name="bytes">The bytes to scan.</param>
    /// <returns><see langword="true"/> when no byte is non-zero.</returns>
    public static bool IsNeutral(ReadOnlySpan<byte> bytes)
    {
        for(int i = 0; i < bytes.Length; i++)
        {
            if(bytes[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}
