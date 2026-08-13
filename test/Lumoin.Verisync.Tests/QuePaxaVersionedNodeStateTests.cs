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
/// The two derived fields carry the whole design. A restore that recomputed the leader and the version from the
/// committed record would compare each with itself, so the stored copies are what let a torn snapshot announce
/// itself, and the tests here fire each rule by moving exactly one stored copy away from what the record
/// implies.
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

    /// <summary>A writer outside the configured order, which the derivation answers leaderless for.</summary>
    private static ReplicaId Stranger { get; } = Replica(9);

    /// <summary>
    /// The membership every record in this suite carries, minted from the agreed order the hosts run under.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis([First, Second, Third]);

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
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> original = new(schedule, Record(4UL, Second));

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

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, snapshot);

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
    /// A host occupies the unwritten register for the whole interval between learning a version and answering
    /// the first request for the next one, so that interval has to survive a restart. The recorder's own restore
    /// refuses a step-zero state and this one rebuilds it, which is the one place the two restores differ.
    /// </summary>
    [TestMethod]
    public void AHostThatHasAnsweredNothingRestoresTheUnwrittenRegister()
    {
        QuePaxaLeaderSchedule schedule = Schedule();

        //Both shapes reach step zero: the bootstrap host that has learned nothing, and a host that learned a
        //record and has not been asked for the version after it.
        QuePaxaVersionedNode<string> bootstrap = new(schedule);
        QuePaxaVersionedNode<string> learned = new(schedule, Record(4UL, Second));

        Assert.IsTrue(learned.Learn(Record(5UL, Third)));

        //The third shape is the one the rebuild can get wrong in silence. A record whose writer left the order
        //makes the instance leaderless, and a rebuild falling back on the configured order's head restores a
        //leader where the derivation supplies none, which is a host that honours reserved claims its neighbours
        //decline. A rebuild reached only through hosts with a derived leader cannot tell the two apart.
        QuePaxaVersionedNode<string> leaderless = new(schedule, Record(4UL, Stranger));

        Assert.IsNull(leaderless.Recorder.ConfiguredLeader);

        foreach(QuePaxaVersionedNode<string> host in (QuePaxaVersionedNode<string>[])[bootstrap, learned, leaderless])
        {
            QuePaxaVersionedNodeState<string> snapshot = host.ToState();

            Assert.AreEqual(RecorderStep.Zero, snapshot.Recorder.Step);

            //The recorder's own restore refuses exactly this state, which is why the versioned restore has to
            //carry a rule of its own rather than delegating the whole range.
            Assert.ThrowsExactly<ArgumentException>(() => QuePaxaRecorder<VersionedValue<string>>.FromState(snapshot.ConfiguredLeader, snapshot.Recorder));

            QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, snapshot);

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

            QuePaxaVersionedNodeState<string> state = new(committed, new RegisterVersion(5UL), leader, RestorableRecorder());

            ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, state));

            Assert.AreEqual("state", refused.ParamName);
        }

        //The mirror case, and the one a fallback to configuration produces: the record's writer left the order,
        //so the derivation is leaderless and a stored leader is the disagreement.
        QuePaxaVersionedNodeState<string> leaderlessInstance = new(Record(4UL, Stranger), new RegisterVersion(5UL), ProposerLane.For(First), RestorableRecorder());

        Assert.IsNull(schedule.LeaderFor(Stranger));
        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, leaderlessInstance));
    }


    /// <summary>
    /// The version cross-check. A recorder from one instance beside a committed record from another is what a
    /// snapshot written in two parts and torn between them leaves behind, and the stored version is what makes
    /// that visible.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsARecorderVersionOtherThanTheOneAfterTheRecord()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        VersionedValue<string> committed = Record(4UL, Second);

        //The record's own version, the version two ahead, and the first version, which is what a register
        //restored from a different key or a reset counter would carry.
        foreach(ulong version in (ulong[])[4UL, 6UL, 1UL])
        {
            TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stored recorder version {version} against derived 5"));

            QuePaxaVersionedNodeState<string> state = new(committed, new RegisterVersion(version), ProposerLane.For(Second), RestorableRecorder());

            ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, state));

            Assert.AreEqual("state", refused.ParamName);
        }

        //A host that has learned nothing serves the first version and nothing else, so the rule fires there too
        //rather than only where a record is present to compare against.
        QuePaxaVersionedNodeState<string> bootstrap = new(null, new RegisterVersion(2UL), ProposerLane.For(First), RestorableRecorder());

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, bootstrap));
    }


    /// <summary>
    /// The step-zero short circuit is the unwritten register exactly. A recorder records nothing below round one
    /// phase zero, so a proposal standing at step zero was never recorded there, and rebuilding the register
    /// unwritten would discard it instead of refusing the snapshot that carried it.
    /// </summary>
    [TestMethod]
    public void FromStateRejectsAStepZeroRecorderCarryingAProposalInAnySlot()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
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

            QuePaxaVersionedNodeState<string> state = new(committed, new RegisterVersion(5UL), ProposerLane.For(Second), recorder);

            ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, state));

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
        QuePaxaLeaderSchedule schedule = Schedule();
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
                QuePaxaVersionedNodeState<string> state = new(committed, new RegisterVersion(5UL), ProposerLane.For(Second), recorder);

                Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, state));
            }
        }

        //A foreign reserved claim at the round's first step, which is the recorder rule the derived leader makes
        //checkable: the stored leader agrees with the record, so only the recorder's own restore can refuse it.
        PrioritizedProposal<VersionedValue<string>> foreignClaim = Reserved(ProposerLane.For(Third), 5UL, Third, "b");
        QuePaxaVersionedNodeState<string> claimAtFour = new(
            committed,
            new RegisterVersion(5UL),
            ProposerLane.For(Second),
            new QuePaxaRecorderState<VersionedValue<string>>(Four, foreignClaim, foreignClaim, null));

        Assert.ThrowsExactly<ArgumentException>(() => QuePaxaVersionedNode<string>.FromState(schedule, claimAtFour));
    }


    /// <summary>
    /// A stale record that is internally consistent restores, and that is correct rather than a gap. The leader
    /// is a deterministic function of the record, so an old record yields exactly the leader its own instance
    /// ran under, and the version gate keeps the restored host from touching the live instance at all.
    /// </summary>
    [TestMethod]
    public void AStaleButConsistentSnapshotRestoresAndServesOnlyItsOwnInstance()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Second));
        QuePaxaVersionedNodeState<string> stale = host.ToState();

        //The deployment moved on by two versions while this snapshot sat on disk.
        Assert.IsTrue(host.Learn(Record(5UL, Third)));
        Assert.IsTrue(host.Learn(Record(6UL, First)));
        Assert.AreEqual(new RegisterVersion(7UL), host.LiveVersion);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, stale);

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
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> crashed = new(schedule, Record(4UL, Second));
        VersionedRecordRequest<VersionedValue<string>> claim = Request(5UL, ProposalPriority.Reserved, Second, "fast");

        VersionedRecordReply<VersionedValue<string>> beforeCrash = crashed.Handle(claim);

        //The leader's claim was honoured rather than downgraded, so the snapshot is the fast path's own.
        Assert.AreEqual(ProposalPriority.Reserved, beforeCrash.Reply.First.Key.Priority);

        QuePaxaVersionedNodeState<string> snapshot = crashed.ToState();
        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, snapshot);

        QuePaxaRecorder<VersionedValue<string>> afterRestore = restored.Recorder;

        //The proposer's retransmission after the restart re-delivers the claim the snapshot was taken after, and
        //the same recorder instance back is the no-durability-write contract surviving the restart.
        VersionedRecordReply<VersionedValue<string>> afterRestart = restored.Handle(claim);

        Assert.AreSame(afterRestore, restored.Recorder);
        Assert.AreEqual(beforeCrash, afterRestart);

        //A host that never restarted serves the rest of the quorum, so the restored host's answer is
        //load-bearing in the decision rather than carried by hosts that all stayed up.
        QuePaxaVersionedNode<string> live = new(schedule, Record(4UL, Second));
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
    [TestMethod]
    public void ALeaderlessInstanceRoundTripsAndKeepsDecliningReservedClaims()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Stranger));

        Assert.IsNull(host.Recorder.ConfiguredLeader);

        _ = host.Handle(Request(5UL, new ProposalPriority(10), Second, "a"));

        QuePaxaVersionedNodeState<string> snapshot = host.ToState();

        Assert.IsNull(snapshot.ConfiguredLeader);

        QuePaxaVersionedNode<string> restored = QuePaxaVersionedNode<string>.FromState(schedule, snapshot);

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
        QuePaxaLeaderSchedule schedule = Schedule();
        VersionedValue<string> spent = new(RegisterVersion.MaxValue, Second, Configuration, "last");
        QuePaxaVersionedNode<string> host = new(schedule, spent);

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = host.ToState());

        QuePaxaVersionedNodeState<string> state = new(spent, RegisterVersion.First, ProposerLane.For(Second), RestorableRecorder());

        Assert.ThrowsExactly<InvalidOperationException>(() => QuePaxaVersionedNode<string>.FromState(schedule, state));
    }


    /// <summary>
    /// The two rules a single field carries on its own, which the restore therefore owes nothing. No host serves
    /// the unwritten version, and a state with no recorder names an instance it cannot serve.
    /// </summary>
    [TestMethod]
    public void AVersionedNodeStateCannotExpressAnUnwrittenVersionOrAnAbsentRecorder()
    {
        ArgumentOutOfRangeException unwritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = new QuePaxaVersionedNodeState<string>(null, RegisterVersion.Unwritten, ProposerLane.For(First), RestorableRecorder()));

        Assert.AreEqual("RecorderVersion", unwritten.ParamName);

        ArgumentNullException absent = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = new QuePaxaVersionedNodeState<string>(null, RegisterVersion.First, ProposerLane.For(First), null!));

        Assert.AreEqual("Recorder", absent.ParamName);

        //A `with` expression reaches the accessor body, while construction reaches the property initializer, so
        //the two paths are separate code and a rule stated on only one of them leaves the other open.
        QuePaxaVersionedNodeState<string> valid = new(null, RegisterVersion.First, ProposerLane.For(First), RestorableRecorder());

        ArgumentOutOfRangeException rewritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = valid with { RecorderVersion = RegisterVersion.Unwritten });
        ArgumentNullException removed = Assert.ThrowsExactly<ArgumentNullException>(() => _ = valid with { Recorder = null! });

        Assert.AreEqual("RecorderVersion", rewritten.ParamName);
        Assert.AreEqual("Recorder", removed.ParamName);
    }


    /// <summary>A null schedule or a null state is refused, because a restore has nothing to rebuild from.</summary>
    [TestMethod]
    public void FromStateRejectsANullScheduleAndANullState()
    {
        QuePaxaVersionedNodeState<string> state = new(null, RegisterVersion.First, ProposerLane.For(First), RestorableRecorder());

        Assert.ThrowsExactly<ArgumentNullException>(() => QuePaxaVersionedNode<string>.FromState(null!, state));
        Assert.ThrowsExactly<ArgumentNullException>(() => QuePaxaVersionedNode<string>.FromState(Schedule(), null!));
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
