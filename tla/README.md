# TLA+ workspace

Model-checked specifications for the protocol tiers this repository ships or is deciding whether to
ship: the anti-entropy session pair with the completion frame, the consensus-anchored checkpoint
seal, and QuePaxa. The first two model the code as it is. QuePaxa models the published protocol
before any code exists, which is the point of ordering it that way. The abstraction decisions are
documented in each module's header comment.

## Negative models

A negative model is a configuration that enables a forbidden behavior (or removes a shipped
guard) and whose TLC run MUST report a violation. A green negative run means the model is too
abstract to trust and its positive runs prove nothing. The runners (`Run-SessionAndSeal.ps1` and
`Run-QuePaxa.ps1`) enforce the expected outcome of every configuration in the matrices below and
fail on any deviation. A configuration that no runner pins does not belong in this directory.

## Toolchain

`C:\tools\tlaplus` with a portable Temurin 21 JRE, current `tla2tools.jar`, and `sany.cmd` /
`tlc.cmd` wrappers (heap-bounded; override with `TLA_JAVA_HEAP`). Run from this directory:

    C:\tools\tlaplus\sany.cmd .\SessionPair.tla
    C:\tools\tlaplus\tlc.cmd -workers 4 -checkpoint 0 -config .\MCSessionPairSafety.cfg .\SessionPair.tla
    .\Run-SessionAndSeal.ps1
    .\Run-QuePaxa.ps1

## The session and seal matrix

The Fast CASPaxos register is out of scope for these two modules and is collapsed to a linearizable
cell with the seal's monotone dominate-or-refuse change function.

| Configuration | Spec | Checks | Expected |
|---|---|---|---|
| `MCSessionPairSafety` | SessionPair | the Interrupted-zero-folds and terminal-converged invariants under crash and wind-down everywhere | green |
| `MCSessionPairTC` | SessionPair | fold implies eventual delivery under arbitrary wind-down | green |
| `MCSessionPairCrashTC` | SessionPair | fold implies eventual delivery with crash at every await; certifies the fetch-answer apply order | green |
| `MCSessionPairDrainClobber` | SessionPair | negative model: the drain epilogue folds a captured context on a bare wind-down | red (required) |
| `MCSessionPairEagerDrops` | SessionPair | negative model: the local drops apply and fold with the fetch still outstanding | red (required) |
| `MCSessionPairLiveness` | SessionPair | crash-free fair sessions converge both contexts in one session | green |
| `MCSealSafety` | SealProtocol | the chain invariant and recorded-generation consistency through seals, applies, adoptions, partitions | green |
| `MCSealIsland` | SealProtocol | negative model: a member seals a frontier the group cannot apply, with partitions enabled | red (required) |
| `MCSealRace` | SealProtocol | negative model: a seal racing gossip wedges the group with no partition at all | red (required) |
| `MCSealProbeOnly` | SealProtocol | negative model: the seal-time probe-fold without the write barrier | red (required) |
| `MCSealGuarded` | SealProtocol | the full host gate: group probe-fold at seal plus the write barrier through the apply window | green |
| `MCRebirthDetector` | SealRebirth | the conflicting-identity detector keeps acquired copies stable | green |
| `MCRebirthSilent` | SealRebirth | negative model: vertex-union-by-overwrite silently merges divergent re-mint camps | red (required) |

## The QuePaxa matrix

`QuePaxaAbstract` is Algorithm 1 of the QuePaxa paper over threshold synchronous broadcast, with the
two tcast properties as the network's contract. `QuePaxaConcrete` is Algorithm 4 over the interval
summary registers of Algorithm 3, asynchronous and message-passing. Each module's header cites the
paper section and lemma behind every invariant it checks and every modelling decision it takes.

| Configuration | Spec | Checks | Expected |
|---|---|---|---|
| `MCAbstractSafety` | QuePaxaAbstract | the five Appendix B lemmas over every starting assignment, two rounds, no crashes | green |
| `MCAbstractCrash` | QuePaxaAbstract | the same with one replica permitted to crash at any point in a round | green |
| `MCAbstractSweep` | QuePaxaAbstract | the baseline swept to three priorities, so a round can hold a strict ranking of all three proposals as well as every tie pattern two priorities can make | green |
| `MCAbstractTieDetection` | QuePaxaAbstract | the five Appendix B lemmas under Algorithm 5's uniqueBest predicate, with the priority key reduced so that ties are reachable | green |
| `MCAbstractNoUniversalWitness` | QuePaxaAbstract | negative model: tcast returns a merely received input rather than a universally delivered one, which is property T2 withdrawn | red (required) |
| `MCAbstractDecideOnCommon` | QuePaxaAbstract | negative model: the decision predicate reads best(C) where Algorithm 1 reads best(E) | red (required) |
| `MCAbstractCarryExistent` | QuePaxaAbstract | negative model: the round carries best(E).value where Algorithm 1 carries best(C).value | red (required) |
| `MCAbstractTiedPriorities` | QuePaxaAbstract | negative model: the proposal key is the priority alone, with the priority space collapsed so every round ties | red (required) |
| `MCConcreteSingleLeader` | QuePaxaConcrete | Algorithm 4 unmodified under the leader agreement Section 4.2.5 assumes | green |
| `MCConcreteTwoLeaders` | QuePaxaConcrete | negative model: two proposers each believe they lead round one, so two proposals carry the reserved priority and Lemma C.10 loses its hypothesis | red (required) |
| `MCConcreteIdenticalKeyOnly` | QuePaxaConcrete | negative model: the fast-path defence alone, which makes two fast decisions agree but does not stop the losing proposer spreading the other reserved-priority proposal | red (required) |
| `MCConcreteFirstClaimBindsOnly` | QuePaxaConcrete | negative model: first-come binding alone, where nothing makes the recorders agree on whom they bound | red (required) |
| `MCConcreteConfiguredLeaderOnly` | QuePaxaConcrete | the configured-leader defence alone, which is the one that holds | green |
| `MCConcreteTwoLeadersGuarded` | QuePaxaConcrete | the same two believed leaders with all three defences in force | green |
| `MCConcreteDeclaredScheduleBinds` | QuePaxaConcrete | the declared-schedule defence alone, where each recorder binds the first leader a request names and refuses any request naming another | green |
| `MCConcreteSingleLeaderDeclaredSchedule` | QuePaxaConcrete | the declared-schedule defence with the schedule agreed, which must match `MCConcreteSingleLeader` state for state | green |

## Scope notes (what a green run does not certify)

- The fail-closed rejection layer (misordered frames, wrong-role frames, the completion
  transfer-count check) is structurally unreachable here: the honest FIFO exactly-once transport
  the protocol contracts for never produces the inputs those guards reject. Rejection behavior is
  owned by the unit suite.
- The liveness property in the session matrix is the recurrence form (always-eventually): a
  once-ever form would stay satisfied by any single early convergence and go blind to a
  converge-then-wedge regression.
- The session model treats sends into a closed channel as silent loss (the socket transport's
  view); the in-process transport's sender-side throw is covered by the separately-enabled crash
  action.
- QuePaxa liveness is not checked at all. Per-round decision probability (Lemmas B.9 and C.11) is
  not a temporal property; the authors argue it rather than check it, and Appendix D records that
  SPIN could not check it either. Deadlock freedom is checked in the positive configurations
  instead, and it carries meaning there: it asserts that no reachable state has run out of outcomes
  the tcast properties admit, or of replies a proposer can act on. The negative configurations set
  `CHECK_DEADLOCK FALSE`, because a model that stops at its first violation says nothing about
  whether the rest of its state space had successors.
- `MCConcreteDeclaredScheduleBinds` is the one positive configuration that does not check deadlock,
  and the reason is the defence rather than the model. A recorder that has bound one declared leader
  refuses every request naming another, so the proposer that is wrong about the schedule is left one
  reply short of a quorum and can never act again. Those terminal states are the defence working.
  What the configuration therefore certifies is agreement, and nothing about progress.
- Code coverage is a gate here, as it was for the paper's own Promela models, and it is a gate on
  the module rather than on any single configuration. No action of either module is unreachable.
  `MCConcreteTwoLeadersGuarded` covers every phase branch, the catch-up branch, the
  demote-the-reserved-priority path and the defence conjuncts it enables. It does not cover
  `QuePaxaConcrete` on its own: the `Refuse` action and the recorder's declared-schedule binding are
  unreachable unless `DeclaredScheduleBinds` is set, so the concrete module's covering union is
  `MCConcreteTwoLeadersGuarded` together with `MCConcreteDeclaredScheduleBinds`, which reaches
  `Refuse` at 209 distinct states of its 292. Reading that number needs one piece of local knowledge:
  TLC names an action only where it can decompose the disjunct, and it cannot here, because the
  quantifier ranges over the `reqs` variable. `Serve` and `Refuse` are therefore reported as
  coordinate-tagged anonymous actions under `Next`, and they are written as two separate disjuncts
  rather than one disjunction precisely so that those coordinates tell them apart. Collapsed into one
  disjunct they share a single entry and no coverage run can say whether a refusal ever happened.
  `QuePaxaAbstract` needs three configurations between them, because the
  round conclusion has one call site per tie regime and a configuration selects exactly one:
  `MCAbstractCrash` takes the ordered-key site, `MCAbstractTieDetection` the tie-detecting site and
  `MCAbstractTiedPriorities` the unordered site. Their union leaves nothing uncovered.
- The concrete configurations run one consensus round, steps 4 to 7. The paper's own Promela
  baseline ran two, so a green concrete run certifies one leadered round and nothing beyond it: the
  carry from phase 3 into the next round's phase 0 is never exercised, and neither is the leaderless
  priority draw that every round after the first uses. The hazard these configurations exist for
  lives entirely inside step 4, so the limit does not weaken what is being claimed, but it does
  bound it. The abstract configurations run two rounds.
- The refinement mapping from `QuePaxaConcrete` onto `QuePaxaAbstract` is argued rather than
  checked. Appendix C is itself a prose refinement argument, and reconstructing the abstract
  proposal sets from the register summaries needs ghost state that the interval summary register
  exists in order not to keep. The argument is tabulated in the ground-truth map; the concrete
  module checks its own invariants and the hazard directly.
- Every green QuePaxa configuration listed above explored its state space to exhaustion; nothing
  here used bitstate-equivalent techniques, and nothing may be reported as verified that did. A red
  configuration stops at its first violation and is not exhaustive by design - what it certifies is
  that the defect is reachable, which is all a negative model is for.
- Value symmetry is declared in the abstract QuePaxa configurations and is sound there: nothing in
  the module reads a value except to compare it with another value, and the single CHOOSE picks by
  a predicate that ignores values. Where the proposal key leaves that choice open, which is the
  tie-detection configuration, the proposal CHOOSE returns is consumed only under a guard that the
  maxima are unique, so an arbitrary choice never reaches a state. Replica symmetry is not
  available in either QuePaxa module, because the tiebreak reads replica and proposer identities.
