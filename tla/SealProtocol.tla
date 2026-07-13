---------------------------- MODULE SealProtocol ----------------------------
(*
The consensus-anchored checkpoint protocol: Seal / ApplyCommittedSeal / Adopt
over the CASPaxos register holding a frontier-carrying commitment. The model
follows src/Lumoin.Verisync.Core/CheckpointedSequence.cs and
OffsetAnchoredSequence.cs.

Abstractions:
 - A member's live state collapses to its causal context (a small vector
   clock of observed inserts) plus its generation identity (the BaseFrontier,
   genesis = the zero clock). Removes are omitted: the stability frontier
   tracks inserts only, and no seal-protocol branch keys on remove state.
 - The digest collapses to the pair (frontier, source generation): two
   members on one generation at one frontier digest equal; a post-base-change
   projection at an old frontier digests different (the sentinel re-keying).
   This one abstraction carries the equal-digest re-seal arm, the apply-side
   digest verification, its generation-bound limit, and the apply-once
   refusal of a base-changing seal.
 - The two CASPaxos phases collapse to one linearizable Choose guarded by the
   monotone dominate-or-refuse change function; a refusal changes nothing and
   is therefore not a transition. Consensus-internal behavior is out of scope
   here. The register stays reachable during gossip partitions: partitions cut
   the gossip plane, not the acceptor quorum.
 - Whether a seal is base-changing is a nondeterministic choice made at seal
   time and recorded in the commitment, so every applier makes the identical
   generation decision (the real flag is a pure function of state at the
   frontier; nondeterminism over-approximates it).
 - The offset insert-quiescence probe makes a sealer's only probe-passing
   proposal its own full context, so Seal(m) proposes exactly ctx[m] - a free,
   member-local frontier, never the group fold. Hard-wiring the group minimum
   here would make the straggler island unconstructible and the liveness
   check vacuous.
 - Adopt collapses wholesale replacement plus the merge higher-ballot
   inheritance into copying the donor's context, generation and recorded
   commitment. It requires gossip reachability. Note adoption is
   host-choreographed: adopting in the wrong direction (the island's last
   holder adopting a group member) deepens the wedge rather than exiting it,
   which is why no liveness credit is taken for it here.

SealGuard and WriteBarrier are the two halves of a host coordination surface
the shipped protocol does not have: the group probe-fold at seal time (a seal
is permitted only when every member's context equals the proposed frontier)
and the write barrier through the apply window (a member inserts only once
its recorded commitment is current). They are separate constants because they
are only jointly sufficient: the probe-fold alone still wedges on an insert
that lands between the seal and a straggling apply (MCSealProbeOnly pins that
red). Both FALSE is the shipped protocol; the seal-side probe there is a
local witness only. The modeled probe-fold attests the FULL group; a gate
attesting only reachable members is defeated by a partition.
*)
EXTENDS Naturals

CONSTANTS
    Members,            \* The group of members, e.g. {m1, m2, m3}.
    MaxC,               \* The per-axis insert bound.
    AllowAdopt,         \* When true, the wholesale-adoption action is enabled.
    AllowPartition,     \* When true, gossip partitions are enabled.
    SealGuard,          \* When true, sealing requires the group probe-fold (one gate half).
    WriteBarrier,       \* When true, a member inserts only once its commitment is current (the other half).
    NoCommit            \* A model value standing for the empty register and an unrecorded commitment.

VARIABLES
    ctx,                \* Each member's vector clock of observed inserts.
    gen,                \* Each member's generation identity; the zero clock is genesis.
    committed,          \* The register cell: NoCommit, or a commitment record.
    mCommit,            \* Each member's recorded commitment; NoCommit until it seals or applies.
    disc                \* The set of gossip-partitioned members.

vars == <<ctx, gen, committed, mCommit, disc>>

Clock == [Members -> 0..MaxC]
ZeroClock == [m \in Members |-> 0]
Commitments == [f: Clock, g: Clock, bc: BOOLEAN]

Dominates(a, b) == \A x \in Members : a[x] >= b[x]
StrictlyDominates(a, b) == Dominates(a, b) /\ a # b
ClockMax(a, b) == [x \in Members |-> IF a[x] >= b[x] THEN a[x] ELSE b[x]]

TypeOK ==
    /\ ctx \in [Members -> Clock]
    /\ gen \in [Members -> Clock]
    /\ committed \in Commitments \union {NoCommit}
    /\ mCommit \in [Members -> Commitments \union {NoCommit}]
    /\ disc \subseteq Members

Init ==
    /\ ctx = [m \in Members |-> ZeroClock]
    /\ gen = [m \in Members |-> ZeroClock]
    /\ committed = NoCommit
    /\ mCommit = [m \in Members |-> NoCommit]
    /\ disc = {}

-----------------------------------------------------------------------------

(* A local insert bumps the member's own axis. A partitioned member keeps
   editing. Under the guarded discipline the write barrier spans the whole
   checkpoint: a member may insert only once its recorded commitment is the
   current one. A barrier held only at seal time still wedges, since an
   insert landing between the seal and a straggling apply fails the
   apply-side probe forever. *)
Insert(m) ==
    /\ WriteBarrier => (committed = NoCommit \/ mCommit[m] = committed)
    /\ ctx[m][m] < MaxC
    /\ ctx' = [ctx EXCEPT ![m][m] = @ + 1]
    /\ UNCHANGED <<gen, committed, mCommit, disc>>

(* Data-plane gossip is a CRDT merge, so the generation fence applies
   (OffsetAnchoredSequence.cs:336-346): members on different generations
   cannot exchange state as peers. *)
Gossip(m, n) ==
    /\ m # n /\ m \notin disc /\ n \notin disc
    /\ gen[m] = gen[n]
    /\ ctx[m] # ClockMax(ctx[m], ctx[n])
    /\ ctx' = [ctx EXCEPT ![m] = ClockMax(ctx[m], ctx[n])]
    /\ UNCHANGED <<gen, committed, mCommit, disc>>

(* Seal (CheckpointedSequence.cs:355-431). The proposal frontier is the
   sealer's own context - the only frontier its local quiescence probe passes.
   The register's change function admits it only from an empty register or by
   strict frontier dominance (the idempotent equal-digest re-seal changes
   nothing and is elided; refusals change nothing and are not transitions).
   A winning base-changing seal advances the sealer's generation to the
   committed frontier; abort-on-lose leaves the sealer untouched, which is
   exactly this action being disabled. *)
Seal(m) ==
    LET F == ctx[m] IN
    /\ SealGuard => \A n \in Members : ctx[n] = F
    /\ IF committed = NoCommit THEN TRUE ELSE StrictlyDominates(F, committed.f)
    /\ \E bc \in BOOLEAN :
        /\ committed' = [f |-> F, g |-> gen[m], bc |-> bc]
        /\ mCommit' = [mCommit EXCEPT ![m] = committed']
        /\ gen' = IF bc THEN [gen EXCEPT ![m] = F] ELSE gen
    /\ UNCHANGED <<ctx, disc>>

(* ApplyCommittedSeal (CheckpointedSequence.cs:470-520), preconditions in
   order: chain order (never regress the recorded commitment), context
   dominance (the applier observed everything below the frontier), the digest
   at the committed frontier (equal exactly when the applier still sits on
   the commitment's source generation - the generation-bound verification),
   and the apply-side quiescence probe (no in-flight inserts above the
   frontier). Dominance plus the probe pin the applier's context to exactly
   the committed frontier: sealing is a group-quiescent checkpoint. *)
ApplyCommitted(m) ==
    /\ committed # NoCommit
    /\ mCommit[m] # committed
    /\ IF mCommit[m] = NoCommit THEN TRUE ELSE Dominates(committed.f, mCommit[m].f)
    /\ Dominates(ctx[m], committed.f)
    /\ Dominates(committed.f, ctx[m])
    /\ gen[m] = committed.g
    /\ mCommit' = [mCommit EXCEPT ![m] = committed]
    /\ gen' = IF committed.bc THEN [gen EXCEPT ![m] = committed.f] ELSE gen
    /\ UNCHANGED <<ctx, committed, disc>>

(* Wholesale adoption (CheckpointedSequence.cs:221-233 plus the merge
   higher-ballot inheritance): the adopter takes the donor's full state; its
   own above-frontier edits are gone. Requires reachability. *)
Adopt(m, donor) ==
    /\ AllowAdopt /\ m # donor /\ m \notin disc /\ donor \notin disc
    /\ ctx[m] # ctx[donor] \/ gen[m] # gen[donor] \/ mCommit[m] # mCommit[donor]
    /\ ctx' = [ctx EXCEPT ![m] = ctx[donor]]
    /\ gen' = [gen EXCEPT ![m] = gen[donor]]
    /\ mCommit' = [mCommit EXCEPT ![m] = mCommit[donor]]
    /\ UNCHANGED <<committed, disc>>

Partition(m) ==
    /\ AllowPartition /\ m \notin disc
    /\ disc' = disc \union {m}
    /\ UNCHANGED <<ctx, gen, committed, mCommit>>

Reconnect(m) ==
    /\ m \in disc
    /\ disc' = disc \ {m}
    /\ UNCHANGED <<ctx, gen, committed, mCommit>>

-----------------------------------------------------------------------------

Next ==
    \/ \E m \in Members : Insert(m) \/ Seal(m) \/ ApplyCommitted(m)
                          \/ Partition(m) \/ Reconnect(m)
    \/ \E m, n \in Members : Gossip(m, n) \/ Adopt(m, n)

Spec == Init /\ [][Next]_vars

FairSpec == Spec /\ WF_vars(Next)

-----------------------------------------------------------------------------

(* The chain invariant, applier side: a recorded commitment is never ahead of
   or concurrent with the register - the committed line only ascends and
   members record only chosen values (map section 5). *)
RecordedOnChain ==
    \A m \in Members :
        mCommit[m] # NoCommit =>
            /\ committed # NoCommit
            /\ Dominates(committed.f, mCommit[m].f)

(* The group always eventually converges on the current committed seal. The
   recurrence form (always-eventually) is load-bearing: the straggler wedge
   is a never-again-converges livelock, and a once-ever form would stay
   satisfied by any single early convergence and go blind to a group that
   was checkpointing and then wedges forever. *)
GroupConverges ==
    []<>(committed # NoCommit /\ \A m \in Members : mCommit[m] = committed)

(* A member that records a commitment sits on the generation that commitment
   implies: the source generation for a drop-only seal, the committed
   frontier for a base-changing one. This is the observable the apply-side
   digest verification (its generation-bound equality) protects; deleting
   that guard admits cross-generation applies this invariant catches. *)
RecordedGenConsistent ==
    \A m \in Members :
        mCommit[m] # NoCommit =>
            gen[m] = (IF mCommit[m].bc THEN mCommit[m].f ELSE mCommit[m].g)

=============================================================================
