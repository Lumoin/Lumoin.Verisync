using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The constant-space interval summary register of the concrete QuePaxa protocol: it holds a step, the first
/// proposal recorded at that step, the aggregate accumulating at that step, and the aggregate carried from
/// the step immediately below.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The register is an immutable value: <see cref="Record(RecorderStep, PrioritizedProposal{TValue})"/> returns
/// a new register alongside the summary it answers with. Its space is constant no matter how many proposals it
/// folds, and for that reason it cannot police the key uniqueness contract described on
/// <see cref="ProposalKey"/>.
/// </para>
/// <para>
/// A register driven through <see cref="QuePaxaRecorder{TValue}"/> and standing above
/// <see cref="RecorderStep.Zero"/> always has a non-null <see cref="First"/>. The initial register sits at
/// step zero and the recorder refuses any request below round one phase zero, so the first record such a
/// register ever takes lands on the advancing branch and sets <see cref="First"/>; the same-step branch can
/// then never see a null. This type itself imposes no floor, because Algorithm 3 has none: recorded at step
/// zero directly it will fold an aggregate while <see cref="First"/> stays null, which is faithful to the
/// algorithm and outside every state the protocol reaches. The obligation to reject a corrupt restored
/// snapshot belongs to <see cref="QuePaxaRecorder{TValue}.FromState"/>, which owns the relational rules a
/// recorder-driven register satisfies; this type's own <see cref="FromState"/> is internal and rebuilds
/// whatever it is handed.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class IntervalSummaryRegister<TValue>
{
    private IntervalSummaryRegister(RecorderStep step, PrioritizedProposal<TValue>? first, PrioritizedProposal<TValue>? currentAggregate, PrioritizedProposal<TValue>? priorAggregate)
    {
        Step = step;
        First = first;
        CurrentAggregate = currentAggregate;
        PriorAggregate = priorAggregate;
    }


    /// <summary>A register that was never written: step zero, and no proposal in any of the three slots.</summary>
    public static IntervalSummaryRegister<TValue> Initial { get; } = new(RecorderStep.Zero, null, null, null);


    /// <summary>The step this register currently holds. It only ever runs forward.</summary>
    public RecorderStep Step { get; }

    /// <summary>The first proposal recorded at <see cref="Step"/>, or <see langword="null"/> for a register that was never written.</summary>
    public PrioritizedProposal<TValue>? First { get; }

    /// <summary>The aggregate accumulating at <see cref="Step"/>, which is the greatest-keyed proposal recorded there.</summary>
    public PrioritizedProposal<TValue>? CurrentAggregate { get; }

    /// <summary>The aggregate carried from the step immediately below <see cref="Step"/>, or <see langword="null"/> when that step was skipped.</summary>
    public PrioritizedProposal<TValue>? PriorAggregate { get; }


    /// <summary>
    /// Records <paramref name="proposal"/> at <paramref name="step"/> and answers with the summary a proposer
    /// reads.
    /// </summary>
    /// <param name="step">The step the proposal is tagged with.</param>
    /// <param name="proposal">The proposal to record.</param>
    /// <returns>The register after the record and the summary taken from it afterwards.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="proposal"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The three cases follow Algorithm 3. At the register's own step the current aggregate folds the proposal
    /// in and <see cref="First"/> is not touched, because the first-proposal field is assigned only on the
    /// advancing branch. Above the register's step the prior aggregate takes the current one when the advance
    /// is by exactly one and is cleared otherwise, which is the skipped-step rule, and then the step, the
    /// first proposal and the current aggregate all take the incoming proposal. Below the register's step
    /// nothing changes, because a value tagged below the current step is obsolete, while the register still
    /// answers with its summary.
    /// </para>
    /// <para>
    /// The same instance is returned exactly when no field would have changed, which holds in two cases: a
    /// record below the register's step, which writes nothing at all, and a same-step record the fold keeps
    /// the incumbent against, which is what an identical re-delivery of one request produces. Reference
    /// identity is therefore an exact "the state changed" test for every layer above, which lets
    /// <see cref="QuePaxaRecorder{TValue}"/> carry the fact upward and lets <see cref="QuePaxaNode{TValue}"/>
    /// decide from it whether a reply needs an <c>fsync</c> before it escapes. An identical same-step record
    /// is the common case on a lossy link, so allocating there would make every retransmission pay for
    /// durability that makes nothing durable.
    /// </para>
    /// <para>
    /// The fold keeps the incumbent on an exact key tie. It is therefore order-independent under
    /// <see cref="ProposalKey"/>'s uniqueness contract, and order-dependent without it. That is also what
    /// makes an identical re-delivery the identity here: the first delivery already folded the proposal in, so
    /// the second finds an aggregate that is at least as great and keeps it.
    /// </para>
    /// </remarks>
    public (IntervalSummaryRegister<TValue> Register, RecordSummary<TValue> Summary) Record(RecorderStep step, PrioritizedProposal<TValue> proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if(step < Step)
        {
            return (this, new RecordSummary<TValue>(Step, First, PriorAggregate));
        }

        if(step == Step)
        {
            PrioritizedProposal<TValue> aggregate = Best(CurrentAggregate, proposal);
            if(ReferenceEquals(aggregate, CurrentAggregate))
            {
                return (this, new RecordSummary<TValue>(Step, First, PriorAggregate));
            }

            var folded = new IntervalSummaryRegister<TValue>(Step, First, aggregate, PriorAggregate);

            return (folded, new RecordSummary<TValue>(folded.Step, folded.First, folded.PriorAggregate));
        }

        PrioritizedProposal<TValue>? carried = step.IsNextAfter(Step) ? CurrentAggregate : null;
        var advanced = new IntervalSummaryRegister<TValue>(step, proposal, proposal, carried);

        return (advanced, new RecordSummary<TValue>(advanced.Step, advanced.First, advanced.PriorAggregate));
    }


    /// <summary>Snapshots the register's four fields for persistence.</summary>
    /// <returns>The register's state.</returns>
    /// <remarks>
    /// No copy is taken, because every field is immutable and no later record on this register can reach the
    /// returned state.
    /// </remarks>
    internal QuePaxaRecorderState<TValue> ToState()
    {
        return new QuePaxaRecorderState<TValue>(Step, First, CurrentAggregate, PriorAggregate);
    }


    /// <summary>
    /// Rebuilds a register from <paramref name="state"/> field for field, performing no relational validation
    /// of any kind.
    /// </summary>
    /// <param name="state">The state to rebuild from.</param>
    /// <returns>The register the state names.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// This is the unvalidated primitive the validated restore in
    /// <see cref="QuePaxaRecorder{TValue}.FromState"/> is built on, and it accepts every combination the state
    /// record can express — including the combinations Algorithm 3 permits and the protocol never reaches,
    /// such as a step above <see cref="RecorderStep.Zero"/> with a null <see cref="First"/>. The relational
    /// rules are true of a register driven through <see cref="QuePaxaRecorder{TValue}"/> and its step floor,
    /// not of this type, so restating them here would refuse states this type legitimately holds.
    /// </para>
    /// <para>
    /// The pair is internal for that reason: a public register restore would be an unvalidated bypass of every
    /// rule the recorder owns, which is the corrupt-snapshot obligation left unowned again. The state record is
    /// named for the recorder because the recorder is the type that owns it, as
    /// <see cref="RaftNodeState{TCommand}"/> belongs to <see cref="RaftNode{TCommand}"/>.
    /// </para>
    /// </remarks>
    internal static IntervalSummaryRegister<TValue> FromState(QuePaxaRecorderState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new IntervalSummaryRegister<TValue>(state.Step, state.First, state.CurrentAggregate, state.PriorAggregate);
    }


    private static PrioritizedProposal<TValue> Best(PrioritizedProposal<TValue>? left, PrioritizedProposal<TValue> right)
    {
        return left is not null && left.Key >= right.Key ? left : right;
    }


    private string DebuggerDisplay => $"IntervalSummaryRegister: step {Step.Value}, first {First?.Key.ToString() ?? "nil"}, aggregate {CurrentAggregate?.Key.ToString() ?? "nil"}";
}
