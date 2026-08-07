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
/// <param name="AcceptedCount">
/// The number of acceptors that accepted the change. It is zero when the attempt failed before any accept
/// was sent, which is the case when the prepare phase did not reach its quorum, and it reports the true
/// count when accepts were sent but fell short of one.
/// </param>
/// <remarks>
/// <para>
/// A failed attempt is not a guarantee the change did not happen: a value that missed its accept quorum
/// may still hold the highest accepted ballot inside some later prepare quorum, and a recovery will then
/// adopt and commit it. This is the classic CASPaxos ambiguity — "not chosen" means "not <em>known</em>
/// chosen". A retried change function must therefore be idempotent (or detect that its effect is already
/// present in the recovered value); a naive retry of a non-idempotent update can apply twice.
/// </para>
/// <para>
/// <see cref="AcceptedCount"/> is a breadth measurement and never a commitment witness: <see cref="IsChosen"/>
/// alone reports whether the change was chosen. Its purpose is the arming rule for a piggybacked next fast
/// ballot, which a host may only reuse when the accept carrying it reached at least the fast quorum. See
/// <see cref="FastProposer{TValue}.RecoverAsync"/>.
/// </para>
/// </remarks>
public sealed record ChangeOutcome<TValue>(bool IsChosen, TValue? Value, int AcceptedCount);
