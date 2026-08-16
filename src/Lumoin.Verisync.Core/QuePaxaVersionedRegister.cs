using System;
using System.Collections.Immutable;
using System.Diagnostics;
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
/// The membership is derived on the same rule and from the same record. A register is handed one genesis
/// configuration and every later one is the next configuration of the record it holds, so
/// <see cref="ActiveConfiguration"/> is a memo rather than a setting, and the recorder set an attempt
/// addresses, the quorum it counts, the hedging order it waits in and the leader it believes are all
/// functions of one agreed fact. An ordinary write carries the membership forward unchanged;
/// <see cref="ReconfigureAsync"/> is the only path that proposes a different one.
/// </para>
/// <para>
/// EVERY ATTEMPT ADDRESSES ONE CAPTURED INSTANCE. An attempt reads the committed record once, builds a
/// <see cref="RegisterInstance"/> from that one reference before it waits its hedging delay, and resolves the
/// version, the membership, the leader and the endpoints from the capture alone. A record learned while the
/// attempt was parked therefore does not move the instance under it: an attempt that resolved its endpoints
/// after the delay would send the version it computed before it to the recorder set of a membership it
/// learned after, and a quorum counted over the wrong set is a decision taken by a minority of the instance
/// it names.
/// </para>
/// <para>
/// Membership is answered per version and never at construction. A register for a replica that is not yet a
/// member is how a joiner starts, and one for a replica a change removed is what that replica becomes, so
/// both are constructible and both report <see cref="QuePaxaWriteStatus.OutsideConfiguration"/> from a write
/// rather than throwing at a caller that could not have known.
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
    /// Initializes a register for <paramref name="self"/> over <paramref name="genesis"/>'s chain.
    /// </summary>
    /// <param name="genesis">The chain's genesis membership, which is deployment configuration and the membership the first instance runs under.</param>
    /// <param name="self">This replica, which need not be a member of <paramref name="genesis"/>.</param>
    /// <param name="baseDelay">The hedging delay increment per position in the membership order. Zero activates every replica at once.</param>
    /// <param name="resolveRecorder">Resolves the transport that reaches one member, called per member per attempt; the attempt's requests then go through the endpoint it returns.</param>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <param name="attemptsPerRecorder">How many times one step may send to one recorder before abandoning it for that step. Must be at least one.</param>
    /// <param name="timeProvider">The clock the hedging delay runs against.</param>
    /// <param name="observeCommittedVersion">
    /// An optional signal that a version has already been committed, letting a delayed writer stand down
    /// instead of running an instance that is closed. When <see langword="null"/> every scheduled writer
    /// activates on its delay.
    /// </param>
    /// <param name="resolveCommittedRecordReader">
    /// An optional resolver of per-member catch-up queries: <see cref="ReadAsync"/> invokes it with a member
    /// and then invokes the query it returns to learn versions this replica missed. When
    /// <see langword="null"/> the register can still write, and <see cref="ReadAsync"/> reports only what it
    /// already holds. A host driven by a <see cref="QuePaxaVersionedRunner{TValue}"/> resolves to that
    /// runner's <see cref="QuePaxaVersionedRunner{TValue}.ReadCommittedAsync"/>, which answers through the
    /// loop after making its state durable.
    /// </param>
    /// <param name="publishCommittedRecord">
    /// An optional sink offered every record an attempt decides, this replica's own and another's alike,
    /// before the attempt returns. When <see langword="null"/> the register decides and retains records
    /// without disseminating them, and the next version stays unservable until a host learns the current one
    /// by some other route.
    /// </param>
    /// <param name="observeMemberVersion">
    /// An optional per-member version query, which is what <see cref="ReadReadinessAsync(TimeSpan, CancellationToken)"/> is built from.
    /// Unlike its resolver neighbours it is the query itself, invoked directly with the member and the token.
    /// When <see langword="null"/> this register reports no readiness at all rather than reporting an empty
    /// one.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="genesis"/>, <paramref name="resolveRecorder"/>, <paramref name="drawPriority"/> or <paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="attemptsPerRecorder"/> is less than one, and if <paramref name="baseDelay"/> is negative or large enough that the last position's delay would not fit in a <see cref="TimeSpan"/>.</exception>
    /// <remarks>
    /// No recorder list and no leader schedule are supplied, and no membership check is made here. All three
    /// are consequences of the same rule: the recorder set, the hedging order and the leader are derived from
    /// the record this register holds, and a genesis membership is the base case of that derivation rather
    /// than a parallel list that could disagree with it.
    /// </remarks>
    public QuePaxaVersionedRegister(
        QuePaxaConfiguration genesis,
        ReplicaId self,
        TimeSpan baseDelay,
        ResolveRecorderEndpointDelegate<TValue> resolveRecorder,
        ProposalPrioritySourceDelegate drawPriority,
        int attemptsPerRecorder,
        TimeProvider timeProvider,
        ObserveCommittedVersionDelegate? observeCommittedVersion = null,
        ResolveCommittedRecordReaderDelegate<TValue>? resolveCommittedRecordReader = null,
        PublishCommittedRecordDelegate<TValue>? publishCommittedRecord = null,
        ObserveMemberVersionDelegate? observeMemberVersion = null)
    {
        ArgumentNullException.ThrowIfNull(genesis);
        ArgumentNullException.ThrowIfNull(resolveRecorder);
        ArgumentNullException.ThrowIfNull(drawPriority);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptsPerRecorder, 1);

        Genesis = genesis;
        Self = self;
        BaseDelay = baseDelay;
        ResolveRecorder = resolveRecorder;
        DrawPriority = drawPriority;
        AttemptsPerRecorder = attemptsPerRecorder;
        TimeProvider = timeProvider;
        ObserveCommittedVersion = observeCommittedVersion;
        ResolveCommittedRecordReader = resolveCommittedRecordReader;
        PublishCommittedRecord = publishCommittedRecord;
        ObserveMemberVersion = observeMemberVersion;
        ActiveConfiguration = genesis;
        LeaderSchedule = ScheduleFor(genesis, baseDelay);

        //The chain a register belongs to never moves, because the active membership's chain is the genesis
        //membership's by an enforced invariant, so the dimension is rendered once instead of per emission.
        Chain = Convert.ToHexStringLower(genesis.Cluster.AsSpan());

        RecordMembership();
    }


    /// <summary>This replica.</summary>
    public ReplicaId Self { get; }

    /// <summary>The chain's genesis membership, which is what the active membership stands at until a record moves it.</summary>
    public QuePaxaConfiguration Genesis { get; }

    /// <summary>The hedging delay increment per position in the membership order.</summary>
    /// <remarks>
    /// Local tuning and not part of the membership. A delay orders sending and settles no protocol rule, so
    /// replicas may disagree on it at the cost of redundant rounds and never of agreement.
    /// </remarks>
    public TimeSpan BaseDelay { get; }

    /// <summary>
    /// The membership this register's next write runs under, which is the committed record's next
    /// configuration or <see cref="Genesis"/> when it holds no record.
    /// </summary>
    /// <remarks>
    /// A memo of the committed record and never a setting, recomputed where the record moves and nowhere
    /// else. It is the same derivation <see cref="QuePaxaVersionedNode{TValue}"/> runs, so a register and the
    /// hosts holding the same record agree on the recorder set without exchanging a message about it.
    /// </remarks>
    public QuePaxaConfiguration ActiveConfiguration { get; private set; }

    /// <summary>The leader derivation this register and its recorders share, taken over <see cref="ActiveConfiguration"/>.</summary>
    /// <remarks>
    /// A memo on the same rule as <see cref="ActiveConfiguration"/>, carrying <see cref="BaseDelay"/> because
    /// this side of the seam is the one that hedges.
    /// </remarks>
    public QuePaxaLeaderSchedule LeaderSchedule { get; private set; }

    /// <summary>How many times one step may send to one recorder before abandoning it for that step.</summary>
    public int AttemptsPerRecorder { get; }

    /// <summary>The highest committed record this register knows of, or <see langword="null"/> when it knows of none.</summary>
    public VersionedValue<TValue>? Committed { get; private set; }

    /// <summary>The version this register's next write runs at.</summary>
    public RegisterVersion NextVersion => Committed is { } committed ? committed.Version.Next() : RegisterVersion.First;

    /// <summary>
    /// The instance this register's next attempt would address, read as one triple.
    /// </summary>
    /// <remarks>
    /// The version, the membership and the previous writer are three consequences of one record, and reading
    /// them one at a time off this register can pair a version from before a learn with a membership from
    /// after it. This is the read that cannot, and it is the same capture an attempt makes.
    /// </remarks>
    public RegisterInstance Instance => InstanceFor(Committed);

    /// <summary>
    /// The delay this replica waits before writing, which is zero for the version's leader and
    /// <see langword="null"/> when this replica is outside <see cref="ActiveConfiguration"/>.
    /// </summary>
    /// <remarks>
    /// A non-member has no position in the hedging order, so it has no delay rather than a zero one: zero is
    /// what the leader waits, and reporting it for a replica that will not write at all would say the
    /// opposite of what is true.
    /// </remarks>
    public TimeSpan? Delay => DelayFor(Instance);


    private ResolveRecorderEndpointDelegate<TValue> ResolveRecorder { get; }

    private ProposalPrioritySourceDelegate DrawPriority { get; }

    private TimeProvider TimeProvider { get; }

    private ObserveCommittedVersionDelegate? ObserveCommittedVersion { get; }

    private ResolveCommittedRecordReaderDelegate<TValue>? ResolveCommittedRecordReader { get; }

    private PublishCommittedRecordDelegate<TValue>? PublishCommittedRecord { get; }

    private ObserveMemberVersionDelegate? ObserveMemberVersion { get; }

    /// <summary>The chain identity this register's measurements are dimensioned by, rendered once.</summary>
    private string Chain { get; }

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
    /// ever runs forward. It is also where the membership moves, on the same rule and for the same reason a
    /// host's does: the record names the membership the version after it runs under, and a record is the only
    /// agreed thing a membership can be derived from.
    /// </remarks>
    public bool Learn(VersionedValue<TValue> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);

        if(Committed is { } held && committed.Version <= held.Version)
        {
            return false;
        }

        Committed = committed;
        ActiveConfiguration = committed.NextConfiguration;
        LeaderSchedule = ScheduleFor(ActiveConfiguration, BaseDelay);

        RecordMembership();

        return true;
    }


    /// <summary>
    /// Catches up on versions this replica missed by asking the members of the active membership what they
    /// have learned.
    /// </summary>
    /// <param name="queryDeadline">
    /// How long one member's query may take before that member is given up on. Must be positive, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait for every member however long it takes.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest committed record known after the round, or <see langword="null"/> when none is known.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="queryDeadline"/> is neither positive nor <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled, and if a host answers with a cancellation of its own.</exception>
    /// <remarks>
    /// <para>
    /// This needs no quorum and takes no consensus step. A committed record is a decided fact and the protocol
    /// assumes crash faults, so one honest host reporting a version settles it. A host that faults is skipped,
    /// so learning from fewer hosts than exist is a weaker result rather than a wrong one, and a member this
    /// register cannot resolve at all is skipped on the same rule. A cancellation ends the round only when it
    /// is the caller's own signal arriving by another route, which the filter tests rather than assumes: a
    /// host whose runner stopped answers its pending read cancelled under the runner's token, and that is the
    /// host's unavailability wearing a cancellation's type, so it is skipped like any other failing host
    /// rather than aborting the catch-up at every host after it.
    /// </para>
    /// <para>
    /// A member that answers nothing at all is skipped on that same rule once
    /// <paramref name="queryDeadline"/> has passed, because a silent host is a failing host that has not
    /// admitted it yet, and a catch-up that parks is worse than one that learns from fewer hosts. The deadline
    /// is spent per member rather than over the round, so one silent member costs one deadline and not the
    /// catch-up.
    /// </para>
    /// <para>
    /// The member list is read once, before the first query. A record adopted mid-round moves the membership,
    /// and re-reading it between queries would ask half a round of one membership and half of another; asking
    /// one membership and letting the next round ask the next one is a weaker result rather than an
    /// inconsistent one.
    /// </para>
    /// <para>
    /// Learning nothing new does not prove this replica is current. It is equally the state left behind by a
    /// writer that committed a version and stopped before telling anyone, which no query can distinguish. A
    /// caller resolves it by writing: the write either commits, proving the register was current, or comes
    /// back superseded carrying the record the recorders were already holding, which is also how that crash is
    /// recovered from.
    /// </para>
    /// </remarks>
    public async Task<VersionedValue<TValue>?> ReadAsync(TimeSpan queryDeadline, CancellationToken cancellationToken)
    {
        ValidateDeadline(queryDeadline, nameof(queryDeadline));

        if(ResolveCommittedRecordReader is null)
        {
            return Committed;
        }

        foreach(HostId configured in ActiveConfiguration.Members)
        {
            ReplicaId member = configured.Replica;

            cancellationToken.ThrowIfCancellationRequested();

            VersionedValue<TValue>? reported;
            try
            {
                reported = await WithinDeadlineAsync(
                    token => ResolveCommittedRecordReader(member)(token).AsTask(),
                    queryDeadline,
                    cancellationToken).ConfigureAwait(false);
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
    /// Asks every member of the active membership how far it has caught up.
    /// </summary>
    /// <param name="probeDeadline">
    /// How long one member's probe may take before that member is reported unreachable. Must be positive, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait for every member however long it takes.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One entry per member, in the membership's own order, beside the membership it was measured over.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="probeDeadline"/> is neither positive nor <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    /// <exception cref="ConsensusRefusedException">Thrown if this register was built without a per-member version query, carrying <see cref="ConsensusRefusal.ReadinessWithoutMemberQuery"/>, if a member's probe was answered by a host asserting another member's identity, carrying <see cref="ConsensusRefusal.ProbeAnsweredByAnotherMember"/>, and if it was answered under that member's identity by a store other than the admitted one, carrying <see cref="ConsensusRefusal.ProbeAnsweredByAnotherStore"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled, and if a member answers with a cancellation of its own.</exception>
    /// <remarks>
    /// The active-membership form of <see cref="ReadReadinessAsync(QuePaxaConfiguration, TimeSpan, CancellationToken)"/>,
    /// which carries the rules: what the report gates, why an unwired query is refused, why a fault is a
    /// member's answer, why silence is the same answer, and why an answer naming another member fails the
    /// report.
    /// </remarks>
    public Task<RegisterReadiness> ReadReadinessAsync(TimeSpan probeDeadline, CancellationToken cancellationToken) => ReadReadinessAsync(ActiveConfiguration, probeDeadline, cancellationToken);


    /// <summary>
    /// Asks every member of <paramref name="membership"/> how far it has caught up, whether or not that
    /// membership is the one this register runs under.
    /// </summary>
    /// <param name="membership">The membership to measure over.</param>
    /// <param name="probeDeadline">
    /// How long one member's probe may take before that member is reported unreachable. Must be positive, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to wait for every member however long it takes.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One entry per member, in the membership's own order, beside the membership it was measured over.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="membership"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="membership"/> names a chain other than this register's.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="probeDeadline"/> is neither positive nor <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    /// <exception cref="ConsensusRefusedException">Thrown if this register was built without a per-member version query, carrying <see cref="ConsensusRefusal.ReadinessWithoutMemberQuery"/>, if a member's probe was answered by a host asserting another member's identity, carrying <see cref="ConsensusRefusal.ProbeAnsweredByAnotherMember"/>, and if it was answered under that member's identity by a store other than the admitted one, carrying <see cref="ConsensusRefusal.ProbeAnsweredByAnotherStore"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled, and if a member answers with a cancellation of its own.</exception>
    /// <remarks>
    /// <para>
    /// This is the observable a membership change is gated on. A joiner is added, disseminated to, and only
    /// then written through, and a host is decommissioned only once a quorum that excludes it has learned the
    /// record that removed it; both gates are a quorum-has-learned question about named replicas, which is
    /// what this answers and what no aggregate can.
    /// </para>
    /// <para>
    /// The membership parameter is what makes the incoming side of a change observable before the change
    /// commits: an operator gating an admission asks the membership the change would install, while no
    /// register yet runs under it, and a post-outage sweep asks over whatever membership a surviving record
    /// names. A membership of another chain is refused, because a report over it would answer a question
    /// about a different register.
    /// </para>
    /// <para>
    /// It refuses rather than reporting nothing when no query was supplied. An empty or all-unreachable report
    /// is what a genuinely silent cluster looks like, and an operator gate cannot tell that apart from a
    /// register that was never wired to ask.
    /// </para>
    /// <para>
    /// A member that faults is reported unreachable rather than failing the round, on the same rule
    /// <see cref="ReadAsync"/> skips a failing host by: the report is about availability, so a host's
    /// unavailability is its answer rather than an error. A cancellation is a member's answer too unless it is
    /// the caller's own signal.
    /// </para>
    /// <para>
    /// A MEMBER THAT ANSWERS NOTHING AT ALL IS THE SAME ANSWER, and <paramref name="probeDeadline"/> is what
    /// decides when nothing has been answered. Silence and a fault are one entry rather than two, because the
    /// report exists to say whether a named replica has learned a version and both answer that with the same
    /// thing: this member did not tell us. A gate given a third state could not act differently on it. What a
    /// deadline changes is that the question terminates: the probe is raced against the deadline rather than
    /// merely told about it, so a query that never returns and ignores its token costs one member's entry
    /// instead of the whole report. An abandoned probe holds whatever it holds until it completes, which is
    /// the price of an answer arriving at all.
    /// </para>
    /// <para>
    /// The deadline is spent per member and not over the report. Members are asked in turn, so a report over a
    /// wholly silent membership takes the deadline once per member; bounding the report instead would mark
    /// later members unreachable because earlier ones were slow, which would report about the caller's
    /// patience rather than about those members.
    /// </para>
    /// <para>
    /// An answer from a host other than the admitted one fails the report loudly rather than being counted or
    /// reported unreachable. The report is counted over distinct members of the membership it measures,
    /// reached through an endpoint map a deployment wires by hand, and two probe routes landing on one host
    /// would let one replica fill two slots and a decommission gate clear on fewer distinct replicas than it
    /// claims — the wiring error the record path refuses at its quorum. An answer carrying the right replica
    /// under another store is refused on the same comparison and reported as its own rule, because a gate
    /// that retires a member on a report from the store that replaced it measures the wrong store at the one
    /// moment the membership is being changed. The identity is the answering host's own claim and is not
    /// authentication.
    /// </para>
    /// </remarks>
    public async Task<RegisterReadiness> ReadReadinessAsync(QuePaxaConfiguration membership, TimeSpan probeDeadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ValidateDeadline(probeDeadline, nameof(probeDeadline));

        if(!membership.Cluster.Equals(ActiveConfiguration.Cluster))
        {
            throw new ArgumentException("The membership names another chain, so a report over it would answer a question about a different register.", nameof(membership));
        }

        if(ObserveMemberVersion is null)
        {
            throw new ConsensusRefusedException(ConsensusRefusal.ReadinessWithoutMemberQuery, "This register was built without a per-member version query and cannot report readiness. A report of nothing is indistinguishable from a cluster that answered nothing, and a decommission gate cleared against the second is how a quorum is lost.");
        }

        using Activity? activity = VerisyncActivitySource.Instance.StartActivity(VerisyncTelemetry.ActivityNameConsensusReadiness);

        ImmutableArray<MemberReadiness>.Builder reports = ImmutableArray.CreateBuilder<MemberReadiness>(membership.Members.Length);
        foreach(HostId configured in membership.Members)
        {
            ReplicaId member = configured.Replica;

            cancellationToken.ThrowIfCancellationRequested();

            MemberVersionReport reported;
            try
            {
                reported = await WithinDeadlineAsync(
                    token => ObserveMemberVersion(member, token).AsTask(),
                    probeDeadline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch(Exception failure)
            {
                //The report collapses silence and a fault into one entry because a gate cannot act
                //differently on them. A human diagnosing can, so the two are told apart here.
                RecordProbe(member, failure is TimeoutException ? VerisyncTelemetry.ProbeTimedOut : VerisyncTelemetry.ProbeFaulted);
                reports.Add(new MemberReadiness(member, null));

                continue;
            }

            //An answer from another host would let one host fill two slots of a report counted over distinct
            //members, so it is surfaced loudly rather than as a weaker reading. The comparison is over the
            //whole admitted host, and the two ways it can fail are different situations: a probe that reached
            //the wrong member is a wiring defect, and one answered under the right member by another store is
            //a store this membership never admitted reporting on a gate that would retire the one it did.
            if(!reported.Recorder.Equals(configured))
            {
                throw reported.Recorder.Replica.Equals(member)
                    ? new ConsensusRefusedException(ConsensusRefusal.ProbeAnsweredByAnotherStore, $"The version probe for member {member} was answered under {reported.Recorder.Incarnation} where the membership admits {configured.Incarnation}, so a store other than the admitted one is reporting for that member. A readiness report counting that answer would clear a decommission gate on a store the membership never admitted.")
                    : new ConsensusRefusedException(ConsensusRefusal.ProbeAnsweredByAnotherMember, $"The version probe for member {member} was answered by {reported.Recorder.Replica}, so this deployment's endpoint map does not reach the membership it names. A readiness report counting that answer would clear a decommission gate on fewer distinct replicas than it claims.");
            }

            RecordProbe(member, VerisyncTelemetry.ProbeAnswered);
            reports.Add(new MemberReadiness(member, reported.Version));
        }

        RegisterReadiness readiness = new(membership, reports.ToImmutable());

        if(activity is not null)
        {
            _ = activity.SetTag(VerisyncTelemetry.TagCluster, Chain);
            _ = activity.SetTag(VerisyncTelemetry.ActivityMeasuredMembers, readiness.Members.Length);
            _ = activity.SetTag(VerisyncTelemetry.ActivityReachableMembers, readiness.Reachable);
        }

        return readiness;
    }


    /// <summary>
    /// Makes one attempt to write <paramref name="value"/> at the next version.
    /// </summary>
    /// <param name="value">The value to propose.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the attempt established.</returns>
    /// <exception cref="ConsensusRefusedException">Thrown if a write is already in flight on this register, carrying <see cref="ConsensusRefusal.ConcurrentWrite"/>; if the version range is spent, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>; or if the round decided a record carrying a version other than the instance's own, which is a misrouted decision refused rather than adopted, carrying <see cref="ConsensusRefusal.MisroutedDecision"/>. A recorder whose reply mis-answers its envelope — another instance's version, or another member's name — is absorbed as an unreachable recorder instead and surfaces as an undecided outcome, never as this throw.</exception>
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
        using Activity? activity = VerisyncActivitySource.Instance.StartActivity(VerisyncTelemetry.ActivityNameConsensusWrite);
        try
        {
            return Measured(activity, await AttemptAsync(_ => value, null, 0, cancellationToken).ConfigureAwait(false));
        }
        catch(Exception failure)
        {
            _ = activity?.SetStatus(ActivityStatusCode.Error, failure.Message);

            throw;
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
    /// <exception cref="ConsensusRefusedException">Thrown if a write is already in flight on this register, carrying <see cref="ConsensusRefusal.ConcurrentWrite"/>; if the version range is spent, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>; or if the round decided a record carrying a version other than the instance's own, which is a misrouted decision refused rather than adopted, carrying <see cref="ConsensusRefusal.MisroutedDecision"/>. A recorder whose reply mis-answers its envelope — another instance's version, or another member's name — is absorbed as an unreachable recorder instead and surfaces as an undecided outcome, never as this throw.</exception>
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
    /// The four outcomes are retried differently. A committed attempt returns. A superseded attempt adopts
    /// the winner, advances to the version after it, and recomputes. An undecided attempt retries at the same
    /// version on a fresh lane and recomputes from the same value, because it learned nothing: its own
    /// proposal may still be carried by another proposer and decided later, so treating it as a loss would
    /// abandon a write that is still live. An attempt this replica is outside the membership of returns at
    /// once and spends no attempt at all, because retrying is what a caller does when the next attempt might
    /// establish something different, and only a configuration change can change this one.
    /// </para>
    /// <para>
    /// The membership is carried forward and never changed here. Every record this writes names the
    /// membership its own instance ran under, so a write that is not a reconfiguration cannot move the
    /// recorder set, whatever it does to the value. <see cref="ReconfigureAsync"/> is the only path that
    /// proposes a different one.
    /// </para>
    /// </remarks>
    public async Task<QuePaxaWriteOutcome<TValue>> WriteAsync(ComputeRegisterValueDelegate<TValue> update, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        EnterWrite();
        using Activity? activity = VerisyncActivitySource.Instance.StartActivity(VerisyncTelemetry.ActivityNameConsensusWrite);
        try
        {
            return Measured(activity, await RunAttemptsAsync(update, null, maxAttempts, cancellationToken).ConfigureAwait(false));
        }
        catch(Exception failure)
        {
            _ = activity?.SetStatus(ActivityStatusCode.Error, failure.Message);

            throw;
        }
        finally
        {
            LeaveWrite();
        }
    }


    /// <summary>
    /// Installs the membership <paramref name="change"/> computes, carrying the committed value forward
    /// unchanged.
    /// </summary>
    /// <param name="change">The membership delta to apply, re-applied against the winner when an attempt is superseded.</param>
    /// <param name="maxAttempts">How many consensus attempts the reconfiguration may spend. Must be at least one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the reconfiguration established, which names the record that installed the membership.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="change"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="maxAttempts"/> is less than one.</exception>
    /// <exception cref="ConsensusRefusedException">Thrown if a write is already in flight on this register, carrying <see cref="ConsensusRefusal.ConcurrentWrite"/>; if nothing is committed yet, carrying <see cref="ConsensusRefusal.NothingCommittedToReconfigure"/>; if the version range is spent, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>; or if the round decided a record carrying a version other than the instance's own, carrying <see cref="ConsensusRefusal.MisroutedDecision"/>. A recorder whose reply mis-answers its envelope — another instance's version, or another member's name — is absorbed as an unreachable recorder instead and surfaces as an undecided outcome, never as this throw.</exception>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// <para>
    /// A reconfiguration is an ordinary write whose record names a different membership. It takes the same
    /// consensus instance, the same quorum over the outgoing membership and the same leader, which is what
    /// makes the change safe without a joint consensus: the instance that decides it runs entirely under the
    /// membership that existed before it, and the new membership governs the version after.
    /// </para>
    /// <para>
    /// It refuses when nothing is committed, because it has no value to carry forward and inventing one would
    /// make a membership change also a value write. A cluster is bootstrapped by writing once under genesis
    /// and reconfiguring after that.
    /// </para>
    /// <para>
    /// A change that computes the membership it was given returns without writing, and the outcome names the
    /// record already committed with no attempt spent. The operator asked for a state the cluster is already
    /// in — adding a member that is already listed, removing one that is already gone — and a consensus
    /// instance run to decide that would cost a round and change nothing.
    /// </para>
    /// <para>
    /// <paramref name="change"/> is re-applied against the membership that won rather than against the one
    /// this attempt captured, which is why it is a delta. Two operators changing membership concurrently
    /// therefore compose, and neither undoes the other; an absolute set would reinstate whatever the rival
    /// removed.
    /// </para>
    /// </remarks>
    public async Task<QuePaxaWriteOutcome<TValue>> ReconfigureAsync(ChangeConfigurationDelegate change, int maxAttempts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        EnterWrite();
        using Activity? activity = VerisyncActivitySource.Instance.StartActivity(VerisyncTelemetry.ActivityNameConsensusWrite);
        try
        {
            if(Committed is null)
            {
                throw new ConsensusRefusedException(ConsensusRefusal.NothingCommittedToReconfigure, "A reconfiguration carries the committed value forward and this register holds no committed record. A chain is bootstrapped by writing once under its genesis membership, and reconfigured after that.");
            }

            return Measured(activity, await RunAttemptsAsync(current => current!, change, maxAttempts, cancellationToken).ConfigureAwait(false));
        }
        catch(Exception failure)
        {
            _ = activity?.SetStatus(ActivityStatusCode.Error, failure.Message);

            throw;
        }
        finally
        {
            LeaveWrite();
        }
    }


    private async Task<QuePaxaWriteOutcome<TValue>> RunAttemptsAsync(
        ComputeRegisterValueDelegate<TValue> update,
        ChangeConfigurationDelegate? change,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        QuePaxaWriteOutcome<TValue> outcome = new(QuePaxaWriteStatus.Undecided, NextVersion, null, RecorderStep.Zero, 0, false);

        for(int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            outcome = await AttemptAsync(update, change, outcome.Attempts, cancellationToken).ConfigureAwait(false);

            //A membership refusal is settled rather than unlucky, so retrying it would spend the budget on an
            //answer that cannot change.
            if(outcome.Status is QuePaxaWriteStatus.Committed or QuePaxaWriteStatus.OutsideConfiguration)
            {
                return outcome;
            }
        }

        return outcome;
    }


    private async Task<QuePaxaWriteOutcome<TValue>> AttemptAsync(
        ComputeRegisterValueDelegate<TValue> update,
        ChangeConfigurationDelegate? change,
        int spent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        //The whole attempt addresses this one capture and nothing below reads the committed record again.
        VersionedValue<TValue>? captured = Committed;
        RegisterInstance instance = InstanceFor(captured);

        //The membership is classified before the delay, so a replica that will not write does not first wait
        //to find out.
        if(!instance.Configuration.Contains(Self))
        {
            return new QuePaxaWriteOutcome<TValue>(QuePaxaWriteStatus.OutsideConfiguration, instance.Version, null, RecorderStep.Zero, spent, false);
        }

        QuePaxaConfiguration next = change is null ? instance.Configuration : change(instance.Configuration);
        if(change is not null && captured is { } installed && next.Equals(instance.Configuration))
        {
            return Unchanged(installed);
        }

        TValue value = update(captured is { } held ? held.Value : default);

        if(DelayFor(instance) is { } delay && delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);

            //The version's leader waits no delay and so never stands down, which leaves the schedule an activator.
            if(ObserveCommittedVersion is not null)
            {
                RegisterVersion observed = await ObserveCommittedVersion(cancellationToken).ConfigureAwait(false);
                if(observed >= instance.Version)
                {
                    return new QuePaxaWriteOutcome<TValue>(QuePaxaWriteStatus.Undecided, instance.Version, null, RecorderStep.Zero, spent, false);
                }
            }
        }

        VersionedValue<TValue> record = new(instance.Version, Self, next, value);
        ProposerLane proposerLane = AllocateLane(instance.Version);
        ProposerLane? believedLeader = ScheduleFor(instance.Configuration, BaseDelay).LeaderFor(instance.PreviousWriter);

        var proposer = new QuePaxaProposer<VersionedValue<TValue>>(
            EndpointsFor(instance),
            proposerLane,
            DrawPriority,
            AttemptsPerRecorder);

        QuePaxaOutcome<VersionedValue<TValue>> outcome = await proposer.ProposeAsync(believedLeader, record, cancellationToken).ConfigureAwait(false);

        if(!outcome.IsDecided || outcome.Value is not { } decided)
        {
            return new QuePaxaWriteOutcome<TValue>(QuePaxaWriteStatus.Undecided, instance.Version, null, RecorderStep.Zero, spent + 1, true);
        }

        //A misrouted decision kept out of the committed state cannot set the next instance's leader.
        if(decided.Version != instance.Version)
        {
            throw new ConsensusRefusedException(ConsensusRefusal.MisroutedDecision, $"The instance for version {instance.Version.Value} decided a record carrying version {decided.Version.Value}, so a request reached an instance it was not addressed to.");
        }

        _ = Learn(decided);

        await PublishAsync(instance.Configuration, decided, cancellationToken).ConfigureAwait(false);

        //A value can be chosen by a quorum and still not be this writer's, so the test is over the whole record.
        QuePaxaWriteStatus status = decided.Equals(record) ? QuePaxaWriteStatus.Committed : QuePaxaWriteStatus.Superseded;

        return new QuePaxaWriteOutcome<TValue>(status, instance.Version, decided, outcome.DecidedAt, spent + 1, true);
    }


    /// <summary>
    /// Offers <paramref name="decided"/> to its audience, absorbing whatever the offer does.
    /// </summary>
    /// <param name="deciding">The membership the instance that decided the record ran under.</param>
    /// <param name="decided">The decided record.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the offer has been made or has failed.</returns>
    /// <remarks>
    /// <para>
    /// A publish fault never reaches the caller, cancellation included and the caller's own token included.
    /// The decision has already been taken and already been learned by the time this runs, so a caller told
    /// its write failed would retry a write that landed; the boundary push in particular addresses exactly
    /// the cold hosts most likely to fault, which would make a reconfiguration the write most likely to
    /// throw after deciding.
    /// </para>
    /// <para>
    /// The register owes this guard whatever the delegate's signature says, which is why the delegate has no
    /// result type reporting failure: a .NET delegate can always throw, so a result would add a second route
    /// for the same event and leave this guard standing anyway. What a caller reads instead is
    /// <see cref="ReadReadinessAsync(TimeSpan, CancellationToken)"/>, which reports what the hosts actually hold rather than what a push
    /// attempt claimed.
    /// </para>
    /// </remarks>
    private async ValueTask PublishAsync(QuePaxaConfiguration deciding, VersionedValue<TValue> decided, CancellationToken cancellationToken)
    {
        if(PublishCommittedRecord is null)
        {
            return;
        }

        try
        {
            await PublishCommittedRecord(decided, AudienceFor(deciding, decided.NextConfiguration), cancellationToken).ConfigureAwait(false);
        }
        catch(Exception)
        {
        }
    }


    /// <summary>The hosts a decided record is offered to.</summary>
    /// <param name="outgoing">The membership the deciding instance ran under.</param>
    /// <param name="incoming">The membership the decided record installs for the version after it.</param>
    /// <returns>The union of the two, in outgoing order with the joiners appended.</returns>
    /// <remarks>
    /// <para>
    /// For an ordinary decide the two are the same membership and the union degenerates to it, which is why
    /// this changes nothing for a cluster that never reconfigures. At a boundary each half carries a duty the
    /// other cannot: the joiners are handed the installing record rather than left to learn it, and the
    /// leavers are handed the record that removed them rather than left in silence.
    /// </para>
    /// <para>
    /// It is computed from the deciding instance's own membership and never from this register's genesis or
    /// its current memo. A stale outgoing half would keep naming replicas a completed change removed, so
    /// every later ordinary decide would push to hosts that are no longer members and the union would never
    /// degenerate again.
    /// </para>
    /// </remarks>
    private static ImmutableArray<ReplicaId> AudienceFor(QuePaxaConfiguration outgoing, QuePaxaConfiguration incoming)
    {
        if(incoming.Equals(outgoing))
        {
            return ImmutableArray.CreateRange(outgoing.Members, static member => member.Replica);
        }

        ImmutableArray<ReplicaId>.Builder audience = ImmutableArray.CreateBuilder<ReplicaId>(outgoing.Members.Length + incoming.Members.Length);
        foreach(HostId outgoingMember in outgoing.Members)
        {
            audience.Add(outgoingMember.Replica);
        }

        foreach(HostId joining in incoming.Members)
        {
            if(!outgoing.Contains(joining.Replica))
            {
                audience.Add(joining.Replica);
            }
        }

        return audience.ToImmutable();
    }


    /// <summary>Reports what <paramref name="outcome"/> established, and returns it unchanged.</summary>
    /// <param name="activity">The span covering the write, or <see langword="null"/> when nothing is listening.</param>
    /// <param name="outcome">What the write established.</param>
    /// <returns><paramref name="outcome"/>.</returns>
    /// <remarks>
    /// It measures the public call and not the attempt, so the attempt count is the number a write spent
    /// rather than a running total and the status is the one a caller was given.
    /// </remarks>
    private QuePaxaWriteOutcome<TValue> Measured(Activity? activity, QuePaxaWriteOutcome<TValue> outcome)
    {
        TagList tags = new()
        {
            { VerisyncTelemetry.TagCluster, Chain },
            { VerisyncTelemetry.TagWriteStatus, Describe(outcome.Status) },
            { VerisyncTelemetry.TagFastPath, outcome.TookFastPath }
        };

        VerisyncMetrics.ConsensusWrites.Add(1, tags);
        VerisyncMetrics.ConsensusWriteAttempts.Record(outcome.Attempts, tags);

        if(activity is not null)
        {
            _ = activity.SetTag(VerisyncTelemetry.TagCluster, Chain);
            _ = activity.SetTag(VerisyncTelemetry.TagWriteStatus, Describe(outcome.Status));
            _ = activity.SetTag(VerisyncTelemetry.TagFastPath, outcome.TookFastPath);
            _ = activity.SetTag(VerisyncTelemetry.ActivityWriteAttempts, outcome.Attempts);
        }

        return outcome;
    }


    /// <summary>Reports how one member answered a version probe.</summary>
    /// <param name="member">The member that was asked.</param>
    /// <param name="outcome">How it answered, which is one of the probe outcomes <see cref="VerisyncTelemetry"/> names.</param>
    private void RecordProbe(ReplicaId member, string outcome)
    {
        VerisyncMetrics.ConsensusProbes.Add(1, new TagList
        {
            { VerisyncTelemetry.TagCluster, Chain },
            { VerisyncTelemetry.TagMember, Convert.ToHexStringLower(member.AsSpan()) },
            { VerisyncTelemetry.TagProbeOutcome, outcome }
        });
    }


    /// <summary>Reports the membership this register's next write runs under.</summary>
    /// <remarks>
    /// Recorded where the membership is set and where a record moves it, which are the only two places it
    /// changes. The quorum is the membership's own arithmetic rather than the proposer's count of resolved
    /// endpoints; the two cannot disagree, because an unresolvable member keeps its slot precisely so that
    /// they cannot, and the membership's is the number the protocol and the operator both reason with.
    /// </remarks>
    private void RecordMembership()
    {
        TagList tags = new() { { VerisyncTelemetry.TagCluster, Chain } };

        VerisyncMetrics.ConsensusMembershipSize.Record(ActiveConfiguration.Members.Length, tags);
        VerisyncMetrics.ConsensusMembershipQuorum.Record(ActiveConfiguration.Quorum, tags);
    }


    /// <summary>The dimension value naming <paramref name="status"/>.</summary>
    /// <param name="status">What a write established.</param>
    /// <returns>A constant, so a measured write allocates nothing to name its own outcome.</returns>
    private static string Describe(QuePaxaWriteStatus status) => status switch
    {
        QuePaxaWriteStatus.Committed => nameof(QuePaxaWriteStatus.Committed),
        QuePaxaWriteStatus.Superseded => nameof(QuePaxaWriteStatus.Superseded),
        QuePaxaWriteStatus.OutsideConfiguration => nameof(QuePaxaWriteStatus.OutsideConfiguration),
        _ => nameof(QuePaxaWriteStatus.Undecided)
    };


    /// <summary>The rule a per-member deadline satisfies.</summary>
    /// <param name="deadline">The deadline a caller supplied.</param>
    /// <param name="parameterName">The parameter the deadline arrived as.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="deadline"/> is neither positive nor <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
    /// <remarks>
    /// Zero is refused rather than read as no patience at all. A zero deadline reports every member
    /// unreachable, and a report in which nothing answered is exactly what a silent cluster produces, which is
    /// the collapse this surface already refuses when no query was supplied. A caller that means to wait
    /// without bound says so with <see cref="Timeout.InfiniteTimeSpan"/>, which states the choice rather than
    /// spelling it as a number nobody recognises.
    /// </remarks>
    private static void ValidateDeadline(TimeSpan deadline, string parameterName)
    {
        if(deadline != Timeout.InfiniteTimeSpan && deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, deadline, "A per-member deadline is positive, or Timeout.InfiniteTimeSpan to wait without bound. A zero deadline reports every member unreachable, which is what a wholly silent cluster reports, and a gate cannot tell those two apart.");
        }
    }


    /// <summary>
    /// Asks one member and gives up on it after <paramref name="deadline"/>.
    /// </summary>
    /// <typeparam name="TAnswer">What the member answers.</typeparam>
    /// <param name="ask">Starts the question against the token this method decides to hand it.</param>
    /// <param name="deadline">How long the answer may take.</param>
    /// <param name="cancellationToken">The caller's own token.</param>
    /// <returns>The member's answer.</returns>
    /// <exception cref="TimeoutException">Thrown when the deadline passed before the member answered, which every caller here reads as unavailability.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// <para>
    /// THE TOKEN IS THE COURTESY AND THE RACE IS THE GUARANTEE. The question is handed a token linked to the
    /// deadline, so a query that honours cancellation stops promptly and releases what it holds; but no
    /// delegate contract obliges a query to honour anything, so the wait is also raced against the deadline
    /// and the loser is abandoned. A deadline enforced by the token alone would bound only the queries that
    /// were never the problem.
    /// </para>
    /// <para>
    /// The clock is the register's own <see cref="TimeProvider"/>, so a deployment and a test measure the
    /// deadline the same way. An infinite deadline takes the direct path, which arms no timer at all: the
    /// caller asked for the behaviour this method exists to replace, and it should cost nothing.
    /// </para>
    /// <para>
    /// The abandoned call keeps running. That is the cost of an answer arriving at all, and the alternative is
    /// the caller waiting on it forever; what this method owes it is that its fault is observed, so a question
    /// nobody is still asking cannot surface later as an unobserved exception.
    /// </para>
    /// </remarks>
    private async Task<TAnswer> WithinDeadlineAsync<TAnswer>(Func<CancellationToken, Task<TAnswer>> ask, TimeSpan deadline, CancellationToken cancellationToken)
    {
        if(deadline == Timeout.InfiniteTimeSpan)
        {
            return await ask(cancellationToken).ConfigureAwait(false);
        }

        using var deadlineSource = new CancellationTokenSource(deadline, TimeProvider);
        using var askSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineSource.Token);

        Task<TAnswer> asked = ask(askSource.Token);

        //An infinite delay under the linked token completes when the deadline or the caller signals and arms
        //no timer of its own, so one probe holds one timer however the race ends.
        Task abandoned = Task.Delay(Timeout.InfiniteTimeSpan, askSource.Token);

        if(await Task.WhenAny(asked, abandoned).ConfigureAwait(false) == asked)
        {
            return await asked.ConfigureAwait(false);
        }

        _ = asked.ContinueWith(
            static abandonedCall => _ = abandonedCall.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        cancellationToken.ThrowIfCancellationRequested();

        throw new TimeoutException($"A member did not answer within {deadline}, so it is reported unavailable rather than waited on. The query was asked under a token carrying the deadline and did not return, so it is abandoned and still running.");
    }


    /// <summary>The endpoints an attempt at <paramref name="instance"/> addresses, one per member in order.</summary>
    /// <param name="instance">The captured instance the attempt runs at.</param>
    /// <returns>Exactly as many endpoints as the instance's membership has members.</returns>
    /// <remarks>
    /// <para>
    /// THE ARRAY HAS ONE SLOT PER MEMBER AND NEVER FEWER. A quorum is
    /// <see cref="QuePaxaProposer{TValue}.Quorum"/>, computed from the number of endpoints, so a member left
    /// out because it could not be resolved does not make the cluster smaller: it makes the majority smaller,
    /// and a decision taken by that majority is taken by fewer replicas than the membership's arithmetic
    /// claims. An unresolvable member therefore keeps its slot as an endpoint that always faults, which the
    /// protocol already knows how to treat, because an unreachable recorder costs availability and a diluted
    /// quorum costs agreement.
    /// </para>
    /// <para>
    /// Each slot also checks that the reply came back from the member it was addressed to, beside the
    /// existing check that it came back from the instance it was addressed to. Both are the same failure in
    /// two dimensions: an answer counted toward a quorum it is not a member of. The identity check is exact
    /// under crash faults and is not authentication — the answering host asserts its own name and nothing
    /// verifies it — so what it catches is a deployment's endpoint map pointing two slots at one host, which
    /// is the wiring error that turns a majority into a minority.
    /// </para>
    /// </remarks>
    private RecorderEndpointDelegate<VersionedValue<TValue>>[] EndpointsFor(RegisterInstance instance)
    {
        ImmutableArray<HostId> members = instance.Configuration.Members;
        var endpoints = new RecorderEndpointDelegate<VersionedValue<TValue>>[members.Length];
        for(int index = 0; index < members.Length; index++)
        {
            HostId admitted = members[index];
            ReplicaId member = admitted.Replica;
            VersionedRecorderEndpointDelegate<VersionedValue<TValue>> host;
            try
            {
                host = ResolveRecorder(member);
            }
            catch(Exception unresolved)
            {
                endpoints[index] = (_, _) => throw new InvalidOperationException($"No transport resolves member {member} of the membership version {instance.Version.Value} runs under, so this recorder is unreachable for the whole instance. Its slot is kept because the quorum is counted over the membership's size and dropping it would decide on a smaller majority than the membership names.", unresolved);

                continue;
            }

            endpoints[index] = async (request, token) =>
            {
                VersionedRecordReply<VersionedValue<TValue>> reply = await host(new VersionedRecordRequest<VersionedValue<TValue>>(instance.Version, request), token).ConfigureAwait(false);

                //A reply from another instance would enter this quorum and break the majority intersection
                //agreement rests on.
                if(reply.Version != instance.Version)
                {
                    throw new InvalidOperationException($"A recorder answering version {instance.Version.Value} replied for version {reply.Version.Value}, so the transport correlated a reply to the wrong instance.");
                }

                //And a reply from another host would enter it twice, which is the same break one dimension
                //over: two slots answered by one host count one replica as two. The comparison is over the
                //whole admitted host, so an answer carrying the right replica under another store is refused
                //here as well, and the two readings are told apart because only one of them is a wiring
                //error.
                if(!reply.Recorder.Equals(admitted))
                {
                    throw new InvalidOperationException(reply.Recorder.Replica.Equals(member)
                        ? $"The endpoint for member {member} was answered under {reply.Recorder.Incarnation} where the membership admits {admitted.Incarnation}, so a store other than the admitted one is answering for that member. Counting that answer would put a store this membership never admitted into a slot of a quorum counted over distinct members."
                        : $"The endpoint for member {member} was answered by {reply.Recorder.Replica}, so this deployment's endpoint map does not reach the membership it names. Counting that answer would let one host fill two slots of a quorum counted over distinct members.");
                }

                return reply.Reply;
            };
        }

        return endpoints;
    }


    /// <summary>The instance an attempt holding <paramref name="committed"/> addresses.</summary>
    /// <param name="committed">The committed record the attempt captured, or <see langword="null"/> when it holds none.</param>
    /// <returns>The version, membership and previous writer that one record implies.</returns>
    /// <remarks>
    /// The one derivation both the public read and every attempt run, so the triple a caller sees and the
    /// triple an attempt addresses cannot come from two expressions that drift apart. It reads the record it
    /// is handed rather than the field, which is what makes the capture a capture.
    /// </remarks>
    private RegisterInstance InstanceFor(VersionedValue<TValue>? committed)
    {
        return committed is { } record
            ? new RegisterInstance(record.Version.Next(), record.NextConfiguration, record.Writer)
            : new RegisterInstance(RegisterVersion.First, Genesis, null);
    }


    /// <summary>The delay this replica waits before writing at <paramref name="instance"/>.</summary>
    /// <param name="instance">The captured instance.</param>
    /// <returns>The delay, or <see langword="null"/> when this replica is not a member of the instance's membership.</returns>
    private TimeSpan? DelayFor(RegisterInstance instance)
    {
        if(!instance.Configuration.Contains(Self))
        {
            return null;
        }

        return ScheduleFor(instance.Configuration, BaseDelay).ScheduleFor(instance.PreviousWriter).DelayFor(Self);
    }


    /// <summary>The leader derivation over <paramref name="configuration"/>'s member order.</summary>
    /// <param name="configuration">The membership the instance runs under.</param>
    /// <param name="baseDelay">The hedging increment this register waits per position.</param>
    /// <returns>The derivation.</returns>
    private static QuePaxaLeaderSchedule ScheduleFor(QuePaxaConfiguration configuration, TimeSpan baseDelay) => new(configuration.ScheduleWith(baseDelay));


    /// <summary>The outcome of a reconfiguration that asked for the membership already installed.</summary>
    /// <param name="committed">The record the attempt captured, which is the one that installed that membership or an ordinary one carrying it forward.</param>
    /// <returns>An outcome naming that record, with no attempt spent and nothing activated.</returns>
    /// <remarks>
    /// It reports committed rather than a status of its own, because the membership the operator asked for
    /// is installed and the record named here is the evidence. Nothing was proposed, which the spent-attempt
    /// count of zero and the unset activation flag are what say.
    /// </remarks>
    private static QuePaxaWriteOutcome<TValue> Unchanged(VersionedValue<TValue> committed)
    {
        return new QuePaxaWriteOutcome<TValue>(QuePaxaWriteStatus.Committed, committed.Version, committed, RecorderStep.Zero, 0, false);
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
            throw new ConsensusRefusedException(ConsensusRefusal.ConcurrentWrite, "A versioned register writes one value at a time; two writes at one version would propose on one lane, and one proposal key naming two values is what the key's uniqueness contract forbids.");
        }
    }


    private void LeaveWrite() => Interlocked.Exchange(ref writing, 0);
}
