using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The AppendEntries RPC a leader sends to replicate log entries and, as a degenerate empty case, to assert
/// its leadership as a heartbeat (Raft paper, Figure 2, AppendEntries RPC).
/// </summary>
/// <typeparam name="TCommand">The application command type carried by the entries.</typeparam>
/// <param name="Term">The leader's term.</param>
/// <param name="LeaderId">The leader sending the request, so a follower can redirect clients to it.</param>
/// <param name="PrevLogIndex">
/// The 1-based index of the log entry immediately preceding <paramref name="Entries"/>, or zero when the
/// entries start at the head of the log.
/// </param>
/// <param name="PrevLogTerm">The term of the entry at <paramref name="PrevLogIndex"/>, ignored when that index is zero.</param>
/// <param name="Entries">
/// The entries to store, beginning at <paramref name="PrevLogIndex"/> + 1. Empty for a pure heartbeat.
/// </param>
/// <param name="LeaderCommit">The leader's commit index, used to advance the follower's own commit index.</param>
/// <remarks>
/// <paramref name="PrevLogIndex"/> and <paramref name="PrevLogTerm"/> form the consistency check: a follower
/// accepts the entries only if its log contains a matching entry at that position, which by induction means
/// the two logs are identical through <paramref name="PrevLogIndex"/>. The leader walks
/// <c>nextIndex</c> backwards on rejection until a matching prefix is found.
/// </remarks>
public sealed record AppendEntriesRequest<TCommand>(
    long Term,
    ReplicaId LeaderId,
    long PrevLogIndex,
    long PrevLogTerm,
    ImmutableArray<RaftLogEntry<TCommand>> Entries,
    long LeaderCommit);
