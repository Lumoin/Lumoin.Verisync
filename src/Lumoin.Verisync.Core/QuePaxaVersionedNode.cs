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
}
