namespace Lumoin.Verisync.Core;

/// <summary>
/// The RequestVote RPC a candidate broadcasts to solicit a vote, carrying the candidate's term and the
/// shape of its log so a recipient can apply the up-to-date test before granting (Raft paper, Figure 2,
/// RequestVote RPC).
/// </summary>
/// <param name="Term">The candidate's term — one greater than the term it last observed.</param>
/// <param name="CandidateId">The replica requesting the vote.</param>
/// <param name="LastLogIndex">The 1-based index of the candidate's last log entry, or zero for an empty log.</param>
/// <param name="LastLogTerm">The term of the candidate's last log entry, or zero for an empty log.</param>
/// <remarks>
/// <see cref="LastLogIndex"/> and <see cref="LastLogTerm"/> are the inputs to the election restriction: a
/// voter grants only to a candidate whose log is at least as up-to-date as its own, which is what guarantees
/// a new leader holds every committed entry.
/// </remarks>
public sealed record RequestVoteRequest(long Term, ReplicaId CandidateId, long LastLogIndex, long LastLogTerm);
