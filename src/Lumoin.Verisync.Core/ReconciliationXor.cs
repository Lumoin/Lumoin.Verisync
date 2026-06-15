using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The reconciliation XOR fold facade the kernel calls. Each member dispatches highest-tier-first over the
/// vector backends — 512, then 256, then 128, then the scalar reference — calling the first whose
/// <c>IsSupported</c> is true, so the kernel never names a width and always runs the widest path the host
/// carries.
/// </summary>
/// <remarks>
/// The dispatch if-chain folds to a single direct call at JIT time because each
/// <c>Vector{N}.IsHardwareAccelerated</c> property is a JIT constant on a given host. The facade adds no
/// validation of its own; the chosen backend validates lengths and throws the same exceptions the scalar
/// reference would. Every backend is byte-identical in effect — pinned by the per-tier agreement tests and by
/// the stream-level agreement test — so which one runs is never wire-visible.
/// </remarks>
public static class ReconciliationXor
{
    /// <summary>
    /// Folds <paramref name="source"/> into <paramref name="destination"/> in place, setting
    /// <c>destination[i] ^= source[i]</c> for every index, through the widest supported backend.
    /// </summary>
    /// <param name="destination">The buffer accumulating the fold; mutated in place.</param>
    /// <param name="source">The bytes to fold in. Its length must equal <paramref name="destination"/>'s.</param>
    /// <exception cref="ArgumentException">Thrown by the chosen backend when the two spans differ in length.</exception>
    public static void Fold(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if(ReconciliationXorVector512Backend.IsSupported)
        {
            ReconciliationXorVector512Backend.Fold(destination, source);
        }
        else if(ReconciliationXorVector256Backend.IsSupported)
        {
            ReconciliationXorVector256Backend.Fold(destination, source);
        }
        else if(ReconciliationXorVector128Backend.IsSupported)
        {
            ReconciliationXorVector128Backend.Fold(destination, source);
        }
        else
        {
            ReconciliationXorScalarBackend.Fold(destination, source);
        }
    }


    /// <summary>
    /// Writes the element-wise XOR of <paramref name="left"/> and <paramref name="right"/> into
    /// <paramref name="destination"/>, setting <c>destination[i] = left[i] ^ right[i]</c> for every index,
    /// through the widest supported backend.
    /// </summary>
    /// <param name="left">The first operand.</param>
    /// <param name="right">The second operand. Its length must equal <paramref name="left"/>'s.</param>
    /// <param name="destination">The buffer the result is written into; may alias either input. Its length must equal the operands'.</param>
    /// <exception cref="ArgumentException">Thrown by the chosen backend when the three spans are not all the same length.</exception>
    public static void Combine(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, Span<byte> destination)
    {
        if(ReconciliationXorVector512Backend.IsSupported)
        {
            ReconciliationXorVector512Backend.Combine(left, right, destination);
        }
        else if(ReconciliationXorVector256Backend.IsSupported)
        {
            ReconciliationXorVector256Backend.Combine(left, right, destination);
        }
        else if(ReconciliationXorVector128Backend.IsSupported)
        {
            ReconciliationXorVector128Backend.Combine(left, right, destination);
        }
        else
        {
            ReconciliationXorScalarBackend.Combine(left, right, destination);
        }
    }


    /// <summary>
    /// Determines whether every byte of <paramref name="bytes"/> is zero, through the widest supported
    /// backend. An empty span is neutral.
    /// </summary>
    /// <param name="bytes">The bytes to scan.</param>
    /// <returns><see langword="true"/> when no byte is non-zero.</returns>
    public static bool IsNeutral(ReadOnlySpan<byte> bytes)
    {
        if(ReconciliationXorVector512Backend.IsSupported)
        {
            return ReconciliationXorVector512Backend.IsNeutral(bytes);
        }

        if(ReconciliationXorVector256Backend.IsSupported)
        {
            return ReconciliationXorVector256Backend.IsNeutral(bytes);
        }

        if(ReconciliationXorVector128Backend.IsSupported)
        {
            return ReconciliationXorVector128Backend.IsNeutral(bytes);
        }

        return ReconciliationXorScalarBackend.IsNeutral(bytes);
    }
}
