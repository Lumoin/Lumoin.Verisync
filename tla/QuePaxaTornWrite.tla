--------------------------- MODULE QuePaxaTornWrite ---------------------------
(*
The durability model's store made unreliable: a write that can land field by
field, a store that can return a tuple it was never given, and the restore's
state-local refusal rules standing between the disk and the register. The
module answers one question the unit suite cannot: which torn snapshots the
restore's rules catch, and which they provably cannot.

QuePaxaDurable assumes a faithful store. Its TypeOK asserts that the durable
tuple is one a faithful host wrote, and the discharge argument for the
step-zero short circuit reads exactly those conjuncts as its premise. A
corruption dimension is the negation of that assumption, so it lives beside
the parent rather than inside it, and a tearing configuration listing the
parent's TypeOK is red - which is the placement argument as a run rather than
as prose.

Extending the parent is what keeps the sibling honest. Serve, Persist,
Observe, Init, the served ghost and all four of the parent's invariants are
inherited as the same operators and cannot drift, no variable is added, and
the atomic arm's transition relation is the parent's exactly: with the store
faithful and writes atomic, GuardedCrash is Crash, because every tuple a
faithful host writes satisfies the restore guard. The two atomic
configurations therefore reproduce MCDurableRecorder's state space digit for
digit, one with the guard in force and one without, which certifies two
facts separately: the sibling is a conservative extension, and the refusal
rules refuse nothing a faithful host writes.

The guard is the composite restore a real restart runs, in the form the
alphabet can express: a step-zero snapshot is rebuilt unwritten and so must
carry nothing in any slot, and every other snapshot must stand at or above
the round's first step, carry a first proposal, carry an aggregate ordering
at or above it, and carry no aggregate down into the round's first step.
Two collapses in that list are deliberate and neither can be pinned apart.
The aggregate-implied-by-a-first-proposal rule is subsumed by the ordering
rule, because the base value is zero and every proposal orders above it, so
no negative configuration can withdraw one without the other and none is
written. And the step floor and the step-zero branch are one test here,
because the steps between zero and the round's first step are not in the
alphabet, so the recorder's floor refusal and the host's step-zero rebuild
are pinned as a single conjunct.

A refused restore is a disabled crash. The C# restore throws and the host
does not start, so a refused host answers nothing further, and every state a
run reaches after a refused restart is a state the same run reaches without
the crash. Disabling the action is therefore exact for safety in both
directions - the reachable states and the violation depths are those an
explicit halted-host encoding would produce, with no extra variable to
destroy the atomic arm's digit-for-digit identity - and silent about
availability, which is a scope note rather than a claim. The refused tears
are not lost to the model: TornWrite still lands them on the disk and
DurableStateIsRestorable fails there, so the refusal itself is checked and
only the restart is elided. No refusal semantics over disabled actions can
deadlock this module, because a request at the highest step is enabled in
every state.

The tear is a per-field mix of the tuple on the disk and the tuple being
written. A prefix tear over a serialized document admits one outcome per cut
point in one field order; the mix admits every outcome in every field order,
so the result is independent of a serialization order nothing in the
repository fixes. The mix runs against the current volatile tuple only,
because the host holds the reply until the persist returns and its runner is
single-consumer, so a second write is never in flight while one is
outstanding. Fabrication is a separate action answering a separate question:
tearing asks what an interrupted write leaves, fabrication asks what a lying
store returns, and the one configuration fabrication earns is the one where
atomicity is in force and the loss still stands - the boundary of what
atomicity buys.

The headline is a pair, and neither arm alone is the result. The guard
catches some tears: DurableStateIsRestorable is red under tearing, and its
earliest violations include a step-zero tuple carrying a proposal, which is
the one snapshot shape the versioned host's own restore refuses, given a
reachable input for the first time anywhere in this directory. And the guard
provably cannot catch the harmful tears: DurableRestoreKeepsEveryAnswer is
red with the guard in force, because a mix of two faithfully written tuples
can be a state some honest run of the register reaches, so it passes every
rule that reads the tuple alone and only the history of what was answered
says otherwise. The accepted mixes are crash-free reachable inside the
parent's own green, so any state predicate that refused them would turn
MCDurableRecorder red: there is no stronger state-local rule to write, and
whole-tuple atomicity is load-bearing in the persist contract rather than
advisory.
*)
EXTENDS QuePaxaDurable

CONSTANTS
    WritesTupleAtomically,          \* Durability: a durable write lands whole or not at all.
    StoreReturnsWhatItWrote,        \* Durability: the store returns only tuples it was given to write.
    RestoreRefusesImpossibleStates  \* Durability: a restart runs the restore's state-local refusal rules.

ASSUME WritesTupleAtomically \in BOOLEAN
ASSUME StoreReturnsWhatItWrote \in BOOLEAN
ASSUME RestoreRefusesImpossibleStates \in BOOLEAN

\* The tear is the tear of a whole-register write. A host persisting only the
\* step and the first proposal names Nil for both aggregates, so its
\* interrupted write is a different mix this module does not model, and no
\* configuration withdraws the whole-register rule while tearing.
ASSUME ~WritesTupleAtomically => PersistsWholeRegister

(* The restore a host runs on a snapshot, as one predicate over the tuple
   alone. A step-zero snapshot is rebuilt as the unwritten register and so
   must carry no proposal in any slot; every other snapshot reaches the
   recorder's relational rules in their expressible form. The conjunct
   requiring an aggregate beside a first proposal is subsumed by the ordering
   conjunct under the integer encoding and stays for traceability to the
   rule it mirrors. *)
RestoreAccepts(s, f, a, p) ==
    IF s = StepZero
    THEN f = Nil /\ a = Nil /\ p = Nil
    ELSE /\ s >= FirstStep
         /\ f # Nil
         /\ a # Nil
         /\ a >= f
         /\ (s = FirstStep) => (p = Nil)


(* An interrupted write. Each field of the durable tuple either keeps what
   the disk held or takes what the write named, which covers every prefix
   tear under every field order a codec could pick; the whole-tuple outcome
   is one of the mixes, so a completed write needs no separate case. The mix
   draws on the current volatile tuple only, because the reply waits for the
   persist and the host's runner is single-consumer, so exactly one write is
   ever in flight. The guard requiring a write to be owed is Persist's own,
   and the change requirement keeps the action from manufacturing stuttering
   successors. *)
TornWrite ==
    /\ ~WritesTupleAtomically
    /\ ~IsDurable
    /\ \E ts, tf, ta, tp \in BOOLEAN :
        /\ dstep'  = IF ts THEN step  ELSE dstep
        /\ dfirst' = IF tf THEN first ELSE dfirst
        /\ dagg'   = IF ta THEN agg   ELSE dagg
        /\ dprior' = IF tp THEN prior ELSE dprior
    /\ <<dstep', dfirst', dagg', dprior'>> # <<dstep, dfirst, dagg, dprior>>
    /\ UNCHANGED <<step, first, agg, prior, served>>


(* A store that returns a tuple it never wrote. The action carries no gate on
   a write being owed, because a lying store is not confined to the moment of
   a write, and it ranges over the whole type domain rather than over mixes,
   because a fabricated value answers a different question than an
   interrupted write does. *)
Fabricate ==
    /\ ~StoreReturnsWhatItWrote
    /\ \E s \in RegisterSteps, f \in Summaries, a \in Summaries, p \in Summaries :
        /\ <<s, f, a, p>> # <<dstep, dfirst, dagg, dprior>>
        /\ dstep' = s
        /\ dfirst' = f
        /\ dagg' = a
        /\ dprior' = p
    /\ UNCHANGED <<step, first, agg, prior, served>>


(* The parent's Crash with the restore guard in front of it. A tuple the
   restore refuses disables the restart rather than rebuilding anything,
   which is the fail-closed host elided to its safety content; the tuple
   itself stays on the disk where DurableStateIsRestorable reads it. Under a
   faithful atomic store the guard never fires and this action is the
   parent's Crash exactly, which is what the atomic configurations' count
   identity checks. *)
GuardedCrash ==
    LET restored == IF RestoresFromDurableState /\ dstep # StepZero
                    THEN <<dstep, dfirst, dagg, dprior>>
                    ELSE <<StepZero, Nil, Nil, Nil>>
    IN  /\ RestoreRefusesImpossibleStates => RestoreAccepts(dstep, dfirst, dagg, dprior)
        /\ restored # <<step, first, agg, prior>>
        /\ step' = restored[1]
        /\ first' = restored[2]
        /\ agg' = restored[3]
        /\ prior' = restored[4]
        /\ UNCHANGED <<dstep, dfirst, dagg, dprior, served>>


TornNext ==
    \/ \E s \in Steps, p \in Proposals : Serve(s, p)
    \/ Persist
    \/ Observe
    \/ TornWrite
    \/ Fabricate
    \/ GuardedCrash


TornSpec == Init /\ [][TornNext]_vars


\* The domains alone. The parent's TypeOK also asserts that the durable tuple
\* is one a faithful host wrote, which is precisely what a tear falsifies, so
\* the tearing configurations check domains here and check the faithful shape
\* nowhere - except the one configuration that lists the parent's TypeOK in
\* order to be red on it.
TornTypeOK ==
    /\ step \in RegisterSteps
    /\ first \in Summaries
    /\ agg \in Summaries
    /\ prior \in Summaries
    /\ dstep \in RegisterSteps
    /\ dfirst \in Summaries
    /\ dagg \in Summaries
    /\ dprior \in Summaries
    /\ served \in [Steps -> Answers \union {NoAnswer}]


\* What a faithful store owes the restore: every tuple on the disk is one the
\* restore will take. Its failure is a tear the rules catch, and its holding
\* under atomicity says the rules refuse nothing a faithful host writes.
DurableStateIsRestorable ==
    RestoreAccepts(dstep, dfirst, dagg, dprior)


\* What the restore's rules would have to guarantee to be a defence against
\* corruption: a tuple they accept contradicts no answer already given at the
\* step it stands at. Its failure with the guard in force is the module's
\* central object - a tear the rules cannot catch. The antecedent is not
\* vacuous, because under atomicity DurableStateIsRestorable makes it true of
\* every reachable durable tuple in the same configuration.
DurableRestoreKeepsEveryAnswer ==
    RestoreAccepts(dstep, dfirst, dagg, dprior) =>
        \A s \in Steps :
            (served[s] # NoAnswer /\ dstep = s) =>
                (dfirst = served[s].first /\ dprior = served[s].prior)


\* The register itself only ever stands in a state a recorder-driven register
\* can hold. With the guard in force this holds under tearing, which says the
\* accepted set is closed under Serve from every state the restore admits;
\* with the guard withdrawn a torn restore installs a state the register
\* cannot reach on its own, which is what the rules are for.
RegisterHoldsOnlyRecorderStates ==
    RestoreAccepts(step, first, agg, prior)

=============================================================================
