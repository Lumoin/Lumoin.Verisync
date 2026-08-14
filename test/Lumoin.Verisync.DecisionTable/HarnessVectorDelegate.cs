namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One vector of the harness's own verification suite.
/// </summary>
/// <param name="failures">The sink a vector records every violation it found into.</param>
/// <remarks>
/// A vector records every violation rather than stopping at the first, because a harness that reported only
/// its first broken invariant would be fixed one run at a time.
/// </remarks>
internal delegate void HarnessVectorDelegate(VectorFailures failures);
