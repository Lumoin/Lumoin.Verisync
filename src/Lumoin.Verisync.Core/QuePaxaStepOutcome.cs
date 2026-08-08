namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of one QuePaxa protocol step.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Kind">What the step produced.</param>
/// <param name="Next">
/// The round to step next. It is non-null for <see cref="QuePaxaStepKind.Advanced"/> and
/// <see cref="QuePaxaStepKind.CaughtUp"/>, and <see langword="null"/> otherwise.
/// </param>
/// <param name="DecidedValue">The decided value; meaningful only for <see cref="QuePaxaStepKind.Decided"/>.</param>
/// <param name="DecidedBy">
/// The owner of the decided proposal, which is not necessarily the proposer that observed the decision and
/// is the same lane whenever an uncontended attempt carries its own value through; meaningful only
/// for <see cref="QuePaxaStepKind.Decided"/>.
/// </param>
/// <param name="DecidedAt">
/// The step the decision was taken at, or <see cref="RecorderStep.Zero"/> when the step did not decide. Zero
/// is a step no request ever carries, so it cannot be mistaken for a real decision step.
/// </param>
/// <param name="SummaryCount">The number of recorders that answered.</param>
/// <remarks>
/// <para>
/// <paramref name="SummaryCount"/> is a breadth measurement and never a decision witness.
/// <paramref name="Kind"/> alone reports what happened.
/// </para>
/// <para>
/// <paramref name="DecidedBy"/> is the field a caller reads to learn whose value won. A decided outcome whose
/// owner is not the caller's own lane means someone else's value was chosen, and the caller must re-read and
/// re-propose rather than treat its own value as committed.
/// </para>
/// <para>
/// A missed quorum is not evidence that the proposer's value was not chosen. Every recorder the step reached
/// still recorded, so that proposal may be carried by another proposer and decided later. Not decided means
/// not known decided.
/// </para>
/// </remarks>
public sealed record QuePaxaStepOutcome<TValue>(
    QuePaxaStepKind Kind,
    QuePaxaRound<TValue>? Next,
    TValue? DecidedValue,
    ProposerLane? DecidedBy,
    RecorderStep DecidedAt,
    int SummaryCount);
