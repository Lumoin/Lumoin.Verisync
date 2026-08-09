using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A QuePaxa recorder: one consensus instance's safety state, which is an interval summary register plus the
/// leader identity the instance is configured with.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// A recorder is an immutable value in the same idiom as <see cref="FastAcceptor{TValue}"/>:
/// <see cref="Record(RecorderStep, PrioritizedProposal{TValue})"/> returns a new recorder alongside the
/// summary it answers with. It holds one instance; indexing recorders by instance belongs with the stateful
/// node above the core, because an instance index carries a lifetime and a resource bound the pure core
/// cannot enforce.
/// </para>
/// <para>
/// The downgrade rule obliges the deployment to keep the recorders in agreement on
/// <see cref="ConfiguredLeader"/>, because recorders that honour different leaders admit two reserved
/// claims at the step the fast path reads, which is the reserved-priority divergence hazard. The agreement
/// needs no message: a deterministic function of committed state such as
/// <see cref="HedgingSchedule.RotateTo(ReplicaId)"/> over the previously committed writer lets every
/// recorder derive the same leader for the next version independently.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class QuePaxaRecorder<TValue>
{
    private QuePaxaRecorder(ProposerLane? configuredLeader, IntervalSummaryRegister<TValue> register)
    {
        ConfiguredLeader = configuredLeader;
        Register = register;
    }


    /// <summary>
    /// A recorder with no configured leader, whose register was never written. Every reserved claim reaching
    /// it at the round's first step is declined.
    /// </summary>
    public static QuePaxaRecorder<TValue> Leaderless { get; } = new(null, IntervalSummaryRegister<TValue>.Initial);


    /// <summary>Creates a recorder configured with <paramref name="leader"/>, whose register was never written.</summary>
    /// <param name="leader">The lane whose reserved-priority claims this recorder honours.</param>
    /// <returns>A new recorder.</returns>
    /// <remarks>
    /// The leader is a lane rather than a replica, because two lanes of one replica each claiming the
    /// reserved priority would reproduce the divergence hazard from inside that replica.
    /// </remarks>
    public static QuePaxaRecorder<TValue> LedBy(ProposerLane leader) => new(leader, IntervalSummaryRegister<TValue>.Initial);


    /// <summary>The lane whose reserved-priority claims this recorder honours, or <see langword="null"/> when it is leaderless.</summary>
    public ProposerLane? ConfiguredLeader { get; }

    /// <summary>The interval summary register holding this instance's recorded proposals.</summary>
    public IntervalSummaryRegister<TValue> Register { get; }

    /// <summary>The step the register holds. It forwards rather than duplicating, so the pair cannot drift.</summary>
    public RecorderStep Step => Register.Step;


    /// <summary>
    /// Records <paramref name="proposal"/> at <paramref name="step"/>, downgrading a reserved-priority claim
    /// that did not come from <see cref="ConfiguredLeader"/> when the step is
    /// <see cref="RecorderStep.RoundOnePhaseZero"/>, and answers with the summary a proposer reads.
    /// </summary>
    /// <param name="step">The step the proposal is tagged with. Must be at least <see cref="RecorderStep.RoundOnePhaseZero"/>.</param>
    /// <param name="proposal">The proposal to record.</param>
    /// <returns>The recorder after the record and the summary. The same instance is returned exactly when the record changed nothing.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="step"/> is below <see cref="RecorderStep.RoundOnePhaseZero"/>.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="proposal"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The upper bound on the step needs no check here, because <see cref="RecorderStep"/> cannot represent a
    /// step above its own maximum.
    /// </para>
    /// <para>
    /// The rule applies at <see cref="RecorderStep.RoundOnePhaseZero"/> alone and a reserved priority
    /// arriving above that step is recorded verbatim, whoever owns it. The reserved priority earns a
    /// decision only where the fast path reads it, which is that one step, so rewriting it higher up
    /// defends nothing and costs what every rewrite costs: a second key for one logical proposal. A
    /// proposal carried through the ordinary phases is therefore recorded identically at a recorder that
    /// honours the leader and at one that does not.
    /// </para>
    /// <para>
    /// A declined reserved claim is downgraded, not dropped: the proposal is recorded at the lowest ordinary
    /// priority and the round proceeds through the ordinary phases, which keeps the register free of holes
    /// and the protocol free of deadlocks. Dropping would leave a recorder with no first proposal at a step a
    /// proposer is about to read. The leaderless case is included: with no configured leader, every reserved
    /// claim reaching that step is declined.
    /// </para>
    /// </remarks>
    public (QuePaxaRecorder<TValue> Recorder, RecordSummary<TValue> Summary) Record(RecorderStep step, PrioritizedProposal<TValue> proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, RecorderStep.RoundOnePhaseZero);

        PrioritizedProposal<TValue> recorded = step == RecorderStep.RoundOnePhaseZero
            && proposal.Key.Priority.IsReserved
            && ConfiguredLeader != proposal.Key.Owner
            ? proposal.WithPriority(ProposalPriority.Lowest)
            : proposal;

        (IntervalSummaryRegister<TValue> register, RecordSummary<TValue> summary) = Register.Record(step, recorded);

        //The register returns its own instance exactly when no field would have changed, of which a stale
        //record is one case and an identical same-step fold is the other, so identity carries "the state did
        //not change" through without a second comparison that could drift from it. A layer above that
        //persists on a change therefore skips a retransmission that made nothing durable.
        QuePaxaRecorder<TValue> recorder = ReferenceEquals(register, Register)
            ? this
            : new QuePaxaRecorder<TValue>(ConfiguredLeader, register);

        return (recorder, summary);
    }


    private string DebuggerDisplay => $"QuePaxaRecorder: step {Step.Value}, leader {ConfiguredLeader?.ToString() ?? "none"}";
}
