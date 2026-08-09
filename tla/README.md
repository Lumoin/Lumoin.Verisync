# TLA+ workspace

Model-checked specifications for the protocol tiers this repository ships or is deciding whether to
ship: the anti-entropy session pair with the completion frame, the consensus-anchored checkpoint
seal, QuePaxa, and Raft's leader election and log rules. The session pair, the seal, the election and the
log model the code as it is. QuePaxa models the published protocol before any code exists, which is the
point of ordering it that way. The abstraction decisions are
documented in each module's header comment.

## Negative models

A negative model is a configuration that enables a forbidden behavior (or removes a shipped
guard) and whose TLC run MUST report a violation. A green negative run means the model is too
abstract to trust and its positive runs prove nothing. The runners (`Run-SessionAndSeal.ps1`,
`Run-QuePaxa.ps1` and `Run-Raft.ps1`) enforce the expected outcome of every configuration in the
matrices below and fail on any deviation. A configuration that no runner pins does not belong in this directory.

A bound is derived from what the property needs to express, not from what finishes quickly. A
constant chosen for cost can leave the hazard unwritable rather than unreached: a scenario that
turns on a minority replication a later majority revisits needs a membership whose minority is at
least two, so at three servers, where the minority is one, the sequence cannot be constructed at
all and the configuration goes green over a state space in which the defect does not exist. That
green is indistinguishable from the green a sound bound produces, which is what makes the rule
worth stating rather than inferring.

The test that settles a bound follows from it: a positive is only meaningful at a bound where its
paired negative is red. Pinning both arms at the same constants is what shows the bound admits the
hazard, and only then does the positive beside them carry the claim that the guard excludes it.
`RaftElection` runs at `MaxTerm = 1` on that basis rather than as a concession to run time, because
both of its negatives are red there; a positive checked at a looser bound than its negative
certifies nothing about the states between them.

## Toolchain

`C:\tools\tlaplus` with a portable Temurin 21 JRE, current `tla2tools.jar`, and `sany.cmd` /
`tlc.cmd` wrappers (heap-bounded; override with `TLA_JAVA_HEAP`). Run from this directory:

    C:\tools\tlaplus\sany.cmd .\SessionPair.tla
    C:\tools\tlaplus\tlc.cmd -workers 4 -checkpoint 0 -config .\MCSessionPairSafety.cfg .\SessionPair.tla
    .\Run-SessionAndSeal.ps1
    .\Run-QuePaxa.ps1
    .\Run-Raft.ps1

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
| `MCConcreteMixedLeaderlessWideDowngrade` | QuePaxaConcrete | negative model: one recorder honours no leader while the others honour the agreed one, which is the state a host reaches before it has learned the version the leader is derived from, with the downgrade applied at every step | red (required) |
| `MCConcreteMixedLeaderlessNarrowDowngrade` | QuePaxaConcrete | the same mixed configuration under the downgrade the library applies, which is the round's first step, where the reserved priority is read | green |
| `MCConcreteSplitLeadersWideDowngrade` | QuePaxaConcrete | negative model: the recorders disagree about whom the defence protects, with the defence in force at every one of them and the downgrade at every step | red (required) |
| `MCConcreteSplitLeadersNarrowDowngrade` | QuePaxaConcrete | negative model: the same split under the downgrade the library applies, which must not rescue it and does not | red (required) |

The last four exist because a versioned register derives the leader from committed state, so two recorders
that have learned different amounts would otherwise be unrepresentable. `RecorderLeader` is therefore a
function over the recorders rather than a single scalar, and `ConfiguredLeader` remains for the
declared-schedule arm. They are the two-by-two of the two misconfigurations against the two downgrade
widths, and the narrow arm carries the rule the library applies: the recorder rewrites a declined reserved
claim at the round's first step and records one arriving above that step verbatim. Every other concrete
configuration carries the narrow rule too, and each reproduced its wide-rule state count exactly, which is
what says the narrowing is inert wherever the recorders are uniform rather than merely harmless there.

The split is red under both widths, and that is what makes the deployment obligation a checked fact rather
than prose: a recorder that derives a different leader for the instance must not serve the instance,
because two reserved claims are then honoured at the step the fast path reads. The mixed pair names the
second key rather than the non-uniform configuration itself as the cause of that configuration's
divergence. Under the wide rule the recorder honouring no leader rewrites the leader's carried template at
every step, so one logical proposal exists under two keys through the ordinary phases and a quorum holding
only the rewritten copy carries an ordinary proposal past it; under the narrow rule the rewrite reaches the
first step alone and the configuration is green. That green is not a licence to run such a cluster: it
holds at one leaderless recorder of three and at this bound, and a host that cannot derive the instance's
leader declines it rather than serving it leaderless.

## The Raft election matrix

`RaftElection` models leader election over a fixed membership. It exists for a hazard no conventional Raft
specification can express: a specification that quantifies messages over the server set makes every sender a
member by construction, so the question of a message from somewhere else cannot be asked. A deployment
cannot assume it, because the sender identity arrives from the wire and a codec that validates field shapes
does not know the membership. `Outsiders` is the set of identities outside the membership that may inject a
well-formed message, and it is empty in the closed-cluster configuration.

Logs are not modelled here. Election safety follows from one vote per term and majority intersection alone,
and the election restriction that compares logs only ever refuses votes, so a model without it admits every
interleaving a model with it admits and more.

| Configuration | Module | What it models | Expected |
| --- | --- | --- | --- |
| `MCRaftElectionClosed` | RaftElection | the closed cluster, where every message comes from a member | green |
| `MCRaftElectionOutsiderUnguarded` | RaftElection | negative model: the tally counts a granted reply from an outsider, so two candidates complete a majority count in one term | red (required) |
| `MCRaftElectionOutsiderGuarded` | RaftElection | the same outsider against the shipped filter, which restores election safety | green |
| `MCRaftElectionTermInflation` | RaftElection | negative model: the filter placed after the term rule, where an outsider that cannot win still moves a member's term | red (required) |
| `MCRaftElectionFilterFirst` | RaftElection | the shipped placement, with the filter before the term rule, which closes the inflation lever | green |

The last two are the pair that decided the placement rather than recording it. Discarding a non-member
before the tally is enough for election safety, and `MCRaftElectionOutsiderGuarded` is green on it, but
`NoTermInflation` still fails there: a stranger that can never complete a quorum can raise a member's term
and unseat a leader the cluster agreed on. Filtering before the term rule closes that, and it costs nothing
for member traffic, because the filter admits every member unconditionally.

## The Raft log matrix

`RaftLog` models the two rules that govern the log: the Figure 8 commit restriction and the
persist-before-reply obligation. Each is a constant, so the model is what says the rule is load-bearing
rather than tidy. Elections are collapsed into a single `Elect` action that raises the term against the
paper's election restriction, because running the votes message by message adds only interleavings that
reach the same leader states, and `RaftElection` already carries the message-level election with the sender
identity a tally must filter. Replication copies the leader's whole log, which is where the consistency
check and conflict truncation converge; it over-approximates only how fast a follower catches up.

| Configuration | Module | What it models | Expected |
| --- | --- | --- | --- |
| `MCRaftLogVolatileReplicas` | RaftLog | negative model: a commit may count a replica holding the entry only in memory, and the crash that follows takes it back | red (required) |
| `MCRaftLogDurable` | RaftLog | both rules in force with crashes enabled, so no crash takes back what a commit was counted on | green |
| `MCRaftLogFigure8Withdrawn` | RaftLog | negative model: the Figure 8 restriction withdrawn, so a leader commits an inherited entry a quorum happens to hold and a server that never held it still wins a later term | red (required) |
| `MCRaftLogFigure8` | RaftLog | the restriction in force, where an inherited entry commits only as a side effect of one above it | green |

Both pairs run at five servers and differ in exactly one constant, which is what makes each positive mean
something. Five is not a comfort setting: both hazards need a minority replication that a later term
revisits, and at three servers the minority is one and neither scenario can be written down at all. The
Figure 8 pair also needs `MaxTerm = 5`; at three the negative runs green, which is the failure the bounds
rule above describes.

A crash clears the leadership when the crashed server holds it. The role is volatile by Figure 2 and
`RaftNode.FromState` restores a follower, so a model that leaves a crashed leader leading reports a
`LeaderCompleteness` violation for a leader no deployment still has: the counterexample is a leader whose
own copy of an entry is still volatile while three followers have made it durable, which is a lawful commit
the leader is not itself counted in.

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
