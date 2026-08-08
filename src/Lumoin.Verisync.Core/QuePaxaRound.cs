using System;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One proposer's position in the concrete QuePaxa proposer loop: the working proposal template it is about
/// to send, and the step it is sending at.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Proposer">The lane this proposer proposes on.</param>
/// <param name="BelievedLeader">The lane this proposer believes leads round one, or <see langword="null"/> when it believes there is none.</param>
/// <param name="Step">The step the next send carries.</param>
/// <param name="Proposal">The working proposal template.</param>
/// <remarks>
/// <para>
/// <paramref name="BelievedLeader"/> is the proposer's own belief and nothing else. It acts through one
/// comparison only, against the proposer's own lane: <see cref="Begin"/> uses it to choose the template's
/// starting priority, and <see cref="ClaimsLeadership"/> reports it. That comparison decides whether the
/// first step claims the reserved priority or draws an ordinary one. It is never consulted on the fast path,
/// and it must never be read as the register's knowledge of who leads, because a real proposer talks to
/// remote recorders and cannot see what they are configured with.
/// </para>
/// <para>
/// Its job is liveness, not safety. A proposer that does not believe it leads must draw an ordinary priority
/// rather than claim the reserved one and be downgraded, because the downgrade lands every declined claim on
/// the same fixed lowest priority and would destroy the randomization the liveness argument depends on.
/// </para>
/// <para>
/// This type carries the whole of the protocol's step rules — <see cref="RedrawsPriority"/>,
/// <see cref="NextSend"/> and <see cref="Conclude"/> — so that the synchronous
/// <see cref="QuePaxaRegister{TValue}"/> and the asynchronous <see cref="QuePaxaProposer{TValue}"/> drive one
/// implementation rather than two.
/// </para>
/// <para>
/// A round value is stepped at most once, and the two drivers close that differently. The model enforces one
/// send per proposer per step through a flag cleared only by an action that decides or advances. This type is
/// an immutable value and cannot stop a caller stepping it twice on its own:
/// <see cref="QuePaxaProposer{TValue}.ProposeAsync"/> latches and a second call throws, while
/// <see cref="QuePaxaRegister{TValue}.Step"/> is public and closes it by written contract only, so stepping
/// one round value twice there is outside the checked behaviour. Re-delivering one identical request is
/// permitted; what is forbidden is a second send at one step under a fresh draw.
/// </para>
/// </remarks>
public sealed record QuePaxaRound<TValue>(ProposerLane Proposer, ProposerLane? BelievedLeader, RecorderStep Step, PrioritizedProposal<TValue> Proposal)
{
    /// <summary>
    /// Starts a round at <see cref="RecorderStep.RoundOnePhaseZero"/> with a template owned by
    /// <paramref name="proposer"/> and carrying <paramref name="value"/>.
    /// </summary>
    /// <param name="proposer">The lane this proposer proposes on.</param>
    /// <param name="believedLeader">The lane this proposer believes leads round one, or <see langword="null"/> for none.</param>
    /// <param name="value">The value this proposer wants decided.</param>
    /// <returns>The opening round.</returns>
    /// <remarks>
    /// The template's priority is the reserved one when the proposer believes it leads, and the absent one
    /// otherwise. The absent priority is a placeholder that is never sent, because phase zero redraws the
    /// priority per recorder for every proposer that does not claim leadership.
    /// </remarks>
    public static QuePaxaRound<TValue> Begin(ProposerLane proposer, ProposerLane? believedLeader, TValue value)
    {
        ProposalPriority priority = believedLeader == proposer ? ProposalPriority.Reserved : ProposalPriority.None;
        var template = new PrioritizedProposal<TValue>(new ProposalKey(priority, proposer), value);

        return new QuePaxaRound<TValue>(proposer, believedLeader, RecorderStep.RoundOnePhaseZero, template);
    }


    /// <summary>Whether this proposer believes it leads round one, and so claims the reserved priority at the first step.</summary>
    public bool ClaimsLeadership => BelievedLeader == Proposer;

    /// <summary>
    /// Whether a send at this position draws a fresh priority per recorder rather than carrying the working
    /// template untouched.
    /// </summary>
    /// <remarks>
    /// This is the model's <c>randomizes</c> exactly: a phase-zero step randomizes unless it is the first
    /// step of a proposer that believes it leads, which is the one send that keeps the reserved priority its
    /// template started with. The reserved priority is therefore claimed only at the first step and only by a
    /// proposer that believes it leads; every other phase-zero send carries a fresh draw, including the
    /// leader's own sends in every later round.
    /// </remarks>
    public bool RedrawsPriority => Step.Phase == 0 && (Step > RecorderStep.RoundOnePhaseZero || !ClaimsLeadership);


    /// <summary>
    /// Produces the proposal one recorder's send carries, drawing a fresh priority when this position
    /// redraws.
    /// </summary>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <returns>The proposal to send to one recorder.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="drawPriority"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if <paramref name="drawPriority"/> returns a priority that is not ordinary.</exception>
    /// <remarks>
    /// <para>
    /// One call is one recorder's send. Calling this once per step and broadcasting the result is a protocol
    /// defect: phase zero redraws the priority per recorder, and a single draw shared across recorders
    /// collapses the independence the liveness argument rests on. A caller's recorder iteration order is
    /// therefore part of its contract, because it is what makes a seeded priority source reproduce a run
    /// exactly.
    /// </para>
    /// <para>
    /// A caller that re-delivers a request to one recorder re-sends the proposal this method already
    /// produced for that recorder. Drawing again for a re-send would put two distinct proposal keys at one
    /// recorder and step, which is the one state the model does not admit.
    /// </para>
    /// </remarks>
    public PrioritizedProposal<TValue> NextSend(ProposalPrioritySourceDelegate drawPriority)
    {
        ArgumentNullException.ThrowIfNull(drawPriority);

        if(!RedrawsPriority)
        {
            return Proposal;
        }

        ProposalPriority drawn = drawPriority();
        if(!drawn.IsOrdinary)
        {
            throw new InvalidOperationException("A priority source must return an ordinary priority; the absent priority is the aggregate's identity and the reserved priority forges a leader claim.");
        }

        return Proposal.WithPriority(drawn);
    }


    /// <summary>
    /// Concludes this step from the answers a proposer gathered, reporting what the step produced and the
    /// round to step next.
    /// </summary>
    /// <param name="answers">The answers gathered at this step, at most one per recorder.</param>
    /// <param name="recorderCount">The number of recorders in the instance. Must be at least one.</param>
    /// <returns>The step's outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="recorderCount"/> is less than one.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="answers"/> is default, contains a <see langword="null"/> element, contains a
    /// recorder index outside the recorder range, or contains the same recorder index twice.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown if a recorder above step zero answered with no first proposal.</exception>
    /// <remarks>
    /// <para>
    /// The quorum is derived from <paramref name="recorderCount"/> and is never supplied. The model's sole
    /// guard on a proposer acting is that the reply set it acts on is a majority: a sub-majority set reaches
    /// the phase-two decision on replies that need not intersect another proposer's, and two proposers then
    /// decide different values without the two-majorities-intersect step Lemmas C.5 and C.9 rest on. A strict
    /// majority is also what the proofs need. Supplying more answers than the majority is normal and
    /// unaffected, because the arithmetic only sets a floor.
    /// </para>
    /// <para>
    /// A duplicate recorder index is refused here, which is not redundant with the request-side check. The
    /// model's majority test counts reply records rather than recorders, so one recorder contributing two
    /// answers would double-count toward the quorum. This method is public and a host may assemble the answer
    /// array itself, so the answer-side check is what the quorum arithmetic actually needs;
    /// <see cref="QuePaxaRegister{TValue}.Step"/>'s request-side check fails earlier with a better message
    /// for the driver it guards.
    /// </para>
    /// <para>
    /// Every reached recorder has recorded before this runs, whatever this concludes. A message that arrives
    /// is recorded even when the proposer never assembles a quorum, which is the behaviour Lemma C.5's second
    /// case turns on. A caller that discarded the mutated recorders on a missed quorum would break that.
    /// </para>
    /// </remarks>
    public QuePaxaStepOutcome<TValue> Conclude(ImmutableArray<RecorderAnswer<TValue>> answers, int recorderCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recorderCount, 1);

        if(answers.IsDefault)
        {
            throw new ArgumentException("A conclusion requires an initialized array of recorder answers.", nameof(answers));
        }

        for(int i = 0; i < answers.Length; i++)
        {
            RecorderAnswer<TValue> answer = answers[i];
            if(answer is null)
            {
                throw new ArgumentException($"The answer at position {i} is null.", nameof(answers));
            }

            //Only the upper bound is tested, because RecorderAnswer refuses a negative recorder index at
            //construction and on a with expression alike.
            if(answer.Recorder >= recorderCount)
            {
                throw new ArgumentException($"Recorder index {answer.Recorder} is out of range.", nameof(answers));
            }

            //LEMMA C.2 IS A PRECONDITION HERE RATHER THAN AN ASSUMPTION, and the catch-up rule below reads it
            //as one: it treats "no summary above my step" as "every summary at my step". A recorder advances
            //to the requested step before it answers, so no answer to this step can come from below it, and a
            //host that supplies one would otherwise have a stale aggregate counted toward this step's quorum
            //and could take a phase-two decision on a majority that never gathered here.
            if(answer.Summary.Step < Step)
            {
                throw new ArgumentException($"The answer from recorder {answer.Recorder} carries step {answer.Summary.Step.Value}, which is below the round's step {Step.Value}; a recorder advances to the requested step before it answers.", nameof(answers));
            }

            for(int j = i + 1; j < answers.Length; j++)
            {
                RecorderAnswer<TValue> other = answers[j];
                if(other is not null && other.Recorder == answer.Recorder)
                {
                    throw new ArgumentException($"Recorder index {answer.Recorder} appears twice, and a duplicate would double-count toward the quorum.", nameof(answers));
                }
            }
        }

        int quorum = (recorderCount / 2) + 1;
        if(answers.Length < quorum)
        {
            return new QuePaxaStepOutcome<TValue>(QuePaxaStepKind.QuorumMissed, null, default, null, RecorderStep.Zero, answers.Length);
        }

        if(TryCatchUp(this, answers, out QuePaxaRound<TValue>? caughtUp))
        {
            return new QuePaxaStepOutcome<TValue>(QuePaxaStepKind.CaughtUp, caughtUp, default, null, RecorderStep.Zero, answers.Length);
        }

        PrioritizedProposal<TValue> template = Proposal;
        if(Step.Phase == 0)
        {
            //Lemma C.4 makes the phase-zero best of the first proposals the abstract layer's best of the
            //proposal set, and Lemma C.10 is what the fast path rests on.
            PrioritizedProposal<TValue> leading = RequireFirst(answers[0].Summary);
            PrioritizedProposal<TValue> greatest = leading;
            bool uniform = true;
            for(int i = 1; i < answers.Length; i++)
            {
                PrioritizedProposal<TValue> candidate = RequireFirst(answers[i].Summary);
                if(!candidate.Equals(leading))
                {
                    uniform = false;
                }

                if(candidate.Key > greatest.Key)
                {
                    greatest = candidate;
                }
            }

            //The test is whole-proposal equality and not key equality: the model's guarded fast path compares
            //whole records, and comparing keys alone would pass under a key collision while leaving which
            //value to return ambiguous. Restricting the fast path to the first step is stricter than the
            //model, which guards it on any phase-zero step, and can lose nothing, because every phase-zero
            //send above the first step is redrawn to an ordinary priority so a reserved first cannot appear
            //there, and a restriction can only refuse a decision rather than add one.
            if(Step == RecorderStep.RoundOnePhaseZero && uniform && leading.Key.Priority.IsReserved)
            {
                return new QuePaxaStepOutcome<TValue>(QuePaxaStepKind.Decided, null, leading.Value, leading.Key.Owner, Step, answers.Length);
            }

            template = greatest;
        }
        else if(Step.Phase == 2 || Step.Phase == 3)
        {
            PrioritizedProposal<TValue>? greatestPrior = GreatestPriorAggregate(answers);
            if(Step.Phase == 2)
            {
                //Lemma C.9 makes the asynchronous test against the best of the prior aggregates the same
                //decision as the abstract layer's comparison of the existent and universal bests. The
                //comparison is whole-proposal because the model's test is whole-record: a proposer must
                //never decide a value other than the one the gathered record actually carries, which is what
                //a key-only test would allow once a key names two values. It removes that ambiguity and
                //nothing more. It does NOT bound how many proposers decide when the proposal key's
                //uniqueness contract is violated, because the losing proposal is spread in phase one and the
                //recorders' first-proposal fields are overwritten, which is the same unrecoverable shape the
                //map records for the fast-path defence. Keeping one key to one value stays the caller's
                //obligation and the lane exists to make it keepable.
                if(greatestPrior is not null && greatestPrior.Equals(Proposal))
                {
                    return new QuePaxaStepOutcome<TValue>(QuePaxaStepKind.Decided, null, Proposal.Value, Proposal.Key.Owner, Step, answers.Length);
                }
            }
            else if(greatestPrior is not null)
            {
                //Lemma C.8 makes the phase-three gather the abstract layer's best of the common set, which is
                //the carry into the next round. A null best is unreachable for a round that began at Begin
                //and reached this step through step outcomes, because such a round advanced from the previous
                //step on a quorum all at that step, and any quorum here intersects it at a recorder whose
                //advance by exactly one froze a real prior aggregate. A round built by hand at this step
                //against recorders that never served the previous one does reach it, and leaving the template
                //unchanged is the honest answer there; the model assigns the best of an all-absent set, which
                //is the same thing on every state the protocol can drive itself into.
                template = greatestPrior;
            }
        }

        //Phase one reads nothing: the proposal was spread by the send itself.
        if(Step.IsExhausted)
        {
            return new QuePaxaStepOutcome<TValue>(QuePaxaStepKind.Exhausted, null, default, null, RecorderStep.Zero, answers.Length);
        }

        QuePaxaRound<TValue> advanced = this with { Step = Step.Next(), Proposal = template };

        return new QuePaxaStepOutcome<TValue>(QuePaxaStepKind.Advanced, advanced, default, null, RecorderStep.Zero, answers.Length);
    }


    private static bool TryCatchUp(QuePaxaRound<TValue> round, ImmutableArray<RecorderAnswer<TValue>> answers, out QuePaxaRound<TValue>? caughtUp)
    {
        RecorderAnswer<TValue> leading = answers[0];
        foreach(RecorderAnswer<TValue> answer in answers)
        {
            //The greatest step wins and the lowest recorder index breaks a tie, so that a run is
            //reproducible. Lemma C.2 makes every summary's step at least the requested one, so any summary
            //not at the requested step is above it, and Lemma C.3 makes the landing state one some proposer
            //reached without catching up. The model lets its adversary take any reply above the current
            //step, and taking the greatest is one of the choices it admits.
            if(answer.Summary.Step > leading.Summary.Step || (answer.Summary.Step == leading.Summary.Step && answer.Recorder < leading.Recorder))
            {
                leading = answer;
            }
        }

        if(leading.Summary.Step <= round.Step)
        {
            caughtUp = null;

            return false;
        }

        caughtUp = round with { Step = leading.Summary.Step, Proposal = RequireFirst(leading.Summary) };

        return true;
    }


    private static PrioritizedProposal<TValue>? GreatestPriorAggregate(ImmutableArray<RecorderAnswer<TValue>> answers)
    {
        PrioritizedProposal<TValue>? greatest = null;
        foreach(RecorderAnswer<TValue> answer in answers)
        {
            PrioritizedProposal<TValue>? prior = answer.Summary.PriorAggregate;
            if(prior is not null && (greatest is null || prior.Key > greatest.Key))
            {
                greatest = prior;
            }
        }

        return greatest;
    }


    private static PrioritizedProposal<TValue> RequireFirst(RecordSummary<TValue> summary)
    {
        //A recorder above step zero always holds a first proposal, because every request arrives at or above
        //round one phase zero and so lands on the register's advancing branch. A null here means the register
        //state is corrupt, and reporting it is the fail-closed reading.
        if(summary.First is null)
        {
            throw new InvalidOperationException("A recorder above step zero answered with no first proposal, so its register state is corrupt.");
        }

        return summary.First;
    }
}
