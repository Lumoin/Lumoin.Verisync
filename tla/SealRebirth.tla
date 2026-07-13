---------------------------- MODULE SealRebirth ----------------------------
(*
The double-rebirth at its protocol-visible abstraction. An adoption recovery
re-mints a lost identity; a recovery that runs more than once - or two
independent recoveries - re-mints it divergently, and the two camps must
never merge silently. The data-plane fact the model carries is the minimal
one the fail-closed detector keys on: two re-mints of one identity are
distinguishable by an abstract content tag (the insertion point and value
chosen at re-mint time); the exact insertion geometry stays out of scope, as
does the rest of the CRDT data plane.

One lost identity suffices. Each member holds either nothing or a tagged copy
of it; copies spread by pairwise merge. The shipped detector
(OffsetAnchoredSequence.cs:370-373 and the Rga.Merge twin) fails a merge
closed when the operands carry the identity with unequal content - modeled as
the conflicting merge simply not being an available transition (the loud
throw changes no state). The negative variant DetectorOn = FALSE is
vertex-union-by-overwrite: the merge silently picks the incoming copy.

TagStability is the non-tautological observable: a member that acquired a
copy never sees its content change. Silent overwrite-merge violates it; the
detector makes it an invariant. Re-mints are bounded at two - the defect
space is exactly "the recovery ran more than once".
*)
EXTENDS Naturals

CONSTANTS
    Members,        \* The group of members.
    Tags,           \* The abstract content tags a re-mint can choose from, e.g. {t1, t2}.
    DetectorOn,     \* When true, the conflicting-identity merge detector is active; false is overwrite-merge.
    NoValue         \* A model value standing for a member that does not hold the identity.

VARIABLES
    held,           \* Each member's copy of the identity as its content tag, or NoValue.
    firstHeld,      \* The first content each member ever acquired; the history TagStability reads.
    remints         \* How many re-mints have happened, bounded at two.

vars == <<held, firstHeld, remints>>

TypeOK ==
    /\ held \in [Members -> Tags \union {NoValue}]
    /\ firstHeld \in [Members -> Tags \union {NoValue}]
    /\ remints \in 0..2

Init ==
    /\ held = [m \in Members |-> NoValue]
    /\ firstHeld = [m \in Members |-> NoValue]
    /\ remints = 0

\* A recovering member re-mints the lost identity with some content choice.
ReMint(m, t) ==
    /\ held[m] = NoValue
    /\ remints < 2
    /\ held' = [held EXCEPT ![m] = t]
    /\ firstHeld' = [firstHeld EXCEPT ![m] = t]
    /\ remints' = remints + 1

(* A pairwise data-plane merge, m pulling from n. A copy spreads cleanly into
   a member that holds nothing. When both hold the identity with unequal
   content, the detector refuses the merge loudly (no transition); without
   the detector the incoming copy silently overwrites. *)
MergePull(m, n) ==
    /\ m # n /\ held[n] # NoValue /\ held[m] # held[n]
    /\ IF held[m] = NoValue
       THEN /\ held' = [held EXCEPT ![m] = held[n]]
            /\ firstHeld' = [firstHeld EXCEPT ![m] = held[n]]
       ELSE /\ ~DetectorOn
            /\ held' = [held EXCEPT ![m] = held[n]]
            /\ firstHeld' = firstHeld
    /\ remints' = remints

Next ==
    \/ \E m \in Members, t \in Tags : ReMint(m, t)
    \/ \E m, n \in Members : MergePull(m, n)

Spec == Init /\ [][Next]_vars

\* A member's copy never changes content behind its back.
TagStability ==
    \A m \in Members :
        held[m] # NoValue => firstHeld[m] # NoValue /\ held[m] = firstHeld[m]

=============================================================================
