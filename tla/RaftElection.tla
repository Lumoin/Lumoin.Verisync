--------------------------- MODULE RaftElection ---------------------------
(*
Raft's leader election over a fixed membership, on an asynchronous network
where a message once sent is never withdrawn, so the adversary chooses
delivery order and may redeliver anything at any time.

This module exists for one hazard, and it is a hazard no conventional Raft
specification can express. Raft specifications quantify messages over the
server set, so every sender is a member by construction and the question of a
message from somewhere else cannot be asked. A deployment cannot make that
assumption: the sender identity arrives from the wire, and a codec that
validates field shapes does not know the membership. Outsiders is the set of
identities outside the membership that may inject a well-formed message, and it
is empty in the configurations that model the closed cluster.

The hazard is that a vote tally counting a reply from any sender lets a
candidate reach a majority of the membership without a majority of the
membership having granted it. Election safety is then violated directly: two
candidates in one term each complete a quorum, one of them on a vote from
outside the cluster.

FilterNonMembers is the defence, and it is the deployment rule that a message
from outside the membership is discarded. FilterBeforeTermRule is a separate
constant because the two placements answer different questions. Raft's term
rule says a message carrying a higher term makes the receiver adopt it and step
down, and that rule is about the cluster rather than about the sender, so
placing the filter after it keeps the rule universal at the cost of letting an
outsider move a member's term. Placing the filter first denies the outsider
that lever and makes the term rule conditional on membership. NoTermInflation
is what tells the two apart: it holds exactly when no member's term exceeds a
term some member campaigned at.

Logs are not modelled. Election safety follows from one vote per term and
majority intersection alone, and the election restriction that compares logs
only ever REFUSES votes, so a model without it admits every interleaving a
model with it admits and more. A separate module carries the log rules.

Terms are bounded by MaxTerm, which is what makes the state space finite.
Outsiders may name one term above that bound, because an outsider restricted to
terms the members can reach could not demonstrate inflation at all.
*)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Servers,               \* The fixed cluster membership.
    Outsiders,             \* Identities outside the membership that may inject messages.
    MaxTerm,               \* The highest term a member may campaign at.
    FilterNonMembers,      \* Defence: a message from outside the membership is discarded.
    FilterBeforeTermRule,  \* Whether the filter runs before the term rule or after it.
    InjectsReplies,        \* Whether an outsider may inject a granted vote reply.
    InjectsRequests,       \* Whether an outsider may inject a vote request.
    Nil                    \* The sentinel for a server that has not voted.

ASSUME Servers # {}
ASSUME Outsiders \intersect Servers = {}
ASSUME Nil \notin Servers
ASSUME Nil \notin Outsiders
ASSUME MaxTerm \in Nat
ASSUME FilterNonMembers \in BOOLEAN
ASSUME FilterBeforeTermRule \in BOOLEAN
ASSUME InjectsReplies \in BOOLEAN
ASSUME InjectsRequests \in BOOLEAN

VARIABLES
    currentTerm,  \* currentTerm[s], the latest term server s has seen.
    votedFor,     \* votedFor[s], the candidate s voted for in currentTerm[s], or Nil.
    role,         \* role[s], one of Follower, Candidate or Leader.
    votes,        \* votes[s], the identities that granted s a vote in currentTerm[s].
    msgs,         \* Every message ever sent. Nothing is removed, so redelivery is always admitted.
    campaigned    \* The terms at which some member started an election. Ghost state for NoTermInflation.

vars == <<currentTerm, votedFor, role, votes, msgs, campaigned>>

Roles == {"Follower", "Candidate", "Leader"}

\* A quorum is a strict majority of the membership, which is what makes the
\* filter load-bearing: the count must be over members for the majority
\* intersection argument to hold.
Quorum == (Cardinality(Servers) \div 2) + 1

\* The terms an outsider may name. One above the members' bound, so that a term
\* no member campaigned at is expressible.
OutsiderTerms == 1..(MaxTerm + 1)

\* Whether a message from this sender is acted on at all.
Admits(id) == (~FilterNonMembers) \/ (id \in Servers)


Init ==
    /\ currentTerm = [s \in Servers |-> 0]
    /\ votedFor = [s \in Servers |-> Nil]
    /\ role = [s \in Servers |-> "Follower"]
    /\ votes = [s \in Servers |-> {}]
    /\ msgs = {}
    /\ campaigned = {}


\* A member campaigns: it raises its term, votes for itself, and asks every
\* other member for a vote.
StartElection(s) ==
    /\ currentTerm[s] < MaxTerm
    /\ LET t == currentTerm[s] + 1
       IN
        /\ currentTerm' = [currentTerm EXCEPT ![s] = t]
        /\ votedFor' = [votedFor EXCEPT ![s] = s]
        /\ role' = [role EXCEPT ![s] = "Candidate"]
        /\ votes' = [votes EXCEPT ![s] = {s}]
        /\ campaigned' = campaigned \union {t}
        /\ msgs' = msgs \union
            {[type |-> "RequestVote", term |-> t, cand |-> s, dest |-> d] : d \in Servers \ {s}}


\* Whether the receiver looks at this request at all. With the filter placed
\* before the term rule a request from outside the membership is discarded
\* whole; with it placed after, the term rule still runs on it.
RequestVoteVisible(s, m) ==
    /\ m.type = "RequestVote"
    /\ m.dest = s
    /\ (FilterBeforeTermRule => Admits(m.cand))


\* The term rule, then the vote. A request below the receiver's term is refused
\* without changing anything, which is a step that would only add states.
HandleRequestVote(s, m) ==
    /\ RequestVoteVisible(s, m)
    /\ m.term >= currentTerm[s]
    /\ LET adopt == m.term > currentTerm[s]
           heldVote == IF adopt THEN Nil ELSE votedFor[s]
           grant == (heldVote = Nil \/ heldVote = m.cand) /\ Admits(m.cand)
       IN
        /\ currentTerm' = [currentTerm EXCEPT ![s] = m.term]
        /\ role' = IF adopt THEN [role EXCEPT ![s] = "Follower"] ELSE role
        /\ votes' = IF adopt THEN [votes EXCEPT ![s] = {}] ELSE votes
        /\ votedFor' = [votedFor EXCEPT ![s] = IF grant THEN m.cand ELSE heldVote]
        /\ msgs' = msgs \union
            {[type |-> "VoteReply", term |-> m.term, from |-> s, dest |-> m.cand, granted |-> grant]}
        /\ UNCHANGED campaigned


\* Whether the receiver looks at this reply at all, on the same rule as a request.
VoteReplyVisible(s, m) ==
    /\ m.type = "VoteReply"
    /\ m.dest = s
    /\ (FilterBeforeTermRule => Admits(m.from))


\* A reply carrying a higher term forces a step-down and counts no vote. This is
\* the arm an outsider reaches when the filter sits after the term rule.
StepDownOnReply(s, m) ==
    /\ VoteReplyVisible(s, m)
    /\ m.term > currentTerm[s]
    /\ currentTerm' = [currentTerm EXCEPT ![s] = m.term]
    /\ role' = [role EXCEPT ![s] = "Follower"]
    /\ votedFor' = [votedFor EXCEPT ![s] = Nil]
    /\ votes' = [votes EXCEPT ![s] = {}]
    /\ UNCHANGED <<msgs, campaigned>>


\* The tally. Admits is read here whichever placement is configured, because a
\* filter placed after the term rule still filters the vote itself; that is the
\* whole of the defence against a forged quorum.
TallyVote(s, m) ==
    /\ VoteReplyVisible(s, m)
    /\ m.term = currentTerm[s]
    /\ m.granted
    /\ role[s] = "Candidate"
    /\ Admits(m.from)
    /\ LET tallied == votes[s] \union {m.from}
       IN
        /\ votes' = [votes EXCEPT ![s] = tallied]
        /\ role' = [role EXCEPT ![s] = IF Cardinality(tallied) >= Quorum THEN "Leader" ELSE "Candidate"]
        /\ UNCHANGED <<currentTerm, votedFor, msgs, campaigned>>


\* An outsider injects a granted vote reply. This is the forged quorum vector.
InjectVoteReply(o, d, t) ==
    /\ msgs' = msgs \union
        {[type |-> "VoteReply", term |-> t, from |-> o, dest |-> d, granted |-> TRUE]}
    /\ UNCHANGED <<currentTerm, votedFor, role, votes, campaigned>>


\* An outsider injects a vote request. This is the term-inflation vector, and it
\* is separate because it moves terms rather than completing a quorum.
InjectRequestVote(o, d, t) ==
    /\ msgs' = msgs \union
        {[type |-> "RequestVote", term |-> t, cand |-> o, dest |-> d]}
    /\ UNCHANGED <<currentTerm, votedFor, role, votes, campaigned>>


Next ==
    \/ \E s \in Servers : StartElection(s)
    \/ \E s \in Servers, m \in msgs : HandleRequestVote(s, m)
    \/ \E s \in Servers, m \in msgs : StepDownOnReply(s, m)
    \/ \E s \in Servers, m \in msgs : TallyVote(s, m)
    \/ /\ InjectsReplies
       /\ \E o \in Outsiders, d \in Servers, t \in OutsiderTerms : InjectVoteReply(o, d, t)
    \/ /\ InjectsRequests
       /\ \E o \in Outsiders, d \in Servers, t \in OutsiderTerms : InjectRequestVote(o, d, t)


Spec == Init /\ [][Next]_vars


TypeOK ==
    /\ currentTerm \in [Servers -> 0..(MaxTerm + 1)]
    /\ votedFor \in [Servers -> Servers \union Outsiders \union {Nil}]
    /\ role \in [Servers -> Roles]
    /\ votes \in [Servers -> SUBSET (Servers \union Outsiders)]
    /\ campaigned \subseteq 1..MaxTerm


\* Raft's election safety: at most one leader per term. A tally that counts a
\* non-member breaks it, because two candidates can each reach a majority count
\* while only one of them holds a majority of the membership.
ElectionSafety ==
    \A s1, s2 \in Servers :
        (/\ role[s1] = "Leader"
         /\ role[s2] = "Leader"
         /\ currentTerm[s1] = currentTerm[s2]) => s1 = s2


\* No member's term exceeds a term some member campaigned at. This separates the
\* two filter placements: with the filter after the term rule an outsider moves
\* a member's term to one no member ever reached.
NoTermInflation ==
    \A s \in Servers : currentTerm[s] = 0 \/ currentTerm[s] \in campaigned


\* A member only ever votes for a member, which is the property the durable-state
\* restore path assumes when it refuses to load a vote naming a non-member.
VoteNamesAMember ==
    \A s \in Servers : votedFor[s] = Nil \/ votedFor[s] \in Servers

=============================================================================
