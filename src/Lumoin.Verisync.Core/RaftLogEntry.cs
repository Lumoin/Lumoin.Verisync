namespace Lumoin.Verisync.Core;

/// <summary>
/// One entry in a Raft replicated log: an application <paramref name="Command"/> tagged with the
/// <paramref name="Term"/> in which the leader that originated it held office.
/// </summary>
/// <typeparam name="TCommand">The application command type carried by the entry.</typeparam>
/// <param name="Term">
/// The term of the leader that created this entry. The term stamp is what lets followers detect log
/// divergence: two logs that agree on the term at a given index agree on every entry up to it, the
/// inductive invariant that the consistency check in
/// <see cref="RaftNode{TCommand}.HandleAppendEntries"/> relies on.
/// </param>
/// <param name="Command">The application command this entry replicates and, once committed, applies.</param>
/// <remarks>
/// An entry is immutable. The log itself is an ordered, append-mostly sequence whose protocol indices are
/// 1-based — protocol index <c>i</c> is the entry at zero-based position <c>i - 1</c> — while truncation on
/// conflict is the only way an already-stored index changes, and only on a follower reconciling with a
/// newer leader.
/// </remarks>
public sealed record RaftLogEntry<TCommand>(long Term, TCommand Command);
