using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of a hedged fast write.
/// </summary>
/// <param name="Activated">
/// <see langword="true"/> if the writer sent its fast round; <see langword="false"/> if it stood down after
/// its hedging delay because the round had already been driven. A writer that did not activate sent nothing,
/// so its update was neither committed nor rejected and the host must reissue it against the current value.
/// </param>
/// <param name="Delay">The hedging delay this writer waited before deciding whether to activate.</param>
/// <param name="AcceptedCount">The number of acceptors that accepted, or zero when the writer stood down.</param>
/// <param name="IsCommitted">Whether the accepting acceptors formed a fast quorum.</param>
public readonly record struct HedgedFastWriteOutcome(bool Activated, TimeSpan Delay, int AcceptedCount, bool IsCommitted);
