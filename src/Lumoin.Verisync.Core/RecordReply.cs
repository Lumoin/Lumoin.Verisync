using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A recorder's reply to a <see cref="RecordRequest{TValue}"/>: the three fields Algorithm 3's <c>record</c>
/// returns.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Step">The recorder's step after the record. Must be at least <see cref="RecorderStep.RoundOnePhaseZero"/>.</param>
/// <param name="First">The first proposal recorded at <paramref name="Step"/>. Must not be <see langword="null"/>.</param>
/// <param name="PriorAggregate">
/// The aggregate accumulated at the step immediately below <paramref name="Step"/>, or
/// <see langword="null"/> when the recorder skipped that step.
/// </param>
/// <remarks>
/// <para>
/// THERE IS NO REJECTION FIELD, AND THAT ABSENCE IS THE PROTOCOL'S RESULT RATHER THAN AN OMISSION. Every
/// request a recorder receives is served: a reserved-priority claim from a proposer that is not the
/// configured leader is recorded at the lowest ordinary priority and the round proceeds through the ordinary
/// phases. The map, section 5, records that this is the paper's own phrasing and that it is what keeps the
/// register free of holes and the protocol free of deadlocks. A reviewer arriving from the Fast CASPaxos
/// family, where <see cref="AcceptReply{TValue}.Accepted"/> and <see cref="PrepareReply{TValue}.Promised"/>
/// are the load-bearing fields, will expect the symmetry and there is none to find.
/// </para>
/// <para>
/// IT RESTATES <see cref="RecordSummary{TValue}"/> RATHER THAN WRAPPING IT, which is the house precedent
/// already: <see cref="FastPrepareResponse{TValue}"/> is the acceptor's answer value and
/// <see cref="PrepareReply{TValue}"/> is the message carrying the same fields, and
/// <see cref="ConsensusNode{TValue}.Handle(ConsensusRequest{TValue})"/> translates one into the other. The
/// message is a wire type and the summary is a core type, and they are allowed to diverge.
/// </para>
/// <para>
/// <paramref name="First"/> IS NON-NULLABLE AND <paramref name="Step"/> IS VALIDATED, AND THE STATE THAT
/// WOULD NEED THE NULL IS UNREACHABLE. <see cref="QuePaxaNode{TValue}.Handle(RecordRequest{TValue})"/> calls
/// <see cref="QuePaxaRecorder{TValue}.Record(RecorderStep, PrioritizedProposal{TValue})"/>, which refuses any
/// step below round one phase zero; an initial register sits at step zero, so the first request it ever takes
/// lands on the advancing branch and sets the first proposal, and every later branch either leaves it alone
/// or advances and resets it. Validating here keeps
/// <see cref="QuePaxaRound{TValue}.Conclude"/>'s corrupt-state check a genuine backstop rather than the place
/// a malformed decoded message aborts a proposal. <paramref name="PriorAggregate"/> stays nullable, because a
/// skipped step legitimately clears it.
/// </para>
/// <para>
/// A LYING RECORDER IS OUT OF SCOPE AND NO BOUND HERE COULD HELP. A reply carrying
/// <see cref="RecorderStep.MaxValue"/> makes any proposer catch up to the last representable step and report
/// an exhausted budget on its next step, which is the step bound behaving as designed against a faulty
/// recorder. QuePaxa's model assumes crash faults rather than Byzantine ones, and a recorder that fabricates
/// steps can stall a proposer whatever this type validates.
/// </para>
/// </remarks>
public sealed record RecordReply<TValue>(RecorderStep Step, PrioritizedProposal<TValue> First, PrioritizedProposal<TValue>? PriorAggregate)
{
    /// <summary>
    /// The recorder's step after the record. It is validated on construction and on a <c>with</c> expression
    /// alike, because the initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the step is below <see cref="RecorderStep.RoundOnePhaseZero"/>.</exception>
    public RecorderStep Step { get; init { field = ValidateStep(value); } } = ValidateStep(Step);


    /// <summary>
    /// The first proposal recorded at <see cref="Step"/>. It is validated on construction and on a
    /// <c>with</c> expression alike, for the same reason the step is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the proposal is <see langword="null"/>.</exception>
    public PrioritizedProposal<TValue> First { get; init { field = ValidateFirst(value); } } = ValidateFirst(First);


    private static RecorderStep ValidateStep(RecorderStep value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a step and not the
        //validator's own parameter, and an exception naming "value" would send a reader to the wrong place.
        ArgumentOutOfRangeException.ThrowIfLessThan(value, RecorderStep.RoundOnePhaseZero, nameof(Step));

        return value;
    }


    private static PrioritizedProposal<TValue> ValidateFirst(PrioritizedProposal<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(First));

        return value;
    }
}
