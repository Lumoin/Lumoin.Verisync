using System;
using System.Buffers;

namespace Lumoin.Verisync.Core;

/// <summary>Clears and releases reconciliation rentals while retaining the first failure.</summary>
internal static class ReconciliationRental
{
    /// <summary>Clears the live bytes and disposes the owner, attempting both steps and retaining the first failure.</summary>
    /// <param name="owner">The rental to clear and release.</param>
    /// <param name="liveBytes">The number of bytes to clear from the start of the rental.</param>
    /// <param name="failure">The first failure, updated only when no failure has been recorded.</param>
    internal static void Release(IMemoryOwner<byte> owner, int liveBytes, ref Exception? failure)
    {
        try
        {
            owner.Memory.Span[..liveBytes].Clear();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            owner.Dispose();
        }
        catch(Exception exception)
        {
            failure ??= exception;
        }
    }
}
