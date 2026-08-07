using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// What one QuePaxa protocol step produced.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="QuorumMissed"/> and <see cref="Exhausted"/> are separate kinds and the distinction is not
/// cosmetic. An exhausted step budget is TERMINAL for the instance, because the round cannot advance and a
/// caller that retries on it spins forever. A missed quorum is not terminal: the instance may still be
/// driven, and the register is left holding whatever the reached recorders recorded.
/// </para>
/// <para>
/// A MISSED QUORUM IS NOT AN INVITATION TO RE-STEP THE SAME ROUND. That is the one recovery this core does
/// not sanction, and the reason is in
/// <see cref="QuePaxaRegister{TValue}.Step(QuePaxaRound{TValue}, System.Collections.Immutable.ImmutableArray{int}, ProposalPrioritySourceDelegate)"/>:
/// a phase-zero re-step draws fresh priorities, so one proposer would hold several distinct proposal keys at
/// one step, which is a state the checked model cannot represent because it sends each step exactly once.
/// The value carried is unaffected and no agreement argument is known to break, but the entropy argument the
/// protocol's progress rests on is stated over one draw per proposer per step.
/// </para>
/// <para>
/// WHAT A PROPOSER MAY DO INSTEAD IS RE-DELIVER. The message-driven layer is
/// <see cref="QuePaxaProposer{TValue}"/>, and its rule is that a request may be delivered to one recorder any
/// number of times provided every delivery is identical — same step, same proposal, same priority. A second
/// identical record is the identity on the recorder, because the first delivery already moved it to at least
/// that step and the same-step fold keeps the incumbent, so the register is unchanged and the duplicate's
/// reply repeats the first. The model admits at most one DISTINCT proposal per proposer, recorder and step,
/// which is what its one-send-per-step flag enforces; several distinct keys at one step across DIFFERENT
/// recorders is the ordinary case, because the phase-zero draw is per recorder. Re-stepping one round value
/// with a fresh draw stays forbidden, and a proposer recovers from a missed quorum by proposing again on a
/// fresh <see cref="ProposerLane"/>, which the model already handles as a second proposer.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "The zero member is QuorumMissed rather than None so that a default outcome reads as a failure, which is fail-closed; a neutral zero would let an uninitialized outcome be mistaken for progress.")]
public enum QuePaxaStepKind
{
    /// <summary>
    /// Fewer recorders than the quorum answered, so the step reached no conclusion. The recorders it did
    /// reach have still recorded.
    /// </summary>
    QuorumMissed = 0,

    /// <summary>The step budget is spent: the round is at the last representable step and no successor exists.</summary>
    Exhausted = 1,

    /// <summary>The step completed and the round advances by one step, carrying its updated template.</summary>
    Advanced = 2,

    /// <summary>A recorder answered from above the requested step, so the round jumps to that step and adopts what was found there.</summary>
    CaughtUp = 3,

    /// <summary>The step decided a value.</summary>
    Decided = 4,
}
