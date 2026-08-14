------------------------------ MODULE RaftLog ------------------------------
(*
Raft's log rules: the Figure 8 commit restriction and the persist-before-reply
obligation, over five servers because both hazards need a minority replication
that a later term revisits and three servers cannot express one.

Elections are abstracted to a single Elect action that raises the term and
requires the paper's election restriction against a quorum, rather than being
run message by message. That abstraction is sound for these two properties:
the restriction is what a real election enforces through votes, and running the
votes explicitly would only add interleavings that reach the same leader states.
RaftElection carries the message-level election, including the sender identity
the vote tally must filter, which this module does not model.

Replication copies the leader's whole log to the follower, which is what
Raft's consistency check plus conflict truncation converge to. It is an
over-approximation only in how FAST a follower catches up, and a faster catch-up
reaches strictly more states than a slower one.

The two rules are constants because the model is what says they are
load-bearing rather than tidy.

CommitRequiresCurrentTerm is Figure 8. A leader may count replicas to commit an
entry only when that entry was created in the leader's own term. Withdrawing it
lets a leader commit an inherited entry that a quorum happens to hold, after
which a server that never held it can still win an election and overwrite it.

CountsOnlyPersistedReplicas is the persist-before-reply obligation. A follower
that has appended an entry but not made it durable has answered on state it can
lose. Withdrawing it lets a commit rest on a replica that a crash rolls back,
and the committed entry then exists nowhere a later leader must carry it.

LeaderCompleteness is the property both rules protect: every committed entry is
present, at the same index and term, in the log of every leader that follows.
It is the invariant the paper's Figure 8 discussion exists to preserve, and a
violation of it is the point at which an applied command can be taken back.
*)
EXTENDS Naturals, Sequences, FiniteSets, TLC

CONSTANTS
    Servers,                     \* The fixed cluster membership.
    MaxTerm,                     \* The highest term a leader may be elected at.
    MaxLogLength,                \* The longest log a server may hold.
    CommitRequiresCurrentTerm,   \* Figure 8: count replicas only for an entry of the leader's own term.
    CountsOnlyPersistedReplicas, \* Durability: count a replica only where the entry is durable there.
    CrashesEnabled,              \* Whether a server may lose everything it has not persisted.
    NoLeader                     \* The sentinel for a term with no leader yet.

ASSUME Servers # {}
ASSUME NoLeader \notin Servers
ASSUME MaxTerm \in Nat
ASSUME MaxLogLength \in Nat
ASSUME CommitRequiresCurrentTerm \in BOOLEAN
ASSUME CountsOnlyPersistedReplicas \in BOOLEAN
ASSUME CrashesEnabled \in BOOLEAN

(* The servers are interchangeable here. Every action and every invariant reads
   a server only through equality, set membership, or a cardinality over a set
   comprehension; a log entry carries the term that created it rather than the
   server that appended it; and nothing chooses a server. Permuting the
   membership therefore carries every behaviour to a behaviour and every
   invariant to itself, so quotienting by it is sound.

   The declaration covers the invariants these configurations check and nothing
   else. A liveness property needs it withdrawn, because the quotient can hide
   the temporal counterexample rather than report it.

   Init gives every server the same empty log and a persisted length of zero,
   which is closed under the permutation. A configuration that pinned an
   asymmetric starting state and kept the declaration would be unsound and would
   not announce itself. *)
ServerSymmetry == Permutations(Servers)

VARIABLES
    logs,          \* logs[s], the sequence of terms server s holds. The command is the term that created it.
    persisted,     \* persisted[s], how much of logs[s] survives a crash.
    term,          \* The current term. Monotone, and elections are the only thing that raises it.
    leader,        \* The leader of the current term, or NoLeader.
    committedLog   \* The prefix some leader has declared committed. Ghost state for LeaderCompleteness.

vars == <<logs, persisted, term, leader, committedLog>>

Quorum == (Cardinality(Servers) \div 2) + 1

\* The term of a log's last entry, and zero for an empty log.
LastTerm(l) == IF Len(l) = 0 THEN 0 ELSE l[Len(l)]

\* Raft's election restriction: a candidate's log must be at least as up to date
\* as the voter's, by last term first and length second.
AtLeastAsUpToDate(a, b) ==
    \/ LastTerm(a) > LastTerm(b)
    \/ (LastTerm(a) = LastTerm(b) /\ Len(a) >= Len(b))

IsPrefix(short, long) ==
    /\ Len(short) <= Len(long)
    /\ \A i \in 1..Len(short) : short[i] = long[i]

\* Whether server f counts toward a commit of index i under the leader's log.
\* The durability clause is the persist-before-reply obligation: an entry a
\* follower can still lose is not replicated for the purpose of committing.
Counts(f, leaderLog, i) ==
    /\ Len(logs[f]) >= i
    /\ \A k \in 1..i : logs[f][k] = leaderLog[k]
    /\ (CountsOnlyPersistedReplicas => persisted[f] >= i)


Init ==
    /\ logs = [s \in Servers |-> <<>>]
    /\ persisted = [s \in Servers |-> 0]
    /\ term = 0
    /\ leader = NoLeader
    /\ committedLog = <<>>


\* A server wins the next term from a quorum whose logs it is at least as up to
\* date as. The votes themselves are not modelled; the restriction they enforce
\* is what matters here.
Elect(s) ==
    /\ term < MaxTerm
    /\ \E voters \in SUBSET Servers :
        /\ Cardinality(voters) >= Quorum
        /\ \A f \in voters : AtLeastAsUpToDate(logs[s], logs[f])
    /\ term' = term + 1
    /\ leader' = s
    /\ UNCHANGED <<logs, persisted, committedLog>>


\* The leader appends a command of its own term. Nothing is durable until
\* Persist runs, which is what lets a crash take it back.
AppendEntry(s) ==
    /\ leader = s
    /\ Len(logs[s]) < MaxLogLength
    /\ logs' = [logs EXCEPT ![s] = Append(logs[s], term)]
    /\ UNCHANGED <<persisted, term, leader, committedLog>>


\* A server makes what it holds durable.
Persist(s) ==
    /\ persisted[s] < Len(logs[s])
    /\ persisted' = [persisted EXCEPT ![s] = Len(logs[s])]
    /\ UNCHANGED <<logs, term, leader, committedLog>>


\* The leader brings a follower's log to its own, which is what the consistency
\* check and conflict truncation converge to. A follower cannot keep as durable
\* more than it now holds.
Replicate(s, f) ==
    /\ leader = s
    /\ s # f
    /\ logs[f] # logs[s]
    /\ logs' = [logs EXCEPT ![f] = logs[s]]
    /\ persisted' = [persisted EXCEPT ![f] = IF persisted[f] > Len(logs[s]) THEN Len(logs[s]) ELSE persisted[f]]
    /\ UNCHANGED <<term, leader, committedLog>>


\* The leader declares a prefix committed. Both rules gate this and nothing else.
Commit(s, i) ==
    /\ leader = s
    /\ i <= Len(logs[s])
    /\ i > Len(committedLog)
    /\ (CommitRequiresCurrentTerm => logs[s][i] = term)
    /\ Cardinality({f \in Servers : Counts(f, logs[s], i)}) >= Quorum
    /\ committedLog' = SubSeq(logs[s], 1, i)
    /\ UNCHANGED <<logs, persisted, term, leader>>


\* A crash takes back everything the server had not made durable, and a server
\* that crashed restarts as a follower. The role is volatile by Figure 2 and
\* RaftNode.FromState restores a follower, so a crash of the leader leaves the
\* term without one until a fresh election. Leaving it leading would let a
\* server that lost its own uncommitted tail keep replicating and committing on
\* it, which is a state no deployment reaches.
\*
\* The guard admits only a crash that loses something. A crash that loses
\* nothing differs from this one only in clearing the leadership, and every
\* state that reaches is reachable without it, because Elect does not require
\* the term to be leaderless.
Crash(s) ==
    /\ CrashesEnabled
    /\ persisted[s] < Len(logs[s])
    /\ logs' = [logs EXCEPT ![s] = SubSeq(logs[s], 1, persisted[s])]
    /\ leader' = IF leader = s THEN NoLeader ELSE leader
    /\ UNCHANGED <<persisted, term, committedLog>>


Next ==
    \/ \E s \in Servers : Elect(s)
    \/ \E s \in Servers : AppendEntry(s)
    \/ \E s \in Servers : Persist(s)
    \/ \E s, f \in Servers : Replicate(s, f)
    \/ \E s \in Servers, i \in 1..MaxLogLength : Commit(s, i)
    \/ \E s \in Servers : Crash(s)


Spec == Init /\ [][Next]_vars


TypeOK ==
    /\ term \in 0..MaxTerm
    /\ leader \in Servers \union {NoLeader}
    /\ Len(committedLog) <= MaxLogLength
    /\ \A s \in Servers : Len(logs[s]) <= MaxLogLength
    /\ \A s \in Servers : persisted[s] <= Len(logs[s])


\* Every committed entry is present, at the same index and term, in the log of
\* the leader of every later term. A leader that lacks a committed entry is a
\* leader that can overwrite it, which is the point an applied command is taken
\* back.
LeaderCompleteness ==
    leader = NoLeader \/ IsPrefix(committedLog, logs[leader])


\* A committed entry is durable somewhere, which is what makes it survive the
\* crash of any single server the commit counted.
CommittedIsDurableAtAQuorum ==
    Len(committedLog) = 0
    \/ Cardinality({f \in Servers :
        /\ persisted[f] >= Len(committedLog)
        /\ IsPrefix(committedLog, logs[f])}) >= Quorum

=============================================================================
