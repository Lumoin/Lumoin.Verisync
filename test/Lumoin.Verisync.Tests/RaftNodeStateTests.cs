using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Covers the durable triple seam: <see cref="RaftNode{TCommand}.ToState"/> snapshots the Figure 2 triple,
/// <see cref="RaftNode{TCommand}.FromState"/> restores a fresh follower at commit zero, and every fail-closed
/// rejection of state no honest history could produce.
/// </summary>
[TestClass]
internal sealed class RaftNodeStateTests
{
    private static ReplicaId N1 { get; } = Replica(1);
    private static ReplicaId N2 { get; } = Replica(2);
    private static ReplicaId N3 { get; } = Replica(3);

    private static ImmutableArray<ReplicaId> Members { get; } = [N1, N2, N3];


    [TestMethod]
    public void ToStateAndFromStateRoundTripADurableTripleWithHistory()
    {
        //Build real history by hand: elect N1, propose two commands, replicate to a follower so the follower
        //holds a non-trivial term/vote/log triple, then snapshot the follower and rebuild it from that snapshot.
        RaftNode<string> leader = new(N1, Members);
        RaftNode<string> follower = new(N2, Members);
        RaftNode<string> follower3 = new(N3, Members);

        RequestVoteRequest campaign = leader.StartElection();
        leader.ReceiveVote(N2, follower.HandleRequestVote(campaign));
        leader.ReceiveVote(N3, follower3.HandleRequestVote(campaign));
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        leader.Propose("set-a");
        leader.Propose("set-b");
        AppendEntriesReply replicated = follower.HandleAppendEntries(leader.CreateAppendEntries(N2));
        Assert.IsTrue(replicated.Success);

        RaftNodeState<string> snapshot = follower.ToState();

        //The snapshot is the durable triple verbatim: the term it has adopted, the vote it cast this term,
        //and the replicated log.
        Assert.AreEqual(follower.CurrentTerm, snapshot.CurrentTerm);
        Assert.HasCount(ReplicaId.Size, snapshot.VotedFor);
        Assert.AreEqual(follower.VotedFor!.Value, ReplicaId.FromSpan(snapshot.VotedFor.AsSpan()));
        Assert.HasCount(follower.Log.Count, snapshot.Log);
        Assert.AreEqual("set-b", snapshot.Log[1].Command);

        RaftNode<string> restored = RaftNode<string>.FromState(N2, Members, snapshot);

        //The restored node carries the same durable triple yet is a fresh follower with a volatile commit
        //index rediscovered from zero and no known leader (Figure 2: commit index is not durable).
        RaftNodeState<string> restoredState = restored.ToState();
        Assert.AreEqual(snapshot.CurrentTerm, restoredState.CurrentTerm);
        Assert.AreSequenceEqual(snapshot.VotedFor.ToArray(), restoredState.VotedFor.ToArray());
        Assert.HasCount(snapshot.Log.Length, restoredState.Log);
        Assert.AreEqual(RaftRole.Follower, restored.Role);
        Assert.AreEqual(LogIndex.BeforeFirst, restored.CommitIndex);
        Assert.IsNull(restored.LeaderId);
        Assert.AreSequenceEqual(Members.ToArray(), restored.Members.ToArray());
    }


    [TestMethod]
    public void FromStateAcceptsAnEmptyVotedForAsNoVote()
    {
        //An empty VotedFor is the documented "no vote yet" encoding, the LwwRegisterState.Writer precedent.
        RaftNodeState<string> state = new(new Term(2), [], [new RaftLogEntry<string>(Term.First, "x")]);

        RaftNode<string> restored = RaftNode<string>.FromState(N1, Members, state);

        Assert.IsNull(restored.VotedFor);
        Assert.AreEqual(new Term(2), restored.CurrentTerm);
    }


    [TestMethod]
    public void ADurableTripleCannotExpressANegativeCurrentTerm()
    {
        //A term below zero is impossible: StartElection only ever increments from zero. The rejection belongs
        //to the type rather than to FromState, so the state cannot be built to be refused.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RaftNodeState<string>(new Term(-1), [], []));
    }


    [TestMethod]
    public void FromStateRejectsAVotedForOfWrongLength()
    {
        //VotedFor must be either empty (no vote) or exactly one replica id wide; a stray short byte string is
        //neither.
        RaftNodeState<string> state = new(Term.First, [0x01, 0x02], []);

        StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => RaftNode<string>.FromState(N1, Members, state));

        Assert.AreEqual(StateRestoreRefusal.RaftVoteMalformed, refused.Refusal);
    }


    [TestMethod]
    public void FromStateRejectsAVotedForThatIsNotAMember()
    {
        //A vote can only ever be cast for a cluster member; a non-member vote could only come from corruption.
        ReplicaId outsider = Replica(9);
        RaftNodeState<string> state = new(Term.First, [.. outsider.AsSpan()], []);

        StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => RaftNode<string>.FromState(N1, Members, state));

        Assert.AreEqual(StateRestoreRefusal.RaftVoteOutsideMembership, refused.Refusal);
    }


    [TestMethod]
    public void ALogEntryCannotExpressATermBelowOne()
    {
        //Entry terms start at one, because only an elected leader creates an entry and term zero is the term
        //a node holds before any election. The rejection belongs to the entry rather than to FromState, so no
        //log carrying one can be assembled to be refused.
        ArgumentOutOfRangeException thrown = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new RaftLogEntry<string>(Term.Zero, "x"));

        Assert.AreEqual("Term", thrown.ParamName);
    }


    [TestMethod]
    public void FromStateRejectsDecreasingLogTerms()
    {
        //Log terms are non-decreasing by construction; a drop means the log was reordered or forged.
        RaftNodeState<string> state = new(new Term(3), [], [new RaftLogEntry<string>(new Term(2), "a"), new RaftLogEntry<string>(Term.First, "b")]);

        StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => RaftNode<string>.FromState(N1, Members, state));

        Assert.AreEqual(StateRestoreRefusal.RaftLogTermsDecrease, refused.Refusal);
    }


    [TestMethod]
    public void FromStateRejectsALastLogTermAboveTheCurrentTerm()
    {
        //No entry can be tagged with a term the node has never reached; CurrentTerm bounds every entry term.
        RaftNodeState<string> state = new(Term.First, [], [new RaftLogEntry<string>(new Term(2), "a")]);

        StateRestoreException refused = Assert.ThrowsExactly<StateRestoreException>(() => RaftNode<string>.FromState(N1, Members, state));

        Assert.AreEqual(StateRestoreRefusal.RaftLastLogTermAboveCurrentTerm, refused.Refusal);
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
