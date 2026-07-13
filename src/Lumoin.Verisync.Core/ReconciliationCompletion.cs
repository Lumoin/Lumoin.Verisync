using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The message a remove-aware initiator sends at the end of a completed exchange, reporting how many transfer
/// envelopes — carrying an elements or a drop payload — it sent in the session. The responder folds the
/// initiator's exchanged context on receipt, its first and only terminal fold, once this count matches the
/// transfers it has applied.
/// </summary>
/// <remarks>
/// A quiescent remove-aware exchange transfers nothing, so a <see cref="TransferCount"/> of zero is legal and
/// meaningful — the responder still reaches the resolving phase because the done signal is sent
/// unconditionally; a negative count is not a valid completion and is rejected at construction. The count is a
/// cardinality consistency check layered on the ordered, exactly-once transport: a mismatch means a transfer
/// envelope was lost, truncated, or duplicated, so the responder fails closed rather than folding a context
/// that might cover entries it never received.
/// </remarks>
public sealed record ReconciliationCompletion
{
    /// <summary>
    /// Initializes a completion message from the count of transfer envelopes the initiator sent, validating
    /// that the count is not negative.
    /// </summary>
    /// <param name="transferCount">The number of elements and drop envelopes the initiator sent in the session; zero or more.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="transferCount"/> is negative.</exception>
    public ReconciliationCompletion(int transferCount)
    {
        if(transferCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transferCount), transferCount, "A transfer count cannot be negative.");
        }

        TransferCount = transferCount;
    }


    /// <summary>The number of transfer envelopes the initiator sent; the responder checks it against its own applied-transfer count before the terminal fold.</summary>
    public int TransferCount { get; }
}
