using System;
using System.Threading;
using System.Threading.Tasks;

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
/// A request for any other instance is declined, and so is one from another chain, one addressed to a host a
/// configuration change removed, and one whose carried record disagrees with the envelope about its own
/// version. Declining is a host act rather than a protocol refusal. <see cref="RecordReply{TValue}"/> has no
/// rejection field: the node throws, a transport reports that as a fault like any other unreachable recorder,
/// and a proposer retries within its attempt budget and otherwise concludes a missed quorum. Nothing in the
/// protocol learns that a decline happened. <see cref="Declines"/> is the classifier and
/// <see cref="Handle"/> the act, and both read one predicate, so a rule cannot hold at one and not the other.
/// </para>
/// <para>
/// The membership every instance runs under is derived and never configured. A host is handed one genesis
/// configuration, and every later one is the next configuration of the record it has learned, so two hosts
/// holding the record for a version derive the same recorder set, the same hedging order and the same leader
/// for the version after it. <see cref="ActiveConfiguration"/> and <see cref="LeaderSchedule"/> are memos of
/// that derivation, recomputed only where the record moves.
/// </para>
/// <para>
/// What this costs a deployment is that no write at v+1 can gather a quorum until a quorum of hosts has
/// learned v. The committed record and the first request of the next version may travel together, so a writer
/// that commits and immediately disseminates pays no extra round trip, and a host that disseminates lazily
/// serializes its writes behind dissemination. A learned record is not verified, which is the same crash-fault
/// assumption that lets a recorder's own reply be believed.
/// </para>
/// <para>
/// The chain is checked on every path a committed record enters a host by — construction,
/// <see cref="FromState"/>, <see cref="Handle"/> and <see cref="Learn"/> — so a deployment wired across two
/// chains cannot install here what this chain never decided, and <see cref="ActiveConfiguration"/> names
/// <see cref="Genesis"/>'s chain in every state this host can reach. The two entry points that take a record
/// from outside the protocol compare it against the genesis they were handed, because a host adopting its
/// first record has no other statement of which chain it runs; the request and the learn compare against the
/// active membership, whose chain identity is that same one. Nothing else about the membership is read at a
/// learn: dissemination stays membership-blind, because a host that a change removed learns it is out from
/// the record that removed it, and a joiner catches up from whoever holds the record.
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
/// <see cref="QuePaxaNode{TValue}"/> is not. A running
/// <see cref="QuePaxaVersionedRunner{TValue}.RunAsync"/> claims this host for the life of its loop, and
/// while it holds the claim <see cref="Handle"/>, <see cref="Learn"/>, <see cref="MakeDurableAsync"/> and
/// <see cref="ToState"/> refuse rather than interleaving with it; the runner's own producers are the
/// sequenced paths there. <see cref="Committed"/>, <see cref="LiveVersion"/>, <see cref="Serves"/>,
/// <see cref="Instance"/> and <see cref="Recorder"/> stay open, because each answers from one read of one
/// reference and an off-loop reader therefore sees a stale answer but never a torn one;
/// <see cref="QuePaxaVersionedRunner{TValue}.ReadCommittedAsync"/> is the sequenced read.
/// <see cref="ToState"/> is not among them because a snapshot pairs two references a learn replaces in
/// two stores, which is the one composite read on this surface.
/// </para>
/// </remarks>
public sealed class QuePaxaVersionedNode<TValue>
{
    //An Interlocked target cannot be a property, and the sibling latches on this surface take the same shape:
    //QuePaxaVersionedRegister.writing and QuePaxaVersionedRunner.started.
    private int owner;

    /// <summary>
    /// Initializes a host of <paramref name="genesis"/>'s chain that has learned <paramref name="committed"/>.
    /// </summary>
    /// <param name="genesis">The chain's genesis membership, which is deployment configuration and the membership the first instance runs under.</param>
    /// <param name="self">This host's own identity, which the membership filter reads against the active configuration.</param>
    /// <param name="committed">The committed record this host starts from, or <see langword="null"/> when it has learned none.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="genesis"/> is <see langword="null"/>.</exception>
    /// <exception cref="StateRestoreException">Thrown if <paramref name="committed"/>'s membership names a chain other than <paramref name="genesis"/>'s, carrying <see cref="StateRestoreRefusal.HostForeignChain"/>.</exception>
    /// <remarks>
    /// <para>
    /// The committed record is taken at construction rather than defaulted, as <see cref="QuePaxaNode{TValue}"/>
    /// takes its recorder: it is what the instance's leader is derived from, and a host that started from
    /// nothing would serve the first version when it should be serving a much later one.
    /// </para>
    /// <para>
    /// Genesis is configuration and the record is protocol state, which is why one comes before the other.
    /// Every later membership is a function of a decided record, so genesis is the only one a deployment
    /// supplies, and it is what <see cref="ActiveConfiguration"/> stands at until a record moves it.
    /// </para>
    /// <para>
    /// The record's chain is compared against the genesis and never against the membership the record itself
    /// names, which is the comparison <see cref="FromState"/> makes at a restore. Genesis is the only chain
    /// identity a host being constructed holds: the membership it would otherwise be compared with is derived
    /// from the very record under test, so the rule would compare that record with itself and refuse nothing.
    /// A host handed a foreign record runs the foreign chain and then declines its own chain's records at
    /// every path that does check, which is a deployment fault reported at the entry point rather than at the
    /// first request. A host handed no record compares the genesis with itself and constructs, which is that
    /// derivation's base case rather than a second rule.
    /// </para>
    /// <para>
    /// Nothing else about the membership is read. A host constructed with a record whose membership does not
    /// list it is exactly how a joiner and a removed replica start, and both are states the protocol reaches.
    /// This host's identity is likewise not derived from the membership it was handed: a replica removed by a
    /// configuration change keeps running until its deployment retires it, and it must be able to say which
    /// replica it is in order to notice.
    /// </para>
    /// </remarks>
    public QuePaxaVersionedNode(QuePaxaConfiguration genesis, ReplicaId self, VersionedValue<TValue>? committed = null)
    {
        ArgumentNullException.ThrowIfNull(genesis);

        QuePaxaConfiguration active = ConfigurationFor(committed, genesis);
        if(!active.Cluster.Equals(genesis.Cluster))
        {
            throw new StateRestoreException(StateRestoreRefusal.HostForeignChain, $"A committed record must name the chain this host was given, which is {genesis.Cluster}, and it names {active.Cluster}. A host started on another chain's record runs that chain and declines its own at every later path, so two independently bootstrapped chains lose progress rather than merging.", nameof(committed));
        }

        Genesis = genesis;
        Self = self;
        Committed = committed;
        ActiveConfiguration = active;
        LeaderSchedule = ScheduleFor(active);
        Serving = new QuePaxaNode<VersionedValue<TValue>>(LeaderSchedule.RecorderFor<VersionedValue<TValue>>(committed?.Writer));
        PersistedCommitted = committed;
        PersistedRecorder = Serving.Recorder;
    }


    private QuePaxaVersionedNode(
        QuePaxaConfiguration genesis,
        ReplicaId self,
        VersionedValue<TValue>? committed,
        QuePaxaConfiguration active,
        QuePaxaLeaderSchedule schedule,
        QuePaxaNode<VersionedValue<TValue>> node)
    {
        Genesis = genesis;
        Self = self;
        Committed = committed;
        ActiveConfiguration = active;
        LeaderSchedule = schedule;
        Serving = node;
        PersistedCommitted = committed;
        PersistedRecorder = node.Recorder;
    }


    /// <summary>The chain's genesis membership, which is what the active configuration stands at until a record moves it.</summary>
    public QuePaxaConfiguration Genesis { get; }

    /// <summary>
    /// This host's own identity, which the membership filter reads against <see cref="ActiveConfiguration"/>
    /// and every reply carries.
    /// </summary>
    /// <remarks>
    /// A reply naming its own producer is what lets a writer check that the endpoint it addressed to a member
    /// reached that member. The host states it and nothing verifies it, which is exact under crash faults and
    /// no defence at all against a host that lies; <see cref="VersionedRecordReply{TValue}"/> carries the
    /// whole of that claim.
    /// </remarks>
    public ReplicaId Self { get; }

    /// <summary>
    /// The membership the live instance runs under, which is the committed record's next configuration or
    /// <see cref="Genesis"/> when this host has learned no record.
    /// </summary>
    /// <remarks>
    /// A memo of the committed record and never a setting. It is recomputed where the record moves and nowhere
    /// else — the constructor, <see cref="Learn"/> and <see cref="FromState"/> — because a configuration a host
    /// can be told is a configuration two hosts can be told differently for one instance, which is the
    /// split-configuration hazard the derivation exists to make unstateable.
    /// </remarks>
    public QuePaxaConfiguration ActiveConfiguration { get; private set; }

    /// <summary>The leader derivation this host and its register share, taken over <see cref="ActiveConfiguration"/>.</summary>
    /// <remarks>
    /// A memo on the same rule as <see cref="ActiveConfiguration"/>, and its lifetime is the record's rather
    /// than the host's. The order it reads is the configuration's member order, so the derivation is a function
    /// of an agreed fact at every version; the hedging base delay it carries is zero, because delays are local
    /// tuning applied by a register and this schedule is read only for who leads.
    /// </remarks>
    public QuePaxaLeaderSchedule LeaderSchedule { get; private set; }

    /// <summary>The committed record this host has learned, or <see langword="null"/> when it has learned none.</summary>
    public VersionedValue<TValue>? Committed { get; private set; }

    /// <summary>The one version this host serves, which is the one after the committed record's.</summary>
    /// <exception cref="ConsensusRefusedException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that no version follows it, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>.</exception>
    public RegisterVersion LiveVersion => LiveVersionFor(Committed);

    /// <summary>
    /// The instance this host serves, as one read: the version, the membership it runs under and the
    /// previous writer, every field derived from a single capture of the committed record.
    /// </summary>
    /// <exception cref="ConsensusRefusedException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that no version follows it, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>.</exception>
    /// <remarks>
    /// The three facts are also readable one property at a time, and a reader beside a running loop must not
    /// pair them that way: a learn replaces the record and then the derived memos, so separate reads can pair
    /// a new record with an old membership. This is the pairing-safe read, on the rule
    /// <see cref="QuePaxaVersionedRegister{TValue}.Instance"/> exists for one layer up, and it stays open off
    /// the loop for the reason <see cref="Committed"/> does.
    /// </remarks>
    public RegisterInstance Instance
    {
        get
        {
            VersionedValue<TValue>? committed = Committed;

            return new RegisterInstance(LiveVersionFor(committed), ConfigurationFor(committed, Genesis), committed?.Writer);
        }
    }

    /// <summary>
    /// The register serving <see cref="LiveVersion"/>, which a request folds into and a learn replaces.
    /// </summary>
    /// <remarks>
    /// A recorder is immutable, so this hands out a reference to state rather than a handle that can change
    /// it. Reading it while a runner owns this host is racy in the ordinary sense and never torn: the read is
    /// a single reference read, and a stale answer is a weaker one rather than a wrong one.
    /// </remarks>
    public QuePaxaRecorder<VersionedValue<TValue>> Recorder => Serving.Recorder;

    /// <summary>The recorder node serving <see cref="LiveVersion"/>.</summary>
    private QuePaxaNode<VersionedValue<TValue>> Serving { get; set; }

    /// <summary>
    /// The committed record <see cref="MakeDurableAsync"/> last made durable, which its gate compares
    /// against beside <see cref="PersistedRecorder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is host state rather than loop state, because a host whose durable write failed restarts by
    /// building a fresh runner over this same node and would otherwise begin by treating whatever the
    /// failed attempt left in memory as already durable. Both baselines start at what the constructor or
    /// <see cref="FromState"/> was handed, which is durable by construction: a restored host's state came
    /// out of its own store, and a constructed host has learned nothing or was given a record its
    /// deployment already wrote, while the register it starts from is unwritten and records nothing.
    /// </para>
    /// <para>
    /// Both baselines are compared and neither alone. The recorder of a leaderless instance is a shared
    /// singleton, so a host that learns one leaderless-instance record after another holds the same
    /// recorder reference across the learn while the record the leader is derived from has moved — the
    /// one case where the committed record moves and the recorder does not.
    /// </para>
    /// </remarks>
    private VersionedValue<TValue>? PersistedCommitted { get; set; }

    /// <summary>The recorder half of the durable baseline documented at <see cref="PersistedCommitted"/>.</summary>
    private QuePaxaRecorder<VersionedValue<TValue>> PersistedRecorder { get; set; }


    /// <summary>
    /// Applies <paramref name="request"/> to the live instance and returns the reply.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <returns>The reply, carrying the version of the instance that answered and this host's own identity.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the request does not name <see cref="LiveVersion"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the carried record names a chain other than <see cref="ActiveConfiguration"/>'s, if
    /// <see cref="Self"/> is outside <see cref="ActiveConfiguration"/>, or if the carried record's own version
    /// is absent or differs from the envelope's.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown if a runner owns this host.</exception>
    /// <exception cref="ConsensusRefusedException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that this host serves no version at all, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>.</exception>
    /// <remarks>
    /// <para>
    /// Every refusal is <see cref="Declines"/>'s classification raised as an exception, and the two are one
    /// predicate consulted twice rather than two rules kept in step. A refusal a classifier reported and a
    /// handler served is a request folded into a register the runner then treats as a defect, and the
    /// arrangement makes that drift unstateable rather than tested for.
    /// </para>
    /// <para>
    /// Both version bounds are refused and for different reasons. A request below the live version belongs to
    /// an instance that has already decided, and folding it into the live register would put a settled
    /// version's proposals into an unsettled one. A request above it belongs to an instance whose leader this
    /// host cannot derive, and serving it under the leader of a different version is the hazard.
    /// </para>
    /// <para>
    /// The three membership refusals are each a different claim. A carried record naming another chain is a
    /// host of a different genesis addressing this one, and answering it would merge two chains that have
    /// never agreed on anything. A host outside its own active configuration is one a change removed, and a
    /// recorder outside the set a quorum is counted over is a shadow recorder no arithmetic accounts for. A
    /// carried record whose own version differs from the envelope's is a defective proposer's request, and
    /// deciding it would wedge the instance, because every host that learns the decision throws on the
    /// mismatch before it can adopt the record.
    /// </para>
    /// <para>
    /// Every refusal precedes any mutation, so a declined request leaves this host exactly as it found it,
    /// which is what lets a runner keep serving after faulting the declined call.
    /// </para>
    /// <para>
    /// The ownership refusal precedes all of them, because a request folded in beside a running loop's own
    /// replaces the recorder the loop's durability gate compares by reference.
    /// <see cref="QuePaxaVersionedRunner{TValue}.RecordAsync"/> is the sequenced path there.
    /// </para>
    /// </remarks>
    public VersionedRecordReply<VersionedValue<TValue>> Handle(VersionedRecordRequest<VersionedValue<TValue>> request)
    {
        ThrowIfOwned();

        return HandleForOwner(request);
    }


    internal VersionedRecordReply<VersionedValue<TValue>> HandleForOwner(VersionedRecordRequest<VersionedValue<TValue>> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequestRefusal refusal = Classify(request);
        if(refusal != RequestRefusal.None)
        {
            throw RefusalFor(refusal, request);
        }

        return new VersionedRecordReply<VersionedValue<TValue>>(LiveVersion, Self, Serving.Handle(request.Request));
    }


    /// <summary>
    /// Reports whether <see cref="Handle"/> would refuse <paramref name="request"/> rather than serving it.
    /// </summary>
    /// <param name="request">The request to classify.</param>
    /// <returns><see langword="true"/> when this host refuses the request on any of its rules.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A classifier and not a guard: no rule here throws, and serving remains <see cref="Handle"/>'s act. A
    /// runner's decline filter reads this to tell a refusal, which faults one call, from a defect, which ends
    /// its loop, and it reads it from inside an exception filter, where a throw would be swallowed and the
    /// refusal would be misread as the defect.
    /// </para>
    /// <para>
    /// The spent-range arm comes first over one snapshot of the committed record, as
    /// <see cref="Serves(RegisterVersion)"/>'s does: a host whose record stands at
    /// <see cref="RegisterVersion.MaxValue"/> refuses every request rather than evaluating the throw
    /// <see cref="LiveVersion"/> documents.
    /// </para>
    /// <para>
    /// The membership arms are operability rather than safety. A removed recorder that kept answering would
    /// not break agreement, because a quorum is counted over the configuration a proposal was addressed
    /// under, and refusing here is what makes the removal enforceable at the host instead of resting on
    /// nobody addressing it.
    /// </para>
    /// </remarks>
    public bool Declines(VersionedRecordRequest<VersionedValue<TValue>> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Classify(request) != RequestRefusal.None;
    }


    /// <summary>Classifies <paramref name="request"/> against every rule this host refuses on.</summary>
    /// <param name="request">The request to classify.</param>
    /// <returns>The rule that refuses it, or <see cref="RequestRefusal.None"/> when this host serves it.</returns>
    private RequestRefusal Classify(VersionedRecordRequest<VersionedValue<TValue>> request)
    {
        VersionedValue<TValue>? committed = Committed;
        if(committed is { } held && held.Version == RegisterVersion.MaxValue)
        {
            return RequestRefusal.Exhausted;
        }

        if(request.Version != LiveVersionFor(committed))
        {
            return RequestRefusal.Instance;
        }

        //The carried record is read before it is compared, so a request naming no record at all is refused on
        //the record arm rather than faulting a classifier that promises not to throw.
        if(request.Request.Proposal.Value is not { } carried)
        {
            return RequestRefusal.CarriedRecord;
        }

        if(NamesAnotherChain(carried))
        {
            return RequestRefusal.Cluster;
        }

        if(!ActiveConfiguration.Contains(Self))
        {
            return RequestRefusal.Membership;
        }

        if(carried.Version != request.Version)
        {
            return RequestRefusal.CarriedRecord;
        }

        return RequestRefusal.None;
    }


    /// <summary>Raises <paramref name="refusal"/> as the exception <see cref="Handle"/> refuses with.</summary>
    /// <param name="refusal">The rule that refused the request, which is never <see cref="RequestRefusal.None"/>.</param>
    /// <param name="request">The refused request, whose values the message names.</param>
    /// <returns>The exception to throw.</returns>
    private Exception RefusalFor(RequestRefusal refusal, VersionedRecordRequest<VersionedValue<TValue>> request)
    {
        return refusal switch
        {
            RequestRefusal.Exhausted => new ConsensusRefusedException(ConsensusRefusal.VersionRangeSpent, "The version range is spent; the last representable version has no successor. This host serves no version at all and refuses every request without recording it."),
            RequestRefusal.Instance => new ArgumentOutOfRangeException(
                nameof(request),
                request.Version.Value,
                $"This host serves version {LiveVersionFor(Committed).Value}; it has learned nothing about the leader of any other version and will not record for one."),
            RequestRefusal.Cluster => new ArgumentException($"This host records for chain {ActiveConfiguration.Cluster} and the request carries a record of another chain. Two independently bootstrapped chains decline each other rather than merging, so a host wired to the wrong one loses progress and never agreement.", nameof(request)),
            RequestRefusal.Membership => new ArgumentException("This host is outside the membership the live instance runs under, so it is not one of the recorders a quorum for that instance is counted over. A configuration change removed it, and a removed recorder that kept answering would be a shadow recorder no arithmetic accounts for.", nameof(request)),
            _ => new ArgumentException($"A request addressed to version {request.Version.Value} must carry a record written at that same version. A record whose own version disagrees with the instance it is proposed to wedges that instance, because every host that learns the decision refuses the mismatch before it can adopt the record.", nameof(request))
        };
    }


    /// <summary>
    /// Adopts <paramref name="committed"/> and moves the live instance to the version after it.
    /// </summary>
    /// <param name="committed">A decided record.</param>
    /// <returns><see langword="true"/> when the record advanced this host, and <see langword="false"/> when it was not newer than what it already held.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the record names a chain other than <see cref="ActiveConfiguration"/>'s.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a runner owns this host.</exception>
    /// <remarks>
    /// <para>
    /// A record that does not advance is ignored rather than refused, because a host disseminating the same
    /// decision twice is ordinary. Knowledge of committed state therefore only ever runs forward, and the live
    /// instance with it.
    /// </para>
    /// <para>
    /// A record of another chain is refused instead, on the comparison <see cref="Handle"/> makes at a request
    /// and <see cref="FromState"/> makes at a restore. Dissemination is the one path that adopts a record
    /// without being asked to serve anything, so a publisher wired across two chains would otherwise install
    /// here a record this chain never decided. The refusal precedes every mutation: the held record, the
    /// membership and the instance are exactly what they were, which is what lets a runner keep serving after
    /// faulting the call. <see cref="DeclinesLearn"/> is the classifier and this is the act, and both read one
    /// predicate, as the request path's pair do.
    /// </para>
    /// <para>
    /// The chain is read before the version, because version order is a fact inside one chain and says nothing
    /// across two: a foreign record standing below the held one is a wiring defect rather than a stale
    /// dissemination, and reporting it as a record that did not advance would hide the defect behind an
    /// ordinary answer.
    /// </para>
    /// <para>
    /// The rule is the chain and never the membership. A record that removes this host is adopted like any
    /// other, because a removed host learns it is out from the record that removed it and a joiner catches up
    /// from whoever holds the record; a membership filter here would leave both waiting on a message they
    /// cannot be sent.
    /// </para>
    /// <para>
    /// A learn beside a running loop is refused, because it replaces both the committed record and the
    /// instance, which are the two references the loop's durability gate compares.
    /// <see cref="QuePaxaVersionedRunner{TValue}.LearnAsync"/> is the sequenced path there.
    /// </para>
    /// <para>
    /// Advancing replaces the instance rather than reconfiguring it. A recorder's configured leader is fixed
    /// for the life of its instance, so learning a new version builds a new recorder for the new instance and
    /// leaves the old one alone; rewriting a live recorder's leader would change the rule mid-instance, after
    /// proposals had already been recorded under the old one.
    /// </para>
    /// <para>
    /// It is also where the membership moves, because the record names the membership the version after it
    /// runs under and a record is the only agreed thing a host can derive one from. A change that keeps the
    /// previous writer keeps that writer leading, so growing or shrinking a configuration around the writer
    /// costs the next instance nothing and never demotes its one-round-trip path; a change that removes the
    /// writer leaves the instance leaderless, uniformly at every host that holds the record.
    /// </para>
    /// <para>
    /// A record that advances is always a strictly newer instance and no record is ever mutated in place,
    /// which is what makes the durability gate's reference comparison on the committed record exact.
    /// </para>
    /// </remarks>
    public bool Learn(VersionedValue<TValue> committed)
    {
        ThrowIfOwned();

        return LearnForOwner(committed);
    }


    internal bool LearnForOwner(VersionedValue<TValue> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);

        if(NamesAnotherChain(committed))
        {
            throw new ArgumentException($"This host records for chain {ActiveConfiguration.Cluster} and the record names another chain. Dissemination adopts what it is handed, so a publisher wired across two chains would install a record this chain never decided, and a host that adopted one would serve an instance under a membership no quorum here agreed on.", nameof(committed));
        }

        if(Committed is { } held && committed.Version <= held.Version)
        {
            return false;
        }

        Committed = committed;
        ActiveConfiguration = ConfigurationFor(committed, Genesis);
        LeaderSchedule = ScheduleFor(ActiveConfiguration);
        Serving = new QuePaxaNode<VersionedValue<TValue>>(LeaderSchedule.RecorderFor<VersionedValue<TValue>>(committed.Writer));

        return true;
    }


    /// <summary>
    /// Reports whether <see cref="Learn"/> would refuse <paramref name="committed"/> rather than considering
    /// it.
    /// </summary>
    /// <param name="committed">The record to classify.</param>
    /// <returns><see langword="true"/> when the record names a chain other than <see cref="ActiveConfiguration"/>'s.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A classifier and not a guard, as <see cref="Declines"/> is: no rule here throws, and adopting remains
    /// <see cref="Learn"/>'s act. A runner's filter reads this to tell a refusal, which faults one call, from a
    /// defect, which ends its loop, and it reads it from inside an exception filter, where a throw would be
    /// swallowed and the refusal would be misread as the defect.
    /// </para>
    /// <para>
    /// A record that does not advance this host reports <see langword="false"/> here, because it is ignored
    /// rather than refused: an ordinary repeated dissemination is not a decline, and a classifier answering
    /// otherwise would have a runner fault the calls that carry it.
    /// </para>
    /// </remarks>
    public bool DeclinesLearn(VersionedValue<TValue> committed)
    {
        ArgumentNullException.ThrowIfNull(committed);

        return NamesAnotherChain(committed);
    }


    /// <summary>Whether <paramref name="carried"/> names a chain other than the one this host runs.</summary>
    /// <param name="carried">The record whose membership names the chain.</param>
    /// <returns><see langword="true"/> when the record's chain identity differs from this host's own.</returns>
    /// <remarks>
    /// The one comparison the request path and the learn read, so the rule cannot hold at a request and not
    /// at a learn. It reads <see cref="ActiveConfiguration"/>, which is what a host that has already adopted
    /// this chain states its chain identity by; construction and <see cref="FromState"/> make the same
    /// comparison against <see cref="Genesis"/>, because the record they are handed is the one the active
    /// membership would be derived from. It compares the chain identity by value and nothing else about the
    /// membership, so two configurations of one chain that list different members agree here and a
    /// configuration minted at another genesis does not.
    /// </remarks>
    private bool NamesAnotherChain(VersionedValue<TValue> carried) => !carried.NextConfiguration.Cluster.Equals(ActiveConfiguration.Cluster);


    /// <summary>
    /// Reports whether <see cref="Handle"/> would serve a request at <paramref name="version"/>.
    /// </summary>
    /// <param name="version">The version to classify.</param>
    /// <returns><see langword="true"/> when the version is the live one this host serves.</returns>
    /// <remarks>
    /// A classifier and not a guard: it never throws, and serving remains <see cref="Handle"/>'s act. Both
    /// arms read one snapshot of the committed record, so the spent-range arm comes first over the same
    /// record the live version is derived from: a host whose committed record stands at
    /// <see cref="RegisterVersion.MaxValue"/> reports <see langword="false"/> for every version rather than
    /// evaluating the throw <see cref="LiveVersion"/> documents, and a learn landing between two reads
    /// cannot put the classifier on the throwing path. A runner's decline filter reads this to tell a
    /// refusal, which faults one call, from a defect, which ends its loop.
    /// </remarks>
    public bool Serves(RegisterVersion version)
    {
        VersionedValue<TValue>? committed = Committed;
        if(committed is { } held && held.Version == RegisterVersion.MaxValue)
        {
            return false;
        }

        return version == LiveVersionFor(committed);
    }


    /// <summary>
    /// Makes this host's state durable through <paramref name="persistNode"/>, unless what it would write
    /// already is.
    /// </summary>
    /// <param name="persistNode">The durable store to write through.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the state is durable or was already.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="persistNode"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if a runner owns this host. Also thrown when the state owing a write holds a committed record at <see cref="RegisterVersion.MaxValue"/>,
    /// because <see cref="ToState"/> serves no version there. A host restored or constructed at that record
    /// owes no write and returns without reaching the snapshot; only a host that learned its way there and
    /// was never made durable ends here, which is terminal by design — every request to it declines without
    /// a write and a deployment retires a spent key.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The gate compares the current committed record and the current recorder, by reference, against what
    /// this method last made durable, and skips both the snapshot and the write when neither has moved. The
    /// two are captured before the write and the baselines advance only after it returns, so a throwing
    /// store leaves this host owing exactly the write it owed before, and <see cref="ToState"/> runs only
    /// inside the firing branch, so a host that owes no write allocates no snapshot.
    /// </para>
    /// <para>
    /// The rule is the or of the two arms and the arms are independent. A request that changes nothing
    /// leaves the recorder reference-identical and costs no write once that state is durable, while one that
    /// follows a failed write finds the recorder still past the baseline and retries it. A learn usually
    /// moves both references, but a host learning one leaderless-instance record after another keeps the
    /// shared leaderless recorder singleton across the learn, and there the committed arm is the only one
    /// that fires. That arm alone never fires on a reply path — a request's step is floored above
    /// <see cref="RecorderStep.Zero"/>, so the first request after any learn advances the recorder — which
    /// is why it is reachable only through one of the four paths that await this with no reply behind
    /// them: <see cref="QuePaxaVersionedRunner{TValue}.MakeDurableAsync"/>, a
    /// <see cref="LearnDurability.Durable"/> learn, a learn that moved
    /// <see cref="ActiveConfiguration"/>, and
    /// <see cref="QuePaxaVersionedRunner{TValue}.ReadCommittedAsync"/>. The last two meet in the routine
    /// case: a change that removes its own writer both installs a membership and leaves the instance after
    /// it leaderless, which is where the recorder reference stands still while the record moves.
    /// </para>
    /// <para>
    /// A host driving <see cref="Handle"/> and <see cref="Learn"/> itself calls this before it lets any
    /// dependent reply leave, which is the sequencing <see cref="QuePaxaVersionedRunner{TValue}"/> performs
    /// for a host that does not. It refuses while a runner owns this node;
    /// <see cref="QuePaxaVersionedRunner{TValue}.MakeDurableAsync"/> is the checkpoint there.
    /// </para>
    /// <para>
    /// Both refusals reach the call site rather than the awaiting continuation, because a misuse guard that
    /// fires only when the caller happens to await is one a caller that stores the task never sees.
    /// </para>
    /// </remarks>
    public ValueTask MakeDurableAsync(PersistVersionedNodeDelegate<TValue> persistNode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistNode);
        ThrowIfOwned();

        return MakeDurableForOwnerAsync(persistNode, cancellationToken);
    }


    internal async ValueTask MakeDurableForOwnerAsync(PersistVersionedNodeDelegate<TValue> persistNode, CancellationToken cancellationToken)
    {
        VersionedValue<TValue>? committed = Committed;
        QuePaxaRecorder<VersionedValue<TValue>> recorder = Recorder;
        if(ReferenceEquals(committed, PersistedCommitted) && ReferenceEquals(recorder, PersistedRecorder))
        {
            return;
        }

        await persistNode(ToStateForOwner(), cancellationToken).ConfigureAwait(false);

        PersistedCommitted = committed;
        PersistedRecorder = recorder;
    }


    /// <summary>
    /// Snapshots the host's durable state, which is the committed record beside the recorder serving the
    /// instance that record implies.
    /// </summary>
    /// <returns>The durable state to make stable before any dependent reply is sent.</returns>
    /// <exception cref="InvalidOperationException">Thrown if a runner owns this host.</exception>
    /// <exception cref="ConsensusRefusedException">Thrown if the committed record is at <see cref="RegisterVersion.MaxValue"/>, so that this host serves no version at all, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>.</exception>
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
    /// <para>
    /// It refuses while a runner owns the host, unlike the read-only members. A snapshot is four fields read
    /// off two references a learn replaces in two stores, so one taken beside a running loop can pair a
    /// record with the register of another instance — the torn snapshot <see cref="FromState"/> exists to
    /// refuse, produced by a reader rather than by a store. The sequenced snapshot is the one taken inside
    /// the host's <see cref="PersistVersionedNodeDelegate{TValue}"/>, which the loop invokes while it owns
    /// the node. On an unowned host the single consumer is the caller, so its snapshot is consistent by
    /// construction, which is what leaves the never-torn claim true of every reader that remains.
    /// </para>
    /// </remarks>
    public QuePaxaVersionedNodeState<TValue> ToState()
    {
        ThrowIfOwned();

        return ToStateForOwner();
    }


    internal QuePaxaVersionedNodeState<TValue> ToStateForOwner()
    {
        VersionedValue<TValue>? committed = Committed;
        QuePaxaRecorder<VersionedValue<TValue>> recorder = Recorder;

        return new QuePaxaVersionedNodeState<TValue>(committed, LiveVersionFor(committed), recorder.ConfiguredLeader, ActiveConfiguration, recorder.ToState());
    }


    /// <summary>
    /// Claims this host for the runner whose loop is starting, refusing a second claim.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if another runner already holds the claim.</exception>
    internal void ClaimForRunner()
    {
        if(Interlocked.Exchange(ref owner, 1) != 0)
        {
            throw new InvalidOperationException("A versioned recorder host is driven by one runner at a time and this one is already owned by a running loop. Two loops over one node interleave, and the node records one request at a time.");
        }
    }


    /// <summary>Releases the claim, which the runner does when its loop ends on any path.</summary>
    internal void ReleaseFromRunner() => Interlocked.Exchange(ref owner, 0);


    private void ThrowIfOwned()
    {
        if(Volatile.Read(ref owner) != 0)
        {
            throw new InvalidOperationException("A running runner owns this host and is the only code that may touch it; a call from outside its loop interleaves with the loop's own. Queue the work through the runner instead, whose RecordAsync, LearnAsync, MakeDurableAsync and ReadCommittedAsync are the sequenced paths.");
        }
    }


    /// <summary>
    /// Reconstructs a host of <paramref name="genesis"/>'s chain from durable <paramref name="state"/>,
    /// refusing fail-closed every snapshot whose parts disagree with one another or with the chain.
    /// </summary>
    /// <param name="genesis">
    /// The chain's genesis membership. It comes before the state because it is configuration rather than
    /// durable protocol state, as identity and membership come before the state in
    /// <see cref="RaftNode{TCommand}.FromState"/>.
    /// </param>
    /// <param name="self">This host's own identity, which the membership filter reads against the restored configuration.</param>
    /// <param name="state">The durable state to restore.</param>
    /// <returns>A host serving the instance the restored record implies.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="genesis"/> or <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="StateRestoreException">
    /// Thrown when the snapshot's parts disagree: a
    /// <see cref="QuePaxaVersionedNodeState{TValue}.ConfiguredLeader"/> other than the one
    /// <see cref="QuePaxaLeaderSchedule.LeaderFor(ReplicaId?)"/> derives from the restored record; a
    /// <see cref="QuePaxaVersionedNodeState{TValue}.RecorderVersion"/> other than the version after the restored
    /// record's; a <see cref="QuePaxaVersionedNodeState{TValue}.ActiveConfiguration"/> other than the one the
    /// restored record implies; a restored membership naming a chain other than
    /// <paramref name="genesis"/>'s; or a recorder standing at <see cref="RecorderStep.Zero"/> that carries a
    /// proposal in any slot. Also thrown for every state no recorder-driven register can hold, as
    /// <see cref="QuePaxaRecorder{TValue}.FromState(ProposerLane?, QuePaxaRecorderState{TValue})"/> defines
    /// them.
    /// </exception>
    /// <exception cref="ConsensusRefusedException">Thrown if the restored record is at <see cref="RegisterVersion.MaxValue"/>, so that no version follows it and this host would serve none, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>.</exception>
    /// <remarks>
    /// <para>
    /// The cross-checks are what a torn snapshot fails, and each reads a stored copy of something the record
    /// already implies. A stored leader other than the derived one means two hosts can hold records implying
    /// different leaders for one instance, which is the reserved-priority divergence hazard, and a host whose
    /// own snapshot says so refuses to start rather than joining as the second leader. A stored version other
    /// than the derived one means a register from one instance beside a record from another. A stored
    /// membership other than the derived one is the same tear one field further along, and it is the one a
    /// snapshot written in two parts across a reconfiguration leaves behind. None of them could fire against a
    /// value the restore recomputed, which is why all three are stored.
    /// </para>
    /// <para>
    /// The chain check is a different claim and not a tear at all. It compares the restored membership's chain
    /// identity against the genesis this host was handed, so a store attached to the wrong cluster, or a
    /// genesis edited under a restarting host, refuses to start rather than joining a chain it never agreed
    /// with. It names an operator act, because no protocol path can produce it.
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
    public static QuePaxaVersionedNode<TValue> FromState(QuePaxaConfiguration genesis, ReplicaId self, QuePaxaVersionedNodeState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(genesis);
        ArgumentNullException.ThrowIfNull(state);

        //The derivation reads the record and the genesis alone, never the stored copies the rules below
        //compare against, so a single torn field trips exactly the one rule that reads it.
        QuePaxaConfiguration derived = ConfigurationFor(state.Committed, genesis);
        QuePaxaLeaderSchedule schedule = ScheduleFor(derived);
        ReplicaId? previousWriter = state.Committed?.Writer;
        ProposerLane? derivedLeader = schedule.LeaderFor(previousWriter);

        if(state.ConfiguredLeader != derivedLeader)
        {
            throw new StateRestoreException(StateRestoreRefusal.HostLeaderMismatch, $"A restored configured leader must be the one the schedule derives from the restored record, which is {Describe(derivedLeader)}, and it is {Describe(state.ConfiguredLeader)}. Two hosts whose records imply different leaders for one instance admit two reserved claims at the step the fast path reads.", nameof(state));
        }

        RegisterVersion live = state.Committed is { } committed ? committed.Version.Next() : RegisterVersion.First;
        if(state.RecorderVersion != live)
        {
            throw new StateRestoreException(StateRestoreRefusal.HostRecorderVersionMismatch, $"A restored recorder must serve the version after the restored record's, which is {live.Value}, and it serves {state.RecorderVersion.Value}. A register from one instance beside a record from another is a snapshot written in two parts and torn between them.", nameof(state));
        }

        if(!state.ActiveConfiguration.Equals(derived))
        {
            throw new StateRestoreException(StateRestoreRefusal.HostConfigurationMismatch, "A restored membership must be the one the restored record implies, which is its next configuration, or the genesis membership when no record was restored. A register from one instance beside a configuration from another is a snapshot written in two parts and torn between them.", nameof(state));
        }

        if(!derived.Cluster.Equals(genesis.Cluster))
        {
            throw new StateRestoreException(StateRestoreRefusal.HostForeignChain, $"A restored membership must name the chain this host was given, which is {genesis.Cluster}, and it names {derived.Cluster}. A store attached to the wrong cluster, or a genesis changed under a restarting host, is an operator act, and joining the wrong chain merges two that have never agreed on anything.", nameof(state));
        }

        QuePaxaRecorder<VersionedValue<TValue>> recorder;
        if(state.Recorder.Step == RecorderStep.Zero)
        {
            if(state.Recorder.First is not null || state.Recorder.CurrentAggregate is not null || state.Recorder.PriorAggregate is not null)
            {
                throw new StateRestoreException(StateRestoreRefusal.HostUnwrittenRecorderCarriesProposal, "A restored recorder at step zero must be the unwritten register, carrying no proposal in any slot. A recorder records nothing below round one phase zero, so a proposal standing at step zero was never recorded there and rebuilding the register unwritten would discard it.", nameof(state));
            }

            recorder = schedule.RecorderFor<VersionedValue<TValue>>(previousWriter);
        }
        else
        {
            recorder = schedule.RecorderFor<VersionedValue<TValue>>(previousWriter, state.Recorder);
        }

        return new QuePaxaVersionedNode<TValue>(genesis, self, state.Committed, derived, schedule, new QuePaxaNode<VersionedValue<TValue>>(recorder));
    }


    /// <summary>The version a host holding <paramref name="committed"/> serves.</summary>
    /// <param name="committed">The committed record to derive from, or <see langword="null"/> for a host that has learned none.</param>
    /// <returns>The version after the record's.</returns>
    /// <exception cref="ConsensusRefusedException">Thrown if the record is at <see cref="RegisterVersion.MaxValue"/>, so that no version follows it, carrying <see cref="ConsensusRefusal.VersionRangeSpent"/>.</exception>
    /// <remarks>
    /// The derivation takes the record rather than reading <see cref="Committed"/>, so a caller needing the
    /// record and the version it implies together reads the field once and derives from that one snapshot.
    /// </remarks>
    private static RegisterVersion LiveVersionFor(VersionedValue<TValue>? committed) => committed is { } record ? record.Version.Next() : RegisterVersion.First;


    /// <summary>The membership a host holding <paramref name="committed"/> of <paramref name="genesis"/>'s chain runs its live instance under.</summary>
    /// <param name="committed">The committed record to derive from, or <see langword="null"/> for a host that has learned none.</param>
    /// <param name="genesis">The chain's genesis membership, which is the base case of the derivation.</param>
    /// <returns>The record's next configuration, or the genesis membership when there is no record.</returns>
    /// <remarks>
    /// The one derivation both the memo and the restore read, so a stored membership is compared against the
    /// same expression the memo is computed from and the two cannot say different things about one record.
    /// </remarks>
    private static QuePaxaConfiguration ConfigurationFor(VersionedValue<TValue>? committed, QuePaxaConfiguration genesis) => committed?.NextConfiguration ?? genesis;


    /// <summary>The leader derivation over <paramref name="configuration"/>'s member order.</summary>
    /// <param name="configuration">The membership the instance runs under.</param>
    /// <returns>The derivation.</returns>
    /// <remarks>
    /// The base delay is zero because nothing on this side of the seam hedges: a host reads the derivation for
    /// who leads, and every delay it could carry is local tuning a register applies to its own sending.
    /// </remarks>
    private static QuePaxaLeaderSchedule ScheduleFor(QuePaxaConfiguration configuration) => new(configuration.ScheduleWith(TimeSpan.Zero));


    private static string Describe(ProposerLane? lane) => lane?.ToString() ?? "leaderless";


    /// <summary>The rules a host refuses a record request on, in the order they are evaluated.</summary>
    private enum RequestRefusal
    {
        /// <summary>No rule refuses the request and the host serves it.</summary>
        None,

        /// <summary>The committed record stands at the last representable version, so this host serves none.</summary>
        Exhausted,

        /// <summary>The request names an instance other than the live one.</summary>
        Instance,

        /// <summary>The carried record names a chain other than the active configuration's.</summary>
        Cluster,

        /// <summary>This host is outside the membership the live instance runs under.</summary>
        Membership,

        /// <summary>The carried record is absent, or its own version differs from the envelope's.</summary>
        CarriedRecord
    }
}
