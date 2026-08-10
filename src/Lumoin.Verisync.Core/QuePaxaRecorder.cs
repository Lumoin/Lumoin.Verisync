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


    /// <summary>
    /// Snapshots the recorder's durable state, which is the four fields of <see cref="Register"/>.
    /// </summary>
    /// <returns>The durable state to make stable before any dependent reply is sent.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="ConfiguredLeader"/> is not part of the snapshot, because it is configuration a deployment
    /// derives from committed state and supplies again to <see cref="FromState"/>. No copy is taken, because
    /// every field of the state is immutable.
    /// </para>
    /// <para>
    /// This is deliberately not the inverse of <see cref="FromState"/> at the bottom of the range. An
    /// unwritten recorder snapshots at <see cref="RecorderStep.Zero"/> and <see cref="FromState"/> refuses
    /// that state, because a recorder returning at step zero is the crash the restore exists to prevent: it
    /// would take a fresh first proposal at a step a proposer has already read. Snapshotting has to succeed
    /// there anyway, since a host that persists unconditionally after every handled item would otherwise
    /// throw before it had recorded anything.
    /// </para>
    /// </remarks>
    public QuePaxaRecorderState<TValue> ToState() => Register.ToState();


    /// <summary>
    /// Reconstructs a recorder from durable <paramref name="state"/> under
    /// <paramref name="configuredLeader"/>, refusing fail-closed every state no recorder-driven register can
    /// hold.
    /// </summary>
    /// <param name="configuredLeader">
    /// The lane whose reserved-priority claims the restored recorder honours, or <see langword="null"/> when
    /// the instance is leaderless. It comes before the state because it is configuration rather than durable
    /// protocol state, as identity and membership come before the state in
    /// <see cref="RaftNode{TCommand}.FromState"/>. A host derives it from the record it has learned, through
    /// <see cref="QuePaxaLeaderSchedule.LeaderFor(ReplicaId?)"/>, and
    /// <see cref="QuePaxaLeaderSchedule.RecorderFor{TValue}(ReplicaId?, QuePaxaRecorderState{TValue})"/> does
    /// the derivation and the restore in one call so that the recorder and the proposer's belief cannot come
    /// from two different expressions. A null lane means the instance is deliberately leaderless and never
    /// that the leader is unknown: a state whose first proposal is ordinary restores under any lane, so a
    /// hand-wired one that does not match the derivation arrives silently and puts two leaders on one
    /// instance.
    /// </param>
    /// <param name="state">The durable state to restore.</param>
    /// <returns>A recorder standing at the restored step.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the durable state is impossible in a way only the whole state shows: a
    /// <see cref="QuePaxaRecorderState{TValue}.Step"/> below <see cref="RecorderStep.RoundOnePhaseZero"/>;
    /// a step above <see cref="RecorderStep.Zero"/> with no
    /// <see cref="QuePaxaRecorderState{TValue}.First"/>; a
    /// reserved priority at <see cref="RecorderStep.RoundOnePhaseZero"/> owned by a lane other than
    /// <paramref name="configuredLeader"/>, in either
    /// <see cref="QuePaxaRecorderState{TValue}.First"/> or
    /// <see cref="QuePaxaRecorderState{TValue}.CurrentAggregate"/>; a first proposal with no current aggregate;
    /// a current aggregate whose key orders below the first proposal's; a non-null
    /// <see cref="QuePaxaRecorderState{TValue}.PriorAggregate"/> at
    /// <see cref="RecorderStep.RoundOnePhaseZero"/>; or a
    /// <see cref="QuePaxaRecorderState{TValue}.PriorAggregate"/> one step above
    /// <see cref="RecorderStep.RoundOnePhaseZero"/> holding a reserved priority owned by a lane other than
    /// <paramref name="configuredLeader"/>. Everything a single value can be wrong about is refused
    /// before a state can be built at all: a step outside its range by <see cref="RecorderStep"/> and a
    /// negative lane by <see cref="ProposerLane"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The rules refuse a state no register driven through
    /// <see cref="Record(RecorderStep, PrioritizedProposal{TValue})"/> can hold, which is stronger than
    /// refusing a state <see cref="IntervalSummaryRegister{TValue}.Record"/> cannot produce. The difference is
    /// this type's step floor: a register recorded at <see cref="RecorderStep.Zero"/> directly folds an
    /// aggregate while its first proposal stays null, and a register advanced from step three to
    /// <see cref="RecorderStep.RoundOnePhaseZero"/> carries an aggregate down, while a recorder reaches step
    /// four only from step zero, which is a non-adjacent advance and clears the carry. Both are why the
    /// register's own restore is internal and unvalidated and this one is public and validated.
    /// </para>
    /// <para>
    /// The reserved-priority rule is confined to <see cref="RecorderStep.RoundOnePhaseZero"/>, because a
    /// reserved priority above that step is recorded verbatim from any owner and refusing it everywhere would
    /// reject states the library produces. It covers both proposal slots, because the downgrade happens
    /// upstream of the register's fold, so at that step the first proposal and the current aggregate are both
    /// drawn from the downgraded stream. The leaderless case needs no separate rule: with a null
    /// <paramref name="configuredLeader"/> the lifted comparison declines every owner.
    /// </para>
    /// <para>
    /// The downgraded stream reaches one step further through the carry, and one step only. A non-null
    /// <see cref="QuePaxaRecorderState{TValue}.PriorAggregate"/> one step above
    /// <see cref="RecorderStep.RoundOnePhaseZero"/> is that step's current aggregate brought down by an advance
    /// of exactly one, since every other advance clears the carry, so it inherits the same freedom from foreign
    /// reserved claims. Two steps above and higher the carry comes from a step that records a reserved priority
    /// verbatim, so the rule stops there.
    /// </para>
    /// <para>
    /// The rules read the state alone, and that bounds what they refuse. A per-field mix of two states a
    /// faithful host wrote can itself be a state a recorder-driven register can hold, so it restores under
    /// these rules and still contradicts a reply already sent from the older of its sources, the tuple the
    /// disk held, through a field the tear took from the newer. Detecting that
    /// needs history no snapshot carries, so the rules are not a substitute for the store landing the write
    /// whole, which is <see cref="PersistRecorderDelegate{TValue}"/>'s obligation.
    /// </para>
    /// <para>
    /// The restored recorder is durable by construction from the host's point of view, which is what
    /// <see cref="QuePaxaNode{TValue}"/> relies on when it treats the recorder it was constructed with as
    /// already persisted.
    /// </para>
    /// </remarks>
    public static QuePaxaRecorder<TValue> FromState(ProposerLane? configuredLeader, QuePaxaRecorderState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if(state.Step < RecorderStep.RoundOnePhaseZero)
        {
            throw new ArgumentException($"A restored step cannot stand below round one phase zero, got step {state.Step.Value}. A recorder records nothing below that step, and a snapshot returning at step zero is the crash this restore exists to prevent rather than a state to rebuild; an unwritten recorder comes from Leaderless or LedBy.", nameof(state));
        }

        if(state.Step > RecorderStep.Zero && state.First is null)
        {
            throw new ArgumentException($"A restored register above step zero must carry a first proposal, got step {state.Step.Value} with none.", nameof(state));
        }

        if(state.Step == RecorderStep.RoundOnePhaseZero && HoldsForeignReservedClaim(state.First, configuredLeader))
        {
            throw new ArgumentException("A restored first proposal at round one phase zero cannot hold the reserved priority for a lane other than the configured leader, because the recorder downgrades that claim before the register sees it.", nameof(state));
        }

        if(state.Step == RecorderStep.RoundOnePhaseZero && HoldsForeignReservedClaim(state.CurrentAggregate, configuredLeader))
        {
            throw new ArgumentException("A restored current aggregate at round one phase zero cannot hold the reserved priority for a lane other than the configured leader, because the downgrade runs upstream of the fold and both slots at that step come from the downgraded stream.", nameof(state));
        }

        if(state.First is not null && state.CurrentAggregate is null)
        {
            throw new ArgumentException("A restored register carrying a first proposal must carry a current aggregate, because the advancing branch sets both from one proposal and the fold only ever replaces the aggregate.", nameof(state));
        }

        if(state.First is not null && state.CurrentAggregate is not null && state.CurrentAggregate.Key < state.First.Key)
        {
            throw new ArgumentException("A restored current aggregate cannot order below the first proposal at the same step, because the fold keeps the greatest key recorded there.", nameof(state));
        }

        if(state.Step == RecorderStep.RoundOnePhaseZero && state.PriorAggregate is not null)
        {
            throw new ArgumentException("A restored register at round one phase zero cannot carry a prior aggregate, because a recorder reaches that step from step zero, which is a non-adjacent advance and clears the carry.", nameof(state));
        }

        if(state.Step.IsNextAfter(RecorderStep.RoundOnePhaseZero) && HoldsForeignReservedClaim(state.PriorAggregate, configuredLeader))
        {
            throw new ArgumentException("A restored prior aggregate one step above round one phase zero cannot hold the reserved priority for a lane other than the configured leader, because a non-null carry at that step is the aggregate an advance by exactly one brought down from round one phase zero, and that step's aggregate is drawn from the downgraded stream.", nameof(state));
        }

        return new QuePaxaRecorder<TValue>(configuredLeader, IntervalSummaryRegister<TValue>.FromState(state));
    }


    //The comparison against the owner is lifted exactly as the downgrade in Record lifts it, so a null
    //configured leader declines every owner and the leaderless case needs no branch of its own.
    private static bool HoldsForeignReservedClaim(PrioritizedProposal<TValue>? proposal, ProposerLane? configuredLeader)
    {
        return proposal is not null && proposal.Key.Priority.IsReserved && configuredLeader != proposal.Key.Owner;
    }


    private string DebuggerDisplay => $"QuePaxaRecorder: step {Step.Value}, leader {ConfiguredLeader?.ToString() ?? "none"}";
}
