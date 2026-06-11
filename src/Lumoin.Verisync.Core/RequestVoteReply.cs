namespace Lumoin.Verisync.Core;

/// <summary>
/// The reply to a <see cref="RequestVoteRequest"/> (Raft paper, Figure 2, RequestVote RPC results).
/// </summary>
/// <param name="Term">
/// The voter's current term. A candidate that sees a term greater than its own here abandons the election
/// and reverts to follower.
/// </param>
/// <param name="VoteGranted"><see langword="true"/> if the voter granted its vote for this term to the candidate.</param>
public sealed record RequestVoteReply(long Term, bool VoteGranted);
