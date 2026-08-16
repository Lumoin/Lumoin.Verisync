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

    //Leader-only volatile bookkeeping, reinitialized each time the node becomes leader. Indexed by position
    //in the membership rather than keyed by identity, so a replica outside the membership has no slot to
    //write to and cannot be recorded at all.
    private readonly FollowerProgress[] progress;

    //Votes collected in the current candidacy, including the implicit self-vote, indexed by position in the
    //membership so a replica outside it has no slot to grant one. Cleared on every term change.
    private readonly bool[] votesGranted;

    //This node's own position in the membership, which the constructor establishes is present.
    private readonly int selfIndex;


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
        progress = new FollowerProgress[members.Length];
        votesGranted = new bool[members.Length];
        selfIndex = IndexOf(id);
    }


    /// <summary>This replica's identity.</summary>
    public ReplicaId Id { get; }

    /// <summary>
    /// The fixed cluster membership, including <see cref="Id"/>. A quorum is a strict majority of its length.
    /// Exposes the immutable membership the node was constructed with so a host can derive the set of peers
    /// (every member except <see cref="Id"/>) it must send per-follower requests to.
    /// </summary>
    public ImmutableArray<ReplicaId> Members => members;

    /// <summary>The role this node currently occupies in <see cref="CurrentTerm"/>.</summary>
    public RaftRole Role { get; private set; } = RaftRole.Follower;

    /// <summary>The latest term this node has seen. Monotonically non-decreasing.</summary>
    public Term CurrentTerm { get; private set; }

    /// <summary>The candidate this node voted for in <see cref="CurrentTerm"/>, or <see langword="null"/> if it has not voted.</summary>
    public ReplicaId? VotedFor { get; private set; }

    /// <summary>
    /// The replicated log. Protocol indices are 1-based: protocol index <c>i</c> is <c>Log[i - 1]</c>, which
    /// is what <see cref="LogIndex.Position"/> converts.
    /// </summary>
    public IReadOnlyList<RaftLogEntry<TCommand>> Log => log;

    /// <summary>The highest log index known to be committed. Committed entries are safe to apply, in order.</summary>
    public LogIndex CommitIndex { get; private set; }

    /// <summary>
    /// The last leader this node observed (the source of an accepted current-term
    /// <see cref="AppendEntriesRequest{TCommand}"/>), or <see langword="null"/> if none has been seen.
    /// </summary>
    public ReplicaId? LeaderId { get; private set; }


    /// <summary>
    /// Snapshots the node's durable state — the Figure 2 persistent triple of
    /// <see cref="CurrentTerm"/>, <see cref="VotedFor"/>, and <see cref="Log"/> — for persistence. The log is
    /// copied into an <see cref="ImmutableArray{T}"/> so the returned snapshot is independent of subsequent
    /// mutations of this node.
    /// </summary>
    /// <returns>The durable state to make stable before any dependent reply is sent.</returns>
    /// <remarks>
    /// The role, commit index, observed leader, and the leader's replication bookkeeping are volatile by
    /// Figure 2 and are intentionally not part of the snapshot; a restored node rediscovers them from the
    /// current leader. The empty-means-no-vote convention of <see cref="LwwRegisterState{TValue}.Writer"/>
    /// applies: an absent vote is an empty byte array, not a sentinel.
    /// </remarks>
    public RaftNodeState<TCommand> ToState()
    {
        ImmutableArray<byte> votedFor = VotedFor is { } vote ? ImmutableArray.Create(vote.AsSpan()) : ImmutableArray<byte>.Empty;

        return new RaftNodeState<TCommand>(CurrentTerm, votedFor, [.. log]);
    }


    /// <summary>
    /// Reconstructs a node from durable <paramref name="state"/>, validating fail-closed against everything no
    /// honest history can produce. The restored node is a <see cref="RaftRole.Follower"/> with
    /// <see cref="CommitIndex"/> zero and no known leader: the commit index is volatile by Figure 2 and is
    /// rediscovered from the leader once a current-term append arrives.
    /// </summary>
    /// <param name="id">This replica's identity. Must appear in <paramref name="members"/>.</param>
    /// <param name="members">The fixed cluster membership, including <paramref name="id"/>.</param>
    /// <param name="state">The durable state to restore.</param>
    /// <returns>A follower restored to the persisted durable triple.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown by the constructor when <paramref name="members"/> is empty or omits <paramref name="id"/>.
    /// </exception>
    /// <exception cref="StateRestoreException">
    /// Thrown when the durable state is internally impossible in a way only the whole state shows: a
    /// <see cref="RaftNodeState{TCommand}.VotedFor"/> that is neither empty nor exactly
    /// <see cref="ReplicaId.Size"/> bytes, carrying <see cref="StateRestoreRefusal.RaftVoteMalformed"/>; a
    /// non-empty vote that is not a member, carrying
    /// <see cref="StateRestoreRefusal.RaftVoteOutsideMembership"/>; log terms that decrease, carrying
    /// <see cref="StateRestoreRefusal.RaftLogTermsDecrease"/>; or a last log term above the current term,
    /// carrying <see cref="StateRestoreRefusal.RaftLastLogTermAboveCurrentTerm"/>. Everything a single value
    /// can be wrong about is refused before a state can be built at all: an out-of-range term or index by
    /// <see cref="Term"/> and <see cref="LogIndex"/>, and a log entry tagged below
    /// <see cref="Term.First"/> by <see cref="RaftLogEntry{TCommand}"/>.
    /// </exception>
    public static RaftNode<TCommand> FromState(ReplicaId id, ImmutableArray<ReplicaId> members, RaftNodeState<TCommand> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        //The constructor performs the membership checks (non-empty, contains id) and throws ArgumentException.
        var node = new RaftNode<TCommand>(id, members);

        ReplicaId? votedFor = null;
        if(!state.VotedFor.IsDefaultOrEmpty)
        {
            if(state.VotedFor.Length != ReplicaId.Size)
            {
                throw new StateRestoreException(StateRestoreRefusal.RaftVoteMalformed, $"A restored vote must be empty or exactly {ReplicaId.Size} bytes, got {state.VotedFor.Length}.", nameof(state));
            }

            ReplicaId vote = ReplicaId.FromSpan(state.VotedFor.AsSpan());
            if(!members.Contains(vote))
            {
                throw new StateRestoreException(StateRestoreRefusal.RaftVoteOutsideMembership, "A restored vote must name a member of the cluster.", nameof(state));
            }

            votedFor = vote;
        }

        ImmutableArray<RaftLogEntry<TCommand>> log = state.Log.IsDefault ? [] : state.Log;
        Term previousTerm = Term.Zero;
        for(int i = 0; i < log.Length; i++)
        {
            Term entryTerm = log[i].Term;
            if(entryTerm < previousTerm)
            {
                throw new StateRestoreException(StateRestoreRefusal.RaftLogTermsDecrease, $"Restored log terms cannot decrease, got {entryTerm.Value} after {previousTerm.Value} at index {i + 1}.", nameof(state));
            }

            previousTerm = entryTerm;
        }

        if(previousTerm > state.CurrentTerm)
        {
            throw new StateRestoreException(StateRestoreRefusal.RaftLastLogTermAboveCurrentTerm, $"A restored last log term ({previousTerm.Value}) cannot exceed the current term ({state.CurrentTerm.Value}).", nameof(state));
        }

        node.CurrentTerm = state.CurrentTerm;
        node.VotedFor = votedFor;
        node.log.AddRange(log);

        return node;
    }


    /// <summary>
    /// Starts a new election: increments <see cref="CurrentTerm"/>, becomes <see cref="RaftRole.Candidate"/>,
    /// votes for itself, and returns the <see cref="RequestVoteRequest"/> the host broadcasts to the other
    /// members.
    /// </summary>
    /// <returns>The vote request to broadcast.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="CurrentTerm"/> is <see cref="Term.MaxValue"/> and the term range is therefore
    /// spent. A cluster that has held two to the fifty-third elections is reconfigured rather than wrapped.
    /// </exception>
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
        CurrentTerm = CurrentTerm.Next();
        Role = RaftRole.Candidate;
        VotedFor = Id;
        ClearVotes();
        votesGranted[selfIndex] = true;

        var request = new RequestVoteRequest(CurrentTerm, Id, LastLogIndex, LastLogTerm);

        //A lone node is its own majority; with no peers to reply there is no later ReceiveVote to win on.
        if(VoteCount >= Quorum)
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
    /// <remarks>
    /// A candidate outside <see cref="Members"/> is discarded before anything else, including the term rule.
    /// Only a member can win an election over this membership, and <see cref="FromState"/> refuses to restore
    /// a vote naming a non-member, so granting one would put the node in a state its own restore path
    /// rejects. Filtering first also denies a non-member the term: a stranger that cannot win an election
    /// could otherwise still raise this node's term and unseat a leader the cluster had agreed on.
    /// </remarks>
    public RequestVoteReply HandleRequestVote(RequestVoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if(!members.Contains(request.CandidateId))
        {
            return new RequestVoteReply(CurrentTerm, false);
        }

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
    /// <para>
    /// A reply whose term exceeds this node's term still forces a step-down, even though no vote is counted —
    /// the universal "greater term seen" rule applies to replies as well as requests.
    /// </para>
    /// <para>
    /// A reply from a replica outside <see cref="Members"/> is discarded before anything else, including the
    /// term rule. The quorum is a majority of the configured membership, so a vote from anywhere else could
    /// complete one without a majority of the cluster having granted it, and <paramref name="from"/> arrives
    /// as wire data that no codec checks. Filtering before the term rule denies a stranger the one lever it
    /// would otherwise keep, which is raising this node's term and unseating an agreed leader.
    /// </para>
    /// </remarks>
    public bool ReceiveVote(ReplicaId from, RequestVoteReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        //A non-member holds no position in the tally, so it cannot contribute to a majority of the membership
        //the quorum is computed from. It is looked up before the term rule so it cannot move the term either.
        int fromIndex = IndexOf(from);
        if(fromIndex < 0)
        {
            return false;
        }

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

        bool alreadyHadMajority = VoteCount >= Quorum;
        votesGranted[fromIndex] = true;
        bool nowHasMajority = VoteCount >= Quorum;

        if(nowHasMajority && !alreadyHadMajority)
        {
            BecomeLeader();

            return true;
        }

        return false;
    }


    /// <summary>
    /// Appends <paramref name="command"/> to the leader's log in the current term and returns its index.
    /// </summary>
    /// <param name="command">The command to replicate.</param>
    /// <returns>The log index of the newly appended entry.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this node is not currently the leader.</exception>
    /// <remarks>
    /// On a multi-node cluster the entry is durable on this node only; it commits and becomes safe to apply
    /// only once <see cref="ReceiveAppendEntriesReply(ReplicaId, AppendEntriesReply)"/> has advanced
    /// <see cref="CommitIndex"/> past it. On a single-node cluster the leader is already its own majority, so
    /// the commit advance attempted here moves <see cref="CommitIndex"/> immediately.
    /// </remarks>
    public LogIndex Propose(TCommand command)
    {
        if(Role != RaftRole.Leader)
        {
            throw new InvalidOperationException("Only the leader can propose commands.");
        }

        log.Add(new RaftLogEntry<TCommand>(CurrentTerm, command));
        LogIndex index = LastLogIndex;

        //The leader trivially matches its own log; keep matchIndex consistent for commit counting, including
        //the single-node case where the leader is the whole majority and there is no peer reply to wait for.
        progress[selfIndex] = progress[selfIndex].Confirmed(index);
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

        int followerIndex = IndexOf(follower);
        if(followerIndex < 0)
        {
            throw new ArgumentException("A request can only be built for a member of the cluster.", nameof(follower));
        }

        LogIndex next = progress[followerIndex].NextIndex;
        LogIndex prevLogIndex = next.Previous();
        Term prevLogTerm = !prevLogIndex.IsBeforeFirst && prevLogIndex <= LastLogIndex ? log[prevLogIndex.Position].Term : Term.Zero;

        ImmutableArray<RaftLogEntry<TCommand>> entries;
        if(next <= LastLogIndex)
        {
            int from = next.Position;
            ImmutableArray<RaftLogEntry<TCommand>>.Builder builder = ImmutableArray.CreateBuilder<RaftLogEntry<TCommand>>(log.Count - from);
            for(int i = from; i < log.Count; i++)
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
    /// <remarks>
    /// A request naming a leader outside <see cref="Members"/> is refused before anything else, including the
    /// term rule. Only a member can win an election over this membership, so no such request comes from a
    /// leader this cluster elected, and appending its entries would replicate a log no quorum ever agreed on.
    /// Filtering first also denies a non-member the term, which is the lever it would otherwise keep.
    /// </remarks>
    public AppendEntriesReply HandleAppendEntries(AppendEntriesRequest<TCommand> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if(!members.Contains(request.LeaderId))
        {
            return new AppendEntriesReply(CurrentTerm, false, LogIndex.BeforeFirst);
        }

        if(request.Term < CurrentTerm)
        {
            return new AppendEntriesReply(CurrentTerm, false, LogIndex.BeforeFirst);
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

        //Consistency check: the log must contain an entry at PrevLogIndex whose term matches PrevLogTerm. The
        //empty prefix names no entry and always matches.
        if(!request.PrevLogIndex.IsBeforeFirst)
        {
            if(request.PrevLogIndex > LastLogIndex || log[request.PrevLogIndex.Position].Term != request.PrevLogTerm)
            {
                return new AppendEntriesReply(CurrentTerm, false, LogIndex.BeforeFirst);
            }
        }

        //Splice the incoming entries in. Walk them against the existing log: an entry already present with a
        //matching term is left untouched (so a stale or duplicate request never truncates committed suffix),
        //the first conflicting term truncates everything from that point, and the remainder is appended.
        for(int i = 0; i < request.Entries.Length; i++)
        {
            int logPosition = request.PrevLogIndex.Advance(i + 1).Position;

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

        LogIndex indexOfLastNewEntry = request.PrevLogIndex.Advance(request.Entries.Length);

        //LeaderCommit can only ever raise our commit index, never lower it, and never past the last entry
        //this request actually delivered (or our log end, whichever is smaller).
        if(request.LeaderCommit > CommitIndex)
        {
            CommitIndex = LogIndex.Min(request.LeaderCommit, indexOfLastNewEntry);
        }

        return new AppendEntriesReply(CurrentTerm, true, indexOfLastNewEntry);
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
    /// rule and is the only place <see cref="CommitIndex"/> moves on the leader. A reply from a replica
    /// outside <see cref="Members"/> is discarded before anything else, including the term rule: the
    /// per-follower indices are addressed by membership position, so a non-member has no slot at all.
    /// </remarks>
    public void ReceiveAppendEntriesReply(ReplicaId from, AppendEntriesReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        int fromIndex = IndexOf(from);
        if(fromIndex < 0)
        {
            return;
        }

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
            progress[fromIndex] = progress[fromIndex].Confirmed(reply.MatchIndex);
            TryAdvanceCommitIndex();

            return;
        }

        //Same-term failure: the consistency check missed, so the next CreateAppendEntries probes a longer
        //prefix.
        progress[fromIndex] = progress[fromIndex].Retreated();
    }


    /// <summary>The index of the last log entry, or <see cref="LogIndex.BeforeFirst"/> when the log is empty.</summary>
    private LogIndex LastLogIndex => new(log.Count);


    /// <summary>The term of the last log entry, or <see cref="Term.Zero"/> when the log is empty.</summary>
    private Term LastLogTerm => log.Count == 0 ? Term.Zero : log[^1].Term;


    /// <summary>A strict majority of the cluster.</summary>
    private int Quorum => (members.Length / 2) + 1;


    /// <summary>
    /// The position <paramref name="replica"/> holds in the membership, or a negative value when it holds
    /// none.
    /// </summary>
    /// <param name="replica">The identity to locate.</param>
    /// <returns>The membership index, or a negative value for a non-member.</returns>
    /// <remarks>
    /// The per-follower indices are addressed through this, so a non-member has no slot to be written to and
    /// cannot be recorded at all. A linear scan is what a Raft membership wants: it is a handful of entries,
    /// held contiguously, and comparing them costs less than hashing an identity.
    /// </remarks>
    private int IndexOf(ReplicaId replica)
    {
        for(int i = 0; i < members.Length; i++)
        {
            if(members[i].Equals(replica))
            {
                return i;
            }
        }

        return -1;
    }


    /// <summary>
    /// Reverts to a follower in <paramref name="newTerm"/>, clearing the vote and any candidacy state. The
    /// observed leader is preserved; the caller updates it when the new term has a known leader.
    /// </summary>
    /// <param name="newTerm">The newer term to adopt.</param>
    private void StepDownTo(Term newTerm)
    {
        CurrentTerm = newTerm;
        Role = RaftRole.Follower;
        VotedFor = null;
        ClearVotes();
    }


    /// <summary>The number of members that have granted this node a vote in the current candidacy.</summary>
    private int VoteCount
    {
        get
        {
            int granted = 0;
            for(int i = 0; i < votesGranted.Length; i++)
            {
                if(votesGranted[i])
                {
                    granted++;
                }
            }

            return granted;
        }
    }


    private void ClearVotes() => Array.Clear(votesGranted);


    /// <summary>
    /// Transitions a winning candidate to leader and initializes per-peer replication state: every peer's
    /// <see cref="FollowerProgress"/> to the entry just past the leader's log with nothing known replicated.
    /// </summary>
    private void BecomeLeader()
    {
        Role = RaftRole.Leader;
        LeaderId = Id;

        FollowerProgress initial = FollowerProgress.StartingFrom(LastLogIndex);
        for(int i = 0; i < members.Length; i++)
        {
            progress[i] = initial;
        }

        //The leader always matches its own complete log; this seeds the majority count for commit decisions.
        progress[selfIndex] = initial.Confirmed(LastLogIndex);
    }


    /// <summary>
    /// Tests whether a candidate's last-entry (<paramref name="candidateLastTerm"/>,
    /// <paramref name="candidateLastIndex"/>) is at least as up-to-date as this node's log, per the election
    /// restriction: a higher last term wins outright, and on an equal last term the longer (or equal) log wins.
    /// </summary>
    /// <param name="candidateLastTerm">The term of the candidate's last log entry.</param>
    /// <param name="candidateLastIndex">The index of the candidate's last log entry.</param>
    /// <returns><see langword="true"/> if the candidate's log is at least as up-to-date as this node's.</returns>
    private bool IsCandidateLogAtLeastAsUpToDate(Term candidateLastTerm, LogIndex candidateLastIndex)
    {
        Term ourLastTerm = LastLogTerm;
        LogIndex ourLastIndex = LastLogIndex;

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
        for(LogIndex candidate = LastLogIndex; candidate > CommitIndex; candidate = candidate.Previous())
        {
            //Figure 8: only entries from the leader's current term are committed by counting replicas.
            if(log[candidate.Position].Term != CurrentTerm)
            {
                continue;
            }

            int replicatedCount = 0;
            for(int i = 0; i < members.Length; i++)
            {
                if(progress[i].MatchIndex >= candidate)
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


    private string DebuggerDisplay => $"RaftNode: {Role}, term {CurrentTerm.Value}, log {log.Count}, commit {CommitIndex.Value}";
}
