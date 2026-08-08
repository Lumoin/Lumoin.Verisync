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
/// There is no rejection field. Every request a recorder receives is served: a reserved-priority claim from a
/// proposer that is not the configured leader is recorded at the lowest ordinary priority and the round
/// proceeds through the ordinary phases. Serving every request is what keeps the register free of holes and
/// the protocol free of deadlocks.
/// </para>
/// <para>
/// <paramref name="First"/> is non-nullable and <paramref name="Step"/> is validated, and the state that
/// would need the null is unreachable. <see cref="QuePaxaNode{TValue}.Handle(RecordRequest{TValue})"/> calls
/// <see cref="QuePaxaRecorder{TValue}.Record(RecorderStep, PrioritizedProposal{TValue})"/>, which refuses any
/// step below round one phase zero; an initial register sits at step zero, so the first request it ever takes
/// lands on the advancing branch and sets the first proposal, and every later branch either leaves it alone
/// or advances and resets it. Validating here keeps
/// <see cref="QuePaxaRound{TValue}.Conclude"/>'s corrupt-state check a backstop rather than the place a
/// malformed decoded message aborts a proposal. <paramref name="PriorAggregate"/> stays nullable, because a
/// skipped step legitimately clears it.
/// </para>
/// <para>
/// A lying recorder is out of scope and no bound here could help. A reply carrying
/// <see cref="RecorderStep.MaxValue"/> makes any proposer catch up to the last representable step and report
/// an exhausted budget on its next step. QuePaxa's model assumes crash faults rather than Byzantine ones, and
/// a recorder that fabricates steps can stall a proposer whatever this type validates.
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
