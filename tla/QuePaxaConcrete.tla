--------------------------- MODULE QuePaxaConcrete ---------------------------
(*
Algorithm 4, the concrete QuePaxa proposer, over the constant-space interval
summary registers of Algorithm 3, on an asynchronous network. Proposers and
recorders are separate roles, all communication is proposer-to-recorder RPC,
and the adversary chooses both the order in which a recorder serves the
requests it holds and which majority of replies a proposer acts on. A step is
4 * round + phase and the execution starts at step 4.

This module exists for one hazard. Algorithm 4's fast path decides at the end
of phase 0 when every reply in the quorum carries a first proposal with the
reserved priority H, and returns the value from any of them. Lemma C.10 proves
that correct, but its proof turns on one sentence - that only the leader's
proposal has priority H - and Section 4.2.5 obtains that by assuming all
proposers have already agreed on who leads the round. When two proposers each
believe they lead, two proposals carry H, recorders record different ones
first, and two quorums of all-H replies can carry different values. That
assumption is the one safety property Fast CASPaxos never has to hold, so it is
modelled here rather than argued.

Three defences are separate constants because they bind the leader in different
places and the model is what says which of them is load-bearing.
IdenticalKeyFastPath moves the fast-path test from the priority to the whole
proposal. ReservedFromLeaderOnly has each recorder honour the reserved priority
only on a proposal from the instance's configured leader. RecorderBindsLeader
has each recorder bind the first proposer it sees claim the reserved priority
and honour no other afterwards. A recorder that declines to honour the reserved
priority records the proposal at the lowest ordinary priority rather than
dropping it, which is the paper's own phrasing that the round then proceeds
through the ordinary phases, and which keeps the register free of holes.

A fourth defence is modelled because it binds the leader somewhere none of the
first three does and because a reference implementation relies on it.
DeclaredScheduleBinds has every request carry the leader its proposer believes
in, and has each recorder bind the first declaration it sees for the instance
and refuse any later request naming a different one. Refusal is not a downgrade:
the register is not written and no reply is created, which is what an error
return is here. A recorder holds exactly one binding, so two proposers naming
different leaders cannot both reach a majority, and the loser makes no progress
rather than diverging. That trade is why its configuration does not check
deadlock - a permanently blocked proposer is the defence working. It is also why
this defence needs no agreed configuration at the recorders, which is the whole
of its appeal against ReservedFromLeaderOnly.

Two modelling decisions are worth stating. A reply the requesting proposer has
already stepped past is not created, because it would be discarded on arrival;
the recorder still applies the request to its register, so a late request keeps
its effect on the clock and the aggregate. And crashes are not modelled here:
asynchrony already admits every interleaving the hazard needs, and the crash
case is carried by QuePaxaAbstract.

Refinement onto QuePaxaAbstract is argued rather than checked. Appendix C is
itself a prose refinement argument, and reconstructing the abstract proposal
sets from the register summaries would need ghost state that the interval
summary register exists to avoid keeping.
*)
EXTENDS Naturals, FiniteSets, TLC

CONSTANTS
    Proposers,                \* The active roles, as naturals, so proposal keys are ordered.
    Recorders,                \* The passive roles.
    Values,                   \* The values a proposer can prefer.
    Priorities,               \* The ordinary priorities, as naturals, all below H.
    H,                        \* The priority reserved for the round-one leader.
    Prefers,                  \* Each proposer's input value.
    Leaders,                  \* The proposers that believe they lead round one.
    ConfiguredLeader,         \* The proposer the recorders were told leads, or NoProposer.
    RecorderLeader,           \* The leader each recorder honours, which need not be uniform.
    MaxStep,                  \* The last step a proposer runs.
    IdenticalKeyFastPath,     \* Defence: the fast path needs one proposal, not one priority.
    ReservedFromLeaderOnly,   \* Defence: a recorder honours H only from the leader it holds.
    RecorderBindsLeader,      \* Defence: recorders bind the first claimant of H.
    DowngradeAtFirstStepOnly, \* Whether the downgrade applies only where the fast path reads it.
    DeclaredScheduleBinds,    \* Defence: recorders bind the first declared schedule and refuse others.
    NoValue                   \* The sentinel for a proposer that has not decided.

NoProposer == 0

ASSUME Proposers \subseteq (Nat \ {0}) /\ Proposers # {}
ASSUME Recorders # {}
ASSUME Priorities \subseteq Nat /\ Priorities # {}
ASSUME \A p \in Priorities : 0 < p /\ p < H
ASSUME Prefers \in [Proposers -> Values]
ASSUME Leaders \subseteq Proposers
ASSUME ConfiguredLeader \in Proposers \union {NoProposer}
ASSUME NoValue \notin Values

\* The leader is per recorder rather than a single scalar, because a deployment
\* derives it from committed state and two recorders that have learned different
\* amounts would otherwise be unrepresentable. A uniform configuration is the
\* constant function UniformLeader, which is what a configuration names when the
\* whole recorder set honours one leader.
ASSUME RecorderLeader \in [Recorders -> Proposers \union {NoProposer}]
ASSUME DowngradeAtFirstStepOnly \in BOOLEAN

\* NoProposer marks a recorder that has bound no declaration yet, so a
\* configuration running the declared-schedule defence must name a real proposer
\* as the agreed leader; otherwise a declaration of "no leader" would be
\* indistinguishable from the absence of a binding.
ASSUME DeclaredScheduleBinds => ConfiguredLeader \in Proposers

VARIABLES
    rstep,       \* Each recorder's ISR step S.
    rfirst,      \* Each recorder's first proposal in step S, which is Algorithm 3's F_c.
    ragg,        \* Each recorder's aggregate over step S, which is A_c.
    rprior,      \* Each recorder's aggregate over step S - 1, which is A_p.
    rbound,      \* The proposer each recorder bound to the reserved priority.
    rsched,      \* The declared schedule each recorder bound, or NoProposer.
    pstep,       \* Each proposer's step s.
    ptmpl,       \* Each proposer's working proposal p.
    pdec,        \* The value each proposer decided, or NoValue.
    sent,        \* Whether each proposer has sent the requests for the step it is on.
    reqs,        \* The requests in flight.
    reps         \* The replies in flight that their proposer can still read.

vars == <<rstep, rfirst, ragg, rprior, rbound, rsched, pstep, ptmpl, pdec, sent, reqs, reps>>

\* The leader each proposer declares: itself when it believes it leads, and the
\* agreed leader otherwise. A declaration is fixed for the whole run, so a
\* request carries no separate field for it and the recorder computes it from the
\* requesting proposer. A protocol whose proposers could revise a declaration
\* mid-run would have to put it in the request.
DeclaredLeader(i) == IF i \in Leaders THEN i ELSE ConfiguredLeader

MinPriority == CHOOSE p \in Priorities : \A q \in Priorities : p =< q

AllPriorities == Priorities \union {H}

\* A proposal is the triple of Section 4.2.4, ordered by priority and then by
\* proposer, which is Appendix A's tiebreaking approach.
Proposals == [priority: AllPriorities, proposer: Proposers, value: Values]

\* The register's base value, for which aggregate(v, nil) = v. The integer
\* encoding of Section 4.2.3 uses zero.
NilProp == [priority |-> 0, proposer |-> NoProposer, value |-> NoValue]

Summaries == Proposals \union {NilProp}

Outranks(p, q) ==
    \/ p.priority > q.priority
    \/ /\ p.priority = q.priority
       /\ p.proposer > q.proposer

Aggregate(p, q) == IF Outranks(q, p) THEN q ELSE p

BestOf(S) == CHOOSE p \in S : \A q \in S : ~Outranks(q, p)

Requests == [prop: Proposers, rec: Recorders, step: Nat, proposal: Proposals]

Replies == [prop: Proposers, rec: Recorders, step: Nat,
            rs: Nat, rf: Summaries, ra: Summaries]

Majority(M) == 2 * Cardinality(M) > Cardinality(Recorders)

TypeOK ==
    /\ rstep \in [Recorders -> 0..(MaxStep + 1)]
    /\ rfirst \in [Recorders -> Summaries]
    /\ ragg \in [Recorders -> Summaries]
    /\ rprior \in [Recorders -> Summaries]
    /\ rbound \in [Recorders -> Proposers \union {NoProposer}]
    /\ rsched \in [Recorders -> Proposers \union {NoProposer}]
    /\ pstep \in [Proposers -> 0..(MaxStep + 1)]
    /\ ptmpl \in [Proposers -> Summaries]
    /\ pdec \in [Proposers -> Values \union {NoValue}]
    /\ sent \in [Proposers -> BOOLEAN]
    /\ reqs \subseteq Requests
    /\ reps \subseteq Replies

Init ==
    /\ rstep = [j \in Recorders |-> 0]
    /\ rfirst = [j \in Recorders |-> NilProp]
    /\ ragg = [j \in Recorders |-> NilProp]
    /\ rprior = [j \in Recorders |-> NilProp]
    /\ rbound = [j \in Recorders |-> NoProposer]
    /\ rsched = [j \in Recorders |-> NoProposer]
    /\ pstep = [i \in Proposers |-> 4]
    /\ ptmpl = [i \in Proposers |-> [priority |-> H, proposer |-> i, value |-> Prefers[i]]]
    /\ pdec = [i \in Proposers |-> NoValue]
    /\ sent = [i \in Proposers |-> FALSE]
    /\ reqs = {}
    /\ reps = {}

(* Proposer i sends its step's requests, one per recorder. Phase 0 draws a
   priority per recorder, which is what "chooses a random priority on behalf of
   each recorder" means, except that a proposer that believes it leads round one
   keeps the reserved priority its template started with. Later phases send the
   working proposal untouched, so the proposer field travels with the proposal
   rather than being restamped by whoever forwards it; that is what keeps two
   proposers carrying the same proposal from splitting it into two keys. *)
Send(i) ==
    /\ pdec[i] = NoValue
    /\ pstep[i] =< MaxStep
    /\ ~sent[i]
    /\ \E draw \in [Recorders -> Priorities] :
           LET s == pstep[i]
               randomizes == s % 4 = 0 /\ (s > 4 \/ i \notin Leaders)
               ProposalFor(j) ==
                   IF randomizes
                   THEN [ptmpl[i] EXCEPT !.priority = draw[j]]
                   ELSE ptmpl[i]
           IN reqs' = reqs \union
                  {[prop |-> i, rec |-> j, step |-> s, proposal |-> ProposalFor(j)] : j \in Recorders}
    /\ sent' = [sent EXCEPT ![i] = TRUE]
    /\ UNCHANGED <<rstep, rfirst, ragg, rprior, rbound, rsched, pstep, ptmpl, pdec, reps>>

\* Whether recorder j honours the reserved priority on this proposal. A proposal
\* that does not carry the reserved priority is never affected.
Honoured(j, p) ==
    \/ p.priority # H
    \/ /\ (ReservedFromLeaderOnly => p.proposer = RecorderLeader[j])
       /\ (RecorderBindsLeader => rbound[j] \in {NoProposer, p.proposer})

\* What the register actually takes in. A proposal whose reserved priority is not
\* honoured is recorded at the lowest ordinary priority, so the round proceeds
\* through the ordinary phases instead of losing the proposal.
\*
\* DowngradeAtFirstStepOnly narrows where the rule applies. The reserved
\* priority earns a decision only at the round's first step, which is the only
\* step FastPath is consulted at, so downgrading above that step defends nothing
\* by itself. It does cost something: a downgrade rewrites the priority, so a
\* recorder that declines a claim its neighbours honour records the same logical
\* proposal under a second key, and a quorum holding only the rewritten copy can
\* carry an ordinary proposal past it. Restricting the rule to the first step is
\* what makes a recorder honouring no leader among led ones survivable; it does
\* nothing for recorders that honour different leaders, and the configurations
\* naming this constant are what establish both halves.
Admitted(j, p, s) ==
    IF \/ Honoured(j, p)
       \/ (DowngradeAtFirstStepOnly /\ s > 4)
    THEN p
    ELSE [p EXCEPT !.priority = MinPriority]

\* Whether recorder j refuses this request outright, which it does only when it
\* has already bound a different declared schedule for the instance. Like the
\* reserved-priority binding, this one is tracked only where a defence reads it,
\* so rsched stays at NoProposer in every configuration that leaves the defence
\* off and the state space of those configurations is unchanged.
Refuses(j, q) ==
    /\ DeclaredScheduleBinds
    /\ rsched[j] # NoProposer
    /\ rsched[j] # DeclaredLeader(q.prop)

(* Recorder j refuses one request it holds. The request leaves the network, the
   register is not written, and no reply is created, so the requesting proposer
   is one recorder short of a quorum for as long as the binding stands. This is
   the whole mechanism of the declared-schedule defence: it converts a possible
   disagreement into a certain lack of progress for the proposer that is wrong
   about the schedule. *)
Refuse(j, q) ==
    /\ q \in reqs
    /\ q.rec = j
    /\ Refuses(j, q)
    /\ reqs' = reqs \ {q}
    /\ UNCHANGED <<rstep, rfirst, ragg, rprior, rbound, rsched, pstep, ptmpl, pdec, sent, reps>>

(* Recorder j serves one request it holds. This is Algorithm 3's record, with
   the three cases on the request's step, followed by the reply. The reply is
   only created when the requesting proposer is still at the step it asked
   about, because a reply from an earlier step is discarded on arrival; the
   register update happens either way, which is what makes a late request still
   count. *)
Serve(j, q) ==
    LET p == Admitted(j, q.proposal, q.step)
        \* The binding is only tracked where a defence reads it. Recording it
        \* anyway would split states that no configuration can tell apart. It
        \* binds on the claim rather than on the register having accepted it,
        \* which is indistinguishable here because the reserved priority is only
        \* ever sent in the round's first step; a configuration that ran the
        \* reserved priority into a later round would have to gate the binding
        \* on acceptance.
        bound == IF /\ RecorderBindsLeader
                    /\ q.proposal.priority = H
                    /\ rbound[j] = NoProposer
                    /\ Honoured(j, q.proposal)
                 THEN q.proposal.proposer
                 ELSE rbound[j]
        advances == q.step > rstep[j]
        newStep == IF advances THEN q.step ELSE rstep[j]
        newFirst == IF advances THEN p ELSE rfirst[j]
        newAgg == IF advances
                  THEN p
                  ELSE IF q.step = rstep[j] THEN Aggregate(ragg[j], p) ELSE ragg[j]
        newPrior == IF advances
                    THEN (IF q.step = rstep[j] + 1 THEN ragg[j] ELSE NilProp)
                    ELSE rprior[j]
        \* The first declaration a recorder serves is the one it binds, and the
        \* binding stands for the rest of the instance.
        newSched == IF DeclaredScheduleBinds /\ rsched[j] = NoProposer
                    THEN DeclaredLeader(q.prop)
                    ELSE rsched[j]
    IN  /\ q \in reqs
        /\ q.rec = j
        /\ ~Refuses(j, q)
        /\ rstep' = [rstep EXCEPT ![j] = newStep]
        /\ rfirst' = [rfirst EXCEPT ![j] = newFirst]
        /\ ragg' = [ragg EXCEPT ![j] = newAgg]
        /\ rprior' = [rprior EXCEPT ![j] = newPrior]
        /\ rbound' = [rbound EXCEPT ![j] = bound]
        /\ rsched' = [rsched EXCEPT ![j] = newSched]
        /\ reqs' = reqs \ {q}
        /\ reps' = IF pstep[q.prop] = q.step
                   THEN reps \union {[prop |-> q.prop, rec |-> j, step |-> q.step,
                                      rs |-> newStep, rf |-> newFirst, ra |-> newPrior]}
                   ELSE reps
        /\ UNCHANGED <<pstep, ptmpl, pdec, sent>>

\* An assignment giving each proposer its own value, so that the model always
\* has a disagreement to resolve. The configurations name this because a
\* configuration file cannot write a function down.
DistinctPreferences ==
    CHOOSE f \in [Proposers -> Values] : \A i, k \in Proposers : i # k => f[i] # f[k]

\* The three recorder-leader assignments the configurations name, for the same
\* reason DistinctPreferences is named: a configuration file cannot write a
\* function down. UniformLeader is the agreed deployment, where every recorder
\* honours the same leader. The other two are the misconfigurations a versioned
\* register must make unreachable. SplitLeaders breaks agreement under either
\* downgrade width; MixedLeaderless breaks it only under the wide one, which is
\* not the rule the library applies, and the configurations naming
\* DowngradeAtFirstStepOnly are what establish the difference.
UniformLeader == [j \in Recorders |-> ConfiguredLeader]

\* One recorder has not learned the previous version and so honours no leader,
\* while the others honour the agreed one. It looks harmless and is not once the
\* downgrade rewrites the priority at every step: the leader's one proposal then
\* exists under two keys through the ordinary phases, and a quorum holding only
\* the downgraded copy can carry an ordinary proposal past it. Under the first
\* step alone the rewrite reaches no carried template and the configuration is
\* green.
MixedLeaderless ==
    [j \in Recorders |-> IF j = 3 THEN NoProposer ELSE ConfiguredLeader]

\* Two recorders honour the agreed leader and one honours a different proposer,
\* which puts two reserved-priority proposals in play at the step the fast path
\* reads rather than one proposal under two keys.
SplitLeaders ==
    [j \in Recorders |->
        IF j = 3 THEN (CHOOSE i \in Proposers : i # ConfiguredLeader) ELSE ConfiguredLeader]

\* The replies proposer i can act on, which are the ones answering the request
\* it sent at the step it is on.
Answers(i) == {r \in reps : r.prop = i /\ r.step = pstep[i]}

\* Algorithm 4's fast path at the end of phase 0. Without the defence the test
\* is the paper's: every first proposal carries the reserved priority.
FastPath(R) ==
    /\ \A r \in R : r.rf.priority = H
    /\ IdenticalKeyFastPath => \A r1, r2 \in R : r1.rf = r2.rf

(* Proposer i acts on a majority of the replies to its current request. The
   quorum is the adversary's choice, so both the all-at-my-step branch and the
   catch-up branch are reachable from the same state whenever both quorums
   exist. Lemma C.2 rules out a reply from below the proposer's step, so the two
   branches are exhaustive. *)
Act(i) ==
    /\ pdec[i] = NoValue
    /\ sent[i]
    /\ \E R \in SUBSET Answers(i) :
           /\ Majority(R)
           /\ LET s == pstep[i]
                  firsts == {r.rf : r \in R}
                  priors == {r.ra : r \in R}
              IN IF \A r \in R : r.rs = s
                 THEN \/ /\ s % 4 = 0
                         /\ FastPath(R)
                         /\ \E r \in R :
                                pdec' = [pdec EXCEPT ![i] = r.rf.value]
                         /\ UNCHANGED <<pstep, ptmpl>>
                      \/ /\ \/ /\ s % 4 = 0
                               /\ ~FastPath(R)
                               /\ ptmpl' = [ptmpl EXCEPT ![i] = BestOf(firsts)]
                               /\ pdec' = pdec
                            \/ /\ s % 4 = 1
                               /\ UNCHANGED <<ptmpl, pdec>>
                            \/ /\ s % 4 = 2
                               /\ IF ptmpl[i] = BestOf(priors)
                                  THEN pdec' = [pdec EXCEPT ![i] = ptmpl[i].value]
                                  ELSE pdec' = pdec
                               /\ UNCHANGED ptmpl
                            \/ /\ s % 4 = 3
                               /\ ptmpl' = [ptmpl EXCEPT ![i] = BestOf(priors)]
                               /\ pdec' = pdec
                         /\ pstep' = [pstep EXCEPT ![i] = s + 1]
                 ELSE /\ \E r \in R :
                             /\ r.rs > s
                             /\ pstep' = [pstep EXCEPT ![i] = r.rs]
                             /\ ptmpl' = [ptmpl EXCEPT ![i] = r.rf]
                      /\ pdec' = pdec
    \* Whatever the proposer just did, the replies it acted on answer a step it
    \* has left, so nothing can read them again.
    /\ reps' = {r \in reps : r.prop # i}
    /\ sent' = [sent EXCEPT ![i] = FALSE]
    /\ UNCHANGED <<rstep, rfirst, ragg, rprior, rbound, rsched, reqs>>

\* The model runs a fixed number of steps. Marking the end explicitly keeps the
\* deadlock check about states in which the protocol still had work to do.
Halted ==
    /\ reqs = {}
    /\ \A i \in Proposers : pdec[i] # NoValue \/ pstep[i] > MaxStep
    /\ UNCHANGED vars

Next ==
    \/ \E i \in Proposers : Send(i) \/ Act(i)
    \* Serving and refusing are separate disjuncts rather than one disjunction
    \* under the quantifiers, so that a coverage run reports them separately and
    \* can say whether a configuration reached the refusal at all.
    \/ \E j \in Recorders : \E q \in reqs : Serve(j, q)
    \/ \E j \in Recorders : \E q \in reqs : Refuse(j, q)
    \/ Halted

Spec == Init /\ [][Next]_vars

\* Lemma B.7 carried through the refinement: no two proposers decide differently.
Agreement ==
    \A i, k \in Proposers :
        (pdec[i] # NoValue /\ pdec[k] # NoValue) => pdec[i] = pdec[k]

\* Lemma B.6: a decided value is one that some proposer preferred.
Validity ==
    \A i \in Proposers :
        pdec[i] # NoValue => \E k \in Proposers : pdec[i] = Prefers[k]

\* Lemma C.2: a proposer never receives a reply from below the step it asked
\* about, which is what makes the catch-up branch the only alternative to
\* advancing.
ReplyStepAtLeastRequest == \A r \in reps : r.rs >= r.step

\* A recorder's clock only ever runs forward, which is what Lemma C.10 leans on
\* when it argues that the first proposal of a step is never overwritten.
StepDiscipline ==
    /\ \A j \in Recorders : rstep[j] = 0 => rfirst[j] = NilProp
    /\ \A r \in reps : r.rs =< rstep[r.rec]

=============================================================================
