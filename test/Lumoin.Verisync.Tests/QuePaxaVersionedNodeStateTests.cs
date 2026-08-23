using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Covers the versioned host's durable seam: <see cref="QuePaxaVersionedNode{TValue}.ToState"/> snapshots the
/// committed record beside the recorder serving the instance it implies,
/// <see cref="QuePaxaVersionedNode{TValue}.FromState"/> rebuilds a host a proposer cannot distinguish from the
/// one that crashed, and the cross-checks that make a snapshot torn across those two parts refusable.
/// </summary>
/// <remarks>
/// <para>
/// Every rejection below builds its state by hand rather than through a host, because the cross-checks exist
/// precisely for the snapshots no honest history writes: a rule exercised only through host-produced states
/// would pass while asserting nothing. Each such state is otherwise well formed, so exactly one rule can be
/// what refuses it, and the recorder half of every hand-built state stands at the round's first step with an
/// ordinary proposal in both slots, which is the shape the recorder's own restore accepts under every
/// configured leader.
/// </para>
/// <para>
/// The three derived fields carry the whole design. A restore that recomputed the leader, the version and the
/// membership from the committed record would compare each with itself, so the stored copies are what let a
/// torn snapshot announce itself, and the tests here fire each rule by moving exactly one stored copy away
/// from what the record implies. The chain check is the exception and reads no stored copy at all: it compares
/// the membership the record implies against the genesis the host was handed, which is the one disagreement no
/// protocol path can produce and only an operator can.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaVersionedNodeStateTests
{
    /// <summary>The configured order's first replica, which leads the instance no record precedes.</summary>
    private static ReplicaId First { get; } = Replica(1);

    /// <summary>The writer of the committed record most of these hosts hold, so it leads the live instance.</summary>
    private static ReplicaId Second { get; } = Replica(2);

    /// <summary>A third member of the order, which leads no instance under test.</summary>
    private static ReplicaId Third { get; } = Replica(3);

    /// <summary>A replica no configuration under test starts with, which a growing change adds.</summary>
    private static ReplicaId Fourth { get; } = Replica(4);

    /// <summary>A writer outside the configured membership, which the derivation answers leaderless for.</summary>
    private static ReplicaId Stranger { get; } = Replica(9);

    /// <summary>
    /// The host the membership admits for <see cref="First"/>. Every host in this suite is that one, so its
    /// incarnation is what a hand-built snapshot carries and what a restore composes its identity from.
    /// </summary>
    private static HostId FirstHost { get; } = Membership.Member(First);

    /// <summary>
    /// The genesis membership every host in this suite runs under, and the membership every record it holds
    /// carries forward unchanged.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third));

    /// <summary>
    /// A membership over the same replicas in the same order on a different chain, which is what an
    /// independently bootstrapped cluster mints.
    /// </summary>
    private static QuePaxaConfiguration ForeignChain { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Stranger)).Without(Stranger).With(Membership.Member(Third));

    /// <summary>The round's first step, which is where the fast path is read and where a host's first record lands.</summary>
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// The round trip, whose load-bearing half is the last assertions rather than the field comparison. Matching
    /// fields prove the snapshot complete; a restored host that serves the same instance and answers a further
    /// request exactly as the original does is what makes the restart invisible to a proposer.
    /// </summary>
    [TestMethod]
    public void ToStateAndFromStateRoundTripAHostThatAnswersIdentically()
    {
        QuePaxaVersionedNode<string> original = new(Configuration, FirstHost, Record(4UL, Second));

        _ = original.Handle(Request(5UL, new ProposalPriority(10), Second, "a"));
        _ = original.Handle(Request(5UL, new ProposalPriority(20), Third, "b"));

        QuePaxaVersionedNodeState<string> snapshot = original.ToState();

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"committed v{snapshot.Committed!.Version.Value}, recorder serving v{snapshot.RecorderVersion.Value} at step {snapshot.Recorder.Step.Value}"));

        Assert.AreEqual(Record(4UL, Second), snapshot.Committed);
        Assert.AreEqual(new RegisterVersion(5UL), snapshot.RecorderVersion);
        Assert.AreEqual(ProposerLane.For(Second), snapshot.ConfiguredLeader);
        Assert.AreEqual(Four, snapshot.Recorder.Step);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, snapshot);

        Assert.AreEqual(snapshot, restored.ToState());
        Assert.AreEqual(new RegisterVersion(5UL), restored.LiveVersion);
        Assert.AreEqual(ProposerLane.For(Second), restored.Recorder.ConfiguredLeader);

        //A request that advances the step answers with one reply at both hosts, so a proposer reading it cannot
        //tell which of the two served it.
        VersionedRecordRequest<VersionedValue<string>> next = Request(5UL, new ProposalPriority(30), Second, "c", Four.Next());

        Assert.AreEqual(original.Handle(next), restored.Handle(next));
        Assert.AreEqual(original.ToState(), restored.ToState());
    }


    /// <summary>
    /// A snapshot names the host that wrote it, and a restore is refused unless the host restoring it is that
    /// host. Both halves of the identity are compared, because a store that came back under a replica it
    /// never served and one restated under an incarnation it does not hold are the same operator act reported
    /// from two sides.
    /// </summary>
    /// <remarks>
    /// The refusal is what keeps the membership filter meaningful. A restore that took the caller's word
    /// would let a deployment restate its store's incarnation as the value the configuration admits however
    /// its store came back, and the filter downstream would then be testing a claim rather than what the
    /// store is; the snapshot holds the fact, so the restatement is the thing refused. The honest restore
    /// stands beside both rejections, so the rule is what refuses them and not the shape of a snapshot no
    /// host writes.
    /// </remarks>
    [TestMethod]
    public void ARestoreRefusesASnapshotWrittenByAnotherHost()
    {
        QuePaxaVersionedNode<string> original = new(Configuration, FirstHost, Record(4UL, Second));
        QuePaxaVersionedNodeState<string> snapshot = original.ToState();

        Assert.AreEqual(FirstHost, snapshot.Host);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, snapshot);

        Assert.AreEqual(FirstHost, restored.Self);
        Assert.IsFalse(restored.Declines(Request(5UL, new ProposalPriority(10), Second, "a")));

        //The same store, restated under an incarnation it does not hold, which is what a deployment reading
        //its incarnation out of the membership rather than out of its store would hand back.
        StateRestoreException restated = Assert.ThrowsExactly<StateRestoreException>(
            () => QuePaxaVersionedNode<string>.FromState(Configuration, Membership.Restored(First), snapshot));

        Assert.AreEqual(StateRestoreRefusal.HostIdentityMismatch, restated.Refusal);
        Assert.AreEqual("state", restated.ParamName);

        //The same store attached to another replica's deployment slot, which is a store copied or moved and
        //not a configuration change: replacing a member's store retires one member and admits another, so no
        //protocol path moves a replica between stores.
        StateRestoreException moved = Assert.ThrowsExactly<StateRestoreException>(
            () => QuePaxaVersionedNode<string>.FromState(Configuration, new HostId(Second, FirstHost.Incarnation), snapshot));

        Assert.AreEqual(StateRestoreRefusal.HostIdentityMismatch, moved.Refusal);

        //And the identity is read before every derivation, so a snapshot of another host is refused as
        //another host's rather than as a tear in a record that was never this host's to hold.
        QuePaxaVersionedNodeState<string> torn = snapshot with { RecorderVersion = new RegisterVersion(9UL) };

        Assert.AreEqual(
            StateRestoreRefusal.HostIdentityMismatch,
            Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, Membership.Restored(First), torn)).Refusal);
    }


    /// <summary>
    /// A store that came back empty reaches no restore at all, because there is no snapshot to restore from.
    /// It is constructed as a new store under a new incarnation, and the configuration that admitted the one
    /// it lost refuses it — which is the whole of what the binding buys and the reason a wipe is not
    /// detectable from a snapshot's own fields.
    /// </summary>
    /// <remarks>
    /// The wiped host and a host that has genuinely learned nothing carry identical state, and both stand at
    /// the first version under the bootstrap leader; the assertions below say so before the refusal, so what
    /// separates them is the store each holds and nothing about what either has learned.
    /// </remarks>
    [TestMethod]
    public void AWipedStoreComesBackAsANewStoreAndTheMembershipRefusesIt()
    {
        QuePaxaVersionedNode<string> wiped = new(Configuration, Membership.Restored(First));
        QuePaxaVersionedNode<string> fresh = new(Configuration, FirstHost);

        Assert.IsNull(wiped.Committed);
        Assert.AreEqual(RegisterVersion.First, wiped.LiveVersion);
        Assert.AreEqual(fresh.LiveVersion, wiped.LiveVersion);
        Assert.AreEqual(fresh.ActiveConfiguration, wiped.ActiveConfiguration);
        Assert.AreEqual(fresh.Recorder.ConfiguredLeader, wiped.Recorder.ConfiguredLeader);

        VersionedRecordRequest<VersionedValue<string>> first = Request(1UL, new ProposalPriority(10), Second, "a");

        Assert.IsFalse(fresh.Declines(first));
        Assert.IsTrue(wiped.Declines(first));
        Assert.AreEqual(
            ConsensusRefusal.StoreNotAdmittedForMember,
            Assert.ThrowsExactly<ConsensusRefusedException>(() => _ = wiped.Handle(first)).Refusal);
    }


    /// <summary>
    /// A host occupies the unwritten register for the whole interval between learning a version and answering
    /// the first request for the next one, so that interval has to survive a restart. The recorder's own restore
    /// refuses a step-zero state and this one rebuilds it, which is the one place the two restores differ.
    /// </summary>
    [TestMethod]
    public void AHostThatHasAnsweredNothingRestoresTheUnwrittenRegister()
    {
        //Both shapes reach step zero: the bootstrap host that has learned nothing, and a host that learned a
        //record and has not been asked for the version after it.
        QuePaxaVersionedNode<string> bootstrap = new(Configuration, FirstHost);
        QuePaxaVersionedNode<string> learned = new(Configuration, FirstHost, Record(4UL, Second));

        Assert.IsTrue(learned.Learn(Record(5UL, Third)));

        //The third shape is the one the rebuild can get wrong in silence. A record whose writer left the
        //membership makes the instance leaderless, and a rebuild falling back on the configured order's head
        //restores a leader where the derivation supplies none, which is a host that honours reserved claims
        //its neighbours decline. A rebuild reached only through hosts with a derived leader cannot tell the
        //two apart.
        QuePaxaVersionedNode<string> leaderless = new(Configuration, FirstHost, Record(4UL, Stranger));

        Assert.IsNull(leaderless.Recorder.ConfiguredLeader);

        //The fourth shape is the one membership makes routine rather than exotic: the writer removed itself,
        //so the configuration the record installs no longer contains it and the derivation is leaderless over
        //a member list that is not the genesis one. A rebuild reading the genesis order rather than the
        //record's own would restore this host led by its first member.
        QuePaxaVersionedNode<string> selfRemoved = new(Configuration, FirstHost, RemovingItsWriter(4UL, Second));

        Assert.IsNull(selfRemoved.Recorder.ConfiguredLeader);
        Assert.IsFalse(selfRemoved.ActiveConfiguration.Contains(Second));

        foreach(QuePaxaVersionedNode<string> host in (QuePaxaVersionedNode<string>[])[bootstrap, learned, leaderless, selfRemoved])
        {
            QuePaxaVersionedNodeState<string> snapshot = host.ToState();

            Assert.AreEqual(RecorderStep.Zero, snapshot.Recorder.Step);

            //The recorder's own restore refuses exactly this state, which is why the versioned restore has to
            //carry a rule of its own rather than delegating the whole range.
            StateRestoreException refusedByTheRecorder = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaRecorder<VersionedValue<string>>.FromState(snapshot.ConfiguredLeader, snapshot.Recorder));

            Assert.AreEqual(StateRestoreRefusal.RecorderStepBelowFloor, refusedByTheRecorder.Refusal);

            QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, snapshot);

            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"restored an unwritten register serving v{restored.LiveVersion.Value}"));

            Assert.AreEqual(snapshot, restored.ToState());
            Assert.AreEqual(host.LiveVersion, restored.LiveVersion);
            Assert.AreEqual(host.Recorder.ConfiguredLeader, restored.Recorder.ConfiguredLeader);
            Assert.AreEqual(RecorderStep.Zero, restored.Recorder.Step);
        }
    }


    /// <summary>
    /// The leader cross-check. Two hosts holding records that imply different leaders for one instance admit
    /// two reserved claims at the step the fast path reads, so a host whose own snapshot says its stored leader
    /// is not the derived one refuses to start rather than joining as the second leader.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAConfiguredLeaderOtherThanTheOneTheRecordDerives()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        VersionedValue<string> committed = Record(4UL, Second);

        Assert.AreEqual(ProposerLane.For(Second), schedule.LeaderFor(Second));

        //Every shape of wrong leader there is: the configured order's head, which is what a restore falling
        //back on configuration rather than derivation would supply; another member; a second lane of the right
        //replica, which the binding declines because it binds a lane rather than a replica; and none at all.
        ProposerLane?[] wrong = [ProposerLane.For(First), ProposerLane.For(Third), new ProposerLane(Second, 1), null];

        foreach(ProposerLane? leader in wrong)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stored leader {Describe(leader)} against derived {Describe(schedule.LeaderFor(Second))}"));

            QuePaxaVersionedNodeState<string> state = new(FirstHost, committed, new RegisterVersion(5UL), leader, Configuration, RestorableRecorder());

            StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

            Assert.AreEqual(StateRestoreRefusal.HostLeaderMismatch, refused.Refusal);
            Assert.AreEqual("state", refused.ParamName);
        }

        //The mirror case, and the one a fallback to configuration produces: the record's writer left the order,
        //so the derivation is leaderless and a stored leader is the disagreement.
        QuePaxaVersionedNodeState<string> leaderlessInstance = new(FirstHost, Record(4UL, Stranger), new RegisterVersion(5UL), ProposerLane.For(First), Configuration, RestorableRecorder());

        Assert.IsNull(schedule.LeaderFor(Stranger));

        StateRestoreException refusedLeaderless = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, leaderlessInstance));

        Assert.AreEqual(StateRestoreRefusal.HostLeaderMismatch, refusedLeaderless.Refusal);
    }


    /// <summary>
    /// The version cross-check. A recorder from one instance beside a committed record from another is what a
    /// snapshot written in two parts and torn between them leaves behind, and the stored version is what makes
    /// that visible.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsARecorderVersionOtherThanTheOneAfterTheRecord()
    {
        VersionedValue<string> committed = Record(4UL, Second);

        //The record's own version, the version two ahead, and the first version, which is what a register
        //restored from a different key or a reset counter would carry.
        foreach(ulong version in (ulong[])[4UL, 6UL, 1UL])
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stored recorder version {version} against derived 5"));

            QuePaxaVersionedNodeState<string> state = new(FirstHost, committed, new RegisterVersion(version), ProposerLane.For(Second), Configuration, RestorableRecorder());

            StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

            Assert.AreEqual(StateRestoreRefusal.HostRecorderVersionMismatch, refused.Refusal);
            Assert.AreEqual("state", refused.ParamName);
        }

        //A host that has learned nothing serves the first version and nothing else, so the rule fires there too
        //rather than only where a record is present to compare against.
        QuePaxaVersionedNodeState<string> bootstrap = new(FirstHost, null, new RegisterVersion(2UL), ProposerLane.For(First), Configuration, RestorableRecorder());

        StateRestoreException refusedBootstrap = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, bootstrap));

        Assert.AreEqual(StateRestoreRefusal.HostRecorderVersionMismatch, refusedBootstrap.Refusal);
    }


    /// <summary>
    /// The step-zero short circuit is the unwritten register exactly. A recorder records nothing below round one
    /// phase zero, so a proposal standing at step zero was never recorded there, and rebuilding the register
    /// unwritten would discard it instead of refusing the snapshot that carried it.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAStepZeroRecorderCarryingAProposalInAnySlot()
    {
        VersionedValue<string> committed = Record(4UL, Second);
        PrioritizedProposal<VersionedValue<string>> proposal = Ordinary(10, ProposerLane.For(Second), 5UL, Second, "a");

        QuePaxaRecorderState<VersionedValue<string>>[] carrying =
        [
            new(RecorderStep.Zero, proposal, null, null),
            new(RecorderStep.Zero, null, proposal, null),
            new(RecorderStep.Zero, null, null, proposal)
        ];

        foreach(QuePaxaRecorderState<VersionedValue<string>> recorder in carrying)
        {
            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"step zero carrying first={recorder.First is not null}, aggregate={recorder.CurrentAggregate is not null}, prior={recorder.PriorAggregate is not null}"));

            QuePaxaVersionedNodeState<string> state = new(FirstHost, committed, new RegisterVersion(5UL), ProposerLane.For(Second), Configuration, recorder);

            StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

            Assert.AreEqual(StateRestoreRefusal.HostUnwrittenRecorderCarriesProposal, refused.Refusal);
            Assert.AreEqual("state", refused.ParamName);
        }
    }


    /// <summary>
    /// Every step but zero reaches the recorder's own restore, so the rules that refuse a state no
    /// recorder-driven register can hold are not weakened by the short circuit above them. The steps between
    /// zero and the recorder's floor are what a short circuit stated as "below the floor" rather than "at step
    /// zero" would let through.
    /// </summary>
    [TestMethod]
    public void FromStateStillRefusesEveryStateTheRecordersOwnRestoreRefuses()
    {
        VersionedValue<string> committed = Record(4UL, Second);
        PrioritizedProposal<VersionedValue<string>> proposal = Ordinary(10, ProposerLane.For(Second), 5UL, Second, "a");

        for(int step = 1; step < RecorderStep.RoundOnePhaseZero.Value; step++)
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"below the recorder's floor at step {step}"));

            //The empty shape is what pins the short circuit to step zero exactly. Widened to the recorder's
            //whole floor it would rebuild this state unwritten rather than refuse it, and the carrying shape
            //alone cannot tell, because a widened short circuit refuses that one for its proposals instead.
            QuePaxaRecorderState<VersionedValue<string>>[] shapes =
            [
                new(new RecorderStep(step), proposal, proposal, null),
                new(new RecorderStep(step), null, null, null)
            ];

            foreach(QuePaxaRecorderState<VersionedValue<string>> recorder in shapes)
            {
                QuePaxaVersionedNodeState<string> state = new(FirstHost, committed, new RegisterVersion(5UL), ProposerLane.For(Second), Configuration, recorder);

                StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

                Assert.AreEqual(StateRestoreRefusal.RecorderStepBelowFloor, refused.Refusal);
            }
        }

        //A foreign reserved claim at the round's first step, which is the recorder rule the derived leader makes
        //checkable: the stored leader agrees with the record, so only the recorder's own restore can refuse it.
        PrioritizedProposal<VersionedValue<string>> foreignClaim = Reserved(ProposerLane.For(Third), 5UL, Third, "b");
        QuePaxaVersionedNodeState<string> claimAtFour = new(FirstHost, 
            committed,
            new RegisterVersion(5UL),
            ProposerLane.For(Second),
            Configuration,
            new QuePaxaRecorderState<VersionedValue<string>>(Four, foreignClaim, foreignClaim, null));

        //The claim stands in both slots, so RecorderForeignClaimInFirstProposal and
        //RecorderForeignClaimInAggregate are jointly reachable and only the order the rules are stated in
        //decides which one answers. The row names no refusal, because what it pins is that a recorder rule
        //still reaches this state through the host's restore rather than which of the two halves states it.
        Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, claimAtFour));
    }


    /// <summary>
    /// A stale record that is internally consistent restores, and that is correct rather than a gap. The leader
    /// is a deterministic function of the record, so an old record yields exactly the leader its own instance
    /// ran under, and the version gate keeps the restored host from touching the live instance at all.
    /// </summary>
    [TestMethod]
    public void AStaleButConsistentSnapshotRestoresAndServesOnlyItsOwnInstance()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, FirstHost, Record(4UL, Second));
        QuePaxaVersionedNodeState<string> stale = host.ToState();

        //The deployment moved on by two versions while this snapshot sat on disk.
        Assert.IsTrue(host.Learn(Record(5UL, Third)));
        Assert.IsTrue(host.Learn(Record(6UL, First)));
        Assert.AreEqual(new RegisterVersion(7UL), host.LiveVersion);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, stale);

        Assert.AreEqual(new RegisterVersion(5UL), restored.LiveVersion);
        Assert.AreEqual(ProposerLane.For(Second), restored.Recorder.ConfiguredLeader);

        //It cannot serve the live instance, so a stale host costs a deployment availability and never agreement.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = restored.Handle(Request(7UL, ProposalPriority.Lowest, First, "live")));

        //Learning what it missed puts it back on the live instance under the leader every other host derives.
        Assert.IsTrue(restored.Learn(Record(6UL, First)));
        Assert.AreEqual(host.LiveVersion, restored.LiveVersion);
        Assert.AreEqual(host.Recorder.ConfiguredLeader, restored.Recorder.ConfiguredLeader);
    }


    /// <summary>
    /// The one-round-trip fast path across a restart of the whole host, which is what a restore has to preserve.
    /// The snapshot is the exact durable state the fast path leaves behind, and a quorum containing the restored
    /// host's answer still decides at the step the round began at.
    /// </summary>
    [TestMethod]
    public void ARestoredHostStillDecidesTheFastPathInOneRoundTrip()
    {
        QuePaxaVersionedNode<string> crashed = new(Configuration, FirstHost, Record(4UL, Second));
        VersionedRecordRequest<VersionedValue<string>> claim = Request(5UL, ProposalPriority.Reserved, Second, "fast");

        VersionedRecordReply<VersionedValue<string>> beforeCrash = crashed.Handle(claim);

        //The leader's claim was honoured rather than downgraded, so the snapshot is the fast path's own.
        Assert.AreEqual(ProposalPriority.Reserved, beforeCrash.Reply.First.Key.Priority);

        QuePaxaVersionedNodeState<string> snapshot = crashed.ToState();
        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, snapshot);

        QuePaxaRecorder<VersionedValue<string>> afterRestore = restored.Recorder;

        //The proposer's retransmission after the restart re-delivers the claim the snapshot was taken after, and
        //the same recorder instance back is the no-durability-write contract surviving the restart.
        VersionedRecordReply<VersionedValue<string>> afterRestart = restored.Handle(claim);

        Assert.AreSame(afterRestore, restored.Recorder);
        Assert.AreEqual(beforeCrash, afterRestart);

        //A host that never restarted serves the rest of the quorum, so the restored host's answer is
        //load-bearing in the decision rather than carried by hosts that all stayed up.
        QuePaxaVersionedNode<string> live = new(Configuration, FirstHost, Record(4UL, Second));
        VersionedRecordReply<VersionedValue<string>> liveAnswer = live.Handle(claim);

        QuePaxaRound<VersionedValue<string>> round = QuePaxaRound<VersionedValue<string>>.Begin(
            ProposerLane.For(Second),
            ProposerLane.For(Second),
            new VersionedValue<string>(new RegisterVersion(5UL), Second, Configuration, "fast"));

        ImmutableArray<RecorderAnswer<VersionedValue<string>>> quorum =
        [
            new RecorderAnswer<VersionedValue<string>>(0, Summary(afterRestart)),
            new RecorderAnswer<VersionedValue<string>>(1, Summary(liveAnswer))
        ];

        QuePaxaStepOutcome<VersionedValue<string>> outcome = round.Conclude(quorum, 3);

        TestContext.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"decided at step {outcome.DecidedAt.Value} on {outcome.SummaryCount} answers of 3 hosts, one of them restored"));

        Assert.AreEqual(QuePaxaStepKind.Decided, outcome.Kind);
        Assert.AreEqual(new VersionedValue<string>(new RegisterVersion(5UL), Second, Configuration, "fast"), outcome.DecidedValue);
        Assert.AreEqual(round.Step, outcome.DecidedAt, "A decision above the step the round began at is more than one round trip.");
        Assert.AreEqual(Four, outcome.DecidedAt);
    }


    /// <summary>
    /// A leaderless instance is a derived fact and not a missing one, so it round-trips as one: the stored
    /// leader is null because the derivation is, and the restored host declines a reserved claim exactly as the
    /// host that crashed did.
    /// </summary>
    /// <remarks>
    /// The record here removes its own writer rather than being written by a stranger, which is the shape
    /// membership makes routine: every self-removal produces a leaderless instance, so the restore is exercised
    /// over a member list that is not the genesis one and a rebuild reading the genesis order rather than the
    /// record's own is caught here.
    /// </remarks>
    [TestMethod]
    public void ALeaderlessInstanceRoundTripsAndKeepsDecliningReservedClaims()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, FirstHost, RemovingItsWriter(4UL, Second));

        Assert.IsNull(host.Recorder.ConfiguredLeader);
        Assert.IsFalse(host.ActiveConfiguration.Contains(Second));

        _ = host.Handle(Request(5UL, new ProposalPriority(10), Second, "a"));

        QuePaxaVersionedNodeState<string> snapshot = host.ToState();

        Assert.IsNull(snapshot.ConfiguredLeader);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, snapshot);

        Assert.IsNull(restored.Recorder.ConfiguredLeader);

        //A restore falling back on the configured order's head would honour this claim at the round's first
        //step, and the reserved priority dominates every ordinary one, so an honoured claim would take the
        //aggregate. Declined, it is recorded at the lowest ordinary priority and loses to the incumbent.
        _ = restored.Handle(Request(5UL, ProposalPriority.Reserved, First, "b"));

        Assert.IsFalse(restored.Recorder.Register.CurrentAggregate!.Key.Priority.IsReserved);
        Assert.AreEqual(new ProposalPriority(10), restored.Recorder.Register.CurrentAggregate.Key.Priority);
    }


    /// <summary>
    /// A host holding the last representable record serves no version at all, so both halves of the durable seam
    /// surface the exhaustion rather than inventing a version, exactly as
    /// <see cref="QuePaxaVersionedNode{TValue}.LiveVersion"/> does.
    /// </summary>
    [TestMethod]
    public void TheSpentVersionSurfacesAsExhaustionOnBothHalvesOfTheSeam()
    {
        VersionedValue<string> spent = new(RegisterVersion.MaxValue, Second, Configuration, "last");
        QuePaxaVersionedNode<string> host = new(Configuration, FirstHost, spent);

        ConsensusRefusedException snapshotting = Assert.ThrowsExactly<ConsensusRefusedException>(() => _ = host.ToState());

        Assert.AreEqual(ConsensusRefusal.VersionRangeSpent, snapshotting.Refusal);

        QuePaxaVersionedNodeState<string> state = new(FirstHost, spent, RegisterVersion.First, ProposerLane.For(Second), Configuration, RestorableRecorder());

        //One rule reported from both halves of the seam, which prose could not tell apart at all: the same
        //sentence stood at the version's own successor and at a host that can serve no version because of it.
        ConsensusRefusedException restoring = Assert.ThrowsExactly<ConsensusRefusedException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

        Assert.AreEqual(ConsensusRefusal.VersionRangeSpent, restoring.Refusal);
    }


    /// <summary>
    /// The membership cross-check. A register from one instance beside a configuration from another is the
    /// same tear the version cross-check refuses, one field further along, and it is what a snapshot written
    /// in two parts across a reconfiguration leaves behind.
    /// </summary>
    /// <remarks>
    /// The vector differs by a member that is not the record's writer, which is what makes it R11's alone: the
    /// leader is derived from the writer and the record's own configuration, so a stored configuration that
    /// gained or lost some other replica leaves the leader check and the version check both passing, and only
    /// the membership comparison can refuse it.
    /// </remarks>
    [TestMethod]
    public void FromStateRejectsAStoredMembershipOtherThanTheOneTheRecordImplies()
    {
        VersionedValue<string> committed = Record(4UL, Second);

        //Second wrote the record and Second stays a member in every stored configuration below, so the leader
        //the restore derives is the same one the snapshot stores whichever of these it carries.
        Assert.AreEqual(ProposerLane.For(Second), Schedule().LeaderFor(Second));

        //A non-writer added, a non-writer removed, and the same members in another order, which is a different
        //configuration because the order is the hedging order and the first member is the bootstrap leader.
        QuePaxaConfiguration[] torn =
        [
            Configuration.With(Membership.Member(Fourth)),
            Configuration.Without(Third),
            QuePaxaConfiguration.Create(Configuration.Cluster, Membership.Of(Third, Second, First))
        ];

        foreach(QuePaxaConfiguration stored in torn)
        {
            TestContext.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"stored membership of {stored.Members.Length} members against the record's {Configuration.Members.Length}"));

            Assert.IsTrue(stored.Contains(Second), "The vector must keep the writer, or the leader check fires instead.");

            QuePaxaVersionedNodeState<string> state = new(FirstHost, committed, new RegisterVersion(5UL), ProposerLane.For(Second), stored, RestorableRecorder());

            StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

            Assert.AreEqual(StateRestoreRefusal.HostConfigurationMismatch, refused.Refusal);
            Assert.AreEqual("state", refused.ParamName);
        }

        //A host that has learned nothing stands at genesis, so the rule fires there too rather than only where
        //a record is present to derive from.
        QuePaxaVersionedNodeState<string> bootstrap = new(FirstHost, null, RegisterVersion.First, ProposerLane.For(First), Configuration.With(Membership.Member(Fourth)), RestorableRecorder());

        StateRestoreException refusedBootstrap = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, bootstrap));

        Assert.AreEqual(StateRestoreRefusal.HostConfigurationMismatch, refusedBootstrap.Refusal);

        //The arm that holds the two rules' inputs apart. The record removed its own writer, so the derivation
        //is leaderless and the stored leader agrees with it, while the stored membership still lists the
        //writer. A leader check reading the stored membership rather than the record's own would derive a
        //leader here and fire first, which would leave the membership comparison pinned by nothing.
        QuePaxaVersionedNodeState<string> writerStillListed = new(FirstHost, RemovingItsWriter(4UL, Second), new RegisterVersion(5UL), null, Configuration, RestorableRecorder());

        Assert.IsTrue(Configuration.Contains(Second));
        Assert.IsFalse(Configuration.Without(Second).Contains(Second));

        StateRestoreException refusedByMembership = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, writerStillListed));

        Assert.AreEqual(StateRestoreRefusal.HostConfigurationMismatch, refusedByMembership.Refusal);

        //And the matching snapshot restores, which is what a rule that refused every membership would fail.
        QuePaxaVersionedNodeState<string> matching = new(FirstHost, committed, new RegisterVersion(5UL), ProposerLane.For(Second), Configuration, RestorableRecorder());

        Assert.AreEqual(Configuration, QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, matching).ActiveConfiguration);
    }


    /// <summary>
    /// The chain check, which is not a tear at all. It names an operator act — a store attached to the wrong
    /// cluster, or a genesis edited under a restarting host — and refusing is what keeps two chains that never
    /// agreed on anything from merging through one host's disk.
    /// </summary>
    /// <remarks>
    /// The vector carries the same members in the same order under a different chain identity, so the stored
    /// membership equals what the record implies and the leader, the version and the membership comparison all
    /// pass. Only the chain comparison can refuse it.
    /// </remarks>
    [TestMethod]
    public void FromStateRejectsAMembershipNamingAnotherChain()
    {
        Assert.IsTrue(Configuration.Members.SequenceEqual(ForeignChain.Members));
        Assert.AreNotEqual(Configuration.Cluster, ForeignChain.Cluster);

        //The record installs the foreign chain's configuration and the snapshot stores exactly that, so the
        //snapshot is internally consistent and disagrees only with the genesis this host was handed.
        VersionedValue<string> committed = new(new RegisterVersion(4UL), Second, ForeignChain, "committed");
        QuePaxaVersionedNodeState<string> state = new(FirstHost, committed, new RegisterVersion(5UL), ProposerLane.For(Second), ForeignChain, RestorableRecorder());

        StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, state));

        Assert.AreEqual(StateRestoreRefusal.HostForeignChain, refused.Refusal);
        Assert.AreEqual("state", refused.ParamName);

        //The same host restores under the genesis the store was written against, so it is the pairing that is
        //refused rather than the snapshot on its own.
        Assert.AreEqual(ForeignChain, QuePaxaVersionedNode<string>.FromState(ForeignChain, FirstHost, state).ActiveConfiguration);
    }


    /// <summary>
    /// The two rules a single field carries on its own, which the restore therefore owes nothing. No host serves
    /// the unwritten version, and a state with no recorder or no membership names an instance it cannot serve.
    /// </summary>
    [TestMethod]
    public void AVersionedNodeStateCannotExpressAnUnwrittenVersionAnAbsentRecorderOrAnAbsentMembership()
    {
        ArgumentOutOfRangeException unwritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = new QuePaxaVersionedNodeState<string>(FirstHost, null, RegisterVersion.Unwritten, ProposerLane.For(First), Configuration, RestorableRecorder()));

        Assert.AreEqual("RecorderVersion", unwritten.ParamName);

        ArgumentNullException absent = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = new QuePaxaVersionedNodeState<string>(FirstHost, null, RegisterVersion.First, ProposerLane.For(First), Configuration, null!));

        Assert.AreEqual("Recorder", absent.ParamName);

        ArgumentNullException unnamed = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = new QuePaxaVersionedNodeState<string>(FirstHost, null, RegisterVersion.First, ProposerLane.For(First), null!, RestorableRecorder()));

        Assert.AreEqual("ActiveConfiguration", unnamed.ParamName);

        //A `with` expression reaches the accessor body, while construction reaches the property initializer, so
        //the two paths are separate code and a rule stated on only one of them leaves the other open.
        QuePaxaVersionedNodeState<string> valid = new(FirstHost, null, RegisterVersion.First, ProposerLane.For(First), Configuration, RestorableRecorder());

        ArgumentOutOfRangeException rewritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = valid with { RecorderVersion = RegisterVersion.Unwritten });
        ArgumentNullException removed = Assert.ThrowsExactly<ArgumentNullException>(() => _ = valid with { Recorder = null! });
        ArgumentNullException erased = Assert.ThrowsExactly<ArgumentNullException>(() => _ = valid with { ActiveConfiguration = null! });

        Assert.AreEqual("RecorderVersion", rewritten.ParamName);
        Assert.AreEqual("Recorder", removed.ParamName);
        Assert.AreEqual("ActiveConfiguration", erased.ParamName);
    }


    /// <summary>A null genesis or a null state is refused, because a restore has nothing to rebuild from.</summary>
    [TestMethod]
    public void FromStateRejectsANullGenesisAndANullState()
    {
        QuePaxaVersionedNodeState<string> state = new(FirstHost, null, RegisterVersion.First, ProposerLane.For(First), Configuration, RestorableRecorder());

        Assert.ThrowsExactly<ArgumentNullException>(() => QuePaxaVersionedNode<string>.FromState(null!, FirstHost, state));
        Assert.ThrowsExactly<ArgumentNullException>(() => QuePaxaVersionedNode<string>.FromState(Configuration, FirstHost, null!));
    }


    /// <summary>
    /// A recorder state the recorder's own restore accepts under every configured leader, so that a rejection
    /// test using it can only be refused by the versioned cross-check it was written for.
    /// </summary>
    /// <returns>The recorder state.</returns>
    private static QuePaxaRecorderState<VersionedValue<string>> RestorableRecorder()
    {
        PrioritizedProposal<VersionedValue<string>> proposal = Ordinary(10, ProposerLane.For(Second), 5UL, Second, "a");

        return new QuePaxaRecorderState<VersionedValue<string>>(Four, proposal, proposal, null);
    }


    /// <summary>Converts a reply into the summary a round concludes over, which carries the same three fields.</summary>
    /// <param name="reply">The reply a host answered with.</param>
    /// <returns>The summary.</returns>
    private static RecordSummary<VersionedValue<string>> Summary(VersionedRecordReply<VersionedValue<string>> reply)
    {
        return new RecordSummary<VersionedValue<string>>(reply.Reply.Step, reply.Reply.First, reply.Reply.PriorAggregate);
    }


    /// <summary>Builds a request addressed to one instance, carrying the record that instance would decide.</summary>
    /// <param name="version">The version the request's instance produces.</param>
    /// <param name="priority">The proposal's priority.</param>
    /// <param name="owner">The replica proposing, whose lane zero owns the proposal.</param>
    /// <param name="value">The application value.</param>
    /// <param name="step">The step the proposal is tagged with.</param>
    /// <returns>The request.</returns>
    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, ProposalPriority priority, ReplicaId owner, string value, RecorderStep? step = null)
    {
        RegisterVersion at = new(version);
        VersionedValue<string> record = new(at, owner, Configuration, value);
        PrioritizedProposal<VersionedValue<string>> proposal = new(new ProposalKey(priority, ProposerLane.For(owner)), record);

        return new VersionedRecordRequest<VersionedValue<string>>(at, new RecordRequest<VersionedValue<string>>(step ?? Four, proposal));
    }


    /// <summary>Builds a proposal carrying an ordinary priority over a decided record.</summary>
    /// <param name="priority">The ordinary priority.</param>
    /// <param name="owner">The lane that owns the proposal.</param>
    /// <param name="version">The version the carried record is written at.</param>
    /// <param name="writer">The replica the carried record is written by.</param>
    /// <param name="value">The application value.</param>
    /// <returns>The proposal.</returns>
    private static PrioritizedProposal<VersionedValue<string>> Ordinary(ulong priority, ProposerLane owner, ulong version, ReplicaId writer, string value)
    {
        return new PrioritizedProposal<VersionedValue<string>>(
            new ProposalKey(new ProposalPriority(priority), owner),
            new VersionedValue<string>(new RegisterVersion(version), writer, Configuration, value));
    }


    /// <summary>Builds a proposal carrying the reserved priority over a decided record.</summary>
    /// <param name="owner">The lane that owns the proposal.</param>
    /// <param name="version">The version the carried record is written at.</param>
    /// <param name="writer">The replica the carried record is written by.</param>
    /// <param name="value">The application value.</param>
    /// <returns>The proposal.</returns>
    private static PrioritizedProposal<VersionedValue<string>> Reserved(ProposerLane owner, ulong version, ReplicaId writer, string value)
    {
        return new PrioritizedProposal<VersionedValue<string>>(
            new ProposalKey(ProposalPriority.Reserved, owner),
            new VersionedValue<string>(new RegisterVersion(version), writer, Configuration, value));
    }


    /// <summary>Builds a committed record.</summary>
    /// <param name="version">The version it was written at.</param>
    /// <param name="writer">The replica that wrote it.</param>
    /// <returns>The record.</returns>
    private static VersionedValue<string> Record(ulong version, ReplicaId writer)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, Configuration, "committed");
    }


    /// <summary>
    /// Builds a committed record whose own next configuration removes the replica that wrote it, which is the
    /// self-removal a membership change makes ordinary and the shape that leaves an instance leaderless.
    /// </summary>
    /// <param name="version">The version it was written at.</param>
    /// <param name="writer">The replica that wrote it and that the change removes.</param>
    /// <returns>The record.</returns>
    private static VersionedValue<string> RemovingItsWriter(ulong version, ReplicaId writer)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, Configuration.Without(writer), "committed");
    }


    /// <summary>The leader derivation every host under test shares.</summary>
    /// <returns>The derivation.</returns>
    private static QuePaxaLeaderSchedule Schedule()
    {
        ImmutableArray<ReplicaId> order = [First, Second, Third];

        return new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, TimeSpan.FromMilliseconds(10)));
    }


    /// <summary>Names a lane for a log line, so that the leaderless answer reads as a fact rather than a blank.</summary>
    /// <param name="lane">The lane, or <see langword="null"/> for a leaderless instance.</param>
    /// <returns>The name.</returns>
    private static string Describe(ProposerLane? lane) => lane?.ToString() ?? "leaderless";


    /// <summary>Builds a replica identity from a single distinguishing byte.</summary>
    /// <param name="id">The distinguishing byte.</param>
    /// <returns>The identity.</returns>
    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
