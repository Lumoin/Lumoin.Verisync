using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The recorder host's suite. The host owns the one consensus instance whose leader it can derive, so the
/// subjects are which version it serves, that it refuses every other one at both bounds, and that learning a
/// version moves the instance rather than reconfiguring the one already running.
/// </summary>
[TestClass]
internal sealed class QuePaxaVersionedNodeTests
{
    private static ReplicaId First { get; } = Replica(1);
    private static ReplicaId Second { get; } = Replica(2);
    private static ReplicaId Third { get; } = Replica(3);
    private static ReplicaId Stranger { get; } = Replica(9);

    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    [TestMethod]
    public void AHostThatHasLearnedNothingServesTheFirstVersionUnderTheBootstrapLeader()
    {
        QuePaxaVersionedNode<string> host = new(Schedule());

        Assert.AreEqual(RegisterVersion.First, host.LiveVersion);
        Assert.AreEqual(ProposerLane.For(First), host.Node.Recorder.ConfiguredLeader);
    }


    //The version is asserted literally, because comparing the two derivations alone would pass at any version.
    [TestMethod]
    public void TheLiveInstanceFollowsTheCommittedRecordAndCarriesItsWritersLane()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Second));

        Assert.AreEqual(new RegisterVersion(5UL), host.LiveVersion);
        Assert.AreEqual(schedule.LeaderFor(Second), host.Node.Recorder.ConfiguredLeader);
        Assert.AreEqual(ProposerLane.For(Second), host.Node.Recorder.ConfiguredLeader);
    }


    [TestMethod]
    public void TwoRequestsAtTheLiveVersionReachOneInstance()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaNode<VersionedValue<string>> instance = host.Node;

        VersionedRecordReply<VersionedValue<string>> first = host.Handle(Request(5UL, ProposalPriority.Lowest, Second, "a"));
        VersionedRecordReply<VersionedValue<string>> second = host.Handle(Request(5UL, new ProposalPriority(7), Second, "b"));

        Assert.AreSame(instance, host.Node);
        Assert.AreEqual(new RegisterVersion(5UL), first.Version);
        Assert.AreEqual(new RegisterVersion(5UL), second.Version);

        //A host creating an instance per request would not fold the second request into the same register.
        Assert.AreEqual(Four, host.Node.Recorder.Step);
    }


    //A host that checked only the upper bound would pass every above-version vector and still be wrong.
    [TestMethod]
    public void ARequestForAnyOtherVersionIsRefusedAtBothBounds()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));
        QuePaxaNode<VersionedValue<string>> instance = host.Node;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = host.Handle(Request(6UL, ProposalPriority.Lowest, Second, "above")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = host.Handle(Request(4UL, ProposalPriority.Lowest, Second, "below")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = host.Handle(Request(1UL, ProposalPriority.Lowest, Second, "far below")));

        //A refusal allocates nothing and records nothing, so a peer naming versions at random costs nothing.
        Assert.AreSame(instance, host.Node);
        Assert.AreEqual(RecorderStep.Zero, host.Node.Recorder.Step);
    }


    [TestMethod]
    public void LearningAdvancesTheLiveInstanceAndIgnoresARecordThatDoesNot()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));

        Assert.IsTrue(host.Learn(Record(5UL, Third)));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);
        Assert.AreEqual(ProposerLane.For(Third), host.Node.Recorder.ConfiguredLeader);

        Assert.IsFalse(host.Learn(Record(5UL, Third)));
        Assert.IsFalse(host.Learn(Record(4UL, Second)));
        Assert.AreEqual(new RegisterVersion(6UL), host.LiveVersion);
    }


    //The identity assertion alone passes a host that rewrote the leader in place; the leader assertion alone
    //passes one that kept the old node.
    [TestMethod]
    public void LearningBuildsANewInstanceAndLeavesTheRunningOnesLeaderAlone()
    {
        QuePaxaVersionedNode<string> host = new(Schedule(), Record(4UL, Second));

        _ = host.Handle(Request(5UL, ProposalPriority.Reserved, Second, "a"));

        QuePaxaNode<VersionedValue<string>> beforeLearning = host.Node;
        ProposerLane? leaderBeforeLearning = beforeLearning.Recorder.ConfiguredLeader;

        Assert.IsTrue(host.Learn(Record(5UL, Third)));

        Assert.AreNotSame(beforeLearning, host.Node);
        Assert.AreEqual(leaderBeforeLearning, beforeLearning.Recorder.ConfiguredLeader);
        Assert.AreEqual(ProposerLane.For(Second), beforeLearning.Recorder.ConfiguredLeader);
        Assert.AreEqual(ProposerLane.For(Third), host.Node.Recorder.ConfiguredLeader);

        //The new instance starts from an unwritten register, so nothing the previous version recorded leaks in.
        Assert.AreEqual(RecorderStep.Zero, host.Node.Recorder.Step);
    }


    //The derivation reads only agreed inputs, so every host makes this instance leaderless and none differs.
    [TestMethod]
    public void AWriterOutsideTheOrderMakesTheNextInstanceLeaderlessRatherThanUnservable()
    {
        QuePaxaLeaderSchedule schedule = Schedule();
        QuePaxaVersionedNode<string> host = new(schedule, Record(4UL, Stranger));

        Assert.AreEqual(new RegisterVersion(5UL), host.LiveVersion);
        Assert.IsNull(host.Node.Recorder.ConfiguredLeader);

        VersionedRecordReply<VersionedValue<string>> reply = host.Handle(Request(5UL, ProposalPriority.Reserved, Second, "a"));

        Assert.AreEqual(ProposalPriority.Lowest, reply.Reply.First.Key.Priority);
    }


    [TestMethod]
    public void TheHostRefusesANullScheduleAndANullRequest()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new QuePaxaVersionedNode<string>(null!));

        QuePaxaVersionedNode<string> host = new(Schedule());

        Assert.ThrowsExactly<ArgumentNullException>(() => _ = host.Handle(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = host.Learn(null!));
    }


    private static VersionedRecordRequest<VersionedValue<string>> Request(ulong version, ProposalPriority priority, ReplicaId owner, string value)
    {
        RegisterVersion at = new(version);
        VersionedValue<string> record = new(at, owner, value);
        PrioritizedProposal<VersionedValue<string>> proposal = new(new ProposalKey(priority, ProposerLane.For(owner)), record);

        return new VersionedRecordRequest<VersionedValue<string>>(at, new RecordRequest<VersionedValue<string>>(Four, proposal));
    }


    private static VersionedValue<string> Record(ulong version, ReplicaId writer)
    {
        return new VersionedValue<string>(new RegisterVersion(version), writer, "committed");
    }


    private static QuePaxaLeaderSchedule Schedule()
    {
        ImmutableArray<ReplicaId> order = [First, Second, Third];

        return new QuePaxaLeaderSchedule(HedgingSchedule.Create(order, TimeSpan.FromMilliseconds(10)));
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
