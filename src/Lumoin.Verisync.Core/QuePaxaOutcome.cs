namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of driving a QuePaxa proposal to a conclusion.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="IsDecided"><see langword="true"/> when a value was decided.</param>
/// <param name="Value">The decided value when <paramref name="IsDecided"/> is <see langword="true"/>; otherwise the default.</param>
/// <param name="DecidedBy">
/// The OWNER OF THE DECIDED PROPOSAL, which is not necessarily the proposer that drove this attempt.
/// </param>
/// <param name="DecidedAt">
/// The step the decision was taken at, or <see cref="RecorderStep.Zero"/> when nothing was decided. Zero is a
/// step no request ever carries, so it cannot be mistaken for a real decision step.
/// </param>
/// <param name="Steps">The number of protocol steps taken.</param>
/// <remarks>
/// <para>
/// A decided outcome whose owner is not the caller's own lane means someone else's value was chosen; the
/// caller must re-read and re-propose rather than treat its own value as committed. That distinction is why
/// this outcome type exists instead of the CASPaxos change outcome being reused.
/// </para>
/// <para>
/// An undecided outcome is not evidence that the proposer's value was not chosen. Every recorder a step
/// reached still recorded, so the proposal may be carried by another proposer and decided later. Not decided
/// means not KNOWN decided.
/// </para>
/// </remarks>
public sealed record QuePaxaOutcome<TValue>(
    bool IsDecided,
    TValue? Value,
    ProposerLane? DecidedBy,
    RecorderStep DecidedAt,
    int Steps);
