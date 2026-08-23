using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Globalization;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned register's reconfiguration scenarios over the scheduled transport: growing onto a host that
/// stood outside the membership, shrinking away from one that keeps running, and replacing a member with a
/// new identity.
/// </summary>
/// <remarks>
/// <para>
/// EVERY SCENARIO RUNS HOSTS THE MEMBERSHIP DOES NOT NAME. That is the whole point of driving them here: a
/// joiner has to be up and reachable before the change that admits it, and a leaver has to keep running after
/// the change that removed it, so a bench where host and member are one fact can express neither and every
/// reconfiguration in it is a change between two memberships whose hosts were all present all along.
/// </para>
/// <para>
/// TWO WITNESSES ARE ASSERTED IN EVERY SCENARIO. The safety witness is that one version is never held as two
/// records — read per version over the hosts by the replica each one is, and over every record any host
/// adopted during the run, so a version that a later one superseded is still covered. The liveness witness is
/// that the write after a change commits inside its attempt budget once a quorum of the new membership has
/// learned the installing record, which is read from <see cref="RegisterReadiness"/> rather than assumed, and
/// that before that the register reports honestly instead of committing on an arithmetic it cannot have.
/// </para>
/// <para>
/// THE READINESS QUESTION IS NOT THE REACHABILITY QUESTION, and the scenarios keep the two apart by holding a
/// host's dissemination rather than partitioning it: a held host answers every request, read and version
/// query while taking no record at all, so a reading that counted the members that answered would report a
/// quorum where none has learned anything.
/// </para>
/// </remarks>
[TestClass]
internal sealed class QuePaxaVersionedRegisterReconfigurationTests
{
    private const int AttemptsPerRecorder = 3;
    private const int AttemptBudget = 3;

    public TestContext TestContext { get; set; } = null!;

    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);
    private static ReplicaId Fourth { get; } = Replica(4);
    private static ReplicaId Fifth { get; } = Replica(5);
    private static ReplicaId Sixth { get; } = Replica(6);

    /// <summary>The replicas every scenario runs a host for, members and outsiders alike.</summary>
    private static ImmutableArray<ReplicaId> AllHosts { get; } = [First, Second, Third, Fourth];

    /// <summary>
    /// The replicas a change between two memberships sharing no member runs a host for, which needs a second
    /// full membership standing outside the first.
    /// </summary>
    private static ImmutableArray<ReplicaId> AllHostsAndASecondMembership { get; } = [First, Second, Third, Fourth, Fifth, Sixth];

    /// <summary>The chain three of those four hosts found, leaving the fourth running outside it.</summary>
    private static QuePaxaConfiguration ThreeMemberGenesis { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third));

    /// <summary>The chain all four found, which is where a scenario shrinks from.</summary>
    private static QuePaxaConfiguration FourMemberGenesis { get; } = QuePaxaConfiguration.CreateGenesis(Membership.Of(First, Second, Third, Fourth));

    private static TimeSpan BaseDelay { get; } = TimeSpan.FromMilliseconds(40);


    [TestMethod]
    public async Task AJoinerOutsideTheMembershipIsHandedTheInstallingRecordWithoutHavingAnsweredAnyRequest()
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(ThreeMemberGenesis, AllHosts, BaseDelay, seed: 21);
        QuePaxaVersionedRegister<string> writer = cluster.CreateRegister(First, AttemptsPerRecorder);
        QuePaxaVersionedRegister<string> joiner = cluster.CreateRegister(Fourth, AttemptsPerRecorder);

        //A register for a replica the membership does not list is how a joiner starts, and a write through one
        //is refused by report rather than by exception and spends no attempt at all.
        QuePaxaWriteOutcome<string> beforeAdmission = await DriveAsync(cluster, joiner.WriteAsync(_ => "d", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.OutsideConfiguration, beforeAdmission.Status);
        Assert.AreEqual(0, beforeAdmission.Attempts, "A write from outside the membership spent an attempt, which is budget spent on an answer only a configuration change can change.");
        Assert.IsFalse(beforeAdmission.Activated, "A write from outside the membership sent something.");

        QuePaxaWriteOutcome<string> bootstrap = await DriveAsync(cluster, writer.WriteAsync(_ => "a", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);
        Assert.IsNull(cluster.CommittedAt(Fourth), "The host outside the membership was offered an ordinary decide, so the audience is this bench's host list rather than the membership.");

        QuePaxaWriteOutcome<string> grown = await DriveAsync(cluster, writer.ReconfigureAsync(current => current.With(Membership.Member(Fourth)), AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, grown.Status);
        Assert.AreEqual(new RegisterVersion(2UL), grown.Version);

        VersionedValue<string>? installing = cluster.CommittedAt(Fourth);

        Assert.IsNotNull(installing, "The joiner holds no record, so nothing handed it the installing one before writing could resume.");
        Assert.AreEqual(grown.Version, installing.Version);
        Assert.IsTrue(installing.NextConfiguration.Contains(Fourth), "The record the joiner holds is not the one that admitted it.");

        //THE PUSH IS THE ONLY PATH THAT CAN HAVE DELIVERED IT. The joiner stood outside the membership the
        //change decided under, so no request was ever addressed to it, and its own count says so beside the
        //counts of the three members that answered.
        Assert.AreEqual(0, cluster.RecordRequestsAt(Fourth), "The joiner answered a record request, so a request path could have carried the installing record and the push is no longer the only route left.");
        Assert.IsGreaterThan(0, cluster.RecordRequestsAt(First), "No request reached the first member, so a count of zero at the joiner says nothing about the request path.");
        Assert.IsGreaterThan(0, cluster.RecordRequestsAt(Second), "No request reached the second member, so a count of zero at the joiner says nothing about the request path.");
        Assert.IsGreaterThan(0, cluster.RecordRequestsAt(Third), "No request reached the third member, so a count of zero at the joiner says nothing about the request path.");

        RegisterReadiness readiness = await DriveAsync(cluster, writer.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness after the grow: {Describe(readiness)}"));
        Assert.AreSequenceEqual(new[] { First, Second, Third, Fourth }, readiness.Members.Select(member => member.Member));
        Assert.AreEqual(grown.Version, readiness.Members.Single(member => member.Member.Equals(Fourth)).Version, "The joiner did not report the installing version, so the gate would clear against a member that has learned nothing.");
        Assert.IsTrue(readiness.QuorumHasLearned(grown.Version), "No quorum of the installed membership reported the installing record, so writing may not resume.");

        //Writing resumes through the register that was refused before the change, and the only thing about it
        //that moved is the membership.
        _ = await DriveAsync(cluster, joiner.ReadAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        QuePaxaWriteOutcome<string> afterAdmission = await DriveAsync(cluster, joiner.WriteAsync(_ => "d", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, afterAdmission.Status);
        Assert.AreEqual(1, afterAdmission.Attempts, "The write after the readiness gate cleared did not commit on its first attempt, so the budget bought something the gate was supposed to have.");
        Assert.HasCount(4, cluster.CommittedAt(First)!.NextConfiguration.Members, "The write that resumed did not run under the installed membership.");

        AssertUniqueHighestCommittedPerVersion(cluster, leastVersions: 3, "grow onto a cold joiner");
    }


    [TestMethod]
    public async Task ARemovedHostLearnsItIsOutFromTheRecordThatRemovedItWhileWritingDecidesOverTheMembersThatRemain()
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(FourMemberGenesis, AllHosts, BaseDelay, seed: 22);
        QuePaxaVersionedRegister<string> writer = cluster.CreateRegister(First, AttemptsPerRecorder);
        QuePaxaVersionedRegister<string> departing = cluster.CreateRegister(Fourth, AttemptsPerRecorder);

        QuePaxaWriteOutcome<string> bootstrap = await DriveAsync(cluster, writer.WriteAsync(_ => "a", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);

        int answeredBeforeTheChange = cluster.RecordRequestsAt(Fourth);

        Assert.IsGreaterThan(0, answeredBeforeTheChange, "The host about to be removed answered nothing while it was a member, so nothing here can show it still answering afterwards.");

        QuePaxaWriteOutcome<string> shrunk = await DriveAsync(cluster, writer.ReconfigureAsync(current => current.Without(Fourth), AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, shrunk.Status);
        Assert.IsGreaterThan(answeredBeforeTheChange, cluster.RecordRequestsAt(Fourth), "The removed host answered no request of the write that removed it, so it was not a recorder of the instance that decided the change.");

        VersionedValue<string>? removing = cluster.CommittedAt(Fourth);

        Assert.IsNotNull(removing, "The removed host was left in silence rather than handed the record that removed it.");
        Assert.AreEqual(shrunk.Version, removing.Version);
        Assert.IsFalse(removing.NextConfiguration.Contains(Fourth), "The record the removed host holds still lists it.");
        Assert.IsFalse(cluster.HostFor(Fourth).ActiveConfiguration.Contains(Fourth), "The removed host did not compute itself out of the membership from the record it holds.");

        //It is running the whole time: the catch-up below reaches it and it answers, and its own write path
        //reports the refusal rather than throwing at a caller that could not have known.
        _ = await DriveAsync(cluster, departing.ReadAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        QuePaxaWriteOutcome<string> refused = await DriveAsync(cluster, departing.WriteAsync(_ => "x", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.OutsideConfiguration, refused.Status);
        Assert.AreEqual(0, refused.Attempts, "The removed replica's own write spent an attempt on a refusal only a configuration change can lift.");

        RegisterReadiness readiness = await DriveAsync(cluster, writer.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness after the shrink: {Describe(readiness)}"));
        Assert.AreSequenceEqual(new[] { First, Second, Third }, readiness.Members.Select(member => member.Member));
        Assert.AreEqual(2, readiness.Configuration.Quorum, "The remaining membership's quorum is not the majority of three.");
        Assert.IsTrue(readiness.QuorumHasLearned(shrunk.Version), "No quorum of the remaining membership reported the record that shrank it, so writing may not resume.");

        int answeredAtTheBoundary = cluster.RecordRequestsAt(Fourth);
        int[] remainingAtTheBoundary = [cluster.RecordRequestsAt(First), cluster.RecordRequestsAt(Second), cluster.RecordRequestsAt(Third)];
        QuePaxaWriteOutcome<string> after = await DriveAsync(cluster, writer.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, after.Status);
        Assert.AreEqual(1, after.Attempts, "The write after the readiness gate cleared did not commit on its first attempt.");
        Assert.HasCount(3, cluster.CommittedAt(First)!.NextConfiguration.Members, "The write after the change did not run under the membership the change installed.");
        Assert.IsGreaterThan(remainingAtTheBoundary[0], cluster.RecordRequestsAt(First), "The first remaining member recorded nothing for the write after the change.");
        Assert.IsGreaterThan(remainingAtTheBoundary[1], cluster.RecordRequestsAt(Second), "The second remaining member recorded nothing for the write after the change.");
        Assert.IsGreaterThan(remainingAtTheBoundary[2], cluster.RecordRequestsAt(Third), "The third remaining member recorded nothing for the write after the change.");
        Assert.AreEqual(answeredAtTheBoundary, cluster.RecordRequestsAt(Fourth), "A write after the change addressed the removed host, so the recorder set is this bench's host list rather than the membership.");
        Assert.AreEqual(shrunk.Version, cluster.CommittedAt(Fourth)!.Version, "The removed host was offered a record decided after it left, so the push went to the hosts rather than to the audience.");

        AssertUniqueHighestCommittedPerVersion(cluster, leastVersions: 3, "shrink away from a host that keeps answering");
    }


    [TestMethod]
    public async Task AReplacementLeavesTheDepartedHostAndTheJoinerBothHoldingTheInstallingRecord()
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(ThreeMemberGenesis, AllHosts, BaseDelay, seed: 23);
        QuePaxaVersionedRegister<string> writer = cluster.CreateRegister(First, AttemptsPerRecorder);

        QuePaxaWriteOutcome<string> bootstrap = await DriveAsync(cluster, writer.WriteAsync(_ => "a", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);
        Assert.IsNull(cluster.CommittedAt(Fourth), "The replacement identity holds a record before it was ever named by one.");

        //One member out and one brand-new identity in, in one change, which is what replacing a machine whose
        //store was wiped amounts to: the old identity never comes back and the new one starts empty.
        QuePaxaWriteOutcome<string> replaced = await DriveAsync(cluster, writer.ReconfigureAsync(current => current.Without(Third).With(Membership.Member(Fourth)), AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, replaced.Status);
        Assert.AreSequenceEqual(new[] { First, Second, Fourth }, writer.ActiveConfiguration.Members.Select(configured => configured.Replica));

        VersionedValue<string>? departed = cluster.CommittedAt(Third);
        VersionedValue<string>? admitted = cluster.CommittedAt(Fourth);

        Assert.IsNotNull(departed, "The replaced member was left in silence rather than handed the record that replaced it.");
        Assert.IsNotNull(admitted, "The replacement holds no record, so nothing handed it the installing one.");
        Assert.AreEqual(replaced.Version, departed.Version);
        Assert.AreEqual(replaced.Version, admitted.Version);
        Assert.AreEqual(departed, admitted, "The two halves of the audience were handed different records at one version.");
        Assert.IsFalse(departed.NextConfiguration.Contains(Third), "The record the replaced member holds still lists it.");
        Assert.IsTrue(admitted.NextConfiguration.Contains(Fourth), "The record the replacement holds does not list it.");

        Assert.AreEqual(0, cluster.RecordRequestsAt(Fourth), "The replacement answered a record request before it was a member, so the push is no longer the only route the installing record can have taken to it.");
        Assert.IsGreaterThan(0, cluster.RecordRequestsAt(Third), "The replaced member answered nothing while it was one, so a count of zero at the replacement says nothing about the request path.");

        RegisterReadiness readiness = await DriveAsync(cluster, writer.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness after the replacement: {Describe(readiness)}"));
        Assert.AreSequenceEqual(new[] { First, Second, Fourth }, readiness.Members.Select(member => member.Member));
        Assert.IsTrue(readiness.QuorumHasLearned(replaced.Version), "No quorum of the installed membership reported the installing record, so writing may not resume.");

        int replacedAnswered = cluster.RecordRequestsAt(Third);
        QuePaxaWriteOutcome<string> after = await DriveAsync(cluster, writer.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, after.Status);
        Assert.AreEqual(1, after.Attempts, "The write after the readiness gate cleared did not commit on its first attempt.");
        Assert.IsGreaterThan(0, cluster.RecordRequestsAt(Fourth), "The replacement recorded nothing for the membership that admitted it, so writing did not continue over the new membership.");
        Assert.AreEqual(replacedAnswered, cluster.RecordRequestsAt(Third), "A write after the change addressed the replaced member, so the recorder set is this bench's host list rather than the membership.");
        Assert.AreEqual(replaced.Version, cluster.CommittedAt(Third)!.Version, "The replaced member was offered a record decided after it left, so the push went to the hosts rather than to the audience.");

        AssertUniqueHighestCommittedPerVersion(cluster, leastVersions: 3, "replace a member with a new identity");
    }


    [TestMethod]
    public async Task AWriteAfterAChangeCommitsOnceAQuorumHasLearnedAndNotWhileTheMembersAreOnlyReachable()
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(ThreeMemberGenesis, AllHosts, BaseDelay, seed: 24);
        QuePaxaVersionedRegister<string> writer = cluster.CreateRegister(First, AttemptsPerRecorder);

        QuePaxaWriteOutcome<string> bootstrap = await DriveAsync(cluster, writer.WriteAsync(_ => "a", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);

        //Two of the four members the change installs answer everything and take nothing, which is the state a
        //readiness report exists to separate from an unreachable one.
        cluster.HoldDissemination(Third);
        cluster.HoldDissemination(Fourth);

        QuePaxaWriteOutcome<string> grown = await DriveAsync(cluster, writer.ReconfigureAsync(current => current.With(Membership.Member(Fourth)), AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, grown.Status);
        Assert.AreEqual(RegisterVersion.First, cluster.CommittedAt(Third)!.Version, "The held member took the record it was offered, so nothing here is behind.");
        Assert.IsNull(cluster.CommittedAt(Fourth), "The held joiner took the record it was offered, so nothing here is behind.");

        RegisterReadiness cold = await DriveAsync(cluster, writer.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness while two members are behind: {Describe(cold)}"));
        Assert.AreEqual(4, cold.Reachable, "A member that answers was reported unreachable, so this reading cannot tell reachability and learning apart.");
        Assert.IsFalse(cold.QuorumHasLearned(grown.Version), "A quorum was claimed at a version only two of four members hold.");

        QuePaxaWriteOutcome<string> stalled = await DriveAsync(cluster, writer.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, stalled.Status, "A write committed over a membership whose quorum has not learned the version it builds on.");
        Assert.AreEqual(AttemptBudget, stalled.Attempts, "The write returned without spending its budget, so the budget is not what bounded it.");
        Assert.AreEqual(grown.Version, writer.Committed!.Version, "The register moved on a write that decided nothing.");

        cluster.ResumeDissemination(Third);
        cluster.ResumeDissemination(Fourth);
        cluster.Disseminate(cluster.CommittedAt(First)!, [Third, Fourth]);
        cluster.RunToQuiescence([]);

        RegisterReadiness warm = await DriveAsync(cluster, writer.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the stragglers caught up: {Describe(warm)}"));
        Assert.AreEqual(4, warm.Reachable);
        Assert.IsTrue(warm.QuorumHasLearned(grown.Version), "The catch-up landed and the report still shows no quorum at the installing version.");

        QuePaxaWriteOutcome<string> resumed = await DriveAsync(cluster, writer.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, resumed.Status);
        Assert.IsLessThanOrEqualTo(AttemptBudget, resumed.Attempts, "The write that followed the cleared gate did not commit inside its budget.");

        AssertUniqueHighestCommittedPerVersion(cluster, leastVersions: 3, "a boundary whose quorum learns late");
    }


    /// <summary>
    /// Two operators change the membership at one instance, one admitting a host and one dropping another.
    /// One change wins the instance and the other is superseded; the superseded one re-applies its delta
    /// against the membership that won, so both operators' intentions survive and neither undoes the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two deltas are chosen so that composing against the winner and composing against the register's
    /// genesis give different memberships. A change that starts at genesis, or one that computes the same
    /// result under both, cannot tell the two apart at all, and every change with one of those shapes leaves
    /// the composition rule unstated. Here the loser removes a host the winner never touched while the winner
    /// admits one the loser never named, so a composition against genesis drops the host the winner admitted.
    /// </para>
    /// <para>
    /// THE SUPERSESSION IS ARRANGED AND NOT RACED. A rival's record closes the instance at every host that
    /// learns it, and a proposer arriving after that is declined rather than told what won, so a scenario that
    /// let the winner's dissemination land would leave the loser undecided and never reach the retry. The
    /// hosts are held back while the winning change is decided, which leaves them serving the instance with
    /// the decided record in their recorder registers, and released before the losing change runs, so the
    /// loser's own attempt learns the winner and its own publish makes the winner's record servable for the
    /// retry.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task ASupersededChangeReAppliesItsDeltaAgainstTheMembershipThatWonTheInstance()
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(ThreeMemberGenesis, AllHosts, BaseDelay, seed: 32);
        QuePaxaVersionedRegister<string> growing = cluster.CreateRegister(First, AttemptsPerRecorder);
        QuePaxaVersionedRegister<string> shrinking = cluster.CreateRegister(Second, AttemptsPerRecorder);

        QuePaxaWriteOutcome<string> bootstrap = await DriveAsync(cluster, growing.WriteAsync(_ => "a", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);

        //The second operator's register catches up before it changes anything, because a reconfiguration
        //carries a committed value forward and this register has written none of its own.
        _ = await DriveAsync(cluster, shrinking.ReadAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(bootstrap.Version, shrinking.Committed!.Version, "The second operator's register did not catch up, so its change would be refused for want of a value rather than contend for the instance.");

        foreach(ReplicaId host in AllHosts)
        {
            cluster.HoldDissemination(host);
        }

        QuePaxaWriteOutcome<string> grown = await DriveAsync(cluster, growing.ReconfigureAsync(current => current.With(Membership.Member(Fourth)), AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, grown.Status);
        Assert.AreEqual(new RegisterVersion(2UL), grown.Version);
        Assert.AreEqual(1, grown.Attempts, "The change that won the instance spent more than one attempt, so what the other one meets is not a single decided record.");
        foreach(ReplicaId member in ThreeMemberGenesis.Members.Select(configured => configured.Replica))
        {
            Assert.AreEqual(bootstrap.Version, cluster.CommittedAt(member)!.Version, "A host took the record the winning change decided, so it declines the losing change rather than telling it what won.");
        }

        foreach(ReplicaId host in AllHosts)
        {
            cluster.ResumeDissemination(host);
        }

        //The second operator's change captured the same instance and meets the record that closed it. Its own
        //attempt adopts that record, publishes it, and the retry runs at the version after it.
        QuePaxaWriteOutcome<string> shrunk = await DriveAsync(cluster, shrinking.ReconfigureAsync(current => current.Without(Third), AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"the change that won committed at version {grown.Version.Value} over {grown.Attempts} attempts, the one that composed at version {shrunk.Version.Value} over {shrunk.Attempts}"));
        Assert.AreEqual(QuePaxaWriteStatus.Committed, shrunk.Status);

        //A change that was never superseded commits at the version it captured, so the version after it and
        //the second attempt are what say a retry ran at all, and the whole reading below rests on that.
        Assert.AreEqual(new RegisterVersion(3UL), shrunk.Version, "The second change committed at the version it captured, so it was never superseded and no retry re-applied a delta.");
        Assert.AreEqual(2, shrunk.Attempts, "The second change committed on its first attempt, so it did not lose the instance and compose against what won it.");
        Assert.AreEqual("a", shrunk.Value, "The change that retried did not carry the committed value forward.");

        //THE DELTA COMPOSES AGAINST THE WINNER. Both operators' intentions are installed, which is what a
        //delta re-applied against the membership that won gives; one re-applied against the register's genesis
        //would drop the host the winner admitted, because the genesis never named it.
        VersionedValue<string> installed = cluster.CommittedAt(First)!;

        Assert.AreEqual(new RegisterVersion(3UL), installed.Version, "The host holds an earlier record than the one the retry decided.");
        Assert.AreSequenceEqual(new[] { First, Second, Fourth }, installed.NextConfiguration.Members.Select(configured => configured.Replica), "The installed membership is not what the two deltas compose to, so the retry re-applied its change against a membership other than the one that won the instance.");
        Assert.AreSequenceEqual(new[] { First, Second, Fourth }, shrinking.ActiveConfiguration.Members.Select(configured => configured.Replica), "The register that retried runs under a membership other than the one its own retry installed.");

        AssertUniqueHighestCommittedPerVersion(cluster, leastVersions: 3, "a change superseded by a rival change");
    }


    /// <summary>
    /// A change to a membership sharing no member with the current one commits and is reported, and the write
    /// after it cannot proceed until dissemination has reached a quorum of the membership that change
    /// installed. Safe, and immediately unavailable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library does not refuse such a change. Reachability is not a fact a register holds, so a refusal
    /// would forbid the operation whenever an operator does know the incoming membership is up, and the
    /// pre-flight is a readiness read rather than a rule.
    /// </para>
    /// <para>
    /// Safe is asserted twice over: the instance that decided the change ran entirely under the membership
    /// that existed before it, so no version is held as two records, and every replica of that membership
    /// reports itself outside the one it installed rather than writing on.
    /// </para>
    /// <para>
    /// Unavailable is asserted as a bounded observation and never as a wait. The two incoming members that
    /// hold no record decline the instance, so the attempt budget is what ends the write, and the outcome is
    /// an undecided report with the budget spent rather than a call that never returns.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task AChangeToADisjointMembershipCommitsAndTheWriteAfterItWaitsForThatMembershipToCatchUp()
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(ThreeMemberGenesis, AllHostsAndASecondMembership, BaseDelay, seed: 33);
        QuePaxaVersionedRegister<string> outgoing = cluster.CreateRegister(First, AttemptsPerRecorder);
        QuePaxaVersionedRegister<string> incoming = cluster.CreateRegister(Fourth, AttemptsPerRecorder);

        QuePaxaWriteOutcome<string> bootstrap = await DriveAsync(cluster, outgoing.WriteAsync(_ => "a", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, bootstrap.Status);

        //Two of the three hosts the change installs answer everything and take nothing, so the record that
        //installs them reaches one of them and never a quorum of them.
        cluster.HoldDissemination(Fifth);
        cluster.HoldDissemination(Sixth);

        RegisterReadiness before = await DriveAsync(cluster, outgoing.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness before the change: {Describe(before)}"));
        Assert.IsTrue(before.QuorumHasLearned(bootstrap.Version), "The membership the change is decided under has not learned the record it builds on, so the change would be unavailable for a reason this scenario is not about.");

        QuePaxaWriteOutcome<string> moved = await DriveAsync(cluster, outgoing.ReconfigureAsync(
            current => current.With(Membership.Member(Fourth)).With(Membership.Member(Fifth)).With(Membership.Member(Sixth)).Without(First).Without(Second).Without(Third),
            AttemptBudget,
            TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, moved.Status, "A change to a membership sharing no member with the current one was refused, and reachability is not a fact the register holds.");
        Assert.AreEqual(new RegisterVersion(2UL), moved.Version);
        Assert.AreSequenceEqual(new[] { Fourth, Fifth, Sixth }, outgoing.ActiveConfiguration.Members.Select(configured => configured.Replica), "The membership installed is not the disjoint one the delta computed.");
        Assert.AreEqual(ThreeMemberGenesis.Cluster, outgoing.ActiveConfiguration.Cluster, "The disjoint membership names another chain, so the change founded a cluster rather than reconfiguring one.");

        //Safe: every replica of the membership that decided the change is now outside the one it installed,
        //and each reports that rather than writing on.
        QuePaxaWriteOutcome<string> refused = await DriveAsync(cluster, outgoing.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.OutsideConfiguration, refused.Status, "A replica the change removed kept writing, so the two memberships can both decide.");
        Assert.AreEqual(0, refused.Attempts, "The refusal spent budget on an answer only another configuration change can change.");

        //The boundary push reaches the incoming member it can, and the two held ones take nothing, so exactly
        //one of the three the change installed holds the record that installed it.
        Assert.AreEqual(moved.Version, cluster.CommittedAt(Fourth)!.Version, "The one incoming host that takes records was not handed the record that admitted it.");
        Assert.IsNull(cluster.CommittedAt(Fifth), "A held host took the record it was offered, so nothing here is behind.");
        Assert.IsNull(cluster.CommittedAt(Sixth), "A held host took the record it was offered, so nothing here is behind.");

        //The incoming register stands on genesis until it reads: its host holds the installing record and its
        //register does not, and the catch-up is what moves it onto the membership that record installs.
        _ = await DriveAsync(cluster, incoming.ReadAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreSequenceEqual(new[] { Fourth, Fifth, Sixth }, incoming.ActiveConfiguration.Members.Select(configured => configured.Replica), "The catch-up read did not move the incoming register onto the membership it is a member of.");

        RegisterReadiness cold = await DriveAsync(cluster, incoming.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness while the incoming membership is behind: {Describe(cold)}"));
        Assert.AreEqual(3, cold.Reachable, "A member that answers was reported unreachable, so this reading cannot tell an unavailable membership from a behind one.");
        Assert.IsFalse(cold.QuorumHasLearned(moved.Version), "A quorum was claimed at a version only one of the three incoming members holds.");

        QuePaxaWriteOutcome<string> stalled = await DriveAsync(cluster, incoming.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Undecided, stalled.Status, "A write committed over a membership whose quorum has not learned the record that installed it.");
        Assert.AreEqual(AttemptBudget, stalled.Attempts, "The write returned without spending its budget, so the budget is not what bounded it and the unavailability read here is not a bounded observation.");
        Assert.AreEqual(moved.Version, incoming.Committed!.Version, "The register moved on a write that decided nothing.");

        //The catch-up a deployment owns, which is the only route left: nothing in the protocol walks a record
        //forward to hosts that hold none of the memberships naming them.
        cluster.ResumeDissemination(Fifth);
        cluster.ResumeDissemination(Sixth);
        cluster.Disseminate(cluster.CommittedAt(Fourth)!, [Fifth, Sixth]);
        cluster.RunToQuiescence([]);

        RegisterReadiness warm = await DriveAsync(cluster, incoming.ReadReadinessAsync(Timeout.InfiniteTimeSpan, TestContext.CancellationToken)).ConfigureAwait(false);

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"readiness once the incoming membership caught up: {Describe(warm)}"));
        Assert.IsTrue(warm.QuorumHasLearned(moved.Version), "The catch-up landed and the report still shows no quorum at the installing version.");

        QuePaxaWriteOutcome<string> resumed = await DriveAsync(cluster, incoming.WriteAsync(_ => "b", AttemptBudget, TestContext.CancellationToken)).ConfigureAwait(false);

        Assert.AreEqual(QuePaxaWriteStatus.Committed, resumed.Status, "The write after the gate cleared did not commit, so the unavailability read above is not the dissemination it was attributed to.");
        Assert.AreEqual(new RegisterVersion(3UL), resumed.Version);

        AssertUniqueHighestCommittedPerVersion(cluster, leastVersions: 3, "a change to a disjoint membership");
    }


    [TestMethod]
    public void TheSafetyWitnessSeparatesALaggingHostFromTwoRecordsAtOneVersion()
    {
        //A witness nothing can fail certifies nothing, so it is read against divergences built by hand and
        //against the one shape that looks like divergence and is not.
        VersionedValue<string> one = new(RegisterVersion.First, First, ThreeMemberGenesis, "a");

        SafetyReading agreeing = ReadTwoHeldRecords(one, one, seed: 25);

        Assert.IsNull(agreeing.Disagreement, "One record held by two hosts was read as a disagreement.");
        Assert.AreEqual(1, agreeing.Versions);

        //A host that has not caught up holds an older version, which is availability and never divergence.
        SafetyReading lagging = ReadTwoHeldRecords(one, new VersionedValue<string>(new RegisterVersion(2UL), Second, ThreeMemberGenesis, "b"), seed: 26);

        Assert.IsNull(lagging.Disagreement, "A host holding an older version was read as a disagreement, so the witness compares hosts rather than versions.");
        Assert.AreEqual(2, lagging.Versions);

        SafetyReading byValue = ReadTwoHeldRecords(one, new VersionedValue<string>(RegisterVersion.First, First, ThreeMemberGenesis, "b"), seed: 27);

        Assert.IsNotNull(byValue.Disagreement, "Two records at one version differing in the value were read as agreement.");

        SafetyReading byWriter = ReadTwoHeldRecords(one, new VersionedValue<string>(RegisterVersion.First, Second, ThreeMemberGenesis, "a"), seed: 28);

        Assert.IsNotNull(byWriter.Disagreement, "Two records at one version differing in the writer were read as agreement.");

        SafetyReading byMembership = ReadTwoHeldRecords(one, new VersionedValue<string>(RegisterVersion.First, First, ThreeMemberGenesis.With(Membership.Member(Fourth)), "a"), seed: 29);

        Assert.IsNotNull(byMembership.Disagreement, "Two records at one version differing in the membership they install were read as agreement.");

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"value: {byValue.Disagreement}"));
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"writer: {byWriter.Disagreement}"));
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"membership: {byMembership.Disagreement}"));
    }


    [TestMethod]
    public void ABenchRefusesAHostListThatIsEmptyOrNamesOneReplicaTwice()
    {
        //Two hosts answering as one replica is a wiring error rather than a topology: every read by replica
        //would find the first of them, and the second would answer requests nothing counted. The two
        //refusals are separated by their vectors, because a duplicated list is not an empty one.
        ArgumentException duplicated = Assert.ThrowsExactly<ArgumentException>(() => _ = new InterleavedVersionedQuePaxaCluster<string>(ThreeMemberGenesis, [First, Second, First], BaseDelay, seed: 30));

        Assert.Contains("cannot name one replica twice", duplicated.Message);

        ArgumentException empty = Assert.ThrowsExactly<ArgumentException>(() => _ = new InterleavedVersionedQuePaxaCluster<string>(ThreeMemberGenesis, [], BaseDelay, seed: 31));

        Assert.Contains("at least one host", empty.Message);
    }


    /// <summary>
    /// Reads the safety witness over a cluster whose first two hosts were made to hold
    /// <paramref name="held"/> and <paramref name="alsoHeld"/>.
    /// </summary>
    /// <param name="held">The record the first host is made to hold.</param>
    /// <param name="alsoHeld">The record the second host is made to hold.</param>
    /// <param name="seed">The delivery-order seed, which no run here uses because nothing is delivered.</param>
    /// <returns>What the witness read.</returns>
    /// <remarks>
    /// The records are learned at the hosts directly rather than offered over the transport, because the
    /// point is a state the protocol cannot produce and a witness that could only read states it produces
    /// would never be exercised against one.
    /// </remarks>
    private static SafetyReading ReadTwoHeldRecords(VersionedValue<string> held, VersionedValue<string> alsoHeld, int seed)
    {
        InterleavedVersionedQuePaxaCluster<string> cluster = new(ThreeMemberGenesis, AllHosts, BaseDelay, seed);

        _ = cluster.HostFor(First).Learn(held);
        _ = cluster.HostFor(Second).Learn(alsoHeld);

        return ReadSafetyWitness(cluster);
    }


    /// <summary>
    /// Asserts that no version of <paramref name="cluster"/>'s chain is held as two different records.
    /// </summary>
    /// <param name="cluster">The cluster to read.</param>
    /// <param name="leastVersions">How many versions the run is known to have produced records at.</param>
    /// <param name="scenario">What the run was, for the report.</param>
    private void AssertUniqueHighestCommittedPerVersion(InterleavedVersionedQuePaxaCluster<string> cluster, int leastVersions, string scenario)
    {
        SafetyReading reading = ReadSafetyWitness(cluster);

        Assert.IsNull(reading.Disagreement, string.Create(CultureInfo.InvariantCulture, $"{scenario}: {reading.Disagreement}"));

        //A witness that covered fewer versions than the run wrote certifies less than the run, and a reading
        //over an empty cluster is null-free for the wrong reason entirely.
        Assert.IsGreaterThanOrEqualTo(leastVersions, reading.Versions, string.Create(CultureInfo.InvariantCulture, $"{scenario}: the witness covered {reading.Versions} versions where the run wrote at least {leastVersions}."));

        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{scenario}: {reading.Versions} versions over {reading.Records} held records, none held twice"));
    }


    /// <summary>
    /// Reads every record <paramref name="cluster"/>'s hosts hold and ever adopted, and reports the first
    /// version two of them disagree at.
    /// </summary>
    /// <param name="cluster">The cluster to read.</param>
    /// <returns>What the reading covered and the first disagreement in it, if there is one.</returns>
    /// <remarks>
    /// The hosts are read by the replica each one is rather than by position, because a position means
    /// nothing once a membership has moved, and the adoption history is read beside them because a version a
    /// later one superseded is still a version every host that held it had to agree about.
    /// </remarks>
    private static SafetyReading ReadSafetyWitness(InterleavedVersionedQuePaxaCluster<string> cluster)
    {
        Dictionary<RegisterVersion, (ReplicaId Holder, VersionedValue<string> Record)> firstAtVersion = [];
        List<string> disagreements = [];
        int records = 0;

        foreach(ReplicaId replica in cluster.Replicas)
        {
            if(cluster.CommittedAt(replica) is { } held)
            {
                records++;
                Fold(firstAtVersion, disagreements, replica, held);
            }
        }

        foreach(InterleavedVersionedQuePaxaCluster<string>.AdoptedRecord adoption in cluster.AdoptedRecords)
        {
            records++;
            Fold(firstAtVersion, disagreements, adoption.Member, adoption.Record);
        }

        return new SafetyReading(firstAtVersion.Count, records, disagreements.Count == 0 ? null : disagreements[0]);
    }


    /// <summary>Folds one held record into the per-version reading.</summary>
    /// <param name="firstAtVersion">The record already read at each version, with the replica it was read from.</param>
    /// <param name="disagreements">Where a version holding two records is reported.</param>
    /// <param name="holder">The replica this record was read from.</param>
    /// <param name="record">The record read.</param>
    /// <remarks>
    /// The whole record is compared and not its version alone. Two records at one version that differ in the
    /// value, in the writer or in the membership they install are each a decision two hosts took differently,
    /// and a comparison over the version would call all three agreement.
    /// </remarks>
    private static void Fold(
        Dictionary<RegisterVersion, (ReplicaId Holder, VersionedValue<string> Record)> firstAtVersion,
        List<string> disagreements,
        ReplicaId holder,
        VersionedValue<string> record)
    {
        if(!firstAtVersion.TryGetValue(record.Version, out (ReplicaId Holder, VersionedValue<string> Record) seen))
        {
            firstAtVersion.Add(record.Version, (holder, record));

            return;
        }

        if(!seen.Record.Equals(record))
        {
            disagreements.Add(string.Create(CultureInfo.InvariantCulture, $"version {record.Version.Value} is held as {Describe(seen.Record)} by {Name(seen.Holder)} and as {Describe(record)} by {Name(holder)}"));
        }
    }


    /// <summary>Pumps <paramref name="cluster"/> until <paramref name="client"/> has completed.</summary>
    /// <typeparam name="TResult">What the client produces.</typeparam>
    /// <param name="cluster">The cluster driving the client.</param>
    /// <param name="client">The client task, which must already have been started.</param>
    /// <returns>What the client produced.</returns>
    /// <remarks>
    /// The client is started by the caller and handed over here rather than started here, because everything
    /// a register does before its first await lands on this thread and a bench that started it inside a
    /// helper would still be the one that has to pump it.
    /// </remarks>
    private static async Task<TResult> DriveAsync<TResult>(InterleavedVersionedQuePaxaCluster<string> cluster, Task<TResult> client)
    {
        cluster.RunToQuiescence([client]);

        return await client.ConfigureAwait(false);
    }


    /// <summary>One readiness report as a line, per member, for the run's own record.</summary>
    /// <param name="readiness">The report.</param>
    /// <returns>The line.</returns>
    private static string Describe(RegisterReadiness readiness)
    {
        return string.Join(", ", readiness.Members.Select(member => member.Version is { } version
            ? string.Create(CultureInfo.InvariantCulture, $"{Name(member.Member)}@{version.Value}")
            : $"{Name(member.Member)}@unreachable"));
    }


    /// <summary>One record as a line, naming everything two records at one version can differ in.</summary>
    /// <param name="record">The record.</param>
    /// <returns>The line.</returns>
    private static string Describe(VersionedValue<string> record)
    {
        return string.Create(CultureInfo.InvariantCulture, $"'{record.Value}' by {Name(record.Writer)} over {record.NextConfiguration.Members.Length} members");
    }


    /// <summary>A replica's leading bytes, which is enough to tell this suite's four apart.</summary>
    /// <param name="replica">The replica.</param>
    /// <returns>Its leading bytes in hexadecimal.</returns>
    private static string Name(ReplicaId replica) => Convert.ToHexStringLower(replica.AsSpan())[..4];


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }


    /// <summary>What a reading of the safety witness covered and found.</summary>
    /// <param name="Versions">How many distinct versions the reading covered.</param>
    /// <param name="Records">How many held records were folded into it.</param>
    /// <param name="Disagreement">The first version held as two records, or <see langword="null"/> when every version is held as one.</param>
    private sealed record SafetyReading(int Versions, int Records, string? Disagreement);
}
