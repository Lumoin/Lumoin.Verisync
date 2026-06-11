using System.Collections.Immutable;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The durable state of a <see cref="RaftNode{TCommand}"/>: the Figure 2 persistent triple a node must
/// have on stable storage before any reply that depends on it leaves the process. Obtain it with
/// <see cref="RaftNode{TCommand}.ToState"/> and reconstruct with <see cref="RaftNode{TCommand}.FromState"/>.
/// </summary>
/// <typeparam name="TCommand">The application command type carried by the log.</typeparam>
/// <param name="CurrentTerm">The latest term the node has seen. Monotonically non-decreasing, never negative.</param>
/// <param name="VotedFor">
/// The raw identifier bytes of the candidate the node voted for in <paramref name="CurrentTerm"/>, or empty
/// when it has not voted — the empty-means-no-vote convention of <see cref="LwwRegisterState{TValue}.Writer"/>.
/// </param>
/// <param name="Log">The replicated log, in protocol-index order (index <c>i</c> is <c>Log[i - 1]</c>).</param>
/// <remarks>
/// These three fields are exactly the Raft paper's Figure 2 persistent state (Ongaro and Ousterhout, "In
/// Search of an Understandable Consensus Algorithm"); the role, commit index, leader, and the leader's
/// replication bookkeeping are all volatile and are rediscovered after a restart. Raft safety across a crash
/// requires this triple to be durable <em>before</em> the reply that depends on it is sent: a node that
/// restarts having forgotten a granted vote can vote twice in a term, and one that forgot an appended entry
/// can lose a committed entry, breaking election safety and log matching. The persist-before-reply
/// obligation is the same fail-closed sequencing <see cref="ConsensusNode{TValue}"/> documents.
/// </remarks>
public sealed record RaftNodeState<TCommand>(long CurrentTerm, ImmutableArray<byte> VotedFor, ImmutableArray<RaftLogEntry<TCommand>> Log);
