using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class RaftNodeTests
{
    private static ReplicaId N1 { get; } = Replica(1);
    private static ReplicaId N2 { get; } = Replica(2);
    private static ReplicaId N3 { get; } = Replica(3);
    private static ReplicaId N4 { get; } = Replica(4);
    private static ReplicaId N5 { get; } = Replica(5);


    [TestMethod]
    public void HappyPathElectsLeaderReplicatesAndCommitsAcrossCluster()
    {
        //A leader is elected from a clean 3-node cluster, proposes two commands, and replicates them to a
        //majority. CommitIndex must advance first on the leader (the entry's term equals CurrentTerm) and
        //then on the followers once the next AppendEntries carries the advanced LeaderCommit.
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> follower2 = new(N2, members);
        RaftNode<string> follower3 = new(N3, members);

        ElectLeader(leader, follower2, follower3);
        Assert.AreEqual(RaftRole.Leader, leader.Role);
        Assert.AreEqual(1, leader.CurrentTerm);

        leader.Propose("set-a");
        leader.Propose("set-b");
        Assert.HasCount(2, leader.Log);
        Assert.AreEqual(0, leader.CommitIndex);
        //The followers hold nothing yet, so the commit point cannot have moved off zero anywhere.
        Assert.AreEqual(0, follower2.CommitIndex);
        Assert.AreEqual(0, follower3.CommitIndex);

        //First replication round delivers both entries to both followers. Once a majority of matchIndex
        //(the leader itself plus at least one follower) reaches index 2 and that entry is of the current
        //term, the leader's own CommitIndex advances to the majority point.
        ReplicateRound(leader, follower2, follower3);
        Assert.AreEqual(2, leader.CommitIndex);
        Assert.HasCount(2, follower2.Log);
        Assert.HasCount(2, follower3.Log);

        //A second round is a heartbeat carrying the leader's now-advanced LeaderCommit. Each follower sets
        //CommitIndex = min(LeaderCommit, its last matching index), so both reach the leader's commit point.
        ReplicateRound(leader, follower2, follower3);
        Assert.AreEqual(2, follower2.CommitIndex);
        Assert.AreEqual(2, follower3.CommitIndex);

        AssertLogsIdentical(leader, follower2, follower3);
    }


    [TestMethod]
    public void ElectionSafetyHoldsWhenTwoCandidatesSplitTheVote()
    {
        //Two candidates contend in the same term and the vote splits so NEITHER reaches the 5-node majority
        //of 3. A collects its self-vote plus C's; B collects its self-vote plus D's — two each. The fifth
        //node E does cast a grant, but that reply is "lost" (never delivered to the candidate), modelling the
        //timeout-then-retry that resolves a split. The contested term therefore yields no leader at all, and
        //a fresh, higher term elects exactly one. Throughout we track every (term, leader) pair observed.
        ImmutableArray<ReplicaId> members = [N1, N2, N3, N4, N5];
        RaftNode<string> a = new(N1, members);
        RaftNode<string> b = new(N2, members);
        RaftNode<string> c = new(N3, members);
        RaftNode<string> d = new(N4, members);
        RaftNode<string> e = new(N5, members);

        List<(long Term, ReplicaId Leader)> leadersSeen = [];

        //Both A and B campaign for the same term. ReceiveVote must return false while below majority and the
        //candidate must stay a Candidate — never a Leader — for the contested term.
        RequestVoteRequest aVote = a.StartElection();
        RequestVoteRequest bVote = b.StartElection();
        long contestedTerm = aVote.Term;
        Assert.AreEqual(contestedTerm, bVote.Term);

        //C grants A, D grants B; E grants A but its reply is dropped on the wire. No candidate is delivered a
        //third vote, so neither completes a majority.
        Assert.IsTrue(c.HandleRequestVote(aVote).VoteGranted);
        Assert.IsTrue(d.HandleRequestVote(bVote).VoteGranted);
        Assert.IsTrue(e.HandleRequestVote(aVote).VoteGranted);

        bool aWonOnC = a.ReceiveVote(N3, new RequestVoteReply(c.CurrentTerm, true));
        bool bWonOnD = b.ReceiveVote(N4, new RequestVoteReply(d.CurrentTerm, true));
        Assert.IsFalse(aWonOnC);
        Assert.IsFalse(bWonOnD);
        Assert.AreEqual(RaftRole.Candidate, a.Role);
        Assert.AreEqual(RaftRole.Candidate, b.Role);

        //Snapshot leadership for the contested term: no node may be a Leader of it.
        foreach((RaftNode<string> node, ReplicaId id) in new[] { (a, N1), (b, N2), (c, N3), (d, N4), (e, N5) })
        {
            if(node.Role == RaftRole.Leader)
            {
                leadersSeen.Add((node.CurrentTerm, id));
            }
        }

        //A higher term breaks the tie. B re-campaigns; every peer adopts the higher term and grants because
        //all logs are empty, so B reaches a clean majority and becomes the sole leader of that term.
        RequestVoteRequest bVote2 = b.StartElection();
        Assert.IsGreaterThan(contestedTerm, bVote2.Term);
        foreach((RaftNode<string> node, ReplicaId id) in new[] { (a, N1), (c, N3), (d, N4), (e, N5) })
        {
            RequestVoteReply reply = node.HandleRequestVote(bVote2);
            b.ReceiveVote(id, reply);
        }

        Assert.AreEqual(RaftRole.Leader, b.Role);
        Assert.AreEqual(RaftRole.Follower, a.Role);
        leadersSeen.Add((b.CurrentTerm, b.Id));

        //The safety property: never two distinct leaders in one term, across the whole run. The contested
        //term contributed zero leaders; the resolving term contributed exactly one.
        Assert.HasCount(0, leadersSeen.Where(p => p.Term == contestedTerm).ToList());
        IEnumerable<IGrouping<long, ReplicaId>> byTerm = leadersSeen.GroupBy(p => p.Term, p => p.Leader);
        foreach(IGrouping<long, ReplicaId> term in byTerm)
        {
            Assert.HasCount(1, term.Distinct().ToList());
        }
    }


    [TestMethod]
    public void VoteIsDeniedToCandidateWithStaleLog()
    {
        //A voter that already holds a freshly-committed entry must refuse a candidate whose log is behind it,
        //covering both up-to-dateness branches: a lower LastLogTerm, and an equal LastLogTerm with a shorter
        //LastLogIndex.
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> voter = new(N1, members);

        //Give the voter a log of [term1, term2] by replicating from a synthetic leader of term 2.
        AppendEntriesReply r1 = voter.HandleAppendEntries(new AppendEntriesRequest<string>(
            2, N2, 0, 0, [new RaftLogEntry<string>(1, "x"), new RaftLogEntry<string>(2, "y")], 0));
        Assert.IsTrue(r1.Success);
        Assert.HasCount(2, voter.Log);
        Assert.AreEqual(2, voter.CurrentTerm);

        //Branch 1: candidate's LastLogTerm (1) is strictly lower than the voter's last term (2) → denied,
        //even though the candidate claims a longer index.
        RequestVoteReply lowerTerm = voter.HandleRequestVote(new RequestVoteRequest(3, N3, 9, 1));
        Assert.IsFalse(lowerTerm.VoteGranted);

        //Branch 2: equal LastLogTerm (2) but a strictly shorter LastLogIndex (1 < voter's 2) → denied.
        RequestVoteReply shorterIndex = voter.HandleRequestVote(new RequestVoteRequest(4, N3, 1, 2));
        Assert.IsFalse(shorterIndex.VoteGranted);

        //Control: an equal-or-better log (same term, index >= ours) is granted, proving the denials above
        //were the up-to-dateness rule and not a blanket refusal.
        RequestVoteReply upToDate = voter.HandleRequestVote(new RequestVoteRequest(5, N3, 2, 2));
        Assert.IsTrue(upToDate.VoteGranted);
    }


    [TestMethod]
    public void Figure8RuleForbidsCommittingPriorTermEntryByCountAlone()
    {
        //The famous Figure 8 hazard. An original leader (S1) writes an entry at index 2 to a minority, then
        //crashes. A different node wins a higher term and writes its OWN competing entry at index 2 locally,
        //then crashes. S1 returns as leader of a still-higher term, finds its old index-2 entry — whose term
        //is strictly below S1's new current term — and re-replicates it to a MAJORITY. The naive "majority of
        //matchIndex" test would commit it, but that entry could still have been overwritten by the competing
        //line, so Raft forbids committing an entry from a PRIOR term by replica count alone. Commitment of
        //index 2 must wait until a current-term entry above it also reaches a majority, which then carries
        //index 2 with it. The exact term numbers here are 1/2/3 (StartElection increments from the node's own
        //term); only their ordering matters — index 2's term stays strictly below S1's final term throughout.
        ImmutableArray<ReplicaId> members = [N1, N2, N3, N4, N5];
        RaftNode<string> s1 = new(N1, members);
        RaftNode<string> s2 = new(N2, members);
        RaftNode<string> s3 = new(N3, members);
        RaftNode<string> s4 = new(N4, members);
        RaftNode<string> s5 = new(N5, members);

        //--- S1's term: S1 leads, and commits a first entry everywhere so every log shares index 1. ---
        ElectLeader(s1, s2, s3, s4, s5);
        Assert.AreEqual(RaftRole.Leader, s1.Role);
        s1.Propose("e1");
        ReplicateRound(s1, s2, s3, s4, s5);
        ReplicateRound(s1, s2, s3, s4, s5);
        foreach(RaftNode<string> n in new[] { s1, s2, s3, s4, s5 })
        {
            Assert.AreEqual(1, n.CommitIndex);
        }

        //--- Still S1's term: it proposes a second entry at index 2 but reaches only a minority (S2). ---
        s1.Propose("e2-original-leader");
        long s1ProposalTerm = s1.CurrentTerm;
        AppendEntriesRequest<string> toS2 = s1.CreateAppendEntries(N2);
        AppendEntriesReply s2Reply = s2.HandleAppendEntries(toS2);
        Assert.IsTrue(s2Reply.Success);
        s1.ReceiveAppendEntriesReply(N2, s2Reply);

        //Only S1 and S2 (a 2-of-5 minority) hold index 2, so it is NOT committed despite being current-term.
        Assert.AreEqual(1, s1.CommitIndex);
        Assert.HasCount(2, s1.Log);
        Assert.HasCount(2, s2.Log);

        //--- A different node (S3) wins a higher term. Its log has only index 1, which is up-to-date enough to
        //win votes from S4/S5, neither of which ever saw S1's minority index-2 entry. ---
        RequestVoteRequest s3Vote = s3.StartElection();
        Assert.IsGreaterThan(s1ProposalTerm, s3Vote.Term);
        foreach((RaftNode<string> node, ReplicaId id) in new[] { (s4, N4), (s5, N5) })
        {
            RequestVoteReply reply = node.HandleRequestVote(s3Vote);
            Assert.IsTrue(reply.VoteGranted);
            s3.ReceiveVote(id, reply);
        }

        Assert.AreEqual(RaftRole.Leader, s3.Role);
        long competingTerm = s3.CurrentTerm;
        Assert.IsGreaterThan(s1ProposalTerm, competingTerm);

        //S3 appends its OWN competing entry at index 2 locally only (it crashes before replicating it).
        s3.Propose("e2-competing-leader");
        Assert.HasCount(2, s3.Log);
        Assert.AreEqual(competingTerm, s3.Log[1].Term);

        //--- S1 returns and wins a still-higher term. To make its campaign strictly outrank the dead competing
        //line deterministically, S1 first adopts the competing term (a higher-term RPC forces term adoption and
        //a step-down to follower), then campaigns, incrementing past it. S4/S5 still hold only index 1, so S1's
        //index-2 entry is "at least as up to date" by term (its LastLogTerm >= their last term), granting votes. ---
        s1.HandleRequestVote(new RequestVoteRequest(competingTerm, N5, 0, 0));
        Assert.AreEqual(competingTerm, s1.CurrentTerm);
        RequestVoteRequest s1Vote = s1.StartElection();
        Assert.IsGreaterThan(competingTerm, s1Vote.Term);
        foreach((RaftNode<string> node, ReplicaId id) in new[] { (s2, N2), (s4, N4), (s5, N5) })
        {
            RequestVoteReply reply = node.HandleRequestVote(s1Vote);
            Assert.IsTrue(reply.VoteGranted);
            s1.ReceiveVote(id, reply);
        }

        Assert.AreEqual(RaftRole.Leader, s1.Role);
        long currentTerm = s1.CurrentTerm;

        //S1's log is still [e1, e2-original-leader]; index 2 carries its OLD (prior) term, not the current term.
        Assert.HasCount(2, s1.Log);
        Assert.IsLessThan(currentTerm, s1.Log[1].Term);

        //Re-replicate that prior-term index-2 entry to a true majority (S1 + S3 + S4 + S5). S3 must truncate
        //its competing index-2 entry and adopt S1's, which is exactly what makes a count-only commit unsafe.
        foreach((RaftNode<string> node, ReplicaId id) in new[] { (s3, N3), (s4, N4), (s5, N5) })
        {
            DeliverUntilCaughtUp(s1, node, id);
        }

        //A majority (S1, S3, S4, S5) now holds index 2 — confirmed by each follower reporting MatchIndex >= 2.
        //But the Figure 8 rule forbids committing it, because the entry's term differs from the leader's
        //current term. CommitIndex must stay at 1 despite the replica count.
        Assert.HasCount(2, s3.Log);
        Assert.HasCount(2, s4.Log);
        Assert.HasCount(2, s5.Log);
        Assert.AreEqual(1, s1.CommitIndex);

        //Now S1 proposes a CURRENT-term entry at index 3 and replicates it to a majority. Committing index 3
        //(current term, majority) lawfully carries index 2 with it, so BOTH commit together.
        s1.Propose("e3-current-term");
        Assert.HasCount(3, s1.Log);
        Assert.AreEqual(currentTerm, s1.Log[2].Term);

        foreach((RaftNode<string> node, ReplicaId id) in new[] { (s3, N3), (s4, N4), (s5, N5) })
        {
            DeliverUntilCaughtUp(s1, node, id);
        }

        //A current-term entry reached a majority, so the commit point advances all the way to index 3,
        //sweeping the previously-uncommittable index-2 entry into the committed prefix.
        Assert.AreEqual(3, s1.CommitIndex);
    }


    [TestMethod]
    public void ConflictingSuffixTruncatesWhileMatchingDuplicateDoesNot()
    {
        //Two faces of the same consistency check. A follower whose uncommitted suffix diverges from the
        //leader's must truncate the bad tail and adopt the leader's entries. A follower receiving a stale
        //duplicate that already matches its prefix must be a no-op — never truncating committed/matching
        //state under re-delivery or reordering.
        ImmutableArray<ReplicaId> members = [N1, N2, N3];

        //--- Truncation path. The follower has [t1, t2(bad)]; the leader of term 3 carries [t1, t3(good)]. ---
        RaftNode<string> diverged = new(N2, members);
        AppendEntriesReply seed = diverged.HandleAppendEntries(new AppendEntriesRequest<string>(
            2, N1, 0, 0, [new RaftLogEntry<string>(1, "shared"), new RaftLogEntry<string>(2, "stale-suffix")], 0));
        Assert.IsTrue(seed.Success);
        Assert.HasCount(2, diverged.Log);

        //The leader's request matches at index 1 (term 1) but conflicts at index 2 (term 3 vs the held term 2),
        //so the follower truncates index 2 and adopts the new entry.
        AppendEntriesReply healed = diverged.HandleAppendEntries(new AppendEntriesRequest<string>(
            3, N1, 1, 1, [new RaftLogEntry<string>(3, "authoritative")], 0));
        Assert.IsTrue(healed.Success);
        Assert.HasCount(2, diverged.Log);
        Assert.AreEqual(3, diverged.Log[1].Term);
        Assert.AreEqual("authoritative", diverged.Log[1].Command);

        //--- Idempotency path. A follower already holding [t1, t2] re-receives the exact same request. ---
        RaftNode<string> stable = new(N3, members);
        AppendEntriesRequest<string> original = new(
            2, N1, 0, 0, [new RaftLogEntry<string>(1, "a"), new RaftLogEntry<string>(2, "b")], 2);
        Assert.IsTrue(stable.HandleAppendEntries(original).Success);
        Assert.HasCount(2, stable.Log);
        Assert.AreEqual(2, stable.CommitIndex);

        //Re-delivering the identical request (a network duplicate) must succeed without truncating: the
        //prefix already matches, so the log and commit index are unchanged.
        AppendEntriesReply duplicate = stable.HandleAppendEntries(original);
        Assert.IsTrue(duplicate.Success);
        Assert.HasCount(2, stable.Log);
        Assert.AreEqual("b", stable.Log[1].Command);
        Assert.AreEqual(2, stable.CommitIndex);

        //A stale-prefix request (only the first entry, already present and matching) is likewise a no-op that
        //must not chop off the second, more recent entry the follower correctly holds.
        AppendEntriesReply stalePrefix = stable.HandleAppendEntries(new AppendEntriesRequest<string>(
            2, N1, 0, 0, [new RaftLogEntry<string>(1, "a")], 1));
        Assert.IsTrue(stalePrefix.Success);
        Assert.HasCount(2, stable.Log);
        Assert.AreEqual("b", stable.Log[1].Command);
    }


    [TestMethod]
    public void TermHandlingRejectsStaleRpcsAndStepsDownOnHigherTerm()
    {
        //Term discipline across the three RPC surfaces: stale requests are rejected with the receiver's term,
        //a higher-term reply demotes a leader, and a candidate yields to a valid current-term AppendEntries.
        ImmutableArray<ReplicaId> members = [N1, N2, N3];

        //A node advanced to term 5 must reject lower-term AppendEntries and RequestVote, reporting term 5.
        RaftNode<string> advanced = new(N1, members);
        advanced.HandleRequestVote(new RequestVoteRequest(5, N2, 0, 0));
        Assert.AreEqual(5, advanced.CurrentTerm);

        AppendEntriesReply staleAppend = advanced.HandleAppendEntries(new AppendEntriesRequest<string>(
            4, N2, 0, 0, [], 0));
        Assert.IsFalse(staleAppend.Success);
        Assert.AreEqual(5, staleAppend.Term);

        RequestVoteReply staleVote = advanced.HandleRequestVote(new RequestVoteRequest(4, N3, 0, 0));
        Assert.IsFalse(staleVote.VoteGranted);
        Assert.AreEqual(5, staleVote.Term);

        //A leader that hears a higher term in a reply must step down to follower and adopt that term.
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> peerB = new(N2, members);
        RaftNode<string> peerC = new(N3, members);
        ElectLeader(leader, peerB, peerC);
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        long higher = leader.CurrentTerm + 7;
        leader.ReceiveAppendEntriesReply(N2, new AppendEntriesReply(higher, false, 0));
        Assert.AreEqual(RaftRole.Follower, leader.Role);
        Assert.AreEqual(higher, leader.CurrentTerm);

        //A candidate that receives a valid AppendEntries at its own current term recognizes an established
        //leader and steps down to follower.
        RaftNode<string> candidate = new(N2, members);
        RequestVoteRequest campaign = candidate.StartElection();
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);

        AppendEntriesReply yielded = candidate.HandleAppendEntries(new AppendEntriesRequest<string>(
            campaign.Term, N1, 0, 0, [], 0));
        Assert.IsTrue(yielded.Success);
        Assert.AreEqual(RaftRole.Follower, candidate.Role);
        Assert.AreEqual(N1, candidate.LeaderId);
    }


    [TestMethod]
    public void CommittedPrefixesAgreeAcrossAllReplicas()
    {
        //State-machine safety: after a hand-built exchange, applying each replica's committed prefix yields
        //the same command sequence. No two replicas may disagree on any committed index.
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> follower2 = new(N2, members);
        RaftNode<string> follower3 = new(N3, members);

        ElectLeader(leader, follower2, follower3);
        leader.Propose("alpha");
        leader.Propose("beta");
        leader.Propose("gamma");

        //Several replication rounds drive both the leader's and the followers' commit indices forward.
        ReplicateRound(leader, follower2, follower3);
        ReplicateRound(leader, follower2, follower3);
        ReplicateRound(leader, follower2, follower3);

        Assert.AreEqual(3, leader.CommitIndex);
        Assert.AreEqual(3, follower2.CommitIndex);
        Assert.AreEqual(3, follower3.CommitIndex);

        //The committed prefix of every replica must be the identical command sequence.
        List<string> leaderApplied = CommittedCommands(leader);
        List<string> follower2Applied = CommittedCommands(follower2);
        List<string> follower3Applied = CommittedCommands(follower3);

        Assert.AreSequenceEqual(leaderApplied, follower2Applied);
        Assert.AreSequenceEqual(leaderApplied, follower3Applied);
        string[] expected = ["alpha", "beta", "gamma"];
        Assert.AreSequenceEqual(expected, leaderApplied);
    }


    [TestMethod]
    public void ConstructorRejectsInvalidMembershipAndNonLeaderOperationsThrow()
    {
        //Argument validation and role guards. Membership must be non-empty and contain the node's own id;
        //Propose and CreateAppendEntries are leader-only operations.
        ImmutableArray<ReplicaId> empty = [];
        Assert.ThrowsExactly<ArgumentException>(() => new RaftNode<string>(N1, empty));

        //Membership that excludes the node's own id is invalid.
        ImmutableArray<ReplicaId> without = [N2, N3];
        Assert.ThrowsExactly<ArgumentException>(() => new RaftNode<string>(N1, without));

        //A freshly-constructed follower cannot propose or fabricate AppendEntries.
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> follower = new(N1, members);
        Assert.AreEqual(RaftRole.Follower, follower.Role);
        Assert.ThrowsExactly<InvalidOperationException>(() => follower.Propose("x"));
        Assert.ThrowsExactly<InvalidOperationException>(() => follower.CreateAppendEntries(N2));

        //A candidate (mid-election, not yet leader) is likewise barred from leader-only operations.
        RaftNode<string> candidate = new(N1, members);
        candidate.StartElection();
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);
        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.Propose("x"));
        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.CreateAppendEntries(N2));
    }


    //--- Helpers --------------------------------------------------------------------------------------------

    //Runs one full election: the leader campaigns and every listed peer that grants its vote is counted,
    //which (for a clean cluster) carries the candidate to a majority and the leader role.
    private static void ElectLeader(RaftNode<string> leader, params RaftNode<string>[] peers)
    {
        RequestVoteRequest request = leader.StartElection();
        foreach(RaftNode<string> peer in peers)
        {
            RequestVoteReply reply = peer.HandleRequestVote(request);
            leader.ReceiveVote(peer.Id, reply);
        }
    }


    //One replication round: the leader sends each follower its tailored AppendEntries and folds the reply
    //back so nextIndex/matchIndex and the leader's CommitIndex advance.
    private static void ReplicateRound(RaftNode<string> leader, params RaftNode<string>[] followers)
    {
        foreach(RaftNode<string> follower in followers)
        {
            AppendEntriesRequest<string> request = leader.CreateAppendEntries(follower.Id);
            AppendEntriesReply reply = follower.HandleAppendEntries(request);
            leader.ReceiveAppendEntriesReply(follower.Id, reply);
        }
    }


    //Repeatedly sends AppendEntries to one follower until it accepts, draining any nextIndex back-off the
    //leader needs to find the follower's matching prefix. Bounded to keep a buggy node from looping forever.
    private static void DeliverUntilCaughtUp(RaftNode<string> leader, RaftNode<string> follower, ReplicaId followerId)
    {
        for(int attempt = 0; attempt < 64; attempt++)
        {
            AppendEntriesRequest<string> request = leader.CreateAppendEntries(followerId);
            AppendEntriesReply reply = follower.HandleAppendEntries(request);
            leader.ReceiveAppendEntriesReply(followerId, reply);
            if(reply.Success && reply.MatchIndex >= leader.Log.Count)
            {
                return;
            }
        }
    }


    private static void AssertLogsIdentical(params RaftNode<string>[] nodes)
    {
        RaftNode<string> reference = nodes[0];
        foreach(RaftNode<string> node in nodes)
        {
            Assert.HasCount(reference.Log.Count, node.Log);
            for(int i = 0; i < reference.Log.Count; i++)
            {
                Assert.AreEqual(reference.Log[i].Term, node.Log[i].Term);
                Assert.AreEqual(reference.Log[i].Command, node.Log[i].Command);
            }
        }
    }


    private static List<string> CommittedCommands(RaftNode<string> node)
    {
        List<string> commands = [];
        for(int i = 0; i < node.CommitIndex; i++)
        {
            commands.Add(node.Log[i].Command);
        }

        return commands;
    }


    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
