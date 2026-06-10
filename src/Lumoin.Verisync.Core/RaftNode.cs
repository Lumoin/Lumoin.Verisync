using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A single Raft replica: the in-memory model of the protocol's safety core, totally ordering an
/// application command stream within one trust domain. It implements the complete state machine of the
/// Raft paper's Figure 2 (Ongaro and Ousterhout, "In Search of an Understandable Consensus Algorithm") —
/// term and vote handling, the RequestVote and AppendEntries RPCs on both sides, and the leader commit
/// rule of Figure 8 — and nothing above it.
/// </summary>
/// <typeparam name="TCommand">The application command type replicated and ordered by the log.</typeparam>
/// <remarks>
/// <para>
/// This is the log-replication sibling of <see cref="CasPaxosRegister{TValue}"/>: where the register makes
/// a single metadata-grade anchor decision and the CRDT plane imposes no order at all, this node imposes a
/// total order on an operation stream inside a trust domain. It is a mutable, single-threaded node in the
/// style of <see cref="ConsensusNode{TValue}"/> — it processes one message at a time and is not safe for
/// concurrent calls. Each handler returns the reply the host transmits; the host owns all transport,
/// retries, and batching.
/// </para>
/// <para>
/// <strong>Liveness is external by construction.</strong> The node holds no timers and draws no entropy —
/// the repository bans wall clocks and randomness in the safety core — so it never decides on its own to
/// start an election. The host triggers a campaign by calling <see cref="StartElection"/> when it judges
/// the leader lost; in a real deployment that judgement is the randomized election timeout, which lives in
/// the networked layer above this core, exactly as the proposer, retries, and mode-switching policy live
/// above <see cref="CasPaxosRegister{TValue}"/>. Heartbeats are likewise host-driven: the host periodically
/// calls <see cref="CreateAppendEntries(ReplicaId)"/> per follower.
/// </para>
/// <para>
/// <strong>Durability follows the same persist-before-reply obligation as
/// <see cref="ConsensusNode{TValue}"/>.</strong> This model keeps <see cref="CurrentTerm"/>,
/// <see cref="VotedFor"/>, and <see cref="Log"/> in memory only. Raft safety across a crash requires those
/// three to be durable <em>before</em> the reply that depends on them leaves the process: a node that
/// restarts having forgotten a granted vote or an appended entry can vote twice in a term or lose a
/// committed entry, breaking election safety and log matching. A persisting host drives the node, makes the
/// observable state durable, and only then sends the reply — the same fail-closed sequencing
/// <see cref="ConsensusNode{TValue}"/> documents. This model omits persistence, membership changes, and log
/// compaction; those layers, and the snapshot transfer they imply, sit above the safety core.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class RaftNode<TCommand>
{
    private readonly List<RaftLogEntry<TCommand>> log = [];
    private readonly ImmutableArray<ReplicaId> members;

    //Leader-only volatile bookkeeping, reinitialized each time the node becomes leader. Keyed by every
    //peer (all members except self). For a peer p: nextIndex[p] is the next log index the leader will send,
    //matchIndex[p] is the highest index known replicated on p.
    private readonly Dictionary<ReplicaId, long> nextIndex = [];
    private readonly Dictionary<ReplicaId, long> matchIndex = [];

    //Votes collected in the current candidacy, including the implicit self-vote. Cleared on every term change.
    private readonly HashSet<ReplicaId> votesReceived = [];


    /// <summary>
    /// Initializes a follower in term zero with an empty log and no recorded vote.
    /// </summary>
    /// <param name="id">This replica's identity. Must appear in <paramref name="members"/>.</param>
    /// <param name="members">
    /// The fixed cluster membership, including <paramref name="id"/>. A quorum is a strict majority of its
    /// length.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="members"/> is empty or does not contain <paramref name="id"/>.
    /// </exception>
    public RaftNode(ReplicaId id, ImmutableArray<ReplicaId> members)
    {
        if(members.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Raft membership must be non-empty.", nameof(members));
        }

        if(!members.Contains(id))
        {
            throw new ArgumentException("Raft membership must contain this node's own id.", nameof(members));
        }

        Id = id;
        this.members = members;
    }


    /// <summary>This replica's identity.</summary>
    public ReplicaId Id { get; }

    /// <summary>The role this node currently occupies in <see cref="CurrentTerm"/>.</summary>
    public RaftRole Role { get; private set; } = RaftRole.Follower;

    /// <summary>The latest term this node has seen. Monotonically non-decreasing.</summary>
    public long CurrentTerm { get; private set; }

    /// <summary>The candidate this node voted for in <see cref="CurrentTerm"/>, or <see langword="null"/> if it has not voted.</summary>
    public ReplicaId? VotedFor { get; private set; }

    /// <summary>
    /// The replicated log. Protocol indices are 1-based: protocol index <c>i</c> is <c>Log[i - 1]</c>, and
    /// index zero denotes "before the first entry".
    /// </summary>
    public IReadOnlyList<RaftLogEntry<TCommand>> Log => log;

    /// <summary>The highest log index known to be committed. Committed entries are safe to apply, in order.</summary>
    public long CommitIndex { get; private set; }

    /// <summary>
    /// The last leader this node observed (the source of an accepted current-term
    /// <see cref="AppendEntriesRequest{TCommand}"/>), or <see langword="null"/> if none has been seen.
    /// </summary>
    public ReplicaId? LeaderId { get; private set; }


    /// <summary>
    /// Starts a new election: increments <see cref="CurrentTerm"/>, becomes <see cref="RaftRole.Candidate"/>,
    /// votes for itself, and returns the <see cref="RequestVoteRequest"/> the host broadcasts to the other
    /// members.
    /// </summary>
    /// <returns>The vote request to broadcast.</returns>
    /// <remarks>
    /// <para>
    /// This is the sole entry point for liveness. The node never calls it itself; the host invokes it when
    /// its (external, randomized) election timeout fires. Calling it again supersedes any in-progress
    /// candidacy with a fresh, higher term, which is exactly how a split vote is broken in a live system.
    /// </para>
    /// <para>
    /// In a single-node cluster the self-vote is already a majority, so the node becomes
    /// <see cref="RaftRole.Leader"/> within this call (there is no peer reply to wait for). The returned
    /// request is still well-formed; a host with no peers simply has no one to send it to.
    /// </para>
    /// </remarks>
    public RequestVoteRequest StartElection()
    {
        CurrentTerm++;
        Role = RaftRole.Candidate;
        VotedFor = Id;
        votesReceived.Clear();
        votesReceived.Add(Id);

        var request = new RequestVoteRequest(CurrentTerm, Id, LastLogIndex, LastLogTerm);

        //A lone node is its own majority; with no peers to reply there is no later ReceiveVote to win on.
        if(votesReceived.Count >= Quorum)
        {
            BecomeLeader();
        }

        return request;
    }


    /// <summary>
    /// Handles an inbound <see cref="RequestVoteRequest"/>, applying the term rule and the election
    /// restriction, and returns the reply.
    /// </summary>
    /// <param name="request">The vote request to evaluate.</param>
    /// <returns>The reply, carrying this node's (possibly updated) term and whether the vote was granted.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    public RequestVoteReply HandleRequestVote(RequestVoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if(request.Term < CurrentTerm)
        {
            return new RequestVoteReply(CurrentTerm, false);
        }

        if(request.Term > CurrentTerm)
        {
            StepDownTo(request.Term);
        }

        bool canVote = VotedFor is null || VotedFor.Value.Equals(request.CandidateId);
        bool logIsUpToDate = IsCandidateLogAtLeastAsUpToDate(request.LastLogTerm, request.LastLogIndex);
        bool grant = canVote && logIsUpToDate;
        if(grant)
        {
            VotedFor = request.CandidateId;
        }

        return new RequestVoteReply(CurrentTerm, grant);
    }


    /// <summary>
    /// Records a vote reply received from <paramref name="from"/> during this node's candidacy.
    /// </summary>
    /// <param name="from">The replica that replied.</param>
    /// <param name="reply">The vote reply.</param>
    /// <returns>
    /// <see langword="true"/> exactly when this vote is the one that completes a majority and the node
    /// transitions to <see cref="RaftRole.Leader"/>; <see langword="false"/> in every other case, including
    /// a vote that is counted but does not yet reach a majority, a duplicate, a stale-term reply, or a reply
    /// arriving when the node is no longer a candidate.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reply"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A reply whose term exceeds this node's term still forces a step-down, even though no vote is counted —
    /// the universal "greater term seen" rule applies to replies as well as requests.
    /// </remarks>
    public bool ReceiveVote(ReplicaId from, RequestVoteReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if(reply.Term > CurrentTerm)
        {
            StepDownTo(reply.Term);

            return false;
        }

        //Only tally while still a candidate in the term the reply answers; older or newer terms are stale.
        if(Role != RaftRole.Candidate || reply.Term != CurrentTerm || !reply.VoteGranted)
        {
            return false;
        }

        bool alreadyHadMajority = votesReceived.Count >= Quorum;
        votesReceived.Add(from);
        bool nowHasMajority = votesReceived.Count >= Quorum;

        if(nowHasMajority && !alreadyHadMajority)
        {
            BecomeLeader();

            return true;
        }

        return false;
    }


    /// <summary>
    /// Appends <paramref name="command"/> to the leader's log in the current term and returns its 1-based
    /// index.
    /// </summary>
    /// <param name="command">The command to replicate.</param>
    /// <returns>The 1-based log index of the newly appended entry.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this node is not currently the leader.</exception>
    /// <remarks>
    /// On a multi-node cluster the entry is durable on this node only; it commits and becomes safe to apply
    /// only once <see cref="ReceiveAppendEntriesReply(ReplicaId, AppendEntriesReply)"/> has advanced
    /// <see cref="CommitIndex"/> past it. On a single-node cluster the leader is already its own majority, so
    /// the commit advance attempted here moves <see cref="CommitIndex"/> immediately.
    /// </remarks>
    public long Propose(TCommand command)
    {
        if(Role != RaftRole.Leader)
        {
            throw new InvalidOperationException("Only the leader can propose commands.");
        }

        log.Add(new RaftLogEntry<TCommand>(CurrentTerm, command));
        long index = log.Count;

        //The leader trivially matches its own log; keep matchIndex consistent for commit counting, including
        //the single-node case where the leader is the whole majority and there is no peer reply to wait for.
        matchIndex[Id] = index;
        TryAdvanceCommitIndex();

        return index;
    }


    /// <summary>
    /// Builds the <see cref="AppendEntriesRequest{TCommand}"/> for <paramref name="follower"/> from the
    /// leader's <c>nextIndex</c> bookkeeping: the entries from <c>nextIndex[follower]</c> to the end of the
    /// log, with the preceding entry as the consistency check.
    /// </summary>
    /// <param name="follower">The follower to build the request for.</param>
    /// <returns>The request to send to <paramref name="follower"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this node is not currently the leader.</exception>
    /// <remarks>
    /// With no new entries to send the request is a heartbeat (empty <c>Entries</c>), which still carries
    /// the leader's term and commit index so a follower learns of new commits and a candidate steps down.
    /// </remarks>
    public AppendEntriesRequest<TCommand> CreateAppendEntries(ReplicaId follower)
    {
        if(Role != RaftRole.Leader)
        {
            throw new InvalidOperationException("Only the leader can create AppendEntries requests.");
        }

        long next = nextIndex.TryGetValue(follower, out long stored) ? stored : LastLogIndex + 1;
        long prevLogIndex = next - 1;
        long prevLogTerm = prevLogIndex >= 1 && prevLogIndex <= log.Count ? log[(int)(prevLogIndex - 1)].Term : 0;

        ImmutableArray<RaftLogEntry<TCommand>> entries;
        if(next <= log.Count)
        {
            ImmutableArray<RaftLogEntry<TCommand>>.Builder builder = ImmutableArray.CreateBuilder<RaftLogEntry<TCommand>>(log.Count - (int)next + 1);
            for(int i = (int)next - 1; i < log.Count; i++)
            {
                builder.Add(log[i]);
            }

            entries = builder.MoveToImmutable();
        }
        else
        {
            entries = [];
        }

        return new AppendEntriesRequest<TCommand>(CurrentTerm, Id, prevLogIndex, prevLogTerm, entries, CommitIndex);
    }


    /// <summary>
    /// Handles an inbound <see cref="AppendEntriesRequest{TCommand}"/> on the follower side: the term rule,
    /// the log consistency check, conflict truncation, idempotent append, and commit-index advance.
    /// </summary>
    /// <param name="request">The append request to apply.</param>
    /// <returns>The reply, carrying this node's term, whether the append succeeded, and the resulting match index.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    public AppendEntriesReply HandleAppendEntries(AppendEntriesRequest<TCommand> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if(request.Term < CurrentTerm)
        {
            return new AppendEntriesReply(CurrentTerm, false, 0);
        }

        //Term is current or newer: there is a legitimate leader for this term. Adopt the term if newer, and
        //in any case (even at an equal term, where a candidate must yield to the established leader) become a
        //follower and record who the leader is.
        if(request.Term > CurrentTerm)
        {
            StepDownTo(request.Term);
        }

        Role = RaftRole.Follower;
        LeaderId = request.LeaderId;

        //Consistency check: the log must contain an entry at PrevLogIndex whose term matches PrevLogTerm.
        //Index zero is the empty-prefix sentinel and always matches.
        if(request.PrevLogIndex > 0)
        {
            if(request.PrevLogIndex > log.Count || log[(int)(request.PrevLogIndex - 1)].Term != request.PrevLogTerm)
            {
                return new AppendEntriesReply(CurrentTerm, false, 0);
            }
        }

        //Splice the incoming entries in. Walk them against the existing log: an entry already present with a
        //matching term is left untouched (so a stale or duplicate request never truncates committed suffix),
        //the first conflicting term truncates everything from that point, and the remainder is appended.
        for(int i = 0; i < request.Entries.Length; i++)
        {
            int logPosition = (int)request.PrevLogIndex + i;

            if(logPosition < log.Count)
            {
                if(log[logPosition].Term == request.Entries[i].Term)
                {
                    continue;
                }

                //Conflict: truncate this entry and all that follow, then append the rest of the incoming run.
                log.RemoveRange(logPosition, log.Count - logPosition);
            }

            log.Add(request.Entries[i]);
        }

        //LeaderCommit can only ever raise our commit index, never lower it, and never past the last entry
        //this request actually delivered (or our log end, whichever is smaller).
        if(request.LeaderCommit > CommitIndex)
        {
            long indexOfLastNewEntry = request.PrevLogIndex + request.Entries.Length;
            CommitIndex = Math.Min(request.LeaderCommit, indexOfLastNewEntry);
        }

        long matchIndexResult = request.PrevLogIndex + request.Entries.Length;

        return new AppendEntriesReply(CurrentTerm, true, matchIndexResult);
    }


    /// <summary>
    /// Applies an <see cref="AppendEntriesReply"/> from <paramref name="from"/> to the leader's replication
    /// bookkeeping: on success it advances that follower's match and next indices and tries to commit; on a
    /// same-term failure it backs <c>nextIndex</c> off by one.
    /// </summary>
    /// <param name="from">The follower that replied.</param>
    /// <param name="reply">The reply.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="reply"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A reply whose term exceeds this node's term forces a step-down; a stale reply (from an old term, or
    /// arriving when this node is no longer the leader) is ignored. The commit advance applies the Figure 8
    /// rule and is the only place <see cref="CommitIndex"/> moves on the leader.
    /// </remarks>
    public void ReceiveAppendEntriesReply(ReplicaId from, AppendEntriesReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if(reply.Term > CurrentTerm)
        {
            StepDownTo(reply.Term);

            return;
        }

        if(Role != RaftRole.Leader || reply.Term != CurrentTerm)
        {
            return;
        }

        if(reply.Success)
        {
            //Out-of-order replies must not regress what we already know is replicated.
            long knownMatch = matchIndex.TryGetValue(from, out long existingMatch) ? existingMatch : 0;
            if(reply.MatchIndex > knownMatch)
            {
                matchIndex[from] = reply.MatchIndex;
            }

            nextIndex[from] = reply.MatchIndex + 1;
            TryAdvanceCommitIndex();

            return;
        }

        //Same-term failure: the consistency check missed, so retreat nextIndex by one (never below 1) and
        //the next CreateAppendEntries will probe a longer prefix.
        long current = nextIndex.TryGetValue(from, out long storedNext) ? storedNext : LastLogIndex + 1;
        nextIndex[from] = Math.Max(1, current - 1);
    }


    /// <summary>The 1-based index of the last log entry, or zero when the log is empty.</summary>
    private long LastLogIndex => log.Count;


    /// <summary>The term of the last log entry, or zero when the log is empty.</summary>
    private long LastLogTerm => log.Count == 0 ? 0 : log[^1].Term;


    /// <summary>A strict majority of the cluster.</summary>
    private int Quorum => (members.Length / 2) + 1;


    /// <summary>
    /// Reverts to a follower in <paramref name="newTerm"/>, clearing the vote and any candidacy state. The
    /// observed leader is preserved; the caller updates it when the new term has a known leader.
    /// </summary>
    /// <param name="newTerm">The newer term to adopt.</param>
    private void StepDownTo(long newTerm)
    {
        CurrentTerm = newTerm;
        Role = RaftRole.Follower;
        VotedFor = null;
        votesReceived.Clear();
    }


    /// <summary>
    /// Transitions a winning candidate to leader and initializes per-peer replication state: every peer's
    /// <c>nextIndex</c> to the entry just past the leader's log, its <c>matchIndex</c> to zero.
    /// </summary>
    private void BecomeLeader()
    {
        Role = RaftRole.Leader;
        LeaderId = Id;

        nextIndex.Clear();
        matchIndex.Clear();

        long initialNext = LastLogIndex + 1;
        for(int i = 0; i < members.Length; i++)
        {
            ReplicaId member = members[i];
            if(member.Equals(Id))
            {
                continue;
            }

            nextIndex[member] = initialNext;
            matchIndex[member] = 0;
        }

        //The leader always matches its own complete log; this seeds the majority count for commit decisions.
        matchIndex[Id] = LastLogIndex;
    }


    /// <summary>
    /// Tests whether a candidate's last-entry (<paramref name="candidateLastTerm"/>,
    /// <paramref name="candidateLastIndex"/>) is at least as up-to-date as this node's log, per the election
    /// restriction: a higher last term wins outright, and on an equal last term the longer (or equal) log wins.
    /// </summary>
    /// <param name="candidateLastTerm">The term of the candidate's last log entry.</param>
    /// <param name="candidateLastIndex">The 1-based index of the candidate's last log entry.</param>
    /// <returns><see langword="true"/> if the candidate's log is at least as up-to-date as this node's.</returns>
    private bool IsCandidateLogAtLeastAsUpToDate(long candidateLastTerm, long candidateLastIndex)
    {
        long ourLastTerm = LastLogTerm;
        long ourLastIndex = LastLogIndex;

        if(candidateLastTerm != ourLastTerm)
        {
            return candidateLastTerm > ourLastTerm;
        }

        return candidateLastIndex >= ourLastIndex;
    }


    /// <summary>
    /// Advances <see cref="CommitIndex"/> to the highest index <c>N</c> for which a majority of
    /// <c>matchIndex</c> (counting the leader itself) is at least <c>N</c> <em>and</em> <c>Log[N]</c> was
    /// created in the current term. The current-term clause is the Figure 8 rule: a leader never commits an
    /// entry from a prior term on replica count alone — such an entry commits only as a side effect of a
    /// current-term entry above it reaching a majority.
    /// </summary>
    private void TryAdvanceCommitIndex()
    {
        for(long candidate = log.Count; candidate > CommitIndex; candidate--)
        {
            //Figure 8: only entries from the leader's current term are committed by counting replicas.
            if(log[(int)(candidate - 1)].Term != CurrentTerm)
            {
                continue;
            }

            int replicatedCount = 0;
            for(int i = 0; i < members.Length; i++)
            {
                long memberMatch = matchIndex.TryGetValue(members[i], out long value) ? value : 0;
                if(memberMatch >= candidate)
                {
                    replicatedCount++;
                }
            }

            if(replicatedCount >= Quorum)
            {
                CommitIndex = candidate;

                return;
            }
        }
    }


    private string DebuggerDisplay => $"RaftNode: {Role}, term {CurrentTerm}, log {log.Count}, commit {CommitIndex}";
}
