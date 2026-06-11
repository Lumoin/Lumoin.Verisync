namespace Lumoin.Verisync.Core;

/// <summary>
/// The role a Raft node occupies in the current term. Every node is exactly one of these at any instant,
/// and the role governs which protocol actions are legal: only a <see cref="Leader"/> may append client
/// commands, only a <see cref="Candidate"/> tallies votes, and a <see cref="Follower"/> is passive,
/// replicating whatever the current leader sends.
/// </summary>
/// <remarks>
/// Role transitions are driven entirely by the safety rules of the protocol (a higher term seen, a granted
/// majority, a valid append from the current term) and never by wall-clock time inside this model: the
/// election-triggering decision that a real deployment bases on a randomized timeout lives in the host above
/// this core, which calls <see cref="RaftNode{TCommand}.StartElection"/> when it judges the leader lost.
/// </remarks>
public enum RaftRole
{
    /// <summary>
    /// The passive role: the node replicates the current leader's log and grants votes, but originates no
    /// entries. A node starts as a follower and reverts to one whenever it observes a higher term.
    /// </summary>
    Follower,

    /// <summary>
    /// The campaigning role: the node has incremented its term, voted for itself, and is soliciting votes.
    /// It becomes a <see cref="Leader"/> on a majority or a <see cref="Follower"/> on a higher term or a
    /// valid append from a current-term leader.
    /// </summary>
    Candidate,

    /// <summary>
    /// The active role: the node has won a majority for its term and is the sole originator of new log
    /// entries, replicating them to followers and advancing the commit index.
    /// </summary>
    Leader
}
