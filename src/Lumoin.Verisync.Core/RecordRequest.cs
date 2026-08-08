using System;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A request that a recorder record a proposal at a step. It is Algorithm 4's <c>record(s, p)</c>, and it is
/// the only request the QuePaxa protocol has.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Step">The step the proposal is tagged with. Must be at least <see cref="RecorderStep.RoundOnePhaseZero"/>.</param>
/// <param name="Proposal">The proposal to record. Must not be <see langword="null"/> and must not carry <see cref="ProposalPriority.None"/>.</param>
/// <remarks>
/// <para>
/// THERE IS NO ABSTRACT BASE MESSAGE. The Fast CASPaxos family has
/// <see cref="ConsensusRequest{TValue}"/> because there are two request kinds to discriminate; QuePaxa has
/// one in each direction, so a base would exist only to be downcast.
/// </para>
/// <para>
/// VALIDATION HAPPENS AT CONSTRUCTION BECAUSE THIS TYPE IS THE WIRE BOUNDARY. <see cref="RecorderStep"/>
/// already refuses a value outside its own range, so the only illegal decoded step is one below round one
/// phase zero, and refusing it here means
/// <see cref="QuePaxaNode{TValue}.Handle(RecordRequest{TValue})"/> can trust its input and
/// <see cref="QuePaxaRecorder{TValue}.Record(RecorderStep, PrioritizedProposal{TValue})"/>'s own floor check
/// is a backstop rather than a live path. The absent priority is refused because it is the aggregate fold's
/// identity: it is never drawn and never sent, and a request carrying it would put the identity element on
/// the wire. <see cref="ProposalPrioritySourceDelegate"/> states that contract against the draw, and stating
/// it on the message makes it true of everything that reaches a recorder.
/// </para>
/// <para>
/// A RESERVED PRIORITY ABOVE ROUND ONE PHASE ZERO IS LEGAL AND MUST NOT BE VALIDATED AWAY. It looks wrong and
/// it is not: when the fast path fails, the phase-zero template becomes the best of the first proposals,
/// which may be the leader's own reserved-priority proposal, and phases one to three then send that template
/// untouched. The model does exactly this, because its <c>ProposalFor(j)</c> is the working template whenever
/// <c>randomizes</c> is false and that template can be the best of the gathered first proposals. A validator
/// refusing it would deadlock the protocol on its own most common contended path.
/// </para>
/// </remarks>
public sealed record RecordRequest<TValue>(RecorderStep Step, PrioritizedProposal<TValue> Proposal)
{
    /// <summary>
    /// The step the proposal is tagged with. It is validated on construction and on a <c>with</c> expression
    /// alike, because the initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the step is below <see cref="RecorderStep.RoundOnePhaseZero"/>.</exception>
    public RecorderStep Step { get; init { field = ValidateStep(value); } } = ValidateStep(Step);


    /// <summary>
    /// The proposal to record. It is validated on construction and on a <c>with</c> expression alike, for the
    /// same reason the step is.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown if the proposal is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the proposal carries <see cref="ProposalPriority.None"/>.</exception>
    public PrioritizedProposal<TValue> Proposal { get; init { field = ValidateProposal(value); } } = ValidateProposal(Proposal);


    private static RecorderStep ValidateStep(RecorderStep value)
    {
        //The parameter name is stated rather than inferred, because the caller sees a step and not the
        //validator's own parameter, and an exception naming "value" would send a reader to the wrong place.
        ArgumentOutOfRangeException.ThrowIfLessThan(value, RecorderStep.RoundOnePhaseZero, nameof(Step));

        return value;
    }


    [SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly", Justification = "The caller sees a proposal member and not the validator's own parameter, so the exception names the member; an exception naming \"value\" would send a reader to the wrong place.")]
    private static PrioritizedProposal<TValue> ValidateProposal(PrioritizedProposal<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value, nameof(Proposal));

        if(value.Key.Priority.IsNone)
        {
            throw new ArgumentException("A record request must not carry the absent priority; it is the aggregate fold's identity and is neither drawn nor sent.", nameof(Proposal));
        }

        return value;
    }
}
