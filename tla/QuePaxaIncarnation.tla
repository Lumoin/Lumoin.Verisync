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
another; the shipped code has no such field, which is why this model exists
before the field does.

The detector is the fix under test: an identity binds to the incarnation
that first answered for it, and a host answering under a different
incarnation for a bound identity is refused. DetectorOn = FALSE is the
shipped behaviour, where nothing distinguishes them and both answers count.

Agreement is the non-tautological observable. It fails without the detector
and holds with it, so the negative earns its red rather than inheriting one.
*)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Identities,     \* The membership, counted by identity.
    Incarnations,   \* The store instances one identity can be answered by, e.g. {1, 2}.
    Values,         \* The values a round can decide.
    Quorum,         \* How many DISTINCT identities a decision rests on.
    DetectorOn,     \* When true, an identity is bound to one incarnation; false is the shipped behaviour.
    NoValue,        \* A model value standing for a host that has recorded nothing.
    NoIncarnation   \* A model value standing for an identity not yet bound.

\* The transport reaches hosts; the arithmetic counts identities. That gap is the whole subject.
Hosts == Identities \X Incarnations

IdentityOf(h) == h[1]
IncarnationOf(h) == h[2]

VARIABLES
    recorded,       \* What each host has recorded, or NoValue.
    boundTo,        \* The incarnation each identity is bound to, or NoIncarnation.
    decided         \* The values a quorum has decided.

vars == <<recorded, boundTo, decided>>

TypeOK ==
    /\ recorded \in [Hosts -> Values \union {NoValue}]
    /\ boundTo \in [Identities -> Incarnations \union {NoIncarnation}]
    /\ decided \subseteq Values

Init ==
    /\ recorded = [h \in Hosts |-> NoValue]
    /\ boundTo = [i \in Identities |-> NoIncarnation]
    /\ decided = {}

\* The identities a set of hosts speaks for. Two hosts of one identity speak for one.
IdentitiesOf(S) == {IdentityOf(h) : h \in S}

(* A host records a value. With the detector, an identity binds to the first
   incarnation that answers for it and a later answer under a different one is
   refused; without it, any host records and the binding is bookkeeping that
   nothing consults. *)
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

Next ==
    \/ \E h \in Hosts, v \in Values : Record(h, v)
    \/ \E v \in Values : Decide(v)

Spec == Init /\ [][Next]_vars

\* One value is decided, however the quorums were assembled.
Agreement == Cardinality(decided) <= 1

=============================================================================
