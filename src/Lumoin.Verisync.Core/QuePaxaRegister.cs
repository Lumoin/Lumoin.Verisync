using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A QuePaxa register: a synchronous in-memory model of the concrete protocol's safety core, holding the
/// recorders of one consensus instance and driving a proposer through the four phases of a round.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The register is an immutable value in the same idiom as <see cref="FastCasPaxosRegister{TValue}"/>:
/// every step returns a new register alongside its outcome. Because it is synchronous, a caller drives one
/// protocol step at a time against a chosen subset of recorders and can therefore interleave two proposers
/// in an arbitrary order.
/// </para>
/// <para>
/// The protocol rules do not live here. This type is one of two drivers over
/// <see cref="QuePaxaRound{TValue}"/>: it decides which recorders a step reaches and records against them in
/// memory, and <see cref="QuePaxaRound{TValue}.NextSend"/> and <see cref="QuePaxaRound{TValue}.Conclude"/>
/// supply what a send carries and what a majority of answers concludes.
/// <see cref="QuePaxaProposer{TValue}"/> is the other driver and runs the identical rules over a transport.
/// </para>
/// <para>
/// Two defences against the reserved-priority divergence hazard are implemented and there is no third. The
/// whole-proposal identical test lives in <see cref="QuePaxaRound{TValue}.Conclude"/>'s phase zero and is
/// defence in depth. The downgrade lives in
/// <see cref="QuePaxaRecorder{TValue}.Record(RecorderStep, PrioritizedProposal{TValue})"/> and is the one
/// that carries safety on its own. The proposer must not check the winning proposal's owner against its own
/// believed leader: such a check is harmful, because it fires exactly when a proposer's belief differs from
/// the recorders' configuration, which is a state a correct deployment reaches constantly, and refusing the
/// decision there steps outside the behaviour safety was established for.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class QuePaxaRegister<TValue>
{
    private QuePaxaRegister(ImmutableArray<QuePaxaRecorder<TValue>> recorders)
    {
        Recorders = recorders;
    }


    /// <summary>Creates a register of <paramref name="recorderCount"/> leaderless recorders.</summary>
    /// <param name="recorderCount">The number of recorders.</param>
    /// <returns>A new register.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="recorderCount"/> is less than one.</exception>
    public static QuePaxaRegister<TValue> WithRecorders(int recorderCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recorderCount, 1);

        ImmutableArray<QuePaxaRecorder<TValue>>.Builder builder = ImmutableArray.CreateBuilder<QuePaxaRecorder<TValue>>(recorderCount);
        for(int i = 0; i < recorderCount; i++)
        {
            builder.Add(QuePaxaRecorder<TValue>.Leaderless);
        }

        return new QuePaxaRegister<TValue>(builder.ToImmutable());
    }


    /// <summary>Creates a register of <paramref name="recorderCount"/> recorders all configured with <paramref name="leader"/>.</summary>
    /// <param name="recorderCount">The number of recorders.</param>
    /// <param name="leader">The lane whose reserved-priority claims every recorder honours.</param>
    /// <returns>A new register.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="recorderCount"/> is less than one.</exception>
    /// <remarks>
    /// Agreement on the leader is the deployment obligation the downgrade rule places on the host; recorders
    /// honouring different leaders reproduce the divergence hazard.
    /// </remarks>
    public static QuePaxaRegister<TValue> LedBy(int recorderCount, ProposerLane leader)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recorderCount, 1);

        ImmutableArray<QuePaxaRecorder<TValue>>.Builder builder = ImmutableArray.CreateBuilder<QuePaxaRecorder<TValue>>(recorderCount);
        for(int i = 0; i < recorderCount; i++)
        {
            builder.Add(QuePaxaRecorder<TValue>.LedBy(leader));
        }

        return new QuePaxaRegister<TValue>(builder.ToImmutable());
    }


    /// <summary>Creates a register over recorders given as they are.</summary>
    /// <param name="recorders">The recorders, in index order.</param>
    /// <returns>A new register.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="recorders"/> is default or empty.</exception>
    /// <remarks>
    /// This lets a caller build a register whose recorders are configured with different leaders, which is
    /// the misconfiguration the downgrade rule fails under.
    /// </remarks>
    public static QuePaxaRegister<TValue> FromRecorders(ImmutableArray<QuePaxaRecorder<TValue>> recorders)
    {
        if(recorders.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A QuePaxa register requires at least one recorder.", nameof(recorders));
        }

        return new QuePaxaRegister<TValue>(recorders);
    }


    /// <summary>The recorders of this instance, in index order.</summary>
    public ImmutableArray<QuePaxaRecorder<TValue>> Recorders { get; }

    /// <summary>The number of recorders.</summary>
    public int RecorderCount => Recorders.Length;

    /// <summary>The quorum size, which is a strict majority.</summary>
    /// <remarks>
    /// A strict majority is what the proofs need: Lemma B.4 asks for sets exceeding half the replicas, and
    /// Lemmas C.5 and C.6 discharge the two threshold-broadcast properties from majority quorums. A
    /// deployment sized above the minimum may use larger quorums and stays safe; it is never required to.
    /// </remarks>
    public int Quorum => (Recorders.Length / 2) + 1;


    /// <summary>
    /// Runs one protocol step of <paramref name="round"/> against the recorders at
    /// <paramref name="recorderIndices"/>.
    /// </summary>
    /// <param name="round">The proposer's current round.</param>
    /// <param name="recorderIndices">
    /// The indices of the recorders this step reaches, in the order it reaches them. An empty but
    /// non-default array is legal and falls through to a missed quorum; it models a step whose every message
    /// was lost, which is a state the protocol must tolerate.
    /// </param>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <returns>The register after the step and the step's outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="round"/> or <paramref name="drawPriority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="recorderIndices"/> is default, contains an index outside the recorder range,
    /// or contains the same index twice.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <paramref name="drawPriority"/> returns a priority that is not ordinary, or if a recorder
    /// above step zero answered with no first proposal.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A duplicate index is refused because it would double-count toward the quorum.
    /// </para>
    /// <para>
    /// The draw is per recorder, not per step. Phase zero redraws the priority for each recorder, and a
    /// single draw shared across recorders would collapse the independence the liveness argument rests on.
    /// The iteration order is part of the contract, because it is what makes a seeded priority source
    /// reproduce a run exactly.
    /// </para>
    /// <para>
    /// Every reached recorder has recorded, whatever happens next. A message that arrives is recorded even
    /// when the proposer never assembles a quorum, which is the behaviour Lemma C.5's second case turns on,
    /// and the register update is kept even for a request the proposer has already stepped past. A caller
    /// that discarded the mutated recorders on a missed quorum would break that.
    /// </para>
    /// <para>
    /// A round value is stepped at most once. Stepping one round value twice is outside the supported
    /// behaviour: a phase-zero re-step redraws priorities, so one proposer would hold two distinct keys at
    /// one step, with the recorders it reached twice folding both and the recorders it reached once holding
    /// the stale draw.
    /// </para>
    /// </remarks>
    public (QuePaxaRegister<TValue> Register, QuePaxaStepOutcome<TValue> Outcome) Step(
        QuePaxaRound<TValue> round,
        ImmutableArray<int> recorderIndices,
        ProposalPrioritySourceDelegate drawPriority)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(drawPriority);

        if(recorderIndices.IsDefault)
        {
            throw new ArgumentException("A step requires an initialized array of recorder indices.", nameof(recorderIndices));
        }

        for(int i = 0; i < recorderIndices.Length; i++)
        {
            int index = recorderIndices[i];
            if(index < 0 || index >= Recorders.Length)
            {
                throw new ArgumentException($"Recorder index {index} is out of range.", nameof(recorderIndices));
            }

            for(int j = i + 1; j < recorderIndices.Length; j++)
            {
                if(recorderIndices[j] == index)
                {
                    throw new ArgumentException($"Recorder index {index} appears twice, and a duplicate would double-count toward the quorum.", nameof(recorderIndices));
                }
            }
        }

        ImmutableArray<QuePaxaRecorder<TValue>>.Builder working = Recorders.ToBuilder();
        ImmutableArray<RecorderAnswer<TValue>>.Builder answers = ImmutableArray.CreateBuilder<RecorderAnswer<TValue>>(recorderIndices.Length);
        foreach(int index in recorderIndices)
        {
            PrioritizedProposal<TValue> sent = round.NextSend(drawPriority);
            (QuePaxaRecorder<TValue> recorder, RecordSummary<TValue> summary) = working[index].Record(round.Step, sent);
            working[index] = recorder;
            answers.Add(new RecorderAnswer<TValue>(index, summary));
        }

        var stepped = new QuePaxaRegister<TValue>(working.ToImmutable());

        return (stepped, round.Conclude(answers.ToImmutable(), Recorders.Length));
    }


    /// <summary>
    /// Drives a proposal against every recorder until it decides or the round can no longer make progress.
    /// </summary>
    /// <param name="proposer">The lane to propose on.</param>
    /// <param name="believedLeader">The lane this proposer believes leads round one, or <see langword="null"/> for none.</param>
    /// <param name="value">The value to propose.</param>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <returns>The register after the attempt and the outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="drawPriority"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <paramref name="drawPriority"/> returns a priority that is not ordinary, or if a recorder
    /// above step zero answered with no first proposal, which both propagate from the underlying step.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Termination is unconditional: advancing raises the step by exactly one and catching up raises it to a
    /// greater one, so the last representable step bounds the loop. Because every step reaches every
    /// recorder, a missed quorum never arises.
    /// </para>
    /// <para>
    /// With no contention this decides at the first step when the caller is the configured leader and
    /// believes it, and at the third step otherwise, which is one round trip and one round respectively. It
    /// demonstrates nothing about contention, because a single synchronous proposer never observes a split;
    /// stepping a round against chosen recorder subsets is what covers that.
    /// </para>
    /// </remarks>
    public (QuePaxaRegister<TValue> Register, QuePaxaOutcome<TValue> Outcome) Propose(
        ProposerLane proposer,
        ProposerLane? believedLeader,
        TValue value,
        ProposalPrioritySourceDelegate drawPriority)
    {
        ArgumentNullException.ThrowIfNull(drawPriority);

        ImmutableArray<int> everyRecorder = Enumerable.Range(0, Recorders.Length).ToImmutableArray();
        QuePaxaRegister<TValue> register = this;
        QuePaxaRound<TValue> round = QuePaxaRound<TValue>.Begin(proposer, believedLeader, value);
        int steps = 0;
        while(true)
        {
            (QuePaxaRegister<TValue> stepped, QuePaxaStepOutcome<TValue> outcome) = register.Step(round, everyRecorder, drawPriority);
            register = stepped;
            steps++;

            if(outcome.Kind == QuePaxaStepKind.Decided)
            {
                return (register, new QuePaxaOutcome<TValue>(true, outcome.DecidedValue, outcome.DecidedBy, outcome.DecidedAt, steps));
            }

            //A successor round exists exactly for the advanced and caught-up kinds, so its absence is the
            //loop's terminal condition and needs no second test against the kind.
            if(outcome.Next is null)
            {
                return (register, new QuePaxaOutcome<TValue>(false, default, null, RecorderStep.Zero, steps));
            }

            round = outcome.Next;
        }
    }


    private string DebuggerDisplay => $"QuePaxaRegister: {Recorders.Length} recorders, quorum {Quorum}";
}
