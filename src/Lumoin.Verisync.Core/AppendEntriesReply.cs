namespace Lumoin.Verisync.Core;

/// <summary>
/// The reply to an <see cref="AppendEntriesRequest{TCommand}"/> (Raft paper, Figure 2, AppendEntries RPC
/// results).
/// </summary>
/// <param name="Term">
/// The follower's current term. A leader that sees a term greater than its own here steps down to follower.
/// </param>
/// <param name="Success">
/// <see langword="true"/> if the follower's log contained an entry matching <c>PrevLogIndex</c> and
/// <c>PrevLogTerm</c> and the entries were stored.
/// </param>
/// <param name="MatchIndex">
/// The highest log index now known to be replicated on the follower as a result of this request. Meaningful
/// only when <paramref name="Success"/> is <see langword="true"/>; ignored otherwise. Carrying it in the
/// reply (rather than re-deriving it on the leader) keeps the leader's <c>matchIndex</c> bookkeeping correct
/// even when replies arrive out of order.
/// </param>
public sealed record AppendEntriesReply(long Term, bool Success, long MatchIndex);
