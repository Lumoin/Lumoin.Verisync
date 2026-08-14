namespace Lumoin.Verisync.Core;

/// <summary>
/// The durable state of a <see cref="QuePaxaRecorder{TValue}"/>: the four fields of the interval summary
/// register a recorder must have on stable storage before any reply that depends on them leaves the process.
/// Obtain it with <see cref="QuePaxaRecorder{TValue}.ToState"/> and reconstruct with
/// <see cref="QuePaxaRecorder{TValue}.FromState"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Step">The step the register holds. It only ever runs forward.</param>
/// <param name="First">
/// The first proposal recorded at <paramref name="Step"/>, or <see langword="null"/> for a register that was
/// never written.
/// </param>
/// <param name="CurrentAggregate">
/// The aggregate accumulating at <paramref name="Step"/>, which is the greatest-keyed proposal recorded there.
/// </param>
/// <param name="PriorAggregate">
/// The aggregate carried from the step immediately below <paramref name="Step"/>, or <see langword="null"/>
/// when that step was skipped.
/// </param>
/// <remarks>
/// <para>
/// These four fields are exactly the state of <see cref="IntervalSummaryRegister{TValue}"/> and nothing else.
/// All four are durable and not the step and the first proposal alone: a reply carries the prior aggregate,
/// which the proposer's phase two and phase three decisions read, and the current aggregate is what an advance
/// by one step carries down as the next step's prior aggregate, so a host that persisted less would lose a
/// field a proposer has already acted on or is about to be answered with.
/// </para>
/// <para>
/// <see cref="QuePaxaRecorder{TValue}.ConfiguredLeader"/> is deliberately absent. It is configuration a
/// deployment derives from committed state rather than protocol state the register accumulates, and it is
/// passed to <see cref="QuePaxaRecorder{TValue}.FromState"/> separately, as identity and membership are passed
/// to <see cref="RaftNode{TCommand}.FromState"/> beside <see cref="RaftNodeState{TCommand}"/>. Keeping the
/// leader an argument is also what lets the restore check a restored reserved-priority claim against the
/// leader the instance runs under.
/// </para>
/// <para>
/// The persist-before-reply obligation is what makes this durable state rather than a convenience. The fast
/// path rests on Lemma C.10's argument that the first proposal of a step is never overwritten, so a recorder
/// that restarted at <see cref="RecorderStep.Zero"/> would take a fresh first proposal at a step whose
/// original first proposal a proposer has already read.
/// <see cref="PersistRecorderDelegate{TValue}"/> is the hook that sequences the write ahead of the reply.
/// </para>
/// </remarks>
public sealed record QuePaxaRecorderState<TValue>(
    RecorderStep Step,
    PrioritizedProposal<TValue>? First,
    PrioritizedProposal<TValue>? CurrentAggregate,
    PrioritizedProposal<TValue>? PriorAggregate);
