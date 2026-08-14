# TLA+ workspace

Model-checked specifications for the protocol tiers this repository ships or is deciding whether to
ship: the anti-entropy session pair with the completion frame, the consensus-anchored checkpoint
seal, QuePaxa, and Raft's leader election and log rules. The session pair, the seal, the election and the
log model the code as it is. The two QuePaxa protocol modules model the published protocol before any
code exists, which is the point of ordering it that way, while the QuePaxa durability module and its
torn-write sibling model the code that followed them. `QuePaxaMembership` is a third case again: it
models a design for code that does not exist yet and is not in the published protocol either, since
the QuePaxa paper is silent on reconfiguration. The abstraction decisions are
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

A JRE, a current `tla2tools.jar`, and `sany` / `tlc` wrappers around it (heap-bounded; override with
`TLA_JAVA_HEAP`). The runners take the wrappers from `PATH`, or from the directory `TLAPLUS_HOME`
names, or from `-ToolchainPath`, and accept whichever extension the operating system uses, so where
the toolchain is installed and what its wrappers are called are properties of the machine and not of
this repository. Run from this directory:

    $env:TLAPLUS_HOME = "<the toolchain directory>"
    sany .\SessionPair.tla
    tlc -workers 4 -checkpoint 0 -config .\MCSessionPairSafety.cfg .\SessionPair.tla
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

## The QuePaxa durability matrix

`QuePaxaDurable` is one recorder across a crash: what has to be on stable storage before it answers,
what a restart brings back, and what a restart is allowed to take away. Lemma C.10's argument for the
fast path turns on the first proposal of a step never being overwritten, which a running recorder holds
by construction, because Algorithm 3 assigns the first proposal only on the branch that advances the
step. A crash is where it can be lost, and a recorder that comes back below the step it answered from
takes a fresh first proposal at a step a proposer has already read.

The module is a single recorder, and that follows from the protocol rather than from cost. Recorders
are passive and never address one another, all communication being proposer-to-recorder, so indexing
every variable by a recorder would give a product of independent copies with no shared variable between
them, and the invariant over that product is the conjunction of the per-recorder ones. The
cross-recorder property, that two quorums never decide differently, is `QuePaxaConcrete`'s and is not
repeated. Three rules are constants in the `RaftLog` mould, so the model is what says each of them is
load-bearing rather than tidy: `RepliesAfterPersist`, the persist-before-reply obligation;
`RestoresFromDurableState`, the restore itself; and `PersistsWholeRegister`, whether the durable state
is all four register fields or the step and the first proposal alone.

| Configuration | Module | What it models | Expected |
| --- | --- | --- | --- |
| `MCDurableRecorder` | QuePaxaDurable | all three rules in force, where no crash takes back an answer a proposer has read | green |
| `MCDurableVolatileReply` | QuePaxaDurable | negative model: the persist-before-reply obligation withdrawn, so an answer leaves on state the disk does not hold | red (required) |
| `MCDurableFreshRestart` | QuePaxaDurable | negative model: the restore withdrawn, so a recorder that persisted faithfully comes back unwritten anyway | red (required) |
| `MCDurablePartialPersist` | QuePaxaDurable | negative model: the durable state is the step and the first proposal alone, so the prior aggregate a reply carried does not survive the crash | red (required) |
| `MCDurableStepZeroServed` | QuePaxaDurable | negative model: the same obligation withdrawn, pinned instead on the premise a host's step-zero short circuit rests on | red (required) |

All five run at the same constants, three proposal keys over steps four to seven, which is one complete
round and is what the concrete configurations run. Three keys rather than two is `MCAbstractSweep`'s
reason: two are enough for a first proposal that differs from another, and three for a strict ranking
of all of them as well as every tie two can make. Every negative is red at exactly the constants the
positive is green at, which is the test the bounds rule above sets.

The ghost state is what makes the property checkable at all. Stated as "a proposer never reads a first
proposal at a step the recorder later re-takes" it refers to the future, and TLC checks state predicates
rather than two-point trace properties. `served[s]` records the summary the recorder has answered for
step `s`, and `ServedFirstIsStable` and `ServedPriorAggregateIsStable` assert that a recorder standing
at `s` still holds it, which is a state predicate. The ghost survives a crash because it is what a
proposer holds rather than what the recorder does, and that is the whole of why it can express the
loss. Keeping one summary per step rather than a set of them loses nothing, because a second answer
differing from the first is reachable only through a state in which the recorder already stands at that
step holding the differing summary, and the invariants fail in that state before any action can
overwrite the ghost.

Each negative lists exactly the invariant it exists for and no other, which here is a requirement
rather than a habit. Two of the three rules break more than one of these invariants when withdrawn,
and TLC stops at its first violation, so a negative listing more than one reports whichever it reaches
first. That choice is sometimes decided and sometimes a tie: withdrawing the persist-before-reply
obligation reaches `StepZeroDurableStateWasNeverServed` strictly earlier than `ServedFirstIsStable`,
so a merged configuration never reaches the second at all, while withdrawing the restore reaches both
at the same depth and names one of them for reasons no run reproduces. Both were checked by running the
merged configurations rather than argued.

The last two configurations carry identical constants and differ only in the invariant each lists,
which is the first of those cases. `MCDurableStepZeroServed` is what the pair exists for: rebuilding
a step-zero durable state as an unwritten register is what a host's restore does with a snapshot the
recorder's own restore refuses, and it is safe only because such a state has answered nothing. What
makes that true is the reply waiting for the write, so the dependency between the two rules is checked
here rather than argued.

`TypeOK` carries the other half of that restore. A durable state at step zero holding no proposal in
any slot is the only step-zero state a faithful host writes, so the snapshot the restore refuses is one
this module says is unreachable rather than one it has to defend against - under a faithful store,
which is this module's standing assumption and is exactly what the torn-write matrix below withdraws.
Which states the restore refuses, as against which states a host reaches, stays with the unit suite,
where each rule is pinned by a rejection test and a mutant; which refusals a real store can make
necessary is the torn-write matrix's subject.

## The QuePaxa torn-write matrix

`QuePaxaTornWrite` is the durability model's store made unreliable: a write that can land field by
field, a store that can return a tuple it was never given, and the restore's state-local refusal
rules standing between the disk and the register. It answers the one durability question the unit
suite cannot: which torn snapshots the restore's rules catch, and which they provably cannot. The
module extends `QuePaxaDurable` rather than copying it: `Serve`, `Persist`, `Observe`, `Init`, the
served ghost and all four parent invariants are inherited as the same operators and cannot drift,
and the crash action is the one restatement - `GuardedCrash` places the restore guard in front of
the parent's `Crash` body, and the atomic configurations' digit-for-digit identity with
`MCDurableRecorder` is what holds that restatement to the parent. That placement is itself a checked result
rather than taste: the parent's `TypeOK` asserts the durable tuple is one a faithful host wrote,
which is precisely what a tear falsifies, so `MCTornDurableShape` lists the parent's `TypeOK` and is
red on it, and an in-module dimension would have had to weaken a shipped invariant under a constant.

Three rules are constants in the shipped mould. `WritesTupleAtomically`: a durable write lands whole
or not at all, and its withdrawal admits every per-field mix of the tuple on the disk and the tuple
being written, which covers every prefix tear under every field order a codec could pick, so the
results are independent of a serialization order nothing in this repository fixes.
`StoreReturnsWhatItWrote`: the store returns only tuples it was given, and its withdrawal admits
fabrication, kept apart from tearing because it answers a different question. And
`RestoreRefusesImpossibleStates`: a restart runs the refusal rules, in their expressible form - the
step floor, a first proposal above step zero, an aggregate ordering at or above it, no carry into the
round's first step, and nothing in any slot at step zero. A refused restore is a disabled crash: the
C# restore throws and the host does not start, so a refused host answers nothing further, and
disabling the action is exact for safety - the reachable states and the violation depths are those an
explicit halted-host encoding would produce - and silent about availability. The refused tuple stays
on the disk, where `DurableStateIsRestorable` reads it, so the refusal is checked and only the
restart is elided.

| Configuration | Module | What it models | Expected |
| --- | --- | --- | --- |
| `MCTornAtomic` | QuePaxaTornWrite | atomic writes, faithful store, restore guard in force; must reproduce `MCDurableRecorder` digit for digit | green |
| `MCTornAtomicUnvalidated` | QuePaxaTornWrite | the same store with the guard withdrawn; must reproduce the same digits, so the rules refuse nothing a faithful host writes | green |
| `MCTornRefusedTear` | QuePaxaTornWrite | negative model: a torn write leaves a tuple the restore refuses - the caught half | red (required) |
| `MCTornAcceptedTear` | QuePaxaTornWrite | negative model: a torn write leaves a tuple the restore accepts that contradicts an answer already given - the uncatchable half | red (required) |
| `MCTornRestoreLoses` | QuePaxaTornWrite | negative model: the accepted tear restored, so the register comes back holding a different first proposal at an answered step | red (required) |
| `MCTornPriorAggregate` | QuePaxaTornWrite | negative model: the accepted tear that moves the aggregate a proposer's phase two and three read | red (required) |
| `MCTornDurableShape` | QuePaxaTornWrite | negative model: the parent's `TypeOK` under tearing, which is the placement argument as a run | red (required) |
| `MCTornStepZeroPremise` | QuePaxaTornWrite | the tearing green: no tear breaks the step-zero premise, and the guarded register never leaves recorder-reachable states | green |
| `MCTornUnguardedRestore` | QuePaxaTornWrite | negative model: the guard withdrawn, so a torn restore installs a register state the register cannot reach on its own | red (required) |
| `MCTornFabricated` | QuePaxaTornWrite | negative model: atomic writes and the guard in force against a lying store - the boundary of what atomicity buys | red (required) |

All ten run at the parent's constants with all three parent durability rules in force, because a
corruption result under a withdrawn durability rule would not say which of the two produced it.

The headline is a pair, and neither arm alone is the result. `MCTornRefusedTear` is the caught half:
the refusable mixes include a landed step with no first proposal beside it and a step-zero tuple
carrying a proposal, which is the one register shape the versioned host's own restore in
`QuePaxaVersionedNode.FromState` refuses rather than handing to the recorder's restore, and
which no other configuration in this directory can hand that check as a reachable input.
`MCTornAcceptedTear` is the uncatchable half: TLC's own counterexample tears a single field - the
register at `(6,1,1,1)` over an answered durable `(5,1,1,Nil)`, the write landing only the carried
aggregate, leaving `(5,1,1,1)` on the disk - and the mix passes every rule because it is a state some
honest run of the register reaches, so only the history of what was answered says otherwise. The
accepted mixes are crash-free reachable inside `MCDurableRecorder`'s own green, so any state
predicate that refused them would turn the shipped positive red: there is no stronger state-local
rule to write, and whole-tuple atomicity is load-bearing in the persist contract rather than
advisory. `MCTornFabricated` bounds the claim from the other side: with writes atomic and the guard
in force, a store that returns a tuple it was never given still hands the restore an accepted state
contradicting an answer already given - atomicity defends against tearing and not against a lying
store, whose defence is a self-checking document, outside this alphabet.

Each negative lists exactly the invariant it exists for, and the merged runs that justify the
assignments were run rather than argued. A configuration listing all five red subjects names the
parent's `TypeOK` on every attempt, because the earliest tears break the durable shape and the
restorability predicate in the same states, so the shallow pair is an order-dependent tie and a
merged listing names whichever comes first in the file - swapping the order swaps the answer.
`DurableRestoreKeepsEveryAnswer` is reached strictly before either stability invariant - it reads
the disk, and the crash that moves the loss into the register is one transition further - so a
merged listing of the three never reports the stability pair at all. The two stability invariants
are reached at the same depth, and repeated merged runs name sometimes one and sometimes the
other - the same irreproducible naming the durability matrix records for its fresh-restart
negative, there between a different pair of invariants. The two singleton-constant negatives are
justified the same way: at the unguarded constants the parent's `TypeOK` masks
`RegisterHoldsOnlyRecorderStates`, and at the fabrication constants it masks everything, so each
lists its own subject alone.

`MCTornStepZeroPremise` is the tearing green and the module's one exhaustive run over the tear
alphabet, at 10,694,841 states generated and 641,422 distinct, reproduced digit for digit over
three runs. It certifies that no tear breaks the premise the host's step-zero short circuit rests
on - after the first completed write both mix sources stand at or above the round's first step, so
the durable step never returns to zero - and, through `RegisterHoldsOnlyRecorderStates`, that the
guarded restore keeps the register inside the states a recorder-driven register can hold, which is
the closure of the accepted set under the register's own operation. One boundary of that green must
be read precisely: the step-zero-carries-a-proposal clause gets a reachable input here, but no
invariant in this alphabet expresses what it defends, which is the silent discard of a proposal that
was never answered. Withdrawing that clause alone breaks nothing the `served` ghost records, so the
green must not be read as the C# check being redundant - what it protects is outside what this
module can state.

## The QuePaxa membership matrix

`QuePaxaMembership` is dynamic membership for the versioned register: the configuration is a field
of the decided record, and the configuration carried by the record decided at version *v* governs
consensus instance *v+1* and no other. It sits above `QuePaxaConcrete` rather than beside it. One
instance's protocol is collapsed into a single atomic `Decide` guarded by a quorum of that
instance's configuration, because the question here is not whether an instance is safe - that is
`QuePaxaConcrete`'s discharged subject - but whether every quorum that ever completes lies inside
one configuration, and whether two hosts serving one instance can derive different ones. The
composition between the two modules is a named predicate rather than a checked refinement:
`QuePaxaConcrete` assumes the recorders of one instance run under one configuration, and
`OneConfigurationPerInstance` is exactly that assumption discharged as a checked invariant here.
That argument is exact for the leader hypothesis and holds for the recorder set only because
`QuePaxaConcrete` carries a single `Recorders` constant.

Every row below has run and matched its pin. The four greens explored their state spaces to
exhaustion with nothing left on the queue - `MCMembershipShipped` at 11,555,481 states generated
and 2,838,113 distinct, `MCMembershipDisjointChange` at 4,477,323 and 1,082,351,
`MCMembershipRemovedProposer` at 946,629 and 245,515, and `MCMembershipGuardedGenesis` at 3,873
and 2,044 - and each negative went red on exactly the invariant its own configuration names. A red
here is pinned by its exit code and by the invariant TLC reports and by nothing else, because a
run that stops at its first violation leaves the state counts to whichever states the workers had
reached at that instant.

The three assignments with more than one candidate were settled by merged runs rather than argued,
which is this directory's rule. `MCMembershipLocalConfig` and `MCMembershipOutsiderCounted` each
list the shallower of their two candidates and the merged listings name it: the local
configuration reaches `OneConfigurationPerInstance` at depth 3 with `Agreement` one decision
further on, and the counted outsider reaches `DecisionsCountOnlyMembers` at depth 2, because the
stranger is counted in the very first decision while the fork that breaks `Agreement` needs a
second decision to stand beside the first. At `MCMembershipSplitGenesis`'s constants the merged
listing names `NoCrossClusterDecision` even though `Agreement` was listed ahead of it, which is
the general fact those runs exist to establish: TLC reports the invariant its breadth-first search
falsifies first rather than the first entry in the `INVARIANTS` list. That is exactly why the
earlier invariant is pinned in a sibling configuration at the same constants instead of as a
second entry in that file.

`Decide` carries one guard that is not a deployment rule but the collapsed-level encoding of
intra-instance agreement: a host counted in a decision at a version is not counted again at that
version for a different record. Without it two quorums of one uniform configuration could both
decide, every positive would be red and the matrix would pin nothing. With it, two different
records at one version need disjoint quorums, which majorities of one member set cannot supply,
while a split genesis affords them across the forked configurations. That is the witness class the
design panel settled on, and it matters: two majorities of one member set can share a single
recorder, and a shared recorder is not a fork, because its aggregate carries the decided proposal
forward.

A decision installs the record at its writer alone. The recorders counted in a quorum have
*recorded* rather than *learned* - dissemination is best effort and reports nothing - and `Learn`
is what moves a record anywhere else. That asymmetry is the model's statement of the mechanism
that preserves a decision: agreement within the instance over its one fixed recorder set, and not
the version gate, which is the activation boundary rather than the preservation rule.

Six rules are constants in the shipped mould. `ConfigDerivedFromRecord`, `VersionGateBinds`,
`RepliesOnlyFromMembers`, `ClusterBinds` and `DecommissionGated` each get a negative.
`ProposerMustBeMember` is the one whose withdrawal stays green, and that green is what its row was
run for.

| Configuration | Module | What it models | Expected |
| --- | --- | --- | --- |
| `MCMembershipShipped` | QuePaxaMembership | every rule in force over an agreeing deployment, with the member set changing at every version of a three-version chain | green |
| `MCMembershipDisjointChange` | QuePaxaMembership | the headline: a change to a member set sharing nothing with the outgoing one, so no pair of quorums could intersect | green |
| `MCMembershipRemovedProposer` | QuePaxaMembership | the non-member proposer filter withdrawn, which must change nothing - the green that prices that filter as operability rather than safety | green |
| `MCMembershipGuardedGenesis` | QuePaxaMembership | a split genesis against the chain-identity check, where the fork becomes a stranded host | green |
| `MCMembershipLocalConfig` | QuePaxaMembership | negative model: the configuration read from each host's deployment file instead of derived from its record, so one instance runs under two member sets | red (required) |
| `MCMembershipSplitGenesis` | QuePaxaMembership | negative model: one host provisioned with a different genesis list and no chain identity to notice, so two disjoint quorums decide one version | red (required) |
| `MCMembershipCrossClusterQuorum` | QuePaxaMembership | negative model: the same constants, pinned instead on the mixed quorum that is reachable one decision earlier than the fork | red (required) |
| `MCMembershipServesAWindow` | QuePaxaMembership | negative model: the version gate withdrawn, so a decision counts hosts that never learned the record installing them | red (required) |
| `MCMembershipOutsiderCounted` | QuePaxaMembership | negative model: an answer counted from a host outside the instance's configuration, which is an endpoint slot wired to the wrong machine | red (required) |
| `MCMembershipEagerDecommission` | QuePaxaMembership | negative model: the decommission gate withdrawn, so the only live holder of the newest record is retired and the register is wedged | red (required) |

One green is half of a constants-matched pair, and it is what keeps the negatives from being the
whole argument. `MCMembershipGuardedGenesis` runs at `MCMembershipSplitGenesis`'s constants and
differs in one flag, which is the bounds rule this directory sets: the positive is only meaningful
where its paired negative is red. The other three greens are not paired, and each is unpaired for
its own reason worth stating rather than glossing. `MCMembershipShipped` is the baseline every
negative except the genesis trio is a one-flag departure from, so its pairing is with all four of
them at once; those four also sit one version shallower than the baseline, which is the permitted
direction, since the rule this directory sets forbids a positive checked shallower than its
negative rather than deeper. `MCMembershipDisjointChange` is a standalone claim - that no overlap is required
between consecutive configurations - and has no negative because the thing it certifies is the
absence of a requirement rather than the presence of a guard. `MCMembershipRemovedProposer` is the
sixth flag's price, and it is green: withdrawing that flag breaks nothing, so a negative would
have nothing to demonstrate. A red there would have made the flag load-bearing and reopened the
design decision resting on it.

`MCMembershipCrossClusterQuorum` carries the split negative's constants
exactly and differs only in the invariant it lists, on the precedent the durability matrix already
records - a fork needs two decisions before `Agreement` can see it, while one mixed quorum breaks
`NoCrossClusterDecision` at the first, so a single file listing both would report whichever TLC
reached first and the other would stop testing what it was written for.

Three things a reader would otherwise over-read from this matrix. **What a green certifies is
narrower than its invariant list.** Every green lists all seven invariants, but three of them -
`DecisionsCountOnlyMembers`, `DecisionsCountOnlyCaughtUpHosts` and `NoCrossClusterDecision` - are
state-predicate restatements of guards `Decide` already conjoins wherever their flag is TRUE, so
they cannot fail in a positive and carry no assurance beyond the guard. They are written that way
on purpose, because each has to be able to go red in the one negative that withdraws its flag, and
a rule that is enforced in the action and checked in the state is the only shape that does both.
In a green, the real checks are `TypeOK`, `Agreement`, `OneConfigurationPerInstance` and
`LatestDecisionSurvives`. **The cluster arm is inert in seven of the ten rows**, which bind
`OneCluster`: one identity is carried by every configuration there, so both sides of the check are
equal by construction and `ClusterBinds = TRUE` constrains nothing. Only the genesis trio exercises
it. **And the recorder-side refusals need no placement negative**, which is the one obligation this
matrix discharges by derivation instead of by a run. `RaftElection` carries a placement pair
(`MCRaftElectionTermInflation` and `MCRaftElectionFilterFirst`) because Raft's term rule moves a
member's term *before* the tally, so a filter placed after it leaves an outsider a lever. The
versioned host has no such lever: every refusal in `QuePaxaVersionedNode.Handle` precedes any
mutation, so no state exists for a wrongly-ordered filter to move, and there is nothing for a
negative to demonstrate. The Raft pair is in the next section and must not be imported here on the
strength of the resemblance.

Three bounds are derived rather than chosen for cost. `Hosts` is strictly larger than every
configuration in every row, or `MCMembershipOutsiderCounted` would have no outsider to inject and
`MCMembershipRemovedProposer` no non-member to propose - the `RaftElection` `Outsiders` shape
applied at the recorder. The split-genesis constants must afford a pair of disjoint majorities
between the two genesis member lists, or under the binding guard that red is unreachable rather
than merely mis-witnessed.

And the chain length is bound per row rather than matrix-wide, because the rows do not all have the
same subject. `MCMembershipShipped` and `MCMembershipDisjointChange` run three versions, which is
what it takes to decide under a configuration that a decision installed rather than one genesis
supplied; at two versions the rule this section opens with is exercised for exactly one step, and
the chained step is outside the alphabet. The other eight rows run two, because neither the
proposer filter, which is per-decision, nor the genesis defence, which lives at the genesis
boundary, gains anything structural from a second change, and a negative is cheapest at the
shallowest bound that still reaches its violation. Three is where the chain stops: every guard and
every invariant here reads the held records, the per-version decision sets and the highest decided
version, nothing quantifies backwards over a chain, and the invariants are at most pairwise, so an
interacting pair spans at most two generations beside the deciding one and a fourth version adds
states without adding a shape. That derivation is the one to revisit first if the module ever
grows chain-depth-sensitive machinery, configuration reclamation above all.

Symmetry is not available. `Members`, `Genesis` and `Cluster` name hosts and configurations by
identity, so a host permutation is a symmetry of the constants only if the configurations are
permuted with them. No configuration declares one.

Two modelling decisions cut the alphabet and are worth stating because each looks like an omission.
A record carries no value: `Agreement` needs two records at one version to be distinguishable, the
writer already distinguishes them, and no invariant or guard in the module reads a value, so a
value field would be a dimension that only multiplies states. And a decision counts exactly a
quorum rather than at least one, which is faithful rather than a reduction - the endpoint array has
one slot per member, so a stranger occupies a slot instead of adding one.

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

All four configurations name `Servers` as model values and declare `SYMMETRY ServerSymmetry`. The module
carries the argument for why that is sound; the short form is that no action and no invariant reads a server
except through equality, set membership or a cardinality, and a log entry carries the term that created it
rather than the server that appended it. The declaration was checked rather than assumed, by running every
configuration BOTH ways before it was taken, and what that showed is the evidence the quotient rests on: both
negatives stay red on their own invariants, both positives stay green at the same depth, and the brute-force
runs reproduce the counts recorded above digit for digit. `MCRaftLogDurable` goes from 4,591,516 distinct
states to 62,021 and `MCRaftLogFigure8` from 76,878,566 to 846,584, which is 74 and 91 times respectively -
both below the 120 that five servers permit, as a sound quotient must be, since a factor above `5!` could
only come from collapsing states no permutation relates. The wall clock falls with the state count on
whatever machine runs both, which is what makes `Run-Raft.ps1` runnable inside a working session;
the figure itself is a property of the machine and is not recorded here. Run the brute-force pair again beside the quotient if the module's actions ever start reading a
server for anything but its identity.

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
  `QuePaxaDurable` needs no union: `MCDurableRecorder` reaches all four actions and both sides of
  every branch in them, the same-step fold at 82,662 and the advance at 11,406, the fold keeping the
  incumbent at 66,556 and taking the newcomer at 16,106, and the carry taking the aggregate at 10,206
  and clearing it at 1,200. `QuePaxaTornWrite` needs a pair, and one member of it is a red:
  `MCTornStepZeroPremise` fires every action except `Fabricate` - `Serve` 2,162,811 times,
  `TornWrite` 7,451,170, `Persist` 599,901, `GuardedCrash` 439,438 and `Observe` 41,520 - and
  `Fabricate` is disabled at every tearing configuration's constants by design, so its firing is
  witnessed by `MCTornFabricated`'s own counterexample trace rather than by an exhaustive run.
  Those invocation totals reproduce digit for digit at one and four workers; the per-action
  distinct-state figures do not, because here more than one action can be first to a state and the
  attribution races, so unlike the parent's, only the invocation totals are recorded. In
  `MCTornAtomic` the guarded crash fires 19,406 times and adds nothing, which is the parent's
  crash-adds-nothing result reproduced through the guard.
- `QuePaxaDurable` and `QuePaxaTornWrite` are one recorder and every invariant they check is local to
  that recorder, so what a green run there certifies is that a crash never takes back an answer the
  recorder has already given. It certifies nothing about a quorum. That two quorums never decide
  differently is `QuePaxaConcrete`'s subject and that module has no crash at all, so no configuration
  in this directory covers a decision resting on a quorum that a crash then disperses - and none
  joins the corruption cause to the divergence consequence either: a torn snapshot here loses one
  recorder's answers, while the divergent committed record it could produce at the versioned host
  needs versions, a committed record, reserved priorities and a second host, all outside this
  alphabet, and stays with the split-leaders configurations and the unit suite's rejection tests for
  a restored snapshot whose configured leader or recorder version is not the one its own committed
  record derives. Reaching either would mean a durability dimension in the concrete module, whose
  largest configuration already holds 27,328,647 distinct states.
- `QuePaxaMembership` checks no progress property. `Learn` is enabled from any live holder, so
  dissemination is over-approximated and nothing there says a joiner catches up; the availability
  cost of a membership change, which is the whole of the operator guidance, is outside what the
  module states. `LatestDecisionSurvives` is the one thing said about it, and it is a possibility
  predicate - recovery remains possible - rather than a claim that recovery happens. The gate it
  checks is also unenforceable: nothing in the library stops a host being killed, so what the red
  beside it buys is that the rule is checked, not that it is imposed.
- `QuePaxaMembership` does not close the standing gap in the note above. That gap joins a
  *corruption* cause to the divergence consequence and needs reserved priorities in the alphabet;
  this module supplies a *configuration-divergence* cause instead, which is the adjacent gap. The
  restore rules that would carry the corruption side stay with the unit suite, on the same
  precedent the durability matrix records, and no configuration here models durability or tearing:
  `QuePaxaTornWrite` extends `QuePaxaDurable`, which has no record, no version and no leader, so
  teaching it a configuration means teaching it all three first.
- No configuration in the membership matrix checks deadlock. The chain is bounded by `MaxVersion`,
  so every run reaches a state with nothing left to decide, and that terminal state is the bound
  rather than a defect. Both Raft matrices set `CHECK_DEADLOCK FALSE` throughout for the same
  reason, though neither writes the reason down; this is where it is written down.
- `QuePaxaMembership` needs no union either, and that is a result rather than a convenience. It has
  three actions (`Decide`, `Learn` and `Decommission`, written as separate `Next` disjuncts so a
  coverage run can tell them apart), and every one of the four greens fires all three a nonzero
  number of times, so the covering union is any single green and no configuration in that matrix
  exists to reach an action the others cannot. In `MCMembershipShipped` `Decide` fires 403,848
  times, `Learn` 8,110,176 and `Decommission` 3,041,456, which with the initial state is the whole
  11,555,481 states that run generates, so the arithmetic itself says no action was missed. Only
  the invocation totals are recorded, on the reason the torn-write note above gives: where more
  than one action can be first to a state, the per-action distinct-state split is an attribution
  race rather than a figure. `Learn` is the largest of the three totals in every green but the
  smallest, where `Decommission` edges past it, which is the ordinary shape of a model where
  learning is the high-fan-out step; `MCMembershipGuardedGenesis` fires the three 444, 1,632 and
  1,796 times, so it covers them shallowly and is a cheap smoke row rather than a substitute for
  the others. The two rows that run three versions were measured at both bounds, and what the
  comparison says is that the third version is not more of the same: `Decide` grows from 11,544
  invocations to 403,848 while the state space around it grows by a smaller multiple, so the
  deeper chain reaches a proposer regime the two-version bound does not, and the same shape
  appears in `MCMembershipDisjointChange`. The gate stated above is now satisfied for every module
  in this directory. What none of it says is whether an action's guards were pinned - coverage
  says only that the action fired.
- `QuePaxaTornWrite`'s tear alphabet has edges that are scope notes rather than claims. The mix runs
  against the current volatile tuple only, because the reply waits for the persist and the host's
  runner is single-consumer, so one write is in flight at a time and reordering across concurrent
  writes cannot arise. The reserved-priority refusals are out of its alphabet - proposals are opaque
  ordered keys, so a tear that preserves the tuple's shape and changes only a key's priority is
  unrepresentable here and stays with the recorder's unit suite. A refused restore says nothing
  about availability: a host that fails closed and never starts is invisible to a safety model. And
  the module cannot say whether a checksum would close the accepted-tear hole, because the durable
  tuple carries no field to check; it covers a checksummed store only insofar as a detected tear is
  a refused restore. Two conjuncts of the restore guard are deliberately inseparable: the
  aggregate-beside-a-first-proposal rule is subsumed by the ordering rule under the integer
  encoding, and the recorder's step floor and the host's step-zero branch are one test because the
  steps between them are not in the alphabet, so no negative pins either pair apart and none
  pretends to.
- In `MCDurableRecorder` the crash action generates 19,406 successor states and none of them is new,
  which is the sharpest available statement of what the restore buys: with all three rules in force a
  crash reaches no state a crash-free run does not. It is a result rather than an artefact of an
  unreachable action, and the two halves of that are worth keeping apart. The action fires 19,406
  times, so it is not disabled; and it adds nothing because an answer requires the durable state to
  match, so the durable step never lags a step that has been answered from and a crash therefore
  returns to a state already visited. Under three of the four negatives the same action does add
  states and stands in the counterexample. The fourth, `MCDurableStepZeroServed`, violates with no
  crash at all, and that is what its invariant is for: it states the premise the short circuit rests
  on rather than the loss that follows, so a negative model exists to make the premise fail. The
  restart itself is exercised against that invariant in the positive, which lists it and crashes
  against it throughout.
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
- Every green QuePaxa configuration listed above explored its state space to exhaustion, the
  membership greens included, and every one of them drained its queue to zero; nothing
  here used bitstate-equivalent techniques, and nothing may be reported as verified that did. A red
  configuration stops at its first violation and is not exhaustive by design - what it certifies is
  that the defect is reachable, which is all a negative model is for.
- Exhaustion is itself probabilistic and TLC reports by how much. Every completed run of any size
  prints an estimate of the chance it skipped a reachable state because two distinct states shared a
  fingerprint, calculated from the state count, and every run but the two smallest prints a second
  one taken from the fingerprints it actually saw. They are not uniformly negligible, and which
  configuration is worst is not the one size alone suggests. Brute-force `MCRaftLogFigure8`, at
  76,878,566 distinct states, reported 0.0042 calculated and 0.0015 from the actual fingerprints,
  which is about one chance in 667 and is not a figure to report a verification against. The same
  configuration quotiented by `ServerSymmetry` reports 5.4e-7 and 1.0e-6 at 846,584 distinct
  states, so the symmetry declaration buys confidence as well as time. The QuePaxa greens were then read rather than assumed, and the
  largest of them is `MCAbstractSweep` and not the concrete configuration this note used to name.

  | Configuration | Distinct states | Calculated | From the actual fingerprints |
  |---|---|---|---|
  | `MCAbstractSweep` | 29,496,528 | 0.0012 | 1.0e-4 |
  | `MCConcreteSingleLeader` | 27,328,647 | 2.8e-4 | 1.5e-5 |
  | `MCAbstractCrash` | 19,703,792 | 1.1e-4 | 3.3e-5 |
  | `MCAbstractSafety` | 8,739,712 | 3.0e-5 | 6.5e-6 |
  | `MCConcreteTwoLeadersGuarded` | 7,038,491 | 1.8e-5 | 1.2e-6 |
  | `MCMembershipShipped` | 2,838,113 | 1.3e-6 | 2.1e-5 |
  | `MCMembershipDisjointChange` | 1,082,351 | 2.0e-7 | 3.7e-8 |
  | `MCTornStepZeroPremise` | 641,422 | 3.5e-7 | 1.9e-10 |
  | `MCMembershipRemovedProposer` | 245,515 | 9.3e-9 | 3.7e-9 |
  | `MCDurableRecorder` | 27,555 | 1.7e-10 | 1.0e-11 |

  The calculated column is a function of the state count and reproduces run to run; the actual
  column follows the fingerprints a run happens to draw, so it is one matrix run's snapshot and the
  whole column is refreshed together rather than row by row.
  `MCConcreteSingleLeaderDeclaredSchedule` matches `MCConcreteSingleLeader` on both counts and on
  the calculated estimate, as it must, and reports 3.9e-5 from its own fingerprints, the one column
  where two runs of the same state graph are entitled to differ. `MCAbstractSweep` is the only
  QuePaxa configuration whose calculated estimate reaches a thousandth, and its actual figure of
  about one in 10,000 is fifteen times better than the brute-force Raft log run's, which still
  leaves it the weakest line in the table on both columns. `MCMembershipShipped` is the one row
  where the actual estimate is the worse of the two rather than the better; everywhere else the
  fingerprints TLC saw were kinder than the state count predicted. That row reported the same
  ordering at its old bound, so it is a property of the row and not of a seed: the two estimates
  come from different formulas, and "optimistic" names the assumptions the first one makes rather
  than a promise to bound the second. Read both lines on any configuration past a few million
  distinct states rather than assuming they stay small. Below a few thousand distinct states there
  is only one line to read: the two smallest greens in the matrix, `MCMembershipGuardedGenesis` at
  2,044 distinct states and calculated 2.0e-13 and `MCConcreteDeclaredScheduleBinds` at 292, print
  the calculated estimate and no second one, because the state space is too small to support one.
- Value symmetry is declared in the abstract QuePaxa configurations and is sound there: nothing in
  the module reads a value except to compare it with another value, and the single CHOOSE picks by
  a predicate that ignores values. Where the proposal key leaves that choice open, which is the
  tie-detection configuration, the proposal CHOOSE returns is consumed only under a guard that the
  maxima are unique, so an arbitrary choice never reaches a state. Replica symmetry is not
  available in either QuePaxa module, because the tiebreak reads replica and proposer identities.
- Server symmetry is declared in all four `RaftLog` configurations, and it is available there for
  exactly the reason it is unavailable in QuePaxa: no action and no invariant reads a server except
  through equality, set membership or a cardinality over a set comprehension, and a log entry carries
  the term that created it rather than the server that appended it, so nothing tiebreaks on an
  identity. The declaration covers the invariants those configurations check and no temporal
  property, because the quotient can hide a temporal counterexample rather than report it. The Raft
  log matrix section above records the both-ways run that licensed it.
