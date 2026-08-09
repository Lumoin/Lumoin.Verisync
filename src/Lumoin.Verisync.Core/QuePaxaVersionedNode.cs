using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A recorder host for a versioned register: it owns the one consensus instance it can derive a leader for,
/// and serves record requests addressed to that instance.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <remarks>
/// <para>
/// The live instance is the version after the committed one, and it is the only one this host serves. A host
/// that has learned version v derives the leader for v+1, which is the writer of v; the leader for v+2 is the
/// writer of v+1, which it has not learned. Serving a window would put two hosts at different committed
/// versions on one instance under two different leaders, which is the reserved-priority divergence hazard.
/// </para>
/// <para>
/// A host that cannot derive the leader declines the instance rather than serving it leaderless. A recorder
/// declining a reserved claim records it at the lowest ordinary priority rather than dropping it, and it does
/// so at the round's first step, which is the step the fast path is read at: the leader's one logical
/// proposal then exists under two keys there, reserved where it was honoured and lowest where it was not, so
/// no quorum containing that recorder can decide on the fast path at all. Uniformity is available outright,
/// every host deriving the leader from the same committed record, so the surface takes it rather than resting
/// on what a non-uniform configuration survives.
/// </para>
/// <para>
/// A request for any other instance is declined, and declining is a host act rather than a protocol refusal.
/// <see cref="RecordReply{TValue}"/> has no rejection field: the node throws, a transport reports that as a
/// fault like any other unreachable recorder, and a proposer retries within its attempt budget and otherwise
/// concludes a missed quorum. Nothing in the protocol learns that a decline happened.
/// </para>
/// <para>
/// What this costs a deployment is that no write at v+1 can gather a quorum until a quorum of hosts has
/// learned v. The committed record and the first request of the next version may travel together, so a writer
/// that commits and immediately disseminates pays no extra round trip, and a host that disseminates lazily
/// serializes its writes behind dissemination. A learned record is not verified, which is the same crash-fault
/// assumption that lets a recorder's own reply be believed.
/// </para>
/// <para>
/// Restarting is this host's own operation and not the recorder's alone. <see cref="ToState"/> snapshots the
/// committed record beside the recorder serving the instance that record implies, and <see cref="FromState"/>
/// refuses a snapshot whose stored leader or stored version disagrees with the record beside it, which is what
/// a write torn across the two leaves behind. The committed record has to be durable before any reply that
/// depends on it leaves the process, for the same reason the register does: the leader every recorder enforces
/// is derived from it, so a host that answered at one version and restarted holding an older record re-opens
/// an instance that has already decided, with an empty register.
/// </para>
/// <para>
/// A node processes its requests sequentially and is not safe for concurrent calls, exactly as
/// <see cref="QuePaxaNode{TValue}"/> is not.
/// </para>
/// </remarks>
public sealed class QuePaxaVersionedNode<TValue>
{
    /// <summary>
    /// Initializes a host that has learned <paramref name="committed"/>.
    /// </summary>
    /// <param name="schedule">The leader derivation every replica shares.</param>
    /// <param name="committed">The committed record this host starts from, or <see langword="null"/> when it has learned none.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The committed record is taken at construction rather than defaulted, as <see cref="QuePaxaNode{TValue}"/>
    /// takes its recorder: it is what the instance's leader is derived from, and a host that started from
    /// nothing would serve the first version when it should be serving a much later one.
    /// </remarks>
    public QuePaxaVersionedNode(QuePaxaLeaderSchedule schedule, VersionedValue<TValue>? committed = null)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        LeaderSchedule = schedule;
        Committed = committed;
        Node = new QuePaxaNode<VersionedValue<TValue>>(schedule.RecorderFor<VersionedValue<TValue>>(committed?.Writer));
    }


    private QuePaxaVersionedNode(QuePaxaLeaderSchedule schedule, VersionedValue<TValue>? committed, QuePaxaNode<VersionedValue<TValue>> node)
    {
        LeaderSchedule = schedule;
        Committed = committed;
        Node = node;
    }


    /// <summary>The leader derivation this host and its register share.</summary>
    public QuePaxaLeaderSchedule LeaderSchedule { get; }

    /// <summary>The committed record this host has learned, or <see langword="null"/> when it has learned none.</summary>
    public VersionedValue<TValue>? Committed { get; private set; }

    /// <summary>The one version this host serves, which is the one after the committed record's.</summary>
    /// <exception cref="InvalidOperationException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that no version follows it.</exception>
    public RegisterVersion LiveVersion => Committed is { } committed ? committed.Version.Next() : RegisterVersion.First;

    /// <summary>The recorder node serving <see cref="LiveVersion"/>.</summary>
    public QuePaxaNode<VersionedValue<TValue>> Node { get; private set; }


    /// <summary>
    /// Applies <paramref name="request"/> to the live instance and returns the reply.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <returns>The reply, carrying the version of the instance that answered.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the request does not name <see cref="LiveVersion"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that this host serves no version at all.</exception>
    /// <remarks>
    /// Both bounds are refused and for different reasons. A request below the live version belongs to an
    /// instance that has already decided, and folding it into the live register would put a settled version's
    /// proposals into an unsettled one. A request above it belongs to an instance whose leader this host
    /// cannot derive, and serving it under the leader of a different version is the hazard.
    /// </remarks>
    public VersionedRecordReply<VersionedValue<TValue>> Handle(VersionedRecordRequest<VersionedValue<TValue>> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RegisterVersion live = LiveVersion;
        if(request.Version != live)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Version.Value,
                $"This host serves version {live.Value}; it has learned nothing about the leader of any other version and will not record for one.");
        }

        return new VersionedRecordReply<VersionedValue<TValue>>(live, Node.Handle(request.Request));
    }


    /// <summary>
    /// Adopts <paramref name="committed"/> and moves the live instance to the version after it.
    /// </summary>
    /// <param name="committed">A decided record.</param>
    /// <returns><see langword="true"/> when the record advanced this host, and <see langword="false"/> when it was not newer than what it already held.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A record that does not advance is ignored rather than refused, because a host disseminating the same
    /// decision twice is ordinary. Knowledge of committed state therefore only ever runs forward, and the live
    /// instance with it.
    /// </para>
    /// <para>
    /// Advancing replaces the instance rather than reconfiguring it. A recorder's configured leader is fixed
    /// for the life of its instance, so learning a new version builds a new recorder for the new instance and
    /// leaves the old one alone; rewriting a live recorder's leader would change the rule mid-instance, after
    /// proposals had already been recorded under the old one.
    /// </para>
    /// </remarks>
    public bool Learn(VersionedValue<TValue> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);

        if(Committed is { } held && committed.Version <= held.Version)
        {
            return false;
        }

        Committed = committed;
        Node = new QuePaxaNode<VersionedValue<TValue>>(LeaderSchedule.RecorderFor<VersionedValue<TValue>>(committed.Writer));

        return true;
    }


    /// <summary>
    /// Snapshots the host's durable state, which is the committed record beside the recorder serving the
    /// instance that record implies.
    /// </summary>
    /// <returns>The durable state to make stable before any dependent reply is sent.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that this host serves no version at all.</exception>
    /// <remarks>
    /// <para>
    /// The derived leader and the live version are written into the snapshot rather than left to be recomputed
    /// on restore. Both are functions of the committed record, so recomputing them would compare each with
    /// itself; writing them down is what lets <see cref="FromState"/> refuse a snapshot torn across the record
    /// and the register.
    /// </para>
    /// <para>
    /// This is the inverse of <see cref="FromState"/> over every state a host reaches, including the interval
    /// between learning a version and answering the first request for the next one, where the recorder stands
    /// at <see cref="RecorderStep.Zero"/>. That is a host state rather than a lost register, so it snapshots and
    /// restores; <see cref="QuePaxaRecorder{TValue}.ToState"/> and
    /// <see cref="QuePaxaRecorder{TValue}.FromState"/> are deliberately not inverses there, and the difference
    /// is that a bare recorder state names no instance while this record names one.
    /// </para>
    /// </remarks>
    public QuePaxaVersionedNodeState<TValue> ToState()
    {
        return new QuePaxaVersionedNodeState<TValue>(Committed, LiveVersion, Node.Recorder.ConfiguredLeader, Node.Recorder.ToState());
    }


    /// <summary>
    /// Reconstructs a host from durable <paramref name="state"/> under <paramref name="schedule"/>, refusing
    /// fail-closed every snapshot whose parts disagree with one another.
    /// </summary>
    /// <param name="schedule">
    /// The leader derivation every replica shares. It comes before the state because it is configuration rather
    /// than durable protocol state, as identity and membership come before the state in
    /// <see cref="RaftNode{TCommand}.FromState"/>.
    /// </param>
    /// <param name="state">The durable state to restore.</param>
    /// <returns>A host serving the instance the restored record implies.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> or <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the snapshot's parts disagree: a
    /// <see cref="QuePaxaVersionedNodeState{TValue}.ConfiguredLeader"/> other than the one
    /// <see cref="QuePaxaLeaderSchedule.LeaderFor(ReplicaId?)"/> derives from the restored record; a
    /// <see cref="QuePaxaVersionedNodeState{TValue}.RecorderVersion"/> other than the version after the restored
    /// record's; or a recorder standing at <see cref="RecorderStep.Zero"/> that carries a proposal in any slot.
    /// Also thrown for every state no recorder-driven register can hold, as
    /// <see cref="QuePaxaRecorder{TValue}.FromState(ProposerLane?, QuePaxaRecorderState{TValue})"/> defines
    /// them.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown if the restored record is at <see cref="RegisterVersion.MaxValue"/>, so that no version follows it and this host would serve none.</exception>
    /// <remarks>
    /// <para>
    /// The two cross-checks are what a torn snapshot fails. A stored leader other than the derived one means
    /// two hosts can hold records implying different leaders for one instance, which is the reserved-priority
    /// divergence hazard, and a host whose own snapshot says so refuses to start rather than joining as the
    /// second leader. A stored version other than the derived one means a register from one instance beside a
    /// record from another, which is the half-written snapshot. Neither can fire against a record the restore
    /// derived from, which is why both are stored.
    /// </para>
    /// <para>
    /// A stale record that is internally consistent restores without complaint, and that is correct rather than
    /// a gap. The leader is a deterministic function of the record, so a genuinely old record yields exactly
    /// the leader its own instance ran under, and <see cref="Handle"/>'s version gate keeps such a host from
    /// serving the live instance at all. What a stale host costs a deployment is availability, since no write
    /// gathers a quorum until a quorum of hosts has learned the previous version, and that is the same cost the
    /// derivation charges a host that has merely fallen behind.
    /// </para>
    /// <para>
    /// A recorder standing at <see cref="RecorderStep.Zero"/> is rebuilt unwritten rather than handed to the
    /// recorder's own restore, which refuses that step. A host occupies it for the whole interval between
    /// learning a version and answering the first request for the next one, so refusing to restart there would
    /// be a defect and not a defence, and nothing was answered from a register at that step because a recorder
    /// records nothing below <see cref="RecorderStep.RoundOnePhaseZero"/>. The short circuit is the unwritten
    /// register exactly: a step-zero snapshot carrying a proposal in any slot is refused here, and every other
    /// step below the recorder's floor still reaches the recorder's restore and is refused there.
    /// </para>
    /// </remarks>
    public static QuePaxaVersionedNode<TValue> FromState(QuePaxaLeaderSchedule schedule, QuePaxaVersionedNodeState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(state);

        ReplicaId? previousWriter = state.Committed?.Writer;
        ProposerLane? derivedLeader = schedule.LeaderFor(previousWriter);

        if(state.ConfiguredLeader != derivedLeader)
        {
            throw new ArgumentException($"A restored configured leader must be the one the schedule derives from the restored record, which is {Describe(derivedLeader)}, and it is {Describe(state.ConfiguredLeader)}. Two hosts whose records imply different leaders for one instance admit two reserved claims at the step the fast path reads.", nameof(state));
        }

        RegisterVersion live = state.Committed is { } committed ? committed.Version.Next() : RegisterVersion.First;
        if(state.RecorderVersion != live)
        {
            throw new ArgumentException($"A restored recorder must serve the version after the restored record's, which is {live.Value}, and it serves {state.RecorderVersion.Value}. A register from one instance beside a record from another is a snapshot written in two parts and torn between them.", nameof(state));
        }

        QuePaxaRecorder<VersionedValue<TValue>> recorder;
        if(state.Recorder.Step == RecorderStep.Zero)
        {
            if(state.Recorder.First is not null || state.Recorder.CurrentAggregate is not null || state.Recorder.PriorAggregate is not null)
            {
                throw new ArgumentException("A restored recorder at step zero must be the unwritten register, carrying no proposal in any slot. A recorder records nothing below round one phase zero, so a proposal standing at step zero was never recorded there and rebuilding the register unwritten would discard it.", nameof(state));
            }

            recorder = schedule.RecorderFor<VersionedValue<TValue>>(previousWriter);
        }
        else
        {
            recorder = schedule.RecorderFor<VersionedValue<TValue>>(previousWriter, state.Recorder);
        }

        return new QuePaxaVersionedNode<TValue>(schedule, state.Committed, new QuePaxaNode<VersionedValue<TValue>>(recorder));
    }


    private static string Describe(ProposerLane? lane) => lane?.ToString() ?? "leaderless";
}
