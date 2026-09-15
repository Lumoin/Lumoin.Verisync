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
    private static ReplicaId Stranger { get; } = Replica(9);


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
        Assert.AreEqual(Term.First, leader.CurrentTerm);

        leader.Propose("set-a");
        leader.Propose("set-b");
        Assert.HasCount(2, leader.Log);
        Assert.AreEqual(LogIndex.BeforeFirst, leader.CommitIndex);
        //The followers hold nothing yet, so the commit point cannot have moved off zero anywhere.
        Assert.AreEqual(LogIndex.BeforeFirst, follower2.CommitIndex);
        Assert.AreEqual(LogIndex.BeforeFirst, follower3.CommitIndex);

        //First replication round delivers both entries to both followers. Once a majority of matchIndex
        //(the leader itself plus at least one follower) reaches index 2 and that entry is of the current
        //term, the leader's own CommitIndex advances to the majority point.
        ReplicateRound(leader, follower2, follower3);
        Assert.AreEqual(new LogIndex(2), leader.CommitIndex);
        Assert.HasCount(2, follower2.Log);
        Assert.HasCount(2, follower3.Log);

        //A second round is a heartbeat carrying the leader's now-advanced LeaderCommit. Each follower sets
        //CommitIndex = min(LeaderCommit, its last matching index), so both reach the leader's commit point.
        ReplicateRound(leader, follower2, follower3);
        Assert.AreEqual(new LogIndex(2), follower2.CommitIndex);
        Assert.AreEqual(new LogIndex(2), follower3.CommitIndex);

        AssertLogsIdentical(leader, follower2, follower3);
    }


    /// <summary>
    /// The quorum is a majority of the configured membership, so only a member can contribute to it.
    /// </summary>
    /// <remarks>
    /// The identity arrives as wire data that no codec checks, and the same membership filter is already
    /// applied when a vote is restored and when a commit quorum is counted, so the election tally was the one
    /// count that omitted it. A candidate one short of a majority must not be carried over the line by a
    /// stranger.
    /// </remarks>
    [TestMethod]
    public void AVoteFromOutsideTheMembershipDoesNotCountTowardsTheElectionQuorum()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3, N4, N5];
        RaftNode<string> candidate = new(N1, members);
        RaftNode<string> member = new(N2, members);

        RequestVoteRequest request = candidate.StartElection();

        //Two of five: the candidate's own vote and one member's. A majority here is three.
        Assert.IsFalse(candidate.ReceiveVote(N2, member.HandleRequestVote(request)));
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);

        RequestVoteReply granted = new(request.Term, true);

        Assert.IsFalse(candidate.ReceiveVote(Stranger, granted), "A non-member completed the election quorum.");
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);

        //The same reply from a member does complete it, so the vector fails for membership rather than
        //because the reply itself was malformed.
        Assert.IsTrue(candidate.ReceiveVote(N3, granted));
        Assert.AreEqual(RaftRole.Leader, candidate.Role);
    }


    /// <summary>
    /// Membership is fixed, so every identity that arrives from the wire is filtered against it.
    /// </summary>
    /// <remarks>
    /// A vote for a non-member is the sharpest case: FromState refuses to restore one, so granting it would
    /// put the node in a state its own restore path rejects. Entries from a non-member leader would replicate
    /// a log no quorum agreed on, and a non-member reply would seed per-follower bookkeeping the leader never
    /// sends to.
    /// </remarks>
    [TestMethod]
    public void EveryWireIdentityIsFilteredAgainstTheMembership()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> node = new(N1, members);

        RequestVoteReply refusedVote = node.HandleRequestVote(new RequestVoteRequest(Term.First, Stranger, LogIndex.BeforeFirst, Term.Zero));

        Assert.IsFalse(refusedVote.VoteGranted, "A non-member was granted a vote.");
        Assert.IsNull(node.VotedFor);

        //The same request from a member is granted, so the refusal is about membership and not the log rule.
        Assert.IsTrue(node.HandleRequestVote(new RequestVoteRequest(Term.First, N2, LogIndex.BeforeFirst, Term.Zero)).VoteGranted);
        Assert.AreEqual(N2, node.VotedFor);

        RaftNode<string> follower = new(N1, members);
        AppendEntriesReply refusedAppend = follower.HandleAppendEntries(
            new AppendEntriesRequest<string>(Term.First, Stranger, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(Term.First, "x")], LogIndex.BeforeFirst));

        Assert.IsFalse(refusedAppend.Success, "A non-member leader appended entries.");
        Assert.IsEmpty(follower.Log);
        Assert.IsNull(follower.LeaderId);
    }


    /// <summary>
    /// The filter runs before the term rule, so a non-member cannot raise this node's term either.
    /// </summary>
    /// <remarks>
    /// It is the weaker lever and the one a tally-only filter leaves open: a stranger that can never complete
    /// a quorum could still unseat a leader the cluster agreed on by making every member adopt a higher term.
    /// </remarks>
    [TestMethod]
    public void ANonMemberCannotRaiseTheTerm()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);

        _ = leader.StartElection();
        _ = leader.ReceiveVote(N2, new RequestVoteReply(leader.CurrentTerm, true));

        Assert.AreEqual(RaftRole.Leader, leader.Role);

        Term term = leader.CurrentTerm;
        Term higher = new(term.Value + 5);

        _ = leader.HandleRequestVote(new RequestVoteRequest(higher, Stranger, LogIndex.BeforeFirst, Term.Zero));
        _ = leader.HandleAppendEntries(new AppendEntriesRequest<string>(higher, Stranger, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst));
        _ = leader.ReceiveVote(Stranger, new RequestVoteReply(higher, false));
        leader.ReceiveAppendEntriesReply(Stranger, new AppendEntriesReply(higher, false, LogIndex.BeforeFirst));

        Assert.AreEqual(term, leader.CurrentTerm, "A non-member raised the term.");
        Assert.AreEqual(RaftRole.Leader, leader.Role, "A non-member unseated the leader.");

        //The same higher term from a member does step the leader down, so the refusal is about membership.
        _ = leader.HandleAppendEntries(new AppendEntriesRequest<string>(higher, N2, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst));

        Assert.AreEqual(higher, leader.CurrentTerm);
        Assert.AreEqual(RaftRole.Follower, leader.Role);
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

        List<(Term Term, ReplicaId Leader)> leadersSeen = [];

        //Both A and B campaign for the same term. ReceiveVote must return false while below majority and the
        //candidate must stay a Candidate — never a Leader — for the contested term.
        RequestVoteRequest aVote = a.StartElection();
        RequestVoteRequest bVote = b.StartElection();
        Term contestedTerm = aVote.Term;
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
        Assert.IsEmpty(leadersSeen.Where(p => p.Term == contestedTerm).ToList());
        IEnumerable<IGrouping<Term, ReplicaId>> byTerm = leadersSeen.GroupBy(p => p.Term, p => p.Leader);
        foreach(IGrouping<Term, ReplicaId> term in byTerm)
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
            new Term(2), N2, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(new Term(1), "x"), new RaftLogEntry<string>(new Term(2), "y")], LogIndex.BeforeFirst));
        Assert.IsTrue(r1.Success);
        Assert.HasCount(2, voter.Log);
        Assert.AreEqual(new Term(2), voter.CurrentTerm);

        //Branch 1: candidate's LastLogTerm (1) is strictly lower than the voter's last term (2) → denied,
        //even though the candidate claims a longer index.
        RequestVoteReply lowerTerm = voter.HandleRequestVote(new RequestVoteRequest(new Term(3), N3, new LogIndex(9), new Term(1)));
        Assert.IsFalse(lowerTerm.VoteGranted);

        //Branch 2: equal LastLogTerm (2) but a strictly shorter LastLogIndex (1 < voter's 2) → denied.
        RequestVoteReply shorterIndex = voter.HandleRequestVote(new RequestVoteRequest(new Term(4), N3, new LogIndex(1), new Term(2)));
        Assert.IsFalse(shorterIndex.VoteGranted);

        //Control: an equal-or-better log (same term, index >= ours) is granted, proving the denials above
        //were the up-to-dateness rule and not a blanket refusal.
        RequestVoteReply upToDate = voter.HandleRequestVote(new RequestVoteRequest(new Term(5), N3, new LogIndex(2), new Term(2)));
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
            Assert.AreEqual(LogIndex.First, n.CommitIndex);
        }

        //--- Still S1's term: it proposes a second entry at index 2 but reaches only a minority (S2). ---
        s1.Propose("e2-original-leader");
        Term s1ProposalTerm = s1.CurrentTerm;
        AppendEntriesRequest<string> toS2 = s1.CreateAppendEntries(N2);
        AppendEntriesReply s2Reply = s2.HandleAppendEntries(toS2);
        Assert.IsTrue(s2Reply.Success);
        s1.ReceiveAppendEntriesReply(N2, s2Reply);

        //Only S1 and S2 (a 2-of-5 minority) hold index 2, so it is NOT committed despite being current-term.
        Assert.AreEqual(LogIndex.First, s1.CommitIndex);
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
        Term competingTerm = s3.CurrentTerm;
        Assert.IsGreaterThan(s1ProposalTerm, competingTerm);

        //S3 appends its OWN competing entry at index 2 locally only (it crashes before replicating it).
        s3.Propose("e2-competing-leader");
        Assert.HasCount(2, s3.Log);
        Assert.AreEqual(competingTerm, s3.Log[1].Term);

        //--- S1 returns and wins a still-higher term. To make its campaign strictly outrank the dead competing
        //line deterministically, S1 first adopts the competing term (a higher-term RPC forces term adoption and
        //a step-down to follower), then campaigns, incrementing past it. S4/S5 still hold only index 1, so S1's
        //index-2 entry is "at least as up to date" by term (its LastLogTerm >= their last term), granting votes. ---
        s1.HandleRequestVote(new RequestVoteRequest(competingTerm, N5, LogIndex.BeforeFirst, Term.Zero));
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
        Term currentTerm = s1.CurrentTerm;

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
        Assert.AreEqual(LogIndex.First, s1.CommitIndex);

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
        Assert.AreEqual(new LogIndex(3), s1.CommitIndex);
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
            new Term(2), N1, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(new Term(1), "shared"), new RaftLogEntry<string>(new Term(2), "stale-suffix")], LogIndex.BeforeFirst));
        Assert.IsTrue(seed.Success);
        Assert.HasCount(2, diverged.Log);

        //The leader's request matches at index 1 (term 1) but conflicts at index 2 (term 3 vs the held term 2),
        //so the follower truncates index 2 and adopts the new entry.
        AppendEntriesReply healed = diverged.HandleAppendEntries(new AppendEntriesRequest<string>(
            new Term(3), N1, LogIndex.First, Term.First, [new RaftLogEntry<string>(new Term(3), "authoritative")], LogIndex.BeforeFirst));
        Assert.IsTrue(healed.Success);
        Assert.HasCount(2, diverged.Log);
        Assert.AreEqual(new Term(3), diverged.Log[1].Term);
        Assert.AreEqual("authoritative", diverged.Log[1].Command);

        //--- Idempotency path. A follower already holding [t1, t2] re-receives the exact same request. ---
        RaftNode<string> stable = new(N3, members);
        AppendEntriesRequest<string> original = new(
            new Term(2), N1, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(new Term(1), "a"), new RaftLogEntry<string>(new Term(2), "b")], new LogIndex(2));
        Assert.IsTrue(stable.HandleAppendEntries(original).Success);
        Assert.HasCount(2, stable.Log);
        Assert.AreEqual(new LogIndex(2), stable.CommitIndex);

        //Re-delivering the identical request (a network duplicate) must succeed without truncating: the
        //prefix already matches, so the log and commit index are unchanged.
        AppendEntriesReply duplicate = stable.HandleAppendEntries(original);
        Assert.IsTrue(duplicate.Success);
        Assert.HasCount(2, stable.Log);
        Assert.AreEqual("b", stable.Log[1].Command);
        Assert.AreEqual(new LogIndex(2), stable.CommitIndex);

        //A stale-prefix request (only the first entry, already present and matching) is likewise a no-op that
        //must not chop off the second, more recent entry the follower correctly holds.
        AppendEntriesReply stalePrefix = stable.HandleAppendEntries(new AppendEntriesRequest<string>(
            new Term(2), N1, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(new Term(1), "a")], LogIndex.First));
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
        advanced.HandleRequestVote(new RequestVoteRequest(new Term(5), N2, LogIndex.BeforeFirst, Term.Zero));
        Assert.AreEqual(new Term(5), advanced.CurrentTerm);

        AppendEntriesReply staleAppend = advanced.HandleAppendEntries(new AppendEntriesRequest<string>(
            new Term(4), N2, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst));
        Assert.IsFalse(staleAppend.Success);
        Assert.AreEqual(new Term(5), staleAppend.Term);

        RequestVoteReply staleVote = advanced.HandleRequestVote(new RequestVoteRequest(new Term(4), N3, LogIndex.BeforeFirst, Term.Zero));
        Assert.IsFalse(staleVote.VoteGranted);
        Assert.AreEqual(new Term(5), staleVote.Term);

        //A leader that hears a higher term in a reply must step down to follower and adopt that term.
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> peerB = new(N2, members);
        RaftNode<string> peerC = new(N3, members);
        ElectLeader(leader, peerB, peerC);
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        Term higher = new(leader.CurrentTerm.Value + 7);
        leader.ReceiveAppendEntriesReply(N2, new AppendEntriesReply(higher, false, LogIndex.BeforeFirst));
        Assert.AreEqual(RaftRole.Follower, leader.Role);
        Assert.AreEqual(higher, leader.CurrentTerm);

        //A candidate that receives a valid AppendEntries at its own current term recognizes an established
        //leader and steps down to follower.
        RaftNode<string> candidate = new(N2, members);
        RequestVoteRequest campaign = candidate.StartElection();
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);

        AppendEntriesReply yielded = candidate.HandleAppendEntries(new AppendEntriesRequest<string>(
            campaign.Term, N1, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst));
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

        Assert.AreEqual(new LogIndex(3), leader.CommitIndex);
        Assert.AreEqual(new LogIndex(3), follower2.CommitIndex);
        Assert.AreEqual(new LogIndex(3), follower3.CommitIndex);

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


    /// <summary>
    /// Pins that the leader-role guard in <see cref="RaftNode{TCommand}.CreateAppendEntries(ReplicaId)"/> is
    /// what refuses a non-leader, not a downstream accident: a demoted leader still carries the follower
    /// progress its leadership initialized, so without the guard the call would build and return a heartbeat
    /// instead of throwing. A freshly constructed follower cannot pin this, because its default progress makes
    /// the downstream index arithmetic throw the same exception type the guard uses.
    /// </summary>
    [TestMethod]
    public void CreateAppendEntriesThrowsAfterALeaderStepsDown()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> demoted = new(N1, members);
        RaftNode<string> challenger = new(N2, members);
        RaftNode<string> third = new(N3, members);

        //The node first wins an election, which initializes its per-follower progress.
        ElectLeader(demoted, challenger, third);
        Assert.AreEqual(RaftRole.Leader, demoted.Role);

        //A higher-term vote request demotes it, and stepping down preserves the initialized progress.
        RequestVoteRequest challenge = challenger.StartElection();
        demoted.HandleRequestVote(challenge);
        Assert.AreEqual(RaftRole.Follower, demoted.Role);

        Assert.ThrowsExactly<InvalidOperationException>(() => demoted.CreateAppendEntries(N2));
    }


    /// <summary>LeaderCommit caps how far a follower may advance its own commit index, even when the request
    /// delivers entries beyond that point — the leader itself has not committed them yet.</summary>
    [TestMethod]
    public void CommitIndexNeverAdvancesPastWhatLeaderCommitAllows()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> follower = new(N2, members);

        AppendEntriesReply reply = follower.HandleAppendEntries(new AppendEntriesRequest<string>(
            Term.First, N1, LogIndex.BeforeFirst, Term.Zero,
            [new RaftLogEntry<string>(Term.First, "a"), new RaftLogEntry<string>(Term.First, "b"), new RaftLogEntry<string>(Term.First, "c")],
            LogIndex.First));

        Assert.IsTrue(reply.Success);
        Assert.HasCount(3, follower.Log);
        Assert.AreEqual(LogIndex.First, follower.CommitIndex, "The commit index advanced past what LeaderCommit allowed.");
    }


    /// <summary>Once the higher-term branch above has been passed, only a reply whose term matches
    /// CurrentTerm may update the leader's per-follower bookkeeping; a lower-term reply is stale wire data and
    /// must be ignored no matter the leader's current role.</summary>
    [TestMethod]
    public void AStaleTermReplyDoesNotAdvanceFollowerProgressEvenWhileStillLeader()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> follower2 = new(N2, members);

        ElectLeader(leader, follower2);
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        //The stale reply claims a MatchIndex that nothing in the current term ever sent.
        leader.ReceiveAppendEntriesReply(N2, new AppendEntriesReply(Term.Zero, true, new LogIndex(5)));

        //If the stale reply had been (wrongly) applied, the next request to N2 would probe from index 5; the
        //untouched initial progress instead probes from the empty prefix.
        AppendEntriesRequest<string> request = leader.CreateAppendEntries(N2);
        Assert.AreEqual(LogIndex.BeforeFirst, request.PrevLogIndex, "A stale reply advanced the follower's progress.");
    }


    /// <summary>The universal "greater term seen" rule applies to vote replies too: even though a
    /// higher-term reply grants nothing, the candidate must adopt the higher term and revert to follower.</summary>
    [TestMethod]
    public void AHigherTermVoteReplyForcesAStepDownEvenWhenNotCounted()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> candidate = new(N1, members);

        RequestVoteRequest request = candidate.StartElection();
        Term higher = new(request.Term.Value + 5);

        bool won = candidate.ReceiveVote(N2, new RequestVoteReply(higher, false));

        Assert.IsFalse(won);
        Assert.AreEqual(RaftRole.Follower, candidate.Role, "A higher-term reply did not step the candidate down.");
        Assert.AreEqual(higher, candidate.CurrentTerm, "A higher-term reply was not adopted.");
        Assert.IsNull(candidate.VotedFor, "Stepping down did not clear the prior vote.");
    }


    /// <summary>The term guard only rejects a strictly lower (stale) term; an equal-term request must still
    /// reach the up-to-date/already-voted evaluation below it.</summary>
    [TestMethod]
    public void AVoteRequestAtTheReceiversExactCurrentTermIsStillEvaluated()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> voter = new(N1, members);

        //Bring the voter to term 3 via a heartbeat that never asks for a vote, leaving VotedFor null.
        AppendEntriesReply heartbeat = voter.HandleAppendEntries(new AppendEntriesRequest<string>(new Term(3), N2, LogIndex.BeforeFirst, Term.Zero, [], LogIndex.BeforeFirst));
        Assert.IsTrue(heartbeat.Success);
        Assert.AreEqual(new Term(3), voter.CurrentTerm);
        Assert.IsNull(voter.VotedFor);

        //A candidate campaigns at that SAME term with an equally up-to-date (empty) log.
        RequestVoteReply reply = voter.HandleRequestVote(new RequestVoteRequest(new Term(3), N3, LogIndex.BeforeFirst, Term.Zero));

        Assert.IsTrue(reply.VoteGranted, "An equal-term request was refused as though it were stale.");
        Assert.AreEqual(N3, voter.VotedFor);
    }


    /// <summary>IndexOf returns 0 for the first membership entry — a valid position, not the "not found"
    /// sentinel — so a reply from that member must be tallied like any other.</summary>
    [TestMethod]
    public void AVoteFromTheFirstMemberInTheMembershipArrayCounts()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> candidate = new(N2, members);

        RequestVoteRequest request = candidate.StartElection();

        Assert.IsTrue(candidate.ReceiveVote(N1, new RequestVoteReply(request.Term, true)));
        Assert.AreEqual(RaftRole.Leader, candidate.Role);
    }


    /// <summary>A successful append must advance the follower's NextIndex to just past the matched entry;
    /// falling through to the failure path's retreat afterward would resend the same, already-confirmed
    /// entry.</summary>
    [TestMethod]
    public void ASuccessfulReplyAdvancesNextIndexPastTheMatchedEntryWithoutRetreatingIt()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> follower2 = new(N2, members);

        ElectLeader(leader, follower2);
        leader.Propose("a");
        leader.Propose("b");

        AppendEntriesRequest<string> first = leader.CreateAppendEntries(N2);
        AppendEntriesReply reply = follower2.HandleAppendEntries(first);
        Assert.IsTrue(reply.Success);

        leader.ReceiveAppendEntriesReply(N2, reply);

        AppendEntriesRequest<string> second = leader.CreateAppendEntries(N2);
        Assert.AreEqual(new LogIndex(2), second.PrevLogIndex, "A successful reply retreated NextIndex instead of advancing past the matched entry.");
        Assert.IsEmpty(second.Entries, "A successful reply caused the already-matched entry to be resent.");
    }


    /// <summary>A PrevLogIndex/PrevLogTerm mismatch must be reported as a failed append; nothing was appended
    /// or changed, so claiming success would tell the leader a prefix matched that never did.</summary>
    [TestMethod]
    public void AConsistencyCheckFailureReportsUnsuccessful()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> follower = new(N2, members);

        AppendEntriesReply seed = follower.HandleAppendEntries(new AppendEntriesRequest<string>(
            Term.First, N1, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(Term.First, "a")], LogIndex.BeforeFirst));
        Assert.IsTrue(seed.Success);

        //The request names the same index with a different PrevLogTerm, which is the mismatch the consistency check exists to catch.
        AppendEntriesReply mismatched = follower.HandleAppendEntries(new AppendEntriesRequest<string>(
            new Term(2), N1, LogIndex.First, new Term(2), [], LogIndex.BeforeFirst));

        Assert.IsFalse(mismatched.Success, "A PrevLogTerm mismatch was reported as a successful append.");
        Assert.AreEqual(LogIndex.BeforeFirst, mismatched.MatchIndex);
        Assert.HasCount(1, follower.Log, "The follower's log changed even though the consistency check failed.");
    }


    /// <summary>Only a request addressed to an actual member is meaningful; a stranger has no per-follower
    /// progress slot to build one from.</summary>
    [TestMethod]
    public void CreateAppendEntriesRejectsATargetOutsideTheMembership()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> follower2 = new(N2, members);

        ElectLeader(leader, follower2);
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        ArgumentException thrown = Assert.ThrowsExactly<ArgumentException>(() => leader.CreateAppendEntries(Stranger));
        Assert.AreEqual("follower", thrown.ParamName);
    }


    /// <summary>Only a strictly higher term forces a step-down on RequestVote; an equal-term request is
    /// evaluated in place. A leader has already voted for itself this term, so a same-term request from a
    /// different candidate must be denied without demoting the leader or clearing its self-vote.</summary>
    [TestMethod]
    public void ARequestVoteAtTheLeadersOwnTermDoesNotForceAStepDown()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N1, members);
        RaftNode<string> follower2 = new(N2, members);
        RaftNode<string> follower3 = new(N3, members);

        ElectLeader(leader, follower2, follower3);
        Assert.AreEqual(RaftRole.Leader, leader.Role);
        Term term = leader.CurrentTerm;

        RequestVoteReply reply = leader.HandleRequestVote(new RequestVoteRequest(term, N3, LogIndex.BeforeFirst, Term.Zero));

        Assert.IsFalse(reply.VoteGranted, "The leader granted a vote away at its own term.");
        Assert.AreEqual(RaftRole.Leader, leader.Role, "An equal-term vote request demoted the leader.");
        Assert.AreEqual(N1, leader.VotedFor, "An equal-term vote request cleared the leader's self-vote.");
    }


    /// <summary>ReceiveVote's return value is a strict "this exact call completed the majority" signal; a
    /// legitimate denial at the matching term must return false.</summary>
    [TestMethod]
    public void ADeniedVoteReplyReturnsFalseWithoutBecomingLeader()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> candidate = new(N1, members);

        RequestVoteRequest request = candidate.StartElection();

        bool result = candidate.ReceiveVote(N2, new RequestVoteReply(request.Term, false));

        Assert.IsFalse(result, "A denied vote reply was reported as completing the election.");
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);
    }


    /// <summary>IndexOf returns 0 for the first membership entry — a valid position — so a leader elsewhere
    /// in the membership must still be able to build a request addressed to it.</summary>
    [TestMethod]
    public void CreateAppendEntriesTargetsTheFirstMemberInTheMembershipArray()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N2, members);
        RaftNode<string> follower3 = new(N3, members);

        ElectLeader(leader, follower3);
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        AppendEntriesRequest<string> request = leader.CreateAppendEntries(N1);

        Assert.AreEqual(leader.CurrentTerm, request.Term);
    }


    /// <summary>Starting a fresh campaign must reset the tally: a peer's grant from the first campaign must
    /// not still count once a second campaign (a re-election after a timeout) begins.</summary>
    [TestMethod]
    public void ReCampaigningClearsStaleVotesFromThePriorTerm()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3, N4, N5];
        RaftNode<string> candidate = new(N1, members);

        RequestVoteRequest firstCampaign = candidate.StartElection();
        Assert.IsFalse(candidate.ReceiveVote(N2, new RequestVoteReply(firstCampaign.Term, true)));
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);

        RequestVoteRequest secondCampaign = candidate.StartElection();
        Assert.AreNotEqual(firstCampaign.Term, secondCampaign.Term);

        //A single further grant in the new term must NOT be enough: self plus exactly one new grant (two of
        //five) stays a minority. Only a stale, uncleared vote from the first campaign could complete this.
        Assert.IsFalse(candidate.ReceiveVote(N3, new RequestVoteReply(secondCampaign.Term, true)));
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);
    }


    /// <summary>LeaderCommit may only ever raise the commit index; a stale re-delivery that repeats the same
    /// LeaderCommit value but whose own range ends before it must never pull the commit index backward.</summary>
    [TestMethod]
    public void ALeaderCommitEqualToTheCurrentCommitIndexNeverLowersIt()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> follower = new(N2, members);

        AppendEntriesReply initial = follower.HandleAppendEntries(new AppendEntriesRequest<string>(
            Term.First, N1, LogIndex.BeforeFirst, Term.Zero,
            [new RaftLogEntry<string>(Term.First, "a"), new RaftLogEntry<string>(Term.First, "b"), new RaftLogEntry<string>(Term.First, "c")],
            new LogIndex(3)));
        Assert.IsTrue(initial.Success);
        Assert.AreEqual(new LogIndex(3), follower.CommitIndex);

        //A stale re-delivery carries only the FIRST (already matching) entry and repeats the SAME LeaderCommit.
        AppendEntriesReply stale = follower.HandleAppendEntries(new AppendEntriesRequest<string>(
            Term.First, N1, LogIndex.BeforeFirst, Term.Zero, [new RaftLogEntry<string>(Term.First, "a")], new LogIndex(3)));

        Assert.IsTrue(stale.Success);
        Assert.AreEqual(new LogIndex(3), follower.CommitIndex, "An equal LeaderCommit pulled the commit index backward.");
    }


    /// <summary>
    /// A default (never-initialized) membership array must be refused by this guard specifically, not merely
    /// reach the "doesn't contain id" guard by coincidence: Contains on a default ImmutableArray throws
    /// NullReferenceException rather than the documented ArgumentException.
    /// </summary>
    [TestMethod]
    public void ConstructorRejectsDefaultMembershipArray()
    {
        ImmutableArray<ReplicaId> defaultMembers = default;

        ArgumentException thrown = Assert.ThrowsExactly<ArgumentException>(() => new RaftNode<string>(N1, defaultMembers));
        Assert.AreEqual("members", thrown.ParamName);
    }


    /// <summary>A reply's term is checked once the higher-term branch has already passed; a granted reply
    /// left over from an earlier (now stale) term must still be rejected, not tallied just because the
    /// candidate role happens to still hold.</summary>
    [TestMethod]
    public void AStaleTermGrantedReplyIsNotTalliedTowardTheCurrentTermsQuorum()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3, N4, N5];
        RaftNode<string> candidate = new(N1, members);

        RequestVoteRequest firstCampaign = candidate.StartElection();
        RequestVoteReply staleGrantedReply = new(firstCampaign.Term, true);

        RequestVoteRequest secondCampaign = candidate.StartElection();
        Assert.AreEqual(RaftRole.Candidate, candidate.Role);

        //One genuine grant in the new term brings the tally to two of five (self + N2) — still a minority.
        Assert.IsFalse(candidate.ReceiveVote(N2, new RequestVoteReply(secondCampaign.Term, true)));

        //Replaying the FIRST campaign's granted reply must be ignored: its term no longer matches, so it must
        //not complete the quorum that a genuine third vote would.
        Assert.IsFalse(candidate.ReceiveVote(N3, staleGrantedReply), "A stale-term granted reply completed the election quorum.");
        Assert.AreEqual(RaftRole.Candidate, candidate.Role, "A stale-term granted reply was tallied.");
    }


    /// <summary>IndexOf returns 0 for the first membership entry — a valid position — so a leader elsewhere
    /// in the membership must still record that follower's reply.</summary>
    [TestMethod]
    public void AReplyFromTheFirstMemberInTheMembershipArrayAdvancesItsProgress()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> leader = new(N2, members);
        RaftNode<string> follower3 = new(N3, members);

        ElectLeader(leader, follower3);
        Assert.AreEqual(RaftRole.Leader, leader.Role);

        leader.Propose("solo");

        //N1 sits at membership position 0. A successful reply from it must count toward the majority just
        //like any other follower's — two of three (the leader plus N1) is already a quorum.
        leader.ReceiveAppendEntriesReply(N1, new AppendEntriesReply(leader.CurrentTerm, true, LogIndex.First));

        Assert.AreEqual(LogIndex.First, leader.CommitIndex, "A reply from the first membership entry was silently dropped.");
    }


    /// <summary>In a one-node cluster the leader is already its own majority, so proposing an entry must
    /// commit it immediately — there is no peer reply to wait for that could otherwise advance the commit
    /// index.</summary>
    [TestMethod]
    public void ProposeAdvancesCommitIndexImmediatelyInASingleNodeCluster()
    {
        ImmutableArray<ReplicaId> members = [N1];
        RaftNode<string> leader = new(N1, members);

        leader.StartElection();
        Assert.AreEqual(RaftRole.Leader, leader.Role, "A lone node did not become its own leader.");

        LogIndex index = leader.Propose("solo");

        Assert.AreEqual(index, leader.CommitIndex, "Proposing on a single-node leader did not advance the commit index.");
    }


    /// <summary>
    /// Every RPC entry point rejects a null request/reply with ArgumentNullException before touching any of
    /// its members, so a malformed transport layer fails fast with the documented exception type rather than
    /// an incidental NullReferenceException from the first field access.
    /// </summary>
    [TestMethod]
    public void NullRequestsAndRepliesAreRejectedAcrossEveryRpcSurface()
    {
        ImmutableArray<ReplicaId> members = [N1, N2, N3];
        RaftNode<string> node = new(N1, members);

        Assert.ThrowsExactly<ArgumentNullException>(() => node.HandleRequestVote(null!));

        //A member id is used so the null check is what fires, not the earlier membership filter returning
        //before ever touching the reply.
        Assert.ThrowsExactly<ArgumentNullException>(() => node.ReceiveVote(N2, null!));

        Assert.ThrowsExactly<ArgumentNullException>(() => node.HandleAppendEntries(null!));

        Assert.ThrowsExactly<ArgumentNullException>(() => node.ReceiveAppendEntriesReply(N2, null!));
    }


    //--- Helpers --------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one full election: the leader campaigns and every listed peer that grants its vote is counted,
    /// which (for a clean cluster) carries the candidate to a majority and the leader role.
    /// </summary>
    private static void ElectLeader(RaftNode<string> leader, params RaftNode<string>[] peers)
    {
        RequestVoteRequest request = leader.StartElection();
        foreach(RaftNode<string> peer in peers)
        {
            RequestVoteReply reply = peer.HandleRequestVote(request);
            leader.ReceiveVote(peer.Id, reply);
        }
    }


    /// <summary>
    /// One replication round: the leader sends each follower its tailored AppendEntries and folds the reply
    /// back so nextIndex/matchIndex and the leader's CommitIndex advance.
    /// </summary>
    private static void ReplicateRound(RaftNode<string> leader, params RaftNode<string>[] followers)
    {
        foreach(RaftNode<string> follower in followers)
        {
            AppendEntriesRequest<string> request = leader.CreateAppendEntries(follower.Id);
            AppendEntriesReply reply = follower.HandleAppendEntries(request);
            leader.ReceiveAppendEntriesReply(follower.Id, reply);
        }
    }


    /// <summary>
    /// Repeatedly sends AppendEntries to one follower until it accepts, draining any nextIndex back-off the
    /// leader needs to find the follower's matching prefix.
    /// </summary>
    /// <remarks>
    /// Bounded to keep a buggy node from looping forever.
    /// </remarks>
    private static void DeliverUntilCaughtUp(RaftNode<string> leader, RaftNode<string> follower, ReplicaId followerId)
    {
        for(int attempt = 0; attempt < 64; attempt++)
        {
            AppendEntriesRequest<string> request = leader.CreateAppendEntries(followerId);
            AppendEntriesReply reply = follower.HandleAppendEntries(request);
            leader.ReceiveAppendEntriesReply(followerId, reply);
            if(reply.Success && reply.MatchIndex >= new LogIndex(leader.Log.Count))
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
        for(LogIndex index = LogIndex.First; index <= node.CommitIndex; index = index.Next())
        {
            commands.Add(node.Log[index.Position].Command);
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
