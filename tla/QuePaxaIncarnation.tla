-------------------------- MODULE QuePaxaIncarnation --------------------------
(*
The duplicated identity, which no other consensus model here can express.

Every other model in this directory ranges over a set of replicas whose
elements ARE the hosts, so one identity is one host by construction and a
state in which two hosts answer under one identity is outside the alphabet.
That is exactly the hazard the operator documentation concedes and cannot
enforce: a store wiped and restarted under the same replica identity, or one
identity provisioned twice, is undetectable by any host. Both answer as that
member while holding divergent state, so two quorums can form that intersect
only at an identity two hosts disagree about.

A host here is therefore a pair of an identity and an incarnation, and the
membership counts IDENTITIES while the transport reaches HOSTS. The
incarnation stands for whatever distinguishes one store instance from
another, and it is minted with a store rather than assigned, so a store that
lost its contents cannot present the one it used to hold.

The detector is the rule under test: an identity is bound to one
incarnation, and a host answering under a different incarnation for a bound
identity is refused. DetectorOn = FALSE is a membership that names
identities alone, where nothing distinguishes two stores of one identity and
both answers count.

THE BINDING IS ADMITTED AND NOT LEARNED, which is why Init ranges over every
assignment of an incarnation to an identity rather than starting unbound. A
binding taken from the first answer binds whoever wins the race, so a wiped
store that answered first would bind the identity to itself and the store it
replaced would be the one refused; the model would then report agreement for
a rule that admits exactly the host the hazard is about. Binding on first
answer survives here only as what a lost binding falls back to, which is the
BindingDurable = FALSE case below.

Agreement is the non-tautological observable. It fails without the detector
and holds with it, so the negative earns its red rather than inheriting one.
*)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Identities,     \* The membership, counted by identity.
    Incarnations,   \* The store instances one identity can be answered by, e.g. {1, 2}.
    Values,         \* The values a round can decide.
    Quorum,         \* How many DISTINCT identities a decision rests on.
    DetectorOn,     \* When true, an identity is bound to one incarnation; false is a membership naming identities alone.
    BindingDurable, \* When false a binding can be lost, standing for one held only in a proposer's memory.
    NoValue,        \* A model value standing for a host that has recorded nothing.
    NoIncarnation   \* A model value standing for an identity whose binding has been lost.

\* The transport reaches hosts; the arithmetic counts identities. That gap is the whole subject.
Hosts == Identities \X Incarnations

IdentityOf(h) == h[1]
IncarnationOf(h) == h[2]

VARIABLES
    recorded,       \* What each host has recorded, or NoValue.
    boundTo,        \* The incarnation each identity is admitted under, or NoIncarnation once a lost binding has forgotten it.
    decided         \* The values a quorum has decided.

vars == <<recorded, boundTo, decided>>

TypeOK ==
    /\ recorded \in [Hosts -> Values \union {NoValue}]
    /\ boundTo \in [Identities -> Incarnations \union {NoIncarnation}]
    /\ decided \subseteq Values

(* Every admission is an initial state: each identity is listed under one of
   its incarnations, which is what a configuration naming a store per member
   is, and the model ranges over all of them rather than over one the runs
   happen to reach. *)
Init ==
    /\ recorded = [h \in Hosts |-> NoValue]
    /\ boundTo \in [Identities -> Incarnations]
    /\ decided = {}

\* The identities a set of hosts speaks for. Two hosts of one identity speak for one.
IdentitiesOf(S) == {IdentityOf(h) : h \in S}

(* A host records a value. With the detector, only the host admitted for an
   identity records under it, and the binding is rewritten with the value it
   already holds; without the detector any host records and the binding is
   bookkeeping that nothing consults. The NoIncarnation arm is reachable only
   after a binding has been lost, and there the first answer binds, which is
   the memo this model exists to reject. *)
Record(h, v) ==
    /\ recorded[h] = NoValue
    /\ DetectorOn => boundTo[IdentityOf(h)] \in {NoIncarnation, IncarnationOf(h)}
    /\ recorded' = [recorded EXCEPT ![h] = v]
    /\ boundTo' = [boundTo EXCEPT ![IdentityOf(h)] = IncarnationOf(h)]
    /\ UNCHANGED decided

(* A value is decided when hosts speaking for a quorum of distinct identities
   have recorded it. The set is required to hold one host per identity, which
   is what a quorum counted over distinct members means. *)
Decide(v) ==
    /\ \E S \in SUBSET Hosts :
        /\ S # {}
        /\ \A h \in S : recorded[h] = v
        /\ Cardinality(S) = Cardinality(IdentitiesOf(S))
        /\ Cardinality(IdentitiesOf(S)) >= Quorum
    /\ decided' = decided \union {v}
    /\ UNCHANGED <<recorded, boundTo>>

(* A binding held only in the memory of the process that learned it is lost when that process is
   replaced. Modelling that loss is what decides where the binding has to live: if agreement survives
   it, a memo suffices; if it does not, the binding must come from something durable that every host
   derives the same answer from. *)
ForgetBindings ==
    /\ ~BindingDurable
    /\ boundTo # [i \in Identities |-> NoIncarnation]
    /\ boundTo' = [i \in Identities |-> NoIncarnation]
    /\ UNCHANGED <<recorded, decided>>

Next ==
    \/ \E h \in Hosts, v \in Values : Record(h, v)
    \/ \E v \in Values : Decide(v)
    \/ ForgetBindings

Spec == Init /\ [][Next]_vars

\* One value is decided, however the quorums were assembled.
Agreement == Cardinality(decided) <= 1

(* The reachability witness, which is checked as a deliberate red. Agreement
   holds of the initial state, so a detector that refused every answer would
   satisfy it and every positive run would prove nothing; this invariant is
   violated exactly where the guarded model still decides. The negatives earn
   their red and the positive earns its green. *)
NothingDecided == Cardinality(decided) = 0

=============================================================================
