namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of a CASPaxos change attempt.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="IsChosen">
/// <see langword="true"/> if the change reached a quorum in both phases and the value is chosen;
/// <see langword="false"/> if the proposing ballot was superseded and the change must be retried with a
/// higher ballot.
/// </param>
/// <param name="Value">The chosen value when <see cref="IsChosen"/> is <see langword="true"/>; otherwise the default.</param>
public sealed record ChangeOutcome<TValue>(bool IsChosen, TValue? Value);
