using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Derives the leader of a versioned register's next consensus instance from committed state, so that every
/// replica reaches the same answer without exchanging a message about it.
/// </summary>
/// <remarks>
/// <para>
/// A recorder honours the reserved priority only from the leader it is configured with, which stops two
/// proposals carrying that priority from coexisting. The identity cannot come from the proposer making the
/// claim, because a proposer that wrongly believes it leads would report it wrongly, so it is a deterministic
/// function of facts every recorder already holds.
/// </para>
/// <para>
/// The two inputs are the agreed order and the previous version's writer. The order is a configured fact. The
/// writer is a field of the decided value carried by <see cref="VersionedValue{TValue}"/>, so consensus itself
/// makes every replica agree on it.
/// </para>
/// <para>
/// Rotating to the previous writer keeps a single-writer register fast. The replica that wrote the last
/// version leads the next one, so a workload with one active writer per key finds its own writer holding the
/// reserved priority at every version and committing in one round trip. The rotation is a committed fact every
/// replica computes the same answer from.
/// </para>
/// <para>
/// The same rotated schedule answers two questions that must not be confused. Who leads is a safety input and
/// must be identical at every recorder serving the instance, and <see cref="LeaderFor(ReplicaId?)"/> is the
/// only answer to it. Every position's delay is a hedging input, which orders sending and settles no protocol
/// rule. A schedule ordering a leaderless fast round gates neither safety nor liveness, while the same
/// schedule read here carries a safety input as well, and the two readings must not be swapped.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class QuePaxaLeaderSchedule
{
    /// <summary>
    /// Initializes a derivation over <paramref name="schedule"/>.
    /// </summary>
    /// <param name="schedule">The agreed replica order, whose first position is the bootstrap leader.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> is <see langword="null"/>.</exception>
    public QuePaxaLeaderSchedule(HedgingSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        Schedule = schedule;
    }


    /// <summary>The agreed replica order this derivation reads.</summary>
    public HedgingSchedule Schedule { get; }


    /// <summary>
    /// The schedule the next instance runs under, rotated so that <paramref name="previousWriter"/> is first.
    /// </summary>
    /// <param name="previousWriter">
    /// The replica that wrote the previous version, or <see langword="null"/> when no version has been
    /// written.
    /// </param>
    /// <returns>The rotated schedule, whose delays are the hedging order for the instance.</returns>
    /// <remarks>
    /// A writer that is not in the order returns the configured order unrotated, which supplies delays for
    /// every replica. The order's first position does not lead in that case: an instance whose previous writer
    /// the order no longer contains is leaderless, and <see cref="LeaderFor(ReplicaId?)"/> is the only answer
    /// to who leads.
    /// </remarks>
    public HedgingSchedule ScheduleFor(ReplicaId? previousWriter)
    {
        if(previousWriter is not { } writer || !Schedule.Contains(writer))
        {
            return Schedule;
        }

        return Schedule.RotateTo(writer);
    }


    /// <summary>
    /// The lane whose reserved-priority claims the next instance's recorders honour.
    /// </summary>
    /// <param name="previousWriter">
    /// The replica that wrote the previous version, or <see langword="null"/> when no version has been
    /// written.
    /// </param>
    /// <returns>The leading lane, or <see langword="null"/> when the instance is leaderless.</returns>
    /// <remarks>
    /// <para>
    /// The three answers are the whole of the derivation. With no previous writer the leader is the configured
    /// order's first replica, which is the agreed bootstrap. With a previous writer in the order the leader is
    /// that writer. With a previous writer the order no longer contains, which is what a membership change
    /// leaves behind, the instance is leaderless: every reserved claim is declined and the round takes the
    /// ordinary phases at the cost of one round instead of one round trip.
    /// </para>
    /// <para>
    /// The leaderless answer is safe because it is uniform, not because a leaderless recorder is harmless among
    /// led ones. Both inputs are agreed, so every replica takes the same arm and a leaderless instance is
    /// leaderless at every recorder. Recorders that honour different leaders violate agreement, because two
    /// reserved claims are then admitted at the step the fast path reads. A recorder honouring none among led
    /// ones is a weaker shape, and the derivation makes it unreachable rather than resting on how much
    /// weaker it is.
    /// </para>
    /// <para>
    /// The answer is a lane and it is always lane zero, which makes lane zero of the leading replica the only
    /// identity that can hold the reserved priority. The priority is reserved for a lane rather than for a
    /// replica so that two lanes of the leader's own replica cannot both claim it. A proposer retrying within
    /// one version runs on a later lane and therefore does not claim leadership at all, because a retry within
    /// a version means the fast path was already lost.
    /// </para>
    /// <para>
    /// It never returns <see langword="null"/> to mean that the leader is unknown. A replica that has not
    /// learned the previous version must not serve the instance at all, because a leader derived from a stale
    /// committed record is a different leader rather than an absent one.
    /// </para>
    /// </remarks>
    public ProposerLane? LeaderFor(ReplicaId? previousWriter)
    {
        if(previousWriter is not { } writer)
        {
            return ProposerLane.For(Schedule.Leader);
        }

        if(!Schedule.Contains(writer))
        {
            return null;
        }

        return ProposerLane.For(writer);
    }


    /// <summary>
    /// The recorder the next instance is served by, configured with the leader
    /// <see cref="LeaderFor(ReplicaId?)"/> derives.
    /// </summary>
    /// <typeparam name="TValue">The consensus value type.</typeparam>
    /// <param name="previousWriter">
    /// The replica that wrote the previous version, or <see langword="null"/> when no version has been
    /// written.
    /// </param>
    /// <returns>A recorder whose register was never written.</returns>
    /// <remarks>
    /// The proposer side reads <see cref="LeaderFor(ReplicaId?)"/> for what it believes and the recorder side
    /// reads this for what it enforces, so the recorder and the belief never come from two different
    /// expressions.
    /// </remarks>
    public QuePaxaRecorder<TValue> RecorderFor<TValue>(ReplicaId? previousWriter)
    {
        ProposerLane? leader = LeaderFor(previousWriter);

        return leader is { } lane ? QuePaxaRecorder<TValue>.LedBy(lane) : QuePaxaRecorder<TValue>.Leaderless;
    }


    /// <summary>
    /// The recorder the next instance is resumed by after a restart, configured with the leader
    /// <see cref="LeaderFor(ReplicaId?)"/> derives and standing at the step <paramref name="state"/> holds.
    /// </summary>
    /// <typeparam name="TValue">The consensus value type.</typeparam>
    /// <param name="previousWriter">
    /// The replica that wrote the previous version, or <see langword="null"/> when no version has been
    /// written.
    /// </param>
    /// <param name="state">The durable state the restarting recorder last made stable.</param>
    /// <returns>A recorder standing at the restored step.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown for every state no recorder-driven register can hold, as
    /// <see cref="QuePaxaRecorder{TValue}.FromState(ProposerLane?, QuePaxaRecorderState{TValue})"/> defines
    /// them.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The proposer side reads <see cref="LeaderFor(ReplicaId?)"/> for what it believes and the recorder side
    /// reads this for what it enforces, so the recorder and the belief never come from two different
    /// expressions.
    /// </para>
    /// <para>
    /// A host restoring an instance derives the leader from the record it has learned rather than supplying one
    /// of its own, because two hosts supplying different leaders for one instance is the reserved-priority
    /// divergence hazard: two reserved claims are then admitted at the step the fast path reads. The restore
    /// admits a state whose first proposal is ordinary under any leader, so a hand-wired leader that does not
    /// match the derivation arrives silently, and reading the derivation here is what keeps a restored recorder
    /// indistinguishable from one that never restarted.
    /// </para>
    /// <para>
    /// A <see langword="null"/> derived leader means the instance is deliberately leaderless and never that the
    /// leader is unknown. A replica that has not learned the previous version must not serve the instance at
    /// all, because a leader derived from a stale committed record is a different leader rather than an absent
    /// one.
    /// </para>
    /// </remarks>
    public QuePaxaRecorder<TValue> RecorderFor<TValue>(ReplicaId? previousWriter, QuePaxaRecorderState<TValue> state)
    {
        return QuePaxaRecorder<TValue>.FromState(LeaderFor(previousWriter), state);
    }


    private string DebuggerDisplay => $"QuePaxaLeaderSchedule: {Schedule.Order.Length} replicas";
}
