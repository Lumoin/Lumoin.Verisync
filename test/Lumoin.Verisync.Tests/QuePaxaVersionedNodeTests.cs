using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The recorder host's suite. The host owns the one consensus instance whose leader it can derive, so the
/// subjects are which version it serves, that it refuses every other one at both bounds, that learning a
/// version moves the instance rather than reconfiguring the one already running, and that the membership the
/// instance runs under is a memo of the record rather than a setting.
/// </summary>
[TestClass]
internal sealed class QuePaxaVersionedNodeTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);
    private static ReplicaId Fourth { get; } = Replica(4);
    private static ReplicaId Stranger { get; } = Replica(9);

    /// <summary>
    /// The genesis membership every host in this suite runs under, and the membership every record it holds
    /// carries forward unchanged.
    /// </summary>
    private static QuePaxaConfiguration Configuration { get; } = QuePaxaConfiguration.CreateGenesis([First, Second, Third]);

    /// <summary>
    /// A membership over the same replicas in the same order on a different chain, which is what an
    /// independently bootstrapped cluster mints.
    /// </summary>
    private static QuePaxaConfiguration ForeignChain { get; } = QuePaxaConfiguration.CreateGenesis([First, Second, Stranger]).Without(Stranger).With(Third);

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    [TestMethod]
    public void AHostThatHasLearnedNothingServesTheFirstVersionUnderTheBootstrapLeader()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First);

        Assert.AreEqual(RegisterVersion.First, host.LiveVersion);
        Assert.AreEqual(ProposerLane.For(First), host.Recorder.ConfiguredLeader);

        //A host that has learned no record runs the genesis membership, which is the induction's base case.
        Assert.AreEqual(Configuration, host.ActiveConfiguration);
        Assert.AreEqual(Configuration, host.Genesis);
        Assert.AreEqual(First, host.Self);
    }


    /// <summary>
    /// The version is asserted literally, because comparing the two derivations alone would pass at any
    /// version.
    /// </summary>
    [TestMethod]
    public void TheLiveInstanceFollowsTheCommittedRecordAndCarriesItsWritersLane()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        Assert.AreEqual(new RegisterVersion(5UL), host.LiveVersion);
        Assert.AreEqual(host.LeaderSchedule.LeaderFor(Second), host.Recorder.ConfiguredLeader);
        Assert.AreEqual(ProposerLane.For(Second), host.Recorder.ConfiguredLeader);
    }


    /// <summary>
    /// The instance triple derives from one capture of the committed record, so its fields agree with the
    /// one-property reads and with each other.
    /// </summary>
    /// <remarks>
    /// The three facts are also readable one property at a time, and a reader beside a running loop must not
    /// pair them that way: a learn replaces the record and then the derived memos, so separate reads can pair
    /// a new record with an old membership. The triple is the pairing-safe read, which is the register's own
    /// rule for the same tear.
    /// </remarks>
    [TestMethod]
    public void TheInstanceTripleDerivesFromOneCaptureOfTheCommittedRecord()
    {
        QuePaxaVersionedNode<string> fresh = new(Configuration, First);

        Assert.AreEqual(new RegisterInstance(RegisterVersion.First, Configuration, null), fresh.Instance);

        QuePaxaVersionedNode<string> caughtUp = new(Configuration, First, Record(4UL, Second));

        Assert.AreEqual(new RegisterInstance(new RegisterVersion(5UL), Configuration, Second), caughtUp.Instance);
    }


    [TestMethod]
    public void TwoRequestsAtTheLiveVersionReachOneInstance()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        VersionedRecordReply<VersionedValue<string>> first = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "a"));
        VersionedRecordReply<VersionedValue<string>> second = host.Handle(Request(5UL, new ProposalPriority(7), Second, "b"));

        Assert.AreEqual(new RegisterVersion(5UL), first.Version);
        Assert.AreEqual(new RegisterVersion(5UL), second.Version);

        //A host creating an instance per request would not fold the second request into the same register: it
        //would report the second proposal as the step's first, and the register is the fact rather than a
        //proxy for it.
        Assert.AreEqual(Four, host.Recorder.Step);
        Assert.AreEqual("a", host.Recorder.Register.First!.Value.Value);
        Assert.AreEqual(new ProposalPriority(7), host.Recorder.Register.CurrentAggregate!.Key.Priority);
    }


    /// <summary>
    /// A host that checked only the upper bound would pass every above-version vector and still be wrong.
    /// </summary>
    [TestMethod]
    public void ARequestForAnyOtherVersionIsRefusedAtBothBounds()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));
        VersionedValue<string>? committed = host.Committed;
        QuePaxaRecorder<VersionedValue<string>> before = host.Recorder;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = host.Handle(Request(6UL, ProposalPriority.Lowest, Second, "above")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = host.Handle(Request(4UL, ProposalPriority.Lowest, Second, "below")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = host.Handle(Request(1UL, ProposalPriority.Lowest, Second, "far below")));

        //A refusal allocates nothing and records nothing, so a peer naming versions at random costs nothing.
        Assert.AreSame(before, host.Recorder);
        Assert.AreSame(committed, host.Committed);
        Assert.AreEqual(RecorderStep.Zero, host.Recorder.Step);
    }


    [TestMethod]
    public void LearningAdvancesTheLiveInstanceAndIgnoresARecordThatDoesNot()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        Assert.IsTrue(host.Learn(Record(5UL, Third)));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);
        Assert.AreEqual(ProposerLane.For(Third), host.Recorder.ConfiguredLeader);

        Assert.IsFalse(host.Learn(Record(5UL, Third)));
        Assert.IsFalse(host.Learn(Record(4UL, Second)));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);
    }


    /// <summary>
    /// The identity assertion alone passes a host that rewrote the leader in place; the leader assertion alone
    /// passes one that kept the old recorder.
    /// </summary>
    [TestMethod]
    public void LearningBuildsANewInstanceAndLeavesTheRunningOnesLeaderAlone()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        _ = host.Handle(Request(5UL, ProposalPriority.Reserved, Second, "a"));

        QuePaxaRecorder<VersionedValue<string>> beforeLearning = host.Recorder;
        ProposerLane? leaderBeforeLearning = beforeLearning.ConfiguredLeader;

        Assert.IsTrue(host.Learn(Record(5UL, Third)));

        //A recorder is immutable, so the captured one still enforces the leader its own instance ran under.
        Assert.AreNotSame(beforeLearning, host.Recorder);
        Assert.AreEqual(leaderBeforeLearning, beforeLearning.ConfiguredLeader);
        Assert.AreEqual(ProposerLane.For(Second), beforeLearning.ConfiguredLeader);
        Assert.AreEqual(ProposerLane.For(Third), host.Recorder.ConfiguredLeader);

        //The new instance starts from an unwritten register, so nothing the previous version recorded leaks in.
        Assert.AreEqual(RecorderStep.Zero, host.Recorder.Step);
    }


    /// <summary>
    /// The derivation reads only agreed inputs, so every host makes this instance leaderless and none differs.
    /// </summary>
    [TestMethod]
    public void AWriterOutsideTheOrderMakesTheNextInstanceLeaderlessRatherThanUnservable()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Stranger));

        Assert.AreEqual(new RegisterVersion(5UL), host.LiveVersion);
        Assert.IsNull(host.Recorder.ConfiguredLeader);

        VersionedRecordReply<VersionedValue<string>> reply = host.Handle(Request(5UL, ProposalPriority.Reserved, Second, "a"));

        Assert.AreEqual(ProposalPriority.Lowest, reply.Reply.First.Key.Priority);
    }


    [TestMethod]
    public void TheHostRefusesANullGenesisANullRequestAndANullRecord()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedNode<string>(null!, First));

        QuePaxaVersionedNode<string> host = new(Configuration, First);

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = host.Handle(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = host.Learn(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = host.Declines(null!));
    }


    [TestMethod]
    public async Task ALearnedRecordIsMadeDurableEvenWhenTheRecorderInstanceIsUnchanged()
    {
        //Constructed WITHOUT serving, deliberately: a served request would advance the recorder off the
        //shared leaderless singleton and silently invert the premise the identity assertion below pins.
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Stranger));
        QuePaxaRecorder<VersionedValue<string>> before = host.Recorder;

        VersionedValue<string> learned = Record(5UL, Stranger);
        Assert.IsTrue(host.Learn(learned));
        Assert.AreSame(before, host.Recorder);

        List<QuePaxaVersionedNodeState<string>> states = [];
        await host.MakeDurableAsync((state, cancellationToken) =>
        {
            states.Add(state);

            return ValueTask.CompletedTask;
        }, TestContext.CancellationToken).ConfigureAwait(false);

        //The committed record moved while the recorder reference did not, so a gate reading the recorder
        //alone would skip this write and a restart would re-open a decided instance.
        Assert.HasCount(1, states);
        Assert.AreSame(learned, states[0].Committed);

        await host.MakeDurableAsync((state, cancellationToken) =>
        {
            states.Add(state);

            return ValueTask.CompletedTask;
        }, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.HasCount(1, states);
    }


    [TestMethod]
    public async Task ACheckpointOnAHostThatServesNoVersionIsANoOpRatherThanAThrow()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, new VersionedValue<string>(RegisterVersion.MaxValue, Second, Configuration, "spent"));

        //The gate short-circuits before the snapshot, whose LiveVersion read throws on a spent host, so a
        //checkpoint hoisting the snapshot above the gate fails here.
        await host.MakeDurableAsync((state, cancellationToken) =>
        {
            Assert.Fail("A host durable by construction owes no write.");

            return ValueTask.CompletedTask;
        }, TestContext.CancellationToken).ConfigureAwait(false);
    }


    [TestMethod]
    public async Task AConstructedHostTreatsTheRecordItWasGivenAsDurable()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        int writes = 0;
        await host.MakeDurableAsync((state, cancellationToken) =>
        {
            writes++;

            return ValueTask.CompletedTask;
        }, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(0, writes);
        _ = await Assert.ThrowsExactlyAsync<ArgumentNullException>(
            async () => await host.MakeDurableAsync(null!, TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }


    [TestMethod]
    public void ServesAgreesWithHandleAtBothBoundsAndNeverThrows()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        Assert.IsFalse(host.Serves(new RegisterVersion(4UL)));
        Assert.IsTrue(host.Serves(new RegisterVersion(5UL)));
        Assert.IsFalse(host.Serves(new RegisterVersion(6UL)));

        //A spent host serves nothing, and the classifier reports that without evaluating the live-version
        //throw, which is what lets a runner's decline filter read it inside an exception filter.
        QuePaxaVersionedNode<string> spent = new(Configuration, First, new VersionedValue<string>(RegisterVersion.MaxValue, Second, Configuration, "spent"));

        Assert.IsFalse(spent.Serves(new RegisterVersion(1UL)));
        Assert.IsFalse(spent.Serves(RegisterVersion.MaxValue));
    }


    [TestMethod]
    public void ALearnInstallsAnUnwrittenRegisterTheFirstReplyMustReplace()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        _ = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "a"));

        Assert.IsTrue(host.Learn(Record(5UL, Third)));
        Assert.AreEqual(RecorderStep.Zero, host.Recorder.Step);

        QuePaxaRecorder<VersionedValue<string>> unwritten = host.Recorder;
        _ = host.Handle(Request(6UL, ProposalPriority.Lowest, Third, "b"));

        //Every request's step sits above zero, so the first request after any learn replaces the register,
        //which is the premise under persist-on-next-reply sufficiency: the durability gate always fires
        //before the first reply that depends on a learned record.
        Assert.AreNotSame(unwritten, host.Recorder);
    }


    [TestMethod]
    public async Task AnUnownedHostServesDirectCallsAsBefore()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));

        VersionedRecordReply<VersionedValue<string>> reply = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "a"));

        Assert.AreEqual(new RegisterVersion(5UL), reply.Version);
        Assert.IsTrue(host.Learn(Record(5UL, Third)));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);

        List<QuePaxaVersionedNodeState<string>> states = [];
        await host.MakeDurableAsync((state, cancellationToken) =>
        {
            states.Add(state);

            return ValueTask.CompletedTask;
        }, TestContext.CancellationToken).ConfigureAwait(false);

        //A claim taken anywhere other than a running loop would refuse all three of these calls, so this is
        //the whole-arm regression against a latch that fires on a host no runner drives.
        Assert.HasCount(1, states);
    }


    /// <summary>
    /// The chain check, which is the whole defence against two independently bootstrapped clusters wired
    /// together. The request is well formed in every other respect — the live version, this host's own
    /// membership, a record written at the version it is proposed to — so the chain arm is the only rule that
    /// can refuse it, and the refusal is named in the message rather than only in the type.
    /// </summary>
    [TestMethod]
    public void ARequestCarryingAnotherChainsMembershipIsDeclinedAndRecordsNothing()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));
        QuePaxaRecorder<VersionedValue<string>> before = host.Recorder;

        //Same replicas in the same order, minted at a different genesis: only the chain identity differs, so
        //nothing about the member list can be what refuses this.
        Assert.IsTrue(Configuration.Members.SequenceEqual(ForeignChain.Members));
        Assert.AreNotEqual(Configuration.Cluster, ForeignChain.Cluster);

        VersionedRecordRequest<VersionedValue<string>> foreign = Carrying(5UL, 5UL, ForeignChain, ProposalPriority.Reserved, Second, "a");

        Assert.IsTrue(host.Declines(foreign));

        ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => _ = host.Handle(foreign));

        Assert.AreEqual("request", refused.ParamName);
        Assert.Contains("another chain", refused.Message);

        //The refusal precedes every mutation, which is what lets the runner keep serving after faulting it.
        Assert.AreSame(before, host.Recorder);
        Assert.AreEqual(RecorderStep.Zero, host.Recorder.Step);
    }


    /// <summary>
    /// The chain check at the learn, which is the path a record arrives by without being asked to serve
    /// anything and the one a publisher drives. The record is well formed in every other respect — strictly
    /// newer than the held one, over the same replicas in the same order — so the chain arm is the only rule
    /// that can refuse it, and nothing this host holds moves.
    /// </summary>
    /// <remarks>
    /// The refusal stands beside the ordinary answer it must remain distinguishable from: a record of this
    /// host's own chain that does not advance is ignored and reported false, while a record of another chain
    /// is refused whether or not it is newer, because version order is a fact inside one chain and says
    /// nothing across two.
    /// </remarks>
    [TestMethod]
    public void ARecordOfAnotherChainIsRefusedAtTheLearnAndMovesNothing()
    {
        VersionedValue<string> held = Record(4UL, Second);
        QuePaxaVersionedNode<string> host = new(Configuration, First, held);
        QuePaxaRecorder<VersionedValue<string>> before = host.Recorder;
        QuePaxaLeaderSchedule schedule = host.LeaderSchedule;

        //Same replicas in the same order, minted at a different genesis, so nothing about the member list can
        //be what refuses this.
        Assert.IsTrue(Configuration.Members.SequenceEqual(ForeignChain.Members));
        Assert.AreNotEqual(Configuration.Cluster, ForeignChain.Cluster);

        VersionedValue<string> foreign = Installing(5UL, Second, ForeignChain);

        Assert.IsTrue(host.DeclinesLearn(foreign));

        ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => _ = host.Learn(foreign));

        Assert.AreEqual("committed", refused.ParamName);
        Assert.Contains("another chain", refused.Message);

        //The committed record is the one the host started from, read by reference because an adoption
        //replaces it, and the live instance still follows it.
        Assert.AreSame(held, host.Committed);
        Assert.AreEqual(new RegisterVersion(5UL), host.LiveVersion);

        //The membership is read by chain and by member rather than by position, and it is this host's own.
        Assert.AreEqual(Configuration, host.ActiveConfiguration);
        Assert.AreEqual(Configuration.Cluster, host.ActiveConfiguration.Cluster);
        Assert.IsTrue(host.ActiveConfiguration.Contains(First));
        Assert.IsTrue(host.ActiveConfiguration.Contains(Third));

        //The schedule is the same memo and still derives the leader from the writer of the record held.
        Assert.AreSame(schedule, host.LeaderSchedule);
        Assert.AreEqual(ProposerLane.For(Second), host.LeaderSchedule.LeaderFor(Second));
        Assert.AreSame(before, host.Recorder);
        Assert.AreEqual(RecorderStep.Zero, host.Recorder.Step);

        //A foreign record standing below the held one is refused on the same arm, because reporting it as a
        //record that did not advance would hide a wiring defect behind an ordinary answer.
        VersionedValue<string> foreignAndOlder = Installing(4UL, Second, ForeignChain);

        Assert.IsTrue(host.DeclinesLearn(foreignAndOlder));
        Assert.ThrowsExactly<ArgumentException>(() => _ = host.Learn(foreignAndOlder));

        //The ordinary answer: a record of this host's own chain that does not advance is no decline at all.
        Assert.IsFalse(host.DeclinesLearn(Record(4UL, Second)));
        Assert.IsFalse(host.Learn(Record(4UL, Second)));
        Assert.AreSame(held, host.Committed);
    }


    /// <summary>
    /// Learning is membership-blind and the chain arm does not narrow that. A record that removes this host is
    /// adopted, and so is the next record on the same chain while the host stands outside the membership the
    /// first one installed, because a removed host learns it is out from the protocol rather than from silence
    /// and a joiner catches up from whoever holds the record.
    /// </summary>
    /// <remarks>
    /// Both records reach the site the chain refusal reads — each is strictly newer and names this host's own
    /// chain — so an arm widened to refuse by membership fails here rather than passing unpinned.
    /// </remarks>
    [TestMethod]
    public void ARecordRemovingTheHostIsLearnedAndSoIsTheNextOneWhileItStandsOutside()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));
        QuePaxaConfiguration without = Configuration.Without(First);
        VersionedValue<string> removing = Installing(5UL, Second, without);

        //This host's own chain and a membership this host is not in, which is the pair the arm separates.
        Assert.AreEqual(Configuration.Cluster, removing.NextConfiguration.Cluster);
        Assert.IsFalse(without.Contains(First));

        Assert.IsFalse(host.DeclinesLearn(removing));
        Assert.IsTrue(host.Learn(removing));
        Assert.AreEqual(without, host.ActiveConfiguration);
        Assert.IsFalse(host.ActiveConfiguration.Contains(First));

        //Being outside the membership refuses requests and nothing else, so the next record on the chain is
        //adopted by a host that may no longer serve a single one.
        Assert.IsTrue(host.Declines(Carrying(6UL, 6UL, without, ProposalPriority.Lowest, Second, "next")));

        VersionedValue<string> later = Installing(6UL, Second, without);

        Assert.IsFalse(host.DeclinesLearn(later));
        Assert.IsTrue(host.Learn(later));
        Assert.AreSame(later, host.Committed);
        Assert.AreEqual(new RegisterVersion(7UL), host.LiveVersion);
    }


    /// <summary>
    /// The chain check at construction, which is the path a committed record enters a host by before that host
    /// has adopted anything. The record is well formed in every other respect — a version with a successor, a
    /// writer its member list carries, a member list this host appears in — so the chain arm is the only rule
    /// that can refuse it, and the same record on this host's own chain constructs.
    /// </summary>
    /// <remarks>
    /// The comparison is against the genesis and never against the membership the record names. A host being
    /// constructed derives its active membership from that same record, so a rule reading the membership would
    /// compare the record with itself and admit every foreign record there is. The restore makes the identical
    /// comparison over a snapshot of the same record, and both are asserted here, because the two entry points
    /// that take a record from outside the protocol owe one rule.
    /// </remarks>
    [TestMethod]
    public void AHostConstructedWithAnotherChainsRecordIsRefusedExactlyAsARestoreIs()
    {
        //Same replicas in the same order, minted at a different genesis, so nothing about the member list can
        //be what refuses this.
        Assert.IsTrue(Configuration.Members.SequenceEqual(ForeignChain.Members));
        Assert.AreNotEqual(Configuration.Cluster, ForeignChain.Cluster);

        VersionedValue<string> foreign = Installing(4UL, Second, ForeignChain);

        ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => _ = new QuePaxaVersionedNode<string>(Configuration, First, foreign));

        Assert.AreEqual("committed", refused.ParamName);
        Assert.Contains("must name the chain this host was given", refused.Message);

        //The chain identity is the only difference the refusal can have read: the same record under this host's
        //own chain constructs, and the host it builds serves the version after it under its writer's lane.
        QuePaxaVersionedNode<string> own = new(Configuration, First, Installing(4UL, Second, Configuration));

        Assert.AreEqual(new RegisterVersion(5UL), own.LiveVersion);
        Assert.AreEqual(ProposerLane.For(Second), own.Recorder.ConfiguredLeader);

        //A host handed no record at all compares its genesis with itself, which is the derivation's base case
        //rather than a second rule, so bootstrapping is untouched.
        QuePaxaVersionedNode<string> bootstrap = new(Configuration, First);

        Assert.AreEqual(Configuration, bootstrap.ActiveConfiguration);
        Assert.AreEqual(RegisterVersion.First, bootstrap.LiveVersion);

        //The restore refuses the same divergence over a snapshot of that same record, taken from the host whose
        //own genesis is the foreign chain, so what separates the two calls is the genesis each was handed.
        QuePaxaVersionedNodeState<string> state = new QuePaxaVersionedNode<string>(ForeignChain, First, foreign).ToState();

        ArgumentException restored = Assert.ThrowsExactly<ArgumentException>(() => _ = QuePaxaVersionedNode<string>.FromState(Configuration, First, state));

        Assert.AreEqual("state", restored.ParamName);
        Assert.Contains("must name the chain this host was given", restored.Message);

        //Each entry point accepts under the chain it was handed, so neither refusal is the record's own shape.
        Assert.AreEqual(new RegisterVersion(5UL), QuePaxaVersionedNode<string>.FromState(ForeignChain, First, state).LiveVersion);
    }


    /// <summary>
    /// Construction is membership-blind and the chain arm does not narrow that. A replica a change removed and
    /// a joiner the genesis never listed are both constructed from a record whose membership differs from the
    /// genesis membership, so the rule reads the chain identity and nothing else the configuration carries.
    /// </summary>
    /// <remarks>
    /// Every record here reaches the comparison the chain arm makes — each names this host's own chain and none
    /// is null — so a rule widened to the member list, or to whether this host appears in it, fails here rather
    /// than passing unpinned. What being outside the membership costs a host is stated beside it: requests are
    /// declined and construction is not, which is how a removed replica keeps running until its deployment
    /// retires it and how a joiner starts before anything lists it.
    /// </remarks>
    [TestMethod]
    public void AHostIsConstructedWithARecordWhoseMembershipDoesNotListIt()
    {
        //A replica the record removed: the membership moved off the genesis and no longer lists this host.
        QuePaxaConfiguration without = Configuration.Without(First);
        QuePaxaVersionedNode<string> removed = new(Configuration, First, Installing(4UL, Second, without));

        Assert.AreEqual(Configuration.Cluster, removed.ActiveConfiguration.Cluster);
        Assert.AreEqual(without, removed.ActiveConfiguration);
        Assert.IsFalse(removed.ActiveConfiguration.Contains(First));
        Assert.IsFalse(Configuration.Members.SequenceEqual(without.Members));
        Assert.IsTrue(removed.Declines(Carrying(5UL, 5UL, without, ProposalPriority.Lowest, Second, "a")));

        //A joiner: outside the genesis this deployment handed it, inside the membership its record installs,
        //and serving from the first version that membership runs.
        QuePaxaConfiguration grown = Configuration.With(Fourth);
        QuePaxaVersionedNode<string> joiner = new(Configuration, Fourth, Installing(4UL, Second, grown));

        Assert.IsFalse(Configuration.Contains(Fourth));
        Assert.AreEqual(grown, joiner.ActiveConfiguration);
        Assert.AreEqual(new RegisterVersion(5UL), joiner.Handle(Carrying(5UL, 5UL, grown, ProposalPriority.Lowest, Second, "a")).Version);

        //A host no membership on this chain lists at any version, which the request filter refuses and
        //construction does not.
        QuePaxaVersionedNode<string> stranger = new(Configuration, Stranger, Record(4UL, Second));

        Assert.IsFalse(stranger.ActiveConfiguration.Contains(Stranger));
        Assert.IsTrue(stranger.Declines(Request(5UL, ProposalPriority.Lowest, Second, "a")));
    }


    /// <summary>
    /// The membership filter. A host a configuration change removed is outside the set a quorum for the live
    /// instance is counted over, so it refuses rather than answering as a recorder no arithmetic accounts for.
    /// Nothing else about the request differs from one this host serves.
    /// </summary>
    [TestMethod]
    public void AHostOutsideTheActiveMembershipDeclinesAndTheSameRequestIsServedByAMember()
    {
        VersionedValue<string> record = Record(4UL, Second);
        QuePaxaVersionedNode<string> outsider = new(Configuration, Stranger, record);
        VersionedRecordRequest<VersionedValue<string>> request = Request(5UL, ProposalPriority.Lowest, Second, "a");

        Assert.IsFalse(outsider.ActiveConfiguration.Contains(Stranger));
        Assert.IsTrue(outsider.Declines(request));

        ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => _ = outsider.Handle(request));

        Assert.AreEqual("request", refused.ParamName);
        Assert.Contains("outside the membership", refused.Message);

        //The identical request at a member host is served, so the identity is what refused it and not the
        //request's own shape.
        QuePaxaVersionedNode<string> member = new(Configuration, First, record);

        Assert.IsFalse(member.Declines(request));
        Assert.AreEqual(new RegisterVersion(5UL), member.Handle(request).Version);

        //A change that removes a host takes effect at the learn that installs it, so a host serving now
        //refuses the next instance once it has learned itself out.
        Assert.IsTrue(member.Learn(Installing(5UL, Second, Configuration.Without(First))));

        VersionedRecordRequest<VersionedValue<string>> next = Request(6UL, ProposalPriority.Lowest, Second, "b");

        Assert.IsTrue(member.Declines(next));
        Assert.Contains("outside the membership", Assert.ThrowsExactly<ArgumentException>(() => _ = member.Handle(next)).Message);
    }


    /// <summary>
    /// The inner-version check. A defective proposer whose carried record names a version other than the one
    /// it addressed would wedge the instance if it decided: every host that learned the decision would refuse
    /// the mismatch before adopting the record, and no later version could be written. The comparison is one
    /// the host can make at the request, so it makes it and declines instead.
    /// </summary>
    [TestMethod]
    public void ARequestWhoseRecordDisagreesWithItsEnvelopeIsDeclined()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));
        QuePaxaRecorder<VersionedValue<string>> before = host.Recorder;

        //Both directions of disagreement, so a comparison written as a one-sided bound is caught here.
        foreach(ulong written in (ulong[])[4UL, 6UL])
        {
            TestContext.WriteLine($"a request addressed to version 5 carrying a record written at version {written}");

            VersionedRecordRequest<VersionedValue<string>> mismatched = Carrying(5UL, written, Configuration, ProposalPriority.Lowest, Second, "a");

            Assert.IsTrue(host.Declines(mismatched));

            ArgumentException refused = Assert.ThrowsExactly<ArgumentException>(() => _ = host.Handle(mismatched));

            Assert.AreEqual("request", refused.ParamName);
            Assert.Contains("must carry a record written at that same version", refused.Message);
        }

        //A request carrying no record at all is refused on the same arm rather than faulting the classifier,
        //which promises to answer for every request it is handed.
        VersionedRecordRequest<VersionedValue<string>> empty = new(
            new RegisterVersion(5UL),
            new RecordRequest<VersionedValue<string>>(Four, new PrioritizedProposal<VersionedValue<string>>(new ProposalKey(ProposalPriority.Lowest, ProposerLane.For(Second)), null!)));

        Assert.IsTrue(host.Declines(empty));
        Assert.ThrowsExactly<ArgumentException>(() => _ = host.Handle(empty));

        Assert.AreSame(before, host.Recorder);
        Assert.AreEqual(RecorderStep.Zero, host.Recorder.Step);
    }


    /// <summary>
    /// The classifier and the act are one predicate consulted twice. A rule that held at one and not the other
    /// would be a request the runner's filter calls a defect while the host refused it, or one the host served
    /// while the filter stood ready to excuse it, so every shape is asserted from both sides at once.
    /// </summary>
    [TestMethod]
    public void DeclinesAndHandleRefuseExactlyTheSameRequests()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First, Record(4UL, Second));
        QuePaxaVersionedNode<string> outsider = new(Configuration, Stranger, Record(4UL, Second));
        QuePaxaVersionedNode<string> spent = new(Configuration, First, new VersionedValue<string>(RegisterVersion.MaxValue, Second, Configuration, "spent"));

        (QuePaxaVersionedNode<string> Host, VersionedRecordRequest<VersionedValue<string>> Request, string Rule)[] refused =
        [
            (host, Request(6UL, ProposalPriority.Lowest, Second, "above"), "an instance above the live one"),
            (host, Request(4UL, ProposalPriority.Lowest, Second, "below"), "an instance below the live one"),
            (host, Carrying(5UL, 5UL, ForeignChain, ProposalPriority.Lowest, Second, "foreign"), "another chain"),
            (outsider, Request(5UL, ProposalPriority.Lowest, Second, "outside"), "a host outside the membership"),
            (host, Carrying(5UL, 4UL, Configuration, ProposalPriority.Lowest, Second, "torn"), "a record disagreeing with its envelope"),
            (spent, Request(5UL, ProposalPriority.Lowest, Second, "spent"), "a host whose version range is spent")
        ];

        foreach((QuePaxaVersionedNode<string> refuser, VersionedRecordRequest<VersionedValue<string>> request, string rule) in refused)
        {
            TestContext.WriteLine($"declined: {rule}");

            Assert.IsTrue(refuser.Declines(request), rule);
            _ = Assert.Throws<Exception>(() => _ = refuser.Handle(request));
        }

        //The accepting side, which is what a classifier that simply answered true everywhere would fail.
        VersionedRecordRequest<VersionedValue<string>> served = Request(5UL, ProposalPriority.Lowest, Second, "live");

        Assert.IsFalse(host.Declines(served));
        Assert.AreEqual(new RegisterVersion(5UL), host.Handle(served).Version);

        //The classifier agrees with the version classifier on the arm they share, so the widened filter is a
        //superset of the one it replaced rather than a different answer at the same version.
        Assert.IsTrue(host.Serves(new RegisterVersion(5UL)));
        Assert.IsFalse(spent.Serves(new RegisterVersion(1UL)));
    }


    /// <summary>
    /// A configuration change that keeps the previous writer must not demote the next instance to leaderless.
    /// Leaderless costs a round instead of a round trip, and every change would pay it if the derivation read
    /// "the membership moved" rather than "the writer left". The removing arm stands beside it, because the
    /// keeping arm alone passes a derivation that never goes leaderless at all.
    /// </summary>
    [TestMethod]
    public void AChangeRetainingTheWriterKeepsTheInstanceLedAndOnlyRemovingTheWriterLeavesItLeaderless()
    {
        QuePaxaVersionedNode<string> grown = new(Configuration, First, Record(4UL, Second));

        Assert.IsTrue(grown.Learn(Installing(5UL, Second, Configuration.With(Fourth))));

        Assert.AreEqual(Configuration.With(Fourth), grown.ActiveConfiguration);
        Assert.HasCount(4, grown.ActiveConfiguration.Members);
        Assert.AreEqual(ProposerLane.For(Second), grown.Recorder.ConfiguredLeader);

        //The reserved claim is the fast path itself: honoured, it stands at the round's first step under the
        //reserved priority, and a demoted instance would record it at the lowest ordinary one.
        VersionedRecordReply<VersionedValue<string>> led = grown.Handle(Carrying(6UL, 6UL, Configuration.With(Fourth), ProposalPriority.Reserved, Second, "fast"));

        Assert.AreEqual(ProposalPriority.Reserved, led.Reply.First.Key.Priority);

        //A change that removes a member other than the writer is the same claim from the other side of the
        //arithmetic: the set shrank and the writer still leads.
        QuePaxaVersionedNode<string> shrunk = new(Configuration, First, Record(4UL, Second));

        Assert.IsTrue(shrunk.Learn(Installing(5UL, Second, Configuration.Without(Third))));
        Assert.AreEqual(ProposerLane.For(Second), shrunk.Recorder.ConfiguredLeader);

        //Only a change that removes the writer itself leaves the instance leaderless, and it does so at every
        //host holding the record, because both inputs are agreed.
        QuePaxaVersionedNode<string> leaderless = new(Configuration, First, Record(4UL, Second));

        Assert.IsTrue(leaderless.Learn(Installing(5UL, Second, Configuration.Without(Second))));
        Assert.IsNull(leaderless.Recorder.ConfiguredLeader);

        VersionedRecordReply<VersionedValue<string>> declinedClaim = leaderless.Handle(Carrying(6UL, 6UL, Configuration.Without(Second), ProposalPriority.Reserved, Second, "claim"));

        Assert.AreEqual(ProposalPriority.Lowest, declinedClaim.Reply.First.Key.Priority);
    }


    /// <summary>
    /// The membership is a memo of the committed record and moves only where the record does. A host handed a
    /// genesis stands at it until it learns, then stands at what each record it adopts names, and a record
    /// that does not advance the host moves nothing.
    /// </summary>
    [TestMethod]
    public void TheActiveMembershipTracksTheCommittedRecordAndFallsBackToGenesis()
    {
        QuePaxaVersionedNode<string> host = new(Configuration, First);

        Assert.AreEqual(Configuration, host.ActiveConfiguration);
        Assert.IsTrue(Configuration.Members.SequenceEqual(host.LeaderSchedule.Schedule.Order));

        QuePaxaConfiguration grown = Configuration.With(Fourth);

        Assert.IsTrue(host.Learn(Installing(1UL, First, grown)));
        Assert.AreEqual(grown, host.ActiveConfiguration);
        Assert.IsTrue(grown.Members.SequenceEqual(host.LeaderSchedule.Schedule.Order));

        //A record that does not advance the host is ignored, membership included: a stale dissemination
        //carrying an older configuration cannot walk the membership backwards.
        Assert.IsFalse(host.Learn(Installing(1UL, First, Configuration)));
        Assert.AreEqual(grown, host.ActiveConfiguration);

        //Genesis is what the fallback reads and never changes, so a host that learned its way forward still
        //names the chain it was bootstrapped on.
        Assert.AreEqual(Configuration, host.Genesis);
        Assert.AreEqual(Configuration.Cluster, host.ActiveConfiguration.Cluster);
    }


    [TestMethod]
    public void TheVersionedHostExposesNoRecorderNode()
    {
        //The prohibition is absent code, so the vector is positive: no public member may hand out the recorder
        //node, whose own loop persists a bare register that names no instance — the torn pairing FromState
        //exists to refuse, written deliberately.
        foreach(System.Reflection.PropertyInfo property in typeof(QuePaxaVersionedNode<string>).GetProperties())
        {
            Assert.IsFalse(IsRecorderNode(property.PropertyType), $"{property.Name} exposes the recorder node.");
        }

        foreach(System.Reflection.FieldInfo field in typeof(QuePaxaVersionedNode<string>).GetFields())
        {
            Assert.IsFalse(IsRecorderNode(field.FieldType), $"{field.Name} exposes the recorder node.");
        }
    }


    private static bool IsRecorderNode(Type type)
    {
        return typeof(QuePaxaNode<VersionedValue<string>>).IsAssignableFrom(type)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(QuePaxaNode<>));
    }


    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, ProposalPriority priority, ReplicaId owner, string value)
    {
        return Carrying(version, version, Configuration, priority, owner, value);
    }


    /// <summary>
    /// Builds a request whose envelope and whose carried record can be made to disagree, which is what the
    /// rules beyond the version bound are pinned with.
    /// </summary>
    /// <param name="addressed">The version the envelope names.</param>
    /// <param name="written">The version the carried record was written at.</param>
    /// <param name="under">The membership the carried record names.</param>
    /// <param name="priority">The proposal's priority.</param>
    /// <param name="owner">The replica proposing, whose lane zero owns the proposal.</param>
    /// <param name="value">The application value.</param>
    /// <returns>The request.</returns>
    private static VersionedRecordRequest<VersionedValue<string>> Carrying(ulong addressed, ulong written, QuePaxaConfiguration under, ProposalPriority priority, ReplicaId owner, string value)
    {
        VersionedValue<string> record = new(new RegisterVersion(written), owner, under, value);
        PrioritizedProposal<VersionedValue<string>> proposal = new(new ProposalKey(priority, ProposerLane.For(owner)), record);

        return new VersionedRecordRequest<VersionedValue<string>>(new RegisterVersion(addressed), new RecordRequest<VersionedValue<string>>(Four, proposal));
    }


    private static VersionedValue<string> Record(ulong version, ReplicaId writer)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, Configuration, "committed");
    }


    /// <summary>Builds a committed record that installs <paramref name="next"/> for the version after it.</summary>
    /// <param name="version">The version it was written at.</param>
    /// <param name="writer">The replica that wrote it.</param>
    /// <param name="next">The membership the next version runs under.</param>
    /// <returns>The record.</returns>
    private static VersionedValue<string> Installing(ulong version, ReplicaId writer, QuePaxaConfiguration next)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, next, "committed");
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
