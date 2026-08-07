--------------------------- MODULE QuePaxaAbstract ---------------------------
(*
Algorithm 1 of QuePaxa, the abstract consensus algorithm for a single slot,
with threshold synchronous broadcast (tcast) present as the two properties it
contracts for rather than as an implementation. Definition B.2 makes the
abstract network lock-step: every live replica invokes tcast exactly once per
time step, so one action here drives all live replicas through one tcast
together, and the adversary's freedom lives entirely in what each replica
receives and in which input tcast certifies as universally delivered.

A round is three tcast steps and a conclusion. Phases 0 to 2 select the tcast
steps and phase 3 is the conclusion, which is the last three lines of
Algorithm 1 lifted into their own action so that each action reads as one line
of the paper. The three sets a replica ends a round with mean: a proposal is in
E when the replica knows the proposal exists, in C when it knows every replica
knows it exists, and in U when it knows every replica knows it is common.

Four constants each remove one guarantee the algorithm depends on, and each has
a configuration that must go red. UniversalWitness removes property T2 from
tcast. Tiebreak removes the ordered proposal key that Appendix A's tiebreaking
approach appends, which is what the shipped prototypes do and what makes best
total. DecideOnCommon reads the decision predicate off the common set where
Algorithm 1 reads it off the existent set. CarryExistent carries the existent
set's best into the next round where Algorithm 1 carries the common set's.
TieDetection is the opposite kind of knob: it selects Algorithm 5 in place of
Algorithm 1, and is the one variation that is meant to survive a tie.

Replicas are naturals because the tiebreak orders proposals by the pair
(priority, replica) and so needs a total order on identities. Replica symmetry
is therefore unavailable and it would be unsound to declare it.

Liveness is out of scope. Lemma B.9's per-round decision probability is not a
temporal property and the authors argue it rather than check it. Deadlock
freedom is checked and carries meaning here: it asserts that no reachable
mid-round state has run out of tcast outcomes the two properties admit.
*)
EXTENDS Naturals, FiniteSets, TLC

CONSTANTS
    Replicas,            \* The replicas, as naturals, so that proposal keys are totally ordered.
    Values,              \* The values a replica can prefer.
    Priorities,          \* The priorities random() draws from, as naturals, larger being better.
    InitialPreferences,  \* The initial preference assignments the model starts from.
    MaxRound,            \* The last round the model runs.
    Faulty,              \* The replicas permitted to crash.
    UniversalWitness,    \* When false, tcast returns a merely received input as its second component.
    Tiebreak,            \* When false, the proposal key is the priority alone and best is not total.
    TieDetection,        \* When true, the decision predicate is Algorithm 5's uniqueBest form.
    DecideOnCommon,      \* When true, the decision predicate reads best(C) where Algorithm 1 reads best(E).
    CarryExistent,       \* When true, the round's outcome is best(E).value where Algorithm 1 uses best(C).value.
    NoValue              \* The sentinel for a replica that has not delivered.

ASSUME Replicas \subseteq Nat /\ Replicas # {}
ASSUME Priorities \subseteq Nat /\ Priorities # {}
ASSUME Faulty \subseteq Replicas
ASSUME InitialPreferences \subseteq [Replicas -> Values] /\ InitialPreferences # {}
ASSUME NoValue \notin Values
ASSUME MaxRound \in Nat /\ MaxRound >= 1

\* Algorithm 5 defines no carry off the existent set, so the two knobs are never
\* combined and the round conclusion does not have to say what that would mean.
ASSUME ~(TieDetection /\ CarryExistent)

\* The configurations that sweep every starting condition name this.
AllPreferences == [Replicas -> Values]

(* Nothing in the module reads a value except to compare it with another value:
   the proposal key orders by priority and replica, and the one CHOOSE picks by
   a predicate that ignores values and is only ever evaluated where the key
   makes the choice unique. Permuting values therefore carries every behaviour
   to a behaviour and every invariant to itself, so quotienting by it is sound.
   Replica identities carry no such freedom, because the tiebreak reads them.

   A configuration that declares this symmetry must also leave the initial
   preferences closed under it, which every configuration does by taking
   AllPreferences. Pinning an asymmetric set of starting assignments and keeping
   the symmetry declaration would be unsound and would not announce itself. *)
ValueSymmetry == Permutations(Values)

VARIABLES
    rnd,            \* The consensus round, running to MaxRound + 1 to mark the model out.
    phase,          \* Which of the three tcast steps comes next, or 3 for the round conclusion.
    alive,          \* The replicas that have not crashed.
    pref,           \* Each replica's preferred value, which is Algorithm 1's v.
    initPref,       \* The preferences the behaviour started from, which validity is read against.
    prio,           \* The priority each replica drew for this round.
    P,              \* The first tcast's first output, the proposals of a majority.
    E,              \* The second tcast's first output, the existent set.
    Pp,             \* The second tcast's second output, Algorithm 1's P'.
    C,              \* The third tcast's first output, the common set.
    U,              \* The third tcast's second output, the universal set.
    decided,        \* The value each replica delivered, written once as Lemma B.8's flag prescribes.
    redelivered     \* Set when the flag suppressed a later delivery that carried a different value.

vars == <<rnd, phase, alive, pref, initPref, prio, P, E, Pp, C, U, decided, redelivered>>

N == Cardinality(Replicas)

Proposals == [replica: Replicas, priority: Priorities, value: Values]

\* Definition B.1: the replica identifier is part of the proposal, so proposals
\* from different replicas are distinct even when priority and value agree.
Prop(i) == [replica |-> i, priority |-> prio[i], value |-> pref[i]]

NoProposals == [i \in Replicas |-> {}]

\* Appendix A's tiebreaking approach appends the replica encoding to the
\* priority. Without it the key is the priority alone and maxima need not be
\* unique.
Better(p, q) ==
    \/ p.priority > q.priority
    \/ /\ Tiebreak
       /\ p.priority = q.priority
       /\ p.replica > q.replica

Maxima(S) == {p \in S : \A q \in S : ~Better(q, p)}

BestOf(S) == CHOOSE p \in S : \A q \in S : ~Better(q, p)

\* Every way the live replicas can read a best proposal off their own sets when
\* the key leaves maxima unordered; Appendix A calls this anyBest.
Picks(f) == {ch \in [alive -> UNION {Maxima(f[i]) : i \in alive}] :
                \A i \in alive : ch[i] \in Maxima(f[i])}

BestFn(f) == [i \in alive |-> BestOf(f[i])]

\* Property T1 requires the received set to cover the inputs of a majority of
\* all replicas, not of the live ones.
Majorities == {M \in SUBSET alive : 2 * Cardinality(M) > N}

(* What tcast can hand each live replica as its first output. Two selections
   that deliver the same proposals are the same outcome, and everything the
   step goes on to decide - property T2's witnesses included - reads the
   delivery and not the selection, so collecting the deliveries into a set
   drops the duplicates before they become successor states. *)
ReceivedFunctions(inputs) ==
    { [i \in alive |-> UNION {inputs[j] : j \in sel[i]}] : sel \in [alive -> Majorities] }

\* Property T2's witness: a replica whose input this step delivered to every
\* live replica. The condition is containment in each received set, which is
\* the paper's own wording and is weaker than reaching every replica directly.
Witnesses(rec, inputs) ==
    {l \in alive : \A k \in alive : inputs[l] \subseteq rec[k]}

\* T2 constrains the delivery as well as the second output, because tcast only
\* returns a universally delivered input when one exists.
Deliverable(rec, inputs) == UniversalWitness => Witnesses(rec, inputs) # {}

(* What tcast can hand each live replica as its second output. Different
   replicas may be given different inputs, which the definition permits. Without
   T2 the only remaining requirement is that the input reached the replica it is
   handed to. *)
CertifiedFunctions(rec, inputs) ==
    LET sources == IF UniversalWitness THEN Witnesses(rec, inputs) ELSE alive
    IN  { [i \in alive |-> inputs[b[i]]] :
            b \in {f \in [alive -> sources] :
                     \A i \in alive : inputs[f[i]] \subseteq rec[i]} }

TypeOK ==
    /\ rnd \in 1..(MaxRound + 1)
    /\ phase \in 0..3
    /\ alive \subseteq Replicas
    /\ pref \in [Replicas -> Values]
    /\ initPref \in [Replicas -> Values]
    /\ prio \in [Replicas -> Priorities]
    /\ P \in [Replicas -> SUBSET Proposals]
    /\ E \in [Replicas -> SUBSET Proposals]
    /\ Pp \in [Replicas -> SUBSET Proposals]
    /\ C \in [Replicas -> SUBSET Proposals]
    /\ U \in [Replicas -> SUBSET Proposals]
    /\ decided \in [Replicas -> Values \union {NoValue}]
    /\ redelivered \in BOOLEAN

Init ==
    /\ rnd = 1
    /\ phase = 0
    /\ alive = Replicas
    /\ pref \in InitialPreferences
    /\ initPref = pref
    /\ prio \in [Replicas -> Priorities]
    /\ P = NoProposals
    /\ E = NoProposals
    /\ Pp = NoProposals
    /\ C = NoProposals
    /\ U = NoProposals
    /\ decided = [i \in Replicas |-> NoValue]
    /\ redelivered = FALSE

(* tcast({p}): each replica disseminates its own prioritized proposal. The
   second component is discarded here, so this step needs only property T1, and
   requiring T2 of it would assume more than the concrete protocol can supply:
   Section 4.2 implements this tcast in a single step precisely because a
   majority is all it needs, and Lemma C.5 discharges T2 only for a spread
   followed by a gather. Three replicas each hearing a different pair is a
   first-step delivery with no universally received proposal at all, and the
   concrete phase 0 produces it. *)
Tcast1 ==
    LET singletons == [j \in Replicas |-> {Prop(j)}]
    IN  /\ rnd <= MaxRound
        /\ phase = 0
        /\ \E rec \in ReceivedFunctions(singletons) :
               P' = [i \in Replicas |-> IF i \in alive THEN rec[i] ELSE {}]
        /\ phase' = 1
        /\ UNCHANGED <<rnd, alive, pref, initPref, prio, E, Pp, C, U, decided, redelivered>>

\* tcast(P): the existent sets, and the input that reached everyone becomes P'.
Tcast2 ==
    /\ phase = 1
    /\ \E rec \in ReceivedFunctions(P) :
           /\ Deliverable(rec, P)
           /\ \E cert \in CertifiedFunctions(rec, P) :
                  /\ E' = [i \in Replicas |-> IF i \in alive THEN rec[i] ELSE {}]
                  /\ Pp' = [i \in Replicas |-> IF i \in alive THEN cert[i] ELSE {}]
    /\ phase' = 2
    /\ UNCHANGED <<rnd, alive, pref, initPref, prio, P, C, U, decided, redelivered>>

\* tcast(P'): the common sets, and the input that reached everyone becomes U.
Tcast3 ==
    /\ phase = 2
    /\ \E rec \in ReceivedFunctions(Pp) :
           /\ Deliverable(rec, Pp)
           /\ \E cert \in CertifiedFunctions(rec, Pp) :
                  /\ C' = [i \in Replicas |-> IF i \in alive THEN rec[i] ELSE {}]
                  /\ U' = [i \in Replicas |-> IF i \in alive THEN cert[i] ELSE {}]
    /\ phase' = 3
    /\ UNCHANGED <<rnd, alive, pref, initPref, prio, P, E, Pp, decided, redelivered>>

(* The last three lines of Algorithm 1, given each live replica's reading of
   best(E), best(C) and best(U). The delivered value is the round's outcome and
   not a separate reading, because the pseudocode assigns v once and both
   delivers and carries it. Lemma B.8's decided flag is the write-once decided
   variable; redelivered records the substance the flag would otherwise hide,
   namely a later round in which the same replica would have delivered a
   different value. *)
Conclusion(chE, chC, chU) ==
    LET Outcome(i) == IF CarryExistent THEN chE[i].value ELSE chC[i].value
        Decides(i) == IF TieDetection
                      THEN Cardinality(Maxima(E[i])) = 1 /\ chE[i] = chU[i]
                      ELSE IF DecideOnCommon
                           THEN chC[i] = chU[i]
                           ELSE chE[i] = chU[i]
        deciders   == {i \in alive : Decides(i)}
    IN  /\ pref' = [i \in Replicas |-> IF i \in alive THEN Outcome(i) ELSE pref[i]]
        /\ decided' = [i \in Replicas |->
                          IF i \in deciders /\ decided[i] = NoValue
                          THEN Outcome(i)
                          ELSE decided[i]]
        /\ redelivered' =
               \/ redelivered
               \/ \E i \in deciders : decided[i] # NoValue /\ decided[i] # Outcome(i)

\* The round conclusion, after which the round's sets are dead and are cleared
\* so that they cannot distinguish states the next round can no longer tell
\* apart.
Conclude ==
    /\ phase = 3
    \* Algorithm 5 reads the existent set through uniqueBest, so that set needs
    \* no choice: where its best is unique BestOf is that proposal, and where it
    \* is not the predicate is false whatever was chosen. Dropping the choice is
    \* exact rather than an approximation, and it is the difference between a
    \* configuration that finishes and one that does not.
    /\ IF Tiebreak
       THEN Conclusion(BestFn(E), BestFn(C), BestFn(U))
       ELSE IF TieDetection
            THEN \E chC \in Picks(C), chU \in Picks(U) :
                     Conclusion(BestFn(E), chC, chU)
            ELSE \E chE \in Picks(E), chC \in Picks(C), chU \in Picks(U) :
                     Conclusion(chE, chC, chU)
    /\ rnd' = rnd + 1
    /\ phase' = 0
    /\ \E np \in [alive -> Priorities] :
           prio' = [i \in Replicas |-> IF i \in alive THEN np[i] ELSE prio[i]]
    /\ P' = NoProposals
    /\ E' = NoProposals
    /\ Pp' = NoProposals
    /\ C' = NoProposals
    /\ U' = NoProposals
    /\ UNCHANGED <<alive, initPref>>

\* A crash stops a replica for good. The guard keeps a majority of all replicas
\* live, which is what n >= 2f + 1 buys and what tcast needs to complete. A
\* replica that reached only some of its peers before crashing is the case
\* where it is still live for the tcast and crashes after it.
Crash(i) ==
    /\ rnd <= MaxRound
    /\ i \in Faulty
    /\ i \in alive
    /\ 2 * Cardinality(alive \ {i}) > N
    /\ alive' = alive \ {i}
    /\ UNCHANGED <<rnd, phase, pref, initPref, prio, P, E, Pp, C, U, decided, redelivered>>

\* The model runs a fixed number of rounds. Marking the end explicitly keeps the
\* deadlock check about mid-round states, where it says something.
Halted == rnd > MaxRound /\ UNCHANGED vars

Next ==
    \/ Tcast1
    \/ Tcast2
    \/ Tcast3
    \/ Conclude
    \/ \E i \in Replicas : Crash(i)
    \/ Halted

Spec == Init /\ [][Next]_vars

\* Lemma B.7: no two replicas decide differently.
Agreement ==
    \A i, j \in Replicas :
        (decided[i] # NoValue /\ decided[j] # NoValue) => decided[i] = decided[j]

\* Lemma B.6: a decided value is one that some replica proposed.
Validity ==
    \A i \in Replicas :
        decided[i] # NoValue => \E j \in Replicas : decided[i] = initPref[j]

\* Lemma B.8: no replica decides twice. The flag makes a second delivery
\* impossible; what remains to check is that it never masks a divergent one.
Integrity == ~redelivered

\* Lemma B.5: every replica's universal set is contained in every replica's
\* common set, which is contained in every replica's existent set. Agreement is
\* a corollary of this and of untied priorities.
CrossNodeSubset ==
    phase = 3 =>
        \A i, j, k \in alive : U[i] \subseteq C[j] /\ C[j] \subseteq E[k]

\* Lemma B.4: each of the five sets exceeds n/2, which is what keeps best total
\* and the next candidate value well defined.
Cardinalities ==
    /\ (phase >= 1 => \A i \in alive : 2 * Cardinality(P[i]) > N)
    /\ (phase >= 2 => \A i \in alive : 2 * Cardinality(E[i]) > N
                                    /\ 2 * Cardinality(Pp[i]) > N)
    /\ (phase = 3 => \A i \in alive : 2 * Cardinality(C[i]) > N
                                   /\ 2 * Cardinality(U[i]) > N)

=============================================================================
