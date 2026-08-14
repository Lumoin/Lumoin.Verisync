---------------------------- MODULE QuePaxaDurable ----------------------------
(*
The durability of one QuePaxa recorder across a crash: what a recorder must
have on stable storage before it answers, what a restart brings back, and what
a restart is allowed to take away.

Lemma C.10's argument for the fast path turns on the first proposal of a step
never being overwritten. A running recorder holds that by construction, because
the interval summary register of Algorithm 3 assigns the first proposal only on
the branch that advances the step. A crash is where it can be lost: a recorder
that comes back below the step it answered from takes a fresh first proposal at
a step a proposer has already read, and two proposers reading the two different
answers can decide differently.

This module is one recorder rather than a quorum of them, and that follows from
the protocol rather than from cost. Recorders are passive and never address one
another, all communication being proposer-to-recorder, so indexing every
variable here by a recorder and quantifying every action over that index would
give a product of independent copies with no shared variable between them; the
property is the stability of what one recorder has answered, so the invariant
over the product is the conjunction of the per-recorder ones and holds exactly
when this one does. The cross-recorder property, that two quorums never decide
differently, is QuePaxaConcrete's: it carries the proposer, the quorum and the
fast path, and this module deliberately does not repeat them.

Three rules are constants, so the model is what says each of them is
load-bearing rather than tidy.

RepliesAfterPersist is the persist-before-reply obligation. A recorder answers
only from state it has made durable. Withdrawing it lets a proposer read a
summary a crash then takes back.

RestoresFromDurableState is the restore itself. A crashed recorder comes back
at its durable state rather than as a fresh unwritten register. Withdrawing it
is the library before the recorder had a restore at all: a host persisting
faithfully and restarting at step zero regardless.

PersistsWholeRegister is what must be durable. The durable state is all four
register fields rather than the step and the first proposal alone, which is
what the persistence prose used to name. Withdrawing it keeps the first
proposal and loses both aggregates, and the prior aggregate is the one a reply
carries and a proposer's phase two and phase three decisions read.

Two abstractions are worth stating. A proposal is an opaque ordered key rather
than the paper's priority, proposer and value triple, because nothing here
reads a proposal except to compare it for equality and to take the aggregate's
maximum; the reserved priority, the leader binding and the downgrade are
QuePaxaConcrete's subject. And a request arrives carrying any proposal at any
step in range in any order, rather than from a modelled proposer, which admits
every request sequence a proposer could produce and more.

The ghost state is what makes the property checkable at all. Stated as "a
proposer never reads a first proposal at a step the recorder later re-takes"
the property refers to the future, and TLC checks state predicates rather than
two-point trace properties. Recording what was answered turns it into one:
served[s] holds the summary the recorder has answered for step s, and the
invariants assert that a recorder standing at s still holds that summary. The
ghost survives a crash because it is what a proposer holds and not what the
recorder does, which is the whole of why it can express the loss.

Keeping one summary per step rather than a set of them loses nothing. A second
answer differing from the first is reachable only through a state in which the
recorder already stands at that step holding the differing summary, the
invariants fail in that state, and TLC reaches it before any action can
overwrite the ghost.
*)
EXTENDS Naturals

CONSTANTS
    Proposals,                \* The keys a request can carry, as naturals so the aggregate can order them.
    FirstStep,                \* The lowest step a recorder records at, which is round one phase zero.
    MaxStep,                  \* The highest step a request may name.
    RepliesAfterPersist,      \* Durability: a recorder answers only from state it has made durable.
    RestoresFromDurableState, \* Durability: a crashed recorder comes back at its durable state.
    PersistsWholeRegister     \* Durability: the durable state is all four register fields, not two of them.

\* The register's base value, for which the aggregate of a proposal with it is
\* that proposal. The integer encoding of Section 4.2.3 uses zero.
Nil == 0

\* The step an unwritten register stands at, which is below every step a request
\* may name and is the step a recorder with no restore comes back at.
StepZero == 0

Steps == FirstStep..MaxStep
RegisterSteps == Steps \union {StepZero}
Summaries == Proposals \union {Nil}

\* What one reply carries: the first proposal at the recorder's step and the
\* aggregate accumulated at the step below it. The aggregate accumulating at the
\* step itself is deliberately absent, because a proposer reads an aggregate one
\* step after it was accumulated and exposing the current one would let a
\* proposer read a half-formed step.
Answers == [first: Proposals, prior: Summaries]

NoAnswer == [first |-> Nil, prior |-> Nil]

ASSUME Proposals \subseteq (Nat \ {Nil}) /\ Proposals # {}
ASSUME FirstStep \in Nat /\ FirstStep > StepZero
ASSUME MaxStep \in Nat /\ MaxStep >= FirstStep
ASSUME RepliesAfterPersist \in BOOLEAN
ASSUME RestoresFromDurableState \in BOOLEAN
ASSUME PersistsWholeRegister \in BOOLEAN

VARIABLES
    step,     \* The register's step S.
    first,    \* The first proposal recorded at step S, which is Algorithm 3's F_c.
    agg,      \* The aggregate over step S, which is A_c.
    prior,    \* The aggregate over step S - 1, which is A_p.
    dstep,    \* The step a crash brings back.
    dfirst,   \* The first proposal a crash brings back.
    dagg,     \* The current aggregate a crash brings back.
    dprior,   \* The prior aggregate a crash brings back.
    served    \* Ghost state: the summary the recorder has answered for each step, or NoAnswer.

vars == <<step, first, agg, prior, dstep, dfirst, dagg, dprior, served>>

\* The fold keeps the incumbent on a tie, which is what makes an identical
\* re-delivery of one request the identity here.
Aggregate(incumbent, arriving) == IF arriving > incumbent THEN arriving ELSE incumbent

\* What a host writes when it persists, and what it has written. A host that
\* persists the step and the first proposal alone leaves both aggregates out of
\* the write, so the comparison saying a reply may leave has to read the same
\* fields the write does. Comparing all four would leave that host unable ever
\* to answer and would make its configuration vacuously green.
Volatile == IF PersistsWholeRegister THEN <<step, first, agg, prior>> ELSE <<step, first>>
Durable == IF PersistsWholeRegister THEN <<dstep, dfirst, dagg, dprior>> ELSE <<dstep, dfirst>>

IsDurable == Volatile = Durable


Init ==
    /\ step = StepZero
    /\ first = Nil
    /\ agg = Nil
    /\ prior = Nil
    /\ dstep = StepZero
    /\ dfirst = Nil
    /\ dagg = Nil
    /\ dprior = Nil
    /\ served = [s \in Steps |-> NoAnswer]


(* The recorder records one proposal at one step, which is Algorithm 3's record
   with two of its three cases. At the register's own step the aggregate folds
   the proposal in and the first proposal is untouched. Above it the step, the
   first proposal and the aggregate all take the incoming proposal, and the
   prior aggregate takes the current one when the advance is by exactly one and
   is cleared otherwise, which is the skipped-step rule. The third case, a
   request tagged below the register's step, writes nothing and answers with the
   summary the register already holds, which Observe reaches directly, so it is
   left out rather than modelled as a step that changes nothing. *)
Serve(s, p) ==
    /\ s >= step
    /\ IF s = step
       THEN /\ agg' = Aggregate(agg, p)
            /\ UNCHANGED <<step, first, prior>>
       ELSE /\ step' = s
            /\ first' = p
            /\ agg' = p
            /\ prior' = IF s = step + 1 THEN agg ELSE Nil
    /\ UNCHANGED <<dstep, dfirst, dagg, dprior, served>>


\* The host makes what the recorder holds durable. What it writes is what
\* PersistsWholeRegister names, and the fields it leaves out are cleared here
\* rather than left stale, because a host that never wrote them has nothing on
\* disk to bring back.
Persist ==
    /\ ~IsDurable
    /\ dstep' = step
    /\ dfirst' = first
    /\ dagg' = IF PersistsWholeRegister THEN agg ELSE Nil
    /\ dprior' = IF PersistsWholeRegister THEN prior ELSE Nil
    /\ UNCHANGED <<step, first, agg, prior, served>>


(* The recorder answers, and the ghost records what the answer carried. A reply
   is built from the state the record left, so an answer is the register's
   current summary; the persist gate is where the obligation lives, and
   withdrawing it lets an answer leave on state the disk does not hold. A
   register that has recorded nothing answers nothing, because every request
   reaching a recorder is at or above the round's first step and so lands on the
   advancing branch. *)
Observe ==
    /\ first # Nil
    /\ (RepliesAfterPersist => IsDurable)
    /\ served' = [served EXCEPT ![step] = [first |-> first, prior |-> prior]]
    /\ UNCHANGED <<step, first, agg, prior, dstep, dfirst, dagg, dprior>>


(* The recorder loses everything volatile and comes back. Under the restore it
   returns at its durable state, except that a durable state still at step zero
   is rebuilt as an unwritten register rather than restored, which is what a
   host's restore does with a snapshot the recorder's own restore refuses.

   That short circuit selects nothing here, and its selecting nothing is the
   result rather than an oversight. A durable state at step zero is the
   unwritten register by TypeOK's last two conjuncts, so the two branches agree
   wherever the durable state is one a faithful host wrote, and
   StepZeroDurableStateWasNeverServed is the other half: such a state has
   answered nothing, so rebuilding it unwritten takes nothing back. Together
   they are the safety argument for the rule, discharged rather than asserted.
   The branch is written the way the restore is written so that the two can be
   read against each other, and a model that dropped it would be green for a
   reason no longer visible in it.

   The guard admits only a crash that changes something. A crash bringing back
   exactly what the recorder already holds reaches no state a run without it
   does not reach. *)
Crash ==
    LET restored == IF RestoresFromDurableState /\ dstep # StepZero
                    THEN <<dstep, dfirst, dagg, dprior>>
                    ELSE <<StepZero, Nil, Nil, Nil>>
    IN  /\ restored # <<step, first, agg, prior>>
        /\ step' = restored[1]
        /\ first' = restored[2]
        /\ agg' = restored[3]
        /\ prior' = restored[4]
        /\ UNCHANGED <<dstep, dfirst, dagg, dprior, served>>


Next ==
    \/ \E s \in Steps, p \in Proposals : Serve(s, p)
    \/ Persist
    \/ Observe
    \/ Crash


Spec == Init /\ [][Next]_vars


TypeOK ==
    /\ step \in RegisterSteps
    /\ first \in Summaries
    /\ agg \in Summaries
    /\ prior \in Summaries
    /\ dstep \in RegisterSteps
    /\ dfirst \in Summaries
    /\ dagg \in Summaries
    /\ dprior \in Summaries
    /\ served \in [Steps -> Answers \union {NoAnswer}]
    \* A register above step zero carries a first proposal and one at step zero
    \* carries nothing in any slot. The recorder's step floor is what makes both
    \* true: the first record such a register ever takes lands on the advancing
    \* branch, so a step-zero state carrying a proposal is a state no host
    \* writes, which is why a restore refuses one rather than rebuilding it.
    /\ (step # StepZero) <=> (first # Nil)
    /\ (dstep # StepZero) <=> (dfirst # Nil)
    /\ step = StepZero => (agg = Nil /\ prior = Nil)
    /\ dstep = StepZero => (dagg = Nil /\ dprior = Nil)


\* Every answer the recorder has given for the step it now stands at is still
\* the answer it would give. This is Lemma C.10's hypothesis made checkable: the
\* first proposal of a step, once answered from, is never replaced.
ServedFirstIsStable ==
    \A s \in Steps : (served[s] # NoAnswer /\ step = s) => first = served[s].first


\* The same for the other half of what a reply carries. The aggregate from the
\* step below is what a proposer's phase two compares its template against and
\* what its phase three carries forward, so a recorder answering a different one
\* for a step it has already answered for takes back a value a proposer acted on.
ServedPriorAggregateIsStable ==
    \A s \in Steps : (served[s] # NoAnswer /\ step = s) => prior = served[s].prior


\* A durable state still at step zero has answered nothing, so rebuilding it as
\* an unwritten register takes nothing back. This is the premise a host's
\* step-zero short circuit rests on, and it holds only because a reply waits for
\* the write: withdraw that and a recorder answers from a step its disk has
\* never reached.
StepZeroDurableStateWasNeverServed ==
    dstep = StepZero => \A s \in Steps : served[s] = NoAnswer

=============================================================================
