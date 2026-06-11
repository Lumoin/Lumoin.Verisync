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
/// <remarks>
/// A failed attempt is not a guarantee the change did not happen: a value that missed its accept quorum
/// may still hold the highest accepted ballot inside some later prepare quorum, and a recovery will then
/// adopt and commit it. This is the classic CASPaxos ambiguity — "not chosen" means "not <em>known</em>
/// chosen". A retried change function must therefore be idempotent (or detect that its effect is already
/// present in the recovered value); a naive retry of a non-idempotent update can apply twice.
/// </remarks>
public sealed record ChangeOutcome<TValue>(bool IsChosen, TValue? Value);
