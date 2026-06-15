using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The message the decoder side sends to close a reconciliation stream, reporting how many symbols completion
/// took. The streamer stops producing symbols on receipt.
/// </summary>
/// <remarks>
/// Completion always takes at least one symbol, so <see cref="AbsorbedCount"/> is positive; a count below one
/// is not a valid completion and is rejected at construction.
/// </remarks>
public sealed record ReconciliationDone
{
    /// <summary>
    /// Initializes a done message from the count of symbols completion took, validating that the count is
    /// positive.
    /// </summary>
    /// <param name="absorbedCount">The number of symbols absorbed to complete the decode; at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="absorbedCount"/> is below one.</exception>
    public ReconciliationDone(int absorbedCount)
    {
        if(absorbedCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(absorbedCount), absorbedCount, "An absorbed count must be at least one.");
        }

        AbsorbedCount = absorbedCount;
    }


    /// <summary>The number of symbols the decoder absorbed to complete; the streamer stops on receipt.</summary>
    public int AbsorbedCount { get; }
}
