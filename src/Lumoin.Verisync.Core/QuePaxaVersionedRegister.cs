using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A versioned register over the QuePaxa protocol: a value carrying a version, where every write is a fresh
/// consensus instance at the next version and the previous version's writer leads the next one.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <remarks>
/// <para>
/// QuePaxa and CasPaxos are not drop-in equivalents. <see cref="CasPaxosRegister{TValue}"/> applies a change
/// function to the value recovered inside the round, so a caller's intent survives contention. QuePaxa
/// decides among proposed values, so a proposer that loses has its whole proposal discarded and must re-read
/// and re-propose. <see cref="WriteAsync"/> therefore applies its update outside the round, once per attempt,
/// which makes read-modify-write optimistic concurrency rather than function composition. The two are
/// interchangeable only for updates that are idempotent, monotone, or explicitly abort-on-lose.
/// </para>
/// <para>
/// The register is single-flight and a concurrent write is refused. Two writes in flight on one register at
/// one version would propose on one lane, and one proposal key naming two values is what
/// <see cref="ProposalKey"/>'s uniqueness contract forbids: it makes the aggregate fold arrival-order
/// dependent at different recorders, and the disagreement is unrecoverable once it has spread.
/// </para>
/// <para>
/// Succession is covered as well as overlap, because the lane is allocated per proposal rather than per call.
/// Every attempt at one version draws the next lane whether it came from a retry inside
/// <see cref="WriteAsync"/> or from a fresh call to <see cref="TryWriteAsync"/>, and a version this register
/// has not proposed at starts again at lane zero. Re-entering cannot put two values under one key.
/// </para>
/// <para>
/// One replica holds one proposer identity per version, and the counter belongs to the register instance
/// rather than to the replica. A second register for the same replica allocates the same lanes, so concurrent
/// proposals for one version are outside what this type supports. A host that needs them drives
/// <see cref="QuePaxaProposer{TValue}"/> directly, which takes the <see cref="ProposerLane"/> explicitly and
/// so leaves the uniqueness obligation with the caller.
/// </para>
/// <para>
/// The leader is derived and never chosen. <see cref="QuePaxaLeaderSchedule"/> answers who leads the next
/// instance from the previous version's writer, which is a field of the decided value and therefore an agreed
/// fact. The register believes that answer, and the recorders enforce it; a register that believed something
/// else would have its reserved claim declined, which costs a round trip and never costs safety.
/// </para>
/// <para>
/// The committed record has one owner per replica, and both roles read it: the register computes the next
/// version and the update's input from it, and the recorder host derives the leader from it. A host feeds
/// both through <see cref="Learn"/>. A recorder host that has not learned the version a write runs at cannot
/// derive that instance's leader and will not serve it, so a write reaches a quorum only where the previous
/// version has been disseminated to one.
/// </para>
/// </remarks>
public sealed class QuePaxaVersionedRegister<TValue>
{
    private int writing;


    /// <summary>
    /// Initializes a register for <paramref name="self"/> over <paramref name="recorders"/>.
    /// </summary>
    /// <param name="recorders">The recorder host endpoints, in index order.</param>
    /// <param name="self">This replica, which must appear in <paramref name="schedule"/>'s order.</param>
    /// <param name="schedule">The leader derivation every replica shares.</param>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <param name="attemptsPerRecorder">How many times one step may send to one recorder before abandoning it for that step. Must be at least one.</param>
    /// <param name="timeProvider">The clock the hedging delay runs against.</param>
    /// <param name="observeCommittedVersion">
    /// An optional signal that a version has already been committed, letting a delayed writer stand down
    /// instead of running an instance that is closed. When <see langword="null"/> every scheduled writer
    /// activates on its delay.
    /// </param>
    /// <param name="readCommittedRecords">
    /// Optional per-host queries used by <see cref="ReadAsync"/> to catch up on versions this replica missed.
    /// When <see langword="null"/> the register can still write, and <see cref="ReadAsync"/> reports only what
    /// it already holds. A host driven by a <see cref="QuePaxaVersionedRunner{TValue}"/> supplies that
    /// runner's <see cref="QuePaxaVersionedRunner{TValue}.ReadCommittedAsync"/>, which answers through the
    /// loop after making its state durable.
    /// </param>
    /// <param name="publishCommittedRecord">
    /// An optional sink offered every record an attempt decides, this replica's own and another's alike,
    /// before the attempt returns. When <see langword="null"/> the register decides and retains records
    /// without disseminating them, and the next version stays unservable until a host learns the current one
    /// by some other route.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="recorders"/>, <paramref name="schedule"/>, <paramref name="drawPriority"/> or <paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="recorders"/> is empty, or if <paramref name="self"/> is not in the schedule.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="attemptsPerRecorder"/> is less than one.</exception>
    public QuePaxaVersionedRegister(
        IReadOnlyList<VersionedRecorderEndpointDelegate<VersionedValue<TValue>>> recorders,
        ReplicaId self,
        QuePaxaLeaderSchedule schedule,
        ProposalPrioritySourceDelegate drawPriority,
        int attemptsPerRecorder,
        TimeProvider timeProvider,
        ObserveCommittedVersionDelegate? observeCommittedVersion = null,
        IReadOnlyList<ReadCommittedRecordDelegate<TValue>>? readCommittedRecords = null,
        PublishCommittedRecordDelegate<TValue>? publishCommittedRecord = null)
    {
        ArgumentNullException.ThrowIfNull(recorders);
        if(recorders.Count == 0)
        {
            throw new ArgumentException("At least one recorder is required.", nameof(recorders));
        }

        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(drawPriority);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsPerRecorder, 1);

        if(!schedule.Schedule.Contains(self))
        {
            throw new ArgumentException("The writing replica is not in the leader schedule.", nameof(self));
        }

        Recorders = recorders;
        Self = self;
        LeaderSchedule = schedule;
        DrawPriority = drawPriority;
        AttemptsPerRecorder = attemptsPerRecorder;
        TimeProvider = timeProvider;
        ObserveCommittedVersion = observeCommittedVersion;
        ReadCommittedRecords = readCommittedRecords;
        PublishCommittedRecord = publishCommittedRecord;
    }


    /// <summary>This replica.</summary>
    public ReplicaId Self { get; }

    /// <summary>The leader derivation this register and its recorders share.</summary>
    public QuePaxaLeaderSchedule LeaderSchedule { get; }

    /// <summary>The number of recorder hosts.</summary>
    public int RecorderCount => Recorders.Count;

    /// <summary>How many times one step may send to one recorder before abandoning it for that step.</summary>
    public int AttemptsPerRecorder { get; }

    /// <summary>The highest committed record this register knows of, or <see langword="null"/> when it knows of none.</summary>
    public VersionedValue<TValue>? Committed { get; private set; }

    /// <summary>The version this register's next write runs at.</summary>
    public RegisterVersion NextVersion => Committed is { } committed ? committed.Version.Next() : RegisterVersion.First;

    /// <summary>The delay this replica waits before writing, which is zero for the version's leader.</summary>
    public TimeSpan Delay => LeaderSchedule.ScheduleFor(Committed?.Writer).DelayFor(Self);


    private IReadOnlyList<VersionedRecorderEndpointDelegate<VersionedValue<TValue>>> Recorders { get; }

    private ProposalPrioritySourceDelegate DrawPriority { get; }

    private TimeProvider TimeProvider { get; }

    private ObserveCommittedVersionDelegate? ObserveCommittedVersion { get; }

    private IReadOnlyList<ReadCommittedRecordDelegate<TValue>>? ReadCommittedRecords { get; }

    private PublishCommittedRecordDelegate<TValue>? PublishCommittedRecord { get; }

    /// <summary>The lane counter this register allocates its own proposals from.</summary>
    private LaneAllocation Lanes { get; set; } = LaneAllocation.None;


    /// <summary>
    /// Adopts <paramref name="committed"/> as this register's knowledge of the register's committed state.
    /// </summary>
    /// <param name="committed">A decided record.</param>
    /// <returns><see langword="true"/> when the record advanced this register, and <see langword="false"/> when it was not newer than what it already held.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A record that does not advance is ignored rather than refused, so knowledge of committed state only
    /// ever runs forward.
    /// </remarks>
    public bool Learn(VersionedValue<TValue> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);

        if(Committed is { } held && committed.Version <= held.Version)
        {
            return false;
        }

        Committed = committed;

        return true;
    }


    /// <summary>
    /// Catches up on versions this replica missed by asking the recorder hosts what they have learned.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest committed record known after the round, or <see langword="null"/> when none is known.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled, and if a host answers with a cancellation of its own.</exception>
    /// <remarks>
    /// <para>
    /// This needs no quorum and takes no consensus step. A committed record is a decided fact and the protocol
    /// assumes crash faults, so one honest host reporting a version settles it. A host that faults is skipped,
    /// so learning from fewer hosts than exist is a weaker result rather than a wrong one. A cancellation ends
    /// the round only when it is the caller's own signal arriving by another route, which the filter tests
    /// rather than assumes: a host whose runner stopped answers its pending read cancelled under the runner's
    /// token, and that is the host's unavailability wearing a cancellation's type, so it is skipped like any
    /// other failing host rather than aborting the catch-up at every host after it.
    /// </para>
    /// <para>
    /// Learning nothing new does not prove this replica is current. It is equally the state left behind by a
    /// writer that committed a version and stopped before telling anyone, which no query can distinguish. A
    /// caller resolves it by writing: the write either commits, proving the register was current, or comes
    /// back superseded carrying the record the recorders were already holding, which is also how that crash is
    /// recovered from.
    /// </para>
    /// </remarks>
    public async Task<VersionedValue<TValue>?> ReadAsync(CancellationToken cancellationToken)
    {
        if(ReadCommittedRecords is null)
        {
            return Committed;
        }

        foreach(ReadCommittedRecordDelegate<TValue> read in ReadCommittedRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            VersionedValue<TValue>? reported;
            try
            {
                reported = await read(cancellationToken).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch(Exception)
            {
                continue;
            }

            if(reported is not null)
            {
                _ = Learn(reported);
            }
        }

        return Committed;
    }


    /// <summary>
    /// Makes one attempt to write <paramref name="value"/> at the next version.
    /// </summary>
    /// <param name="value">The value to propose.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the attempt established.</returns>
    /// <exception cref="InvalidOperationException">Thrown if a write is already in flight on this register, if the version range is spent, or if a recorder answered for an instance other than the one addressed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// One attempt and no retry, so an undecided outcome is reported rather than retried. A caller that wants
    /// the retry loop calls <see cref="WriteAsync"/>, which is the only one of the two that can recompute a
    /// value against a version someone else closed. Calling this one again at a version it left undecided is
    /// safe: the register allocates the next lane, so the second attempt proposes under its own key.
    /// </remarks>
    public async Task<QuePaxaWriteOutcome<TValue>> TryWriteAsync(TValue value, CancellationToken cancellationToken)
    {
        EnterWrite();
        try
        {
            return await AttemptAsync(value, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LeaveWrite();
        }
    }


    /// <summary>
    /// Writes the value <paramref name="update"/> computes, retrying against whatever won when another
    /// replica's write closes the version first.
    /// </summary>
    /// <param name="update">
    /// Computes the value to propose from the value this register currently believes committed, which is the
    /// default when nothing is. It runs once per attempt and outside the consensus round.
    /// </param>
    /// <param name="maxAttempts">How many consensus attempts the write may spend. Must be at least one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the write established.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="update"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxAttempts"/> is less than one.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a write is already in flight on this register, if the version range is spent, or if a recorder answered for an instance other than the one addressed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// <para>
    /// <paramref name="update"/> runs outside the round. It sees the value this replica believes committed,
    /// not a value recovered inside the round, so a losing attempt discards its proposal entirely and the next
    /// attempt recomputes from the winner. An update that is not idempotent is safe here only because a
    /// superseded attempt is never composed with the winner; an update that assumed composition would silently
    /// lose a write.
    /// </para>
    /// <para>
    /// The three outcomes are retried differently. A committed attempt returns. A superseded attempt adopts
    /// the winner, advances to the version after it, and recomputes. An undecided attempt retries at the same
    /// version on a fresh lane and recomputes from the same value, because it learned nothing: its own
    /// proposal may still be carried by another proposer and decided later, so treating it as a loss would
    /// abandon a write that is still live.
    /// </para>
    /// </remarks>
    public async Task<QuePaxaWriteOutcome<TValue>> WriteAsync(Func<TValue?, TValue> update, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        EnterWrite();
        try
        {
            QuePaxaWriteOutcome<TValue> outcome = new(QuePaxaWriteStatus.Undecided, NextVersion, default, null, RecorderStep.Zero, 0, false);

            for(int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                TValue proposed = update(Committed is { } committed ? committed.Value : default);
                outcome = await AttemptAsync(proposed, outcome.Attempts, cancellationToken).ConfigureAwait(false);

                if(outcome.Status == QuePaxaWriteStatus.Committed)
                {
                    return outcome;
                }
            }

            return outcome;
        }
        finally
        {
            LeaveWrite();
        }
    }


    private async Task<QuePaxaWriteOutcome<TValue>> AttemptAsync(TValue value, int spent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ReplicaId? previousWriter = Committed?.Writer;
        RegisterVersion version = NextVersion;

        TimeSpan delay = LeaderSchedule.ScheduleFor(previousWriter).DelayFor(Self);
        if(delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);

            //The version's leader waits no delay and so never stands down, which leaves the schedule an activator.
            if(ObserveCommittedVersion is not null)
            {
                RegisterVersion observed = await ObserveCommittedVersion(cancellationToken).ConfigureAwait(false);
                if(observed >= version)
                {
                    return new QuePaxaWriteOutcome<TValue>(QuePaxaWriteStatus.Undecided, version, default, null, RecorderStep.Zero, spent, false);
                }
            }
        }

        VersionedValue<TValue> record = new(version, Self, value);
        ProposerLane proposerLane = AllocateLane(version);
        ProposerLane? believedLeader = LeaderSchedule.LeaderFor(previousWriter);

        var proposer = new QuePaxaProposer<VersionedValue<TValue>>(
            EndpointsFor(version),
            proposerLane,
            DrawPriority,
            AttemptsPerRecorder);

        QuePaxaOutcome<VersionedValue<TValue>> outcome = await proposer.ProposeAsync(believedLeader, record, cancellationToken).ConfigureAwait(false);

        if(!outcome.IsDecided || outcome.Value is not { } decided)
        {
            return new QuePaxaWriteOutcome<TValue>(QuePaxaWriteStatus.Undecided, version, default, null, RecorderStep.Zero, spent + 1, true);
        }

        //A misrouted decision kept out of the committed state cannot set the next instance's leader.
        if(decided.Version != version)
        {
            throw new InvalidOperationException($"The instance for version {version.Value} decided a record carrying version {decided.Version.Value}, so a request reached an instance it was not addressed to.");
        }

        _ = Learn(decided);

        if(PublishCommittedRecord is not null)
        {
            await PublishCommittedRecord(decided, cancellationToken).ConfigureAwait(false);
        }

        //A value can be chosen by a quorum and still not be this writer's, so the test is over the whole record.
        QuePaxaWriteStatus status = decided.Equals(record) ? QuePaxaWriteStatus.Committed : QuePaxaWriteStatus.Superseded;

        return new QuePaxaWriteOutcome<TValue>(status, version, decided.Value, decided.Writer, outcome.DecidedAt, spent + 1, true);
    }


    private RecorderEndpointDelegate<VersionedValue<TValue>>[] EndpointsFor(RegisterVersion version)
    {
        var endpoints = new RecorderEndpointDelegate<VersionedValue<TValue>>[Recorders.Count];
        for(int index = 0; index < Recorders.Count; index++)
        {
            VersionedRecorderEndpointDelegate<VersionedValue<TValue>> host = Recorders[index];
            endpoints[index] = async (request, token) =>
            {
                VersionedRecordReply<VersionedValue<TValue>> reply = await host(new VersionedRecordRequest<VersionedValue<TValue>>(version, request), token).ConfigureAwait(false);

                //A reply from another instance would enter this quorum and break the majority intersection
                //agreement rests on.
                if(reply.Version != version)
                {
                    throw new InvalidOperationException($"A recorder answering version {version.Value} replied for version {reply.Version.Value}, so the transport correlated a reply to the wrong instance.");
                }

                return reply.Reply;
            };
        }

        return endpoints;
    }


    /// <summary>
    /// The lane this register's next proposal at <paramref name="version"/> runs on.
    /// </summary>
    /// <param name="version">The version the proposal is made at.</param>
    /// <returns>A lane no proposal of this register's has used at that version.</returns>
    /// <remarks>
    /// Every proposal this register makes draws its lane here, which is what keeps one proposal key to one
    /// value. A second proposal at one version needs a second identity whatever brought it about, so the
    /// counter is keyed on the version rather than on the call that asked for it, and a version this register
    /// has not proposed at yet starts again at lane zero.
    /// </remarks>
    private ProposerLane AllocateLane(RegisterVersion version)
    {
        Lanes = Lanes.At(version);
        var lane = new ProposerLane(Self, Lanes.NextLane);
        Lanes = Lanes.Advanced();

        return lane;
    }


    private void EnterWrite()
    {
        if(Interlocked.Exchange(ref writing, 1) != 0)
        {
            throw new InvalidOperationException("A versioned register writes one value at a time; two writes at one version would propose on one lane, and one proposal key naming two values is what the key's uniqueness contract forbids.");
        }
    }


    private void LeaveWrite() => Interlocked.Exchange(ref writing, 0);
}
