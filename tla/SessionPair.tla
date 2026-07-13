---------------------------- MODULE SessionPair ----------------------------
(*
The anti-entropy session pair as two communicating finite-phase roles over
directed, ordered, lossless FIFO channels, including the completion frame.
The model follows src/Lumoin.Verisync.Core/AntiEntropySession.cs (cited
below as AES:NNN) and covers the remove-aware mode only: add-only sessions
fold nothing anywhere, so the fold-delivery obligations are vacuous there
and the add-only guard rejections sit below this abstraction.

The three commissioned abstraction cuts:
 (a) The symbols phase collapses to one step. Decode fires spontaneously once
     the initiator is Reconciling with the peer context captured; the decode
     outcome reveals a ground-truth difference chosen nondeterministically in
     the initial state (diffPush, diffFetch, diffDrops, diffTrail). The
     done-before-every-transfer / completion-after-every-transfer bracket is
     preserved by the micro-step order. Probabilistic decode failure is
     subsumed by the crash action.
 (b) Contexts are abstract tokens. "folded" stands for "this side folded the
     peer's full exchange context"; the covers relation collapses to the
     delivery observables (pushApplied, trailApplied, answerApplied): a fold
     is poisoned exactly when an insert-bearing transfer the folded context
     covers is never applied. Early coverage of remove-dots (the responder's
     fold at the push apply while a trailing drop is still in flight) is the
     shipped, verified semantics and deliberately outside the property.
 (c) Wind-down and crash are enabled at every control point. Wind-down closes
     a role's inbound channel one-shot; queued frames still dispatch, the
     running handler's micro-steps run to completion, and the drain epilogue
     fires when the closed channel is empty. Crash stops a role dead at any
     micro-step boundary (a hook fault or process death at an await), leaving
     its control state frozen and non-terminal - the faulted third condition.
     Sends into a closed channel are lost silently (the socket transport's
     view; the in-process throw is covered by the crash action).

Negative models (executed defects the spec must reproduce; enabled by
constants, both FALSE for the shipped protocol):
 - BuggyDrainFold: the drain epilogue folds the peer context on a bare
   wind-down on both roles and reports Completed unconditionally (target A,
   the drain clobber).
 - BuggyEagerDrops: CompleteDecode applies the local drops - and folds the
   full peer context - before the fetch goes out (target B, the
   initiator-side twin). The shipped code defers them to the answer's apply.
*)
EXTENDS Naturals, Sequences

CONSTANTS
    BuggyDrainFold,     \* When true, the drain epilogue folds a captured context on a bare wind-down (negative model A).
    BuggyEagerDrops,    \* When true, the local drops apply eagerly with a fetch outstanding (negative model B).
    AllowCrash,         \* When true, the crash action is enabled.
    AllowWindDown       \* When true, the wind-down action is enabled.

VARIABLES
    diffPush,           \* Ground truth: the exchange carries one push the responder lacks.
    diffFetch,          \* Ground truth: the exchange carries one fetch the initiator lacks.
    diffDrops,          \* Ground truth: the resolver classifies local drops at the initiator.
    diffTrail,          \* Ground truth: applying the fetch answer surfaces a trailing drop.
    cIR,                \* The initiator-to-responder channel as a sequence of frames.
    cRI,                \* The responder-to-initiator channel as a sequence of frames.
    irClosed,           \* True once a wind-down has closed the responder's inbound.
    riClosed,           \* True once a wind-down has closed the initiator's inbound.
    i,                  \* The initiator's role record.
    r,                  \* The responder's role record.
    pushApplied,        \* True once the responder has applied the initiator's push.
    trailApplied,       \* True once the responder has applied the initiator's trailing drop.
    answerApplied       \* True once the initiator has applied the responder's fetch answer.

vars == <<diffPush, diffFetch, diffDrops, diffTrail, cIR, cRI, irClosed,
          riClosed, i, r, pushApplied, trailApplied, answerApplied>>
diffs == <<diffPush, diffFetch, diffDrops, diffTrail>>

States == {"Pinning", "Reconciling", "Resolving", "Completed", "Interrupted"}
InitiatorSteps == {"idle", "decodeDrops", "decodeFetch", "decodePush", "decodeMerge", "decodeCompletion", "decodeFinish",
         "answerDeferredDrops", "answerApply", "answerTrailingDrop", "answerMerge", "answerCompletion", "answerFinish"}
FrameTypes == {"offer", "context", "done", "fetch", "elements", "drop", "completion"}
Frames == [t: FrameTypes, n: 0..2]

Fr(kind) == [t |-> kind, n |-> 0]
Completion(count) == [t |-> "completion", n |-> count]

TypeOK ==
    /\ diffPush \in BOOLEAN /\ diffFetch \in BOOLEAN
    /\ diffDrops \in BOOLEAN /\ diffTrail \in BOOLEAN
    /\ cIR \in Seq(Frames) /\ cRI \in Seq(Frames)
    /\ irClosed \in BOOLEAN /\ riClosed \in BOOLEAN
    /\ i \in [st: States, pc: InitiatorSteps, folded: BOOLEAN, conv: BOOLEAN,
              deferred: BOOLEAN, sent: 0..2, captured: BOOLEAN, stopped: BOOLEAN]
    /\ r \in [st: States, folded: BOOLEAN, conv: BOOLEAN, cnt: 0..2,
              captured: BOOLEAN, stopped: BOOLEAN]
    /\ pushApplied \in BOOLEAN /\ trailApplied \in BOOLEAN /\ answerApplied \in BOOLEAN

Init ==
    /\ diffPush \in BOOLEAN /\ diffFetch \in BOOLEAN
    /\ diffDrops \in BOOLEAN /\ diffTrail \in BOOLEAN
    /\ diffTrail => diffFetch
    \* Both prologues have run, so an offer and a context frame are already on each outbound queue (AES:318-326).
    /\ cIR = <<Fr("offer"), Fr("context")>>
    /\ cRI = <<Fr("offer"), Fr("context")>>
    /\ irClosed = FALSE /\ riClosed = FALSE
    /\ i = [st |-> "Pinning", pc |-> "idle", folded |-> FALSE, conv |-> FALSE,
            deferred |-> FALSE, sent |-> 0, captured |-> FALSE, stopped |-> FALSE]
    /\ r = [st |-> "Pinning", folded |-> FALSE, conv |-> FALSE, cnt |-> 0,
            captured |-> FALSE, stopped |-> FALSE]
    /\ pushApplied = FALSE /\ trailApplied = FALSE /\ answerApplied = FALSE

-----------------------------------------------------------------------------
(* Initiator frame receipt. The consumer is serial: a frame dispatches only
   between handlers (pc = idle), and a completed initiator returned early and
   never dispatches again (AES:333-336). *)

InitiatorReceiveEnabled == ~i.stopped /\ i.pc = "idle" /\ i.st # "Completed" /\ cRI # <<>>

InitiatorLegalFrame(f) ==
    \/ f.t = "offer" /\ i.st = "Pinning"
    \/ f.t = "context" /\ i.st \in {"Pinning", "Reconciling"} /\ ~i.captured
    \/ f.t = "elements" /\ i.st = "Resolving"

InitiatorReceiveOffer ==
    /\ InitiatorReceiveEnabled /\ Head(cRI).t = "offer" /\ i.st = "Pinning"
    /\ i' = [i EXCEPT !.st = "Reconciling"]
    /\ cRI' = Tail(cRI)
    /\ UNCHANGED <<diffs, cIR, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorReceiveContext ==
    /\ InitiatorReceiveEnabled /\ Head(cRI).t = "context"
    /\ i.st \in {"Pinning", "Reconciling"} /\ ~i.captured
    /\ i' = [i EXCEPT !.captured = TRUE]
    /\ cRI' = Tail(cRI)
    /\ UNCHANGED <<diffs, cIR, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorReceiveAnswer ==
    /\ InitiatorReceiveEnabled /\ Head(cRI).t = "elements" /\ i.st = "Resolving"
    /\ i' = [i EXCEPT !.pc = "answerApply"]
    /\ cRI' = Tail(cRI)
    /\ UNCHANGED <<diffs, cIR, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

\* A frame the guards reject ends the loop loudly with the control state frozen (fail closed).
InitiatorFailClosed ==
    /\ InitiatorReceiveEnabled /\ ~InitiatorLegalFrame(Head(cRI))
    /\ i' = [i EXCEPT !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

-----------------------------------------------------------------------------
(* The collapsed decode and the CompleteDecode micro-steps (AES:633-721).
   Cut (a): decode fires spontaneously at Reconciling once the context is
   captured; done is sent first, before every transfer the initiator ever
   sends. Each micro-step boundary is an await in the code. *)

InitiatorDecode ==
    /\ ~i.stopped /\ i.pc = "idle" /\ i.st = "Reconciling" /\ i.captured
    /\ cIR' = IF irClosed THEN cIR ELSE Append(cIR, Fr("done"))
    /\ i' = [i EXCEPT !.pc = "decodeDrops"]
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

(* The local-drop arm (AES:666-686): apply-and-fold now only when no fetch is
   outstanding; defer otherwise. The buggy variant folds eagerly regardless. *)
InitiatorDecodeDrops ==
    /\ ~i.stopped /\ i.pc = "decodeDrops"
    /\ LET foldNow == diffDrops /\ (~diffFetch \/ BuggyEagerDrops)
           defer == diffDrops /\ diffFetch /\ ~BuggyEagerDrops
       IN i' = [i EXCEPT !.pc = "decodeFetch",
                         !.folded = i.folded \/ foldNow,
                         !.deferred = i.deferred \/ defer]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorDecodeFetch ==
    /\ ~i.stopped /\ i.pc = "decodeFetch"
    /\ cIR' = IF diffFetch /\ ~irClosed THEN Append(cIR, Fr("fetch")) ELSE cIR
    /\ i' = [i EXCEPT !.pc = "decodePush"]
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

(* After the push, a fetchless exchange proceeds to the terminal micro-steps;
   a fetch-bearing one parks at Resolving awaiting the answer (AES:699-720). *)
InitiatorDecodePush ==
    /\ ~i.stopped /\ i.pc = "decodePush"
    /\ cIR' = IF diffPush /\ ~irClosed THEN Append(cIR, Fr("elements")) ELSE cIR
    /\ i' = [i EXCEPT !.sent = IF diffPush THEN @ + 1 ELSE @,
                      !.pc = IF diffFetch THEN "idle" ELSE "decodeMerge",
                      !.st = IF diffFetch THEN "Resolving" ELSE @]
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorDecodeMerge ==
    /\ ~i.stopped /\ i.pc = "decodeMerge"
    /\ i' = [i EXCEPT !.pc = "decodeCompletion", !.folded = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorDecodeCompletion ==
    /\ ~i.stopped /\ i.pc = "decodeCompletion"
    /\ cIR' = IF irClosed THEN cIR ELSE Append(cIR, Completion(i.sent))
    /\ i' = [i EXCEPT !.pc = "decodeFinish"]
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

\* A completed initiator's loop returns without ever draining (the early return at AES:333-336).
InitiatorDecodeFinish ==
    /\ ~i.stopped /\ i.pc = "decodeFinish"
    /\ i' = [i EXCEPT !.pc = "idle", !.st = "Completed", !.conv = TRUE, !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

(* The fetch-answer handler micro-steps (AES:781-854). The elements apply
   runs first and its fold rides the same hook call as the entries that fold
   covers; the deferred drops follow. A crash at any boundary in the handler
   therefore never leaves a folded context covering unapplied entries -
   MCSessionPairCrashTC certifies exactly that. *)

InitiatorAnswerApply ==
    /\ ~i.stopped /\ i.pc = "answerApply"
    /\ answerApplied' = TRUE
    /\ i' = [i EXCEPT !.pc = "answerDeferredDrops", !.folded = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied>>

InitiatorAnswerDeferredDrops ==
    /\ ~i.stopped /\ i.pc = "answerDeferredDrops"
    /\ i' = [i EXCEPT !.pc = "answerTrailingDrop", !.folded = i.folded \/ i.deferred, !.deferred = FALSE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorAnswerTrailingDrop ==
    /\ ~i.stopped /\ i.pc = "answerTrailingDrop"
    /\ cIR' = IF diffTrail /\ ~irClosed THEN Append(cIR, Fr("drop")) ELSE cIR
    /\ i' = [i EXCEPT !.pc = "answerMerge", !.sent = IF diffTrail THEN @ + 1 ELSE @]
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

(* A deliberately retained no-op step: on this path the elements apply has
   always folded already, so the code skips its guarded terminal merge and
   holds no await here. The extra step and crash boundary are a conservative
   over-approximation that cannot mask a violation. *)
InitiatorAnswerMerge ==
    /\ ~i.stopped /\ i.pc = "answerMerge"
    /\ i' = [i EXCEPT !.pc = "answerCompletion", !.folded = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorAnswerCompletion ==
    /\ ~i.stopped /\ i.pc = "answerCompletion"
    /\ cIR' = IF irClosed THEN cIR ELSE Append(cIR, Completion(i.sent))
    /\ i' = [i EXCEPT !.pc = "answerFinish"]
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

InitiatorAnswerFinish ==
    /\ ~i.stopped /\ i.pc = "answerFinish"
    /\ i' = [i EXCEPT !.pc = "idle", !.st = "Completed", !.conv = TRUE, !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

(* The drain epilogue (AES:339-346): the loop exits once the closed inbound is
   empty. The shipped epilogue folds nothing and maps a non-responder to
   Interrupted; the buggy variant folds a captured, unfolded context and
   reports Completed unconditionally. *)
InitiatorDrain ==
    /\ ~i.stopped /\ i.pc = "idle" /\ i.st # "Completed"
    /\ riClosed /\ cRI = <<>>
    /\ i' = IF BuggyDrainFold
            THEN [i EXCEPT !.st = "Completed", !.stopped = TRUE,
                           !.folded = i.folded \/ i.captured]
            ELSE [i EXCEPT !.st = "Interrupted", !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

-----------------------------------------------------------------------------
(* Responder handlers. Each is atomic: every fold rides inside the same hook
   call as its apply, so no intra-handler crash window exists on this role
   (map section 3.1); the completion fold and the Completed transition are
   collapsed likewise - a crash between them drains Resolving-to-Completed
   with the fold already licensed, which the drain preservation covers. *)

ResponderReceiveEnabled == ~r.stopped /\ cIR # <<>>

ResponderLegalFrame(f) ==
    \/ f.t = "offer" /\ r.st = "Pinning"
    \/ f.t = "context" /\ r.st \in {"Pinning", "Reconciling"} /\ ~r.captured
    \/ f.t = "done" /\ r.st = "Reconciling" /\ r.captured
    \/ f.t = "fetch" /\ r.st = "Resolving"
    \/ f.t = "elements" /\ r.st = "Resolving"
    \/ f.t = "drop" /\ r.st = "Resolving"
    \/ f.t = "completion" /\ r.st = "Resolving" /\ f.n = r.cnt

ResponderReceiveOffer ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "offer" /\ r.st = "Pinning"
    /\ r' = [r EXCEPT !.st = "Reconciling"]
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

ResponderReceiveContext ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "context"
    /\ r.st \in {"Pinning", "Reconciling"} /\ ~r.captured
    /\ r' = [r EXCEPT !.captured = TRUE]
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

\* Done attests the decode covered the whole difference; the responder converges here, pre-terminally (AES:747).
ResponderReceiveDone ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "done" /\ r.st = "Reconciling" /\ r.captured
    /\ r' = [r EXCEPT !.st = "Resolving", !.conv = TRUE]
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

ResponderReceiveFetch ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "fetch" /\ r.st = "Resolving"
    /\ cRI' = IF riClosed THEN cRI ELSE Append(cRI, Fr("elements"))
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, irClosed, riClosed, i, r, pushApplied, trailApplied, answerApplied>>

ResponderReceivePush ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "elements" /\ r.st = "Resolving"
    /\ pushApplied' = TRUE
    /\ r' = [r EXCEPT !.folded = TRUE, !.cnt = @ + 1]
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, i, trailApplied, answerApplied>>

ResponderReceiveDrop ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "drop" /\ r.st = "Resolving"
    /\ trailApplied' = TRUE
    /\ r' = [r EXCEPT !.folded = TRUE, !.cnt = @ + 1]
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, i, pushApplied, answerApplied>>

(* The licensed terminal fold (AES:897-944): add-only, role and phase guards
   are structural here; the count check is the explicit guard. It has no
   folded-already gate - the license is the count. *)
ResponderReceiveCompletion ==
    /\ ResponderReceiveEnabled /\ Head(cIR).t = "completion" /\ r.st = "Resolving"
    /\ Head(cIR).n = r.cnt
    /\ r' = [r EXCEPT !.folded = TRUE, !.st = "Completed"]
    /\ cIR' = Tail(cIR)
    /\ UNCHANGED <<diffs, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

\* Count drift, misordered frames and duplicate completions fail closed before any fold runs.
ResponderFailClosed ==
    /\ ResponderReceiveEnabled /\ ~ResponderLegalFrame(Head(cIR))
    /\ r' = [r EXCEPT !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

(* The responder's drain (AES:1040-1051): past done it completes, otherwise it
   is interrupted; a frame-earned Completed is preserved. The shipped epilogue
   folds nothing. *)
ResponderDrain ==
    /\ ~r.stopped /\ irClosed /\ cIR = <<>>
    /\ r' = IF BuggyDrainFold
            THEN [r EXCEPT !.st = "Completed", !.stopped = TRUE,
                           !.folded = r.folded \/ r.captured]
            ELSE [r EXCEPT !.st = IF r.st \in {"Resolving", "Completed"}
                                  THEN "Completed" ELSE "Interrupted",
                           !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

-----------------------------------------------------------------------------
(* Cut (c): the host actions. *)

WindDownInitiator ==
    /\ AllowWindDown /\ ~riClosed
    /\ riClosed' = TRUE
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, i, r, pushApplied, trailApplied, answerApplied>>

WindDownResponder ==
    /\ AllowWindDown /\ ~irClosed
    /\ irClosed' = TRUE
    /\ UNCHANGED <<diffs, cIR, cRI, riClosed, i, r, pushApplied, trailApplied, answerApplied>>

CrashInitiator ==
    /\ AllowCrash /\ ~i.stopped
    /\ i' = [i EXCEPT !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, r, pushApplied, trailApplied, answerApplied>>

CrashResponder ==
    /\ AllowCrash /\ ~r.stopped
    /\ r' = [r EXCEPT !.stopped = TRUE]
    /\ UNCHANGED <<diffs, cIR, cRI, irClosed, riClosed, i, pushApplied, trailApplied, answerApplied>>

-----------------------------------------------------------------------------

Next ==
    \/ InitiatorReceiveOffer \/ InitiatorReceiveContext \/ InitiatorReceiveAnswer \/ InitiatorFailClosed
    \/ InitiatorDecode \/ InitiatorDecodeDrops \/ InitiatorDecodeFetch \/ InitiatorDecodePush \/ InitiatorDecodeMerge \/ InitiatorDecodeCompletion \/ InitiatorDecodeFinish
    \/ InitiatorAnswerDeferredDrops \/ InitiatorAnswerApply \/ InitiatorAnswerTrailingDrop \/ InitiatorAnswerMerge \/ InitiatorAnswerCompletion \/ InitiatorAnswerFinish
    \/ InitiatorDrain
    \/ ResponderReceiveOffer \/ ResponderReceiveContext \/ ResponderReceiveDone \/ ResponderReceiveFetch \/ ResponderReceivePush
    \/ ResponderReceiveDrop \/ ResponderReceiveCompletion \/ ResponderFailClosed \/ ResponderDrain
    \/ WindDownInitiator \/ WindDownResponder \/ CrashInitiator \/ CrashResponder

Spec == Init /\ [][Next]_vars

FairSpec == Spec /\ WF_vars(Next)

-----------------------------------------------------------------------------
(* The pinned contracts (AntiEntropySessionState.cs:23-41, map section 4.1). *)

InterruptedZeroFolds ==
    /\ i.st = "Interrupted" => ~i.folded
    /\ r.st = "Interrupted" => ~r.folded

TerminalConverged ==
    /\ i.st = "Completed" => i.conv
    /\ r.st = "Completed" => r.conv
    /\ i.st = "Interrupted" => ~i.conv
    /\ r.st = "Interrupted" => ~r.conv

(* The TC theorem, cut (b): a fold that covers an insert-bearing transfer is
   sound only if that transfer is eventually applied by the folding side.
   Checked under fairness so a mid-handler state is not a spurious stutter
   witness; a genuinely dead session that folded early violates it. *)
FoldImpliesDelivery ==
    /\ []((r.folded /\ diffPush) => <>pushApplied)
    /\ []((i.folded /\ diffFetch) => <>answerApplied)

(* Crash-free, wind-down-free liveness: one session converges both members -
   both roles reach Completed with both contexts folded. *)
Convergence ==
    <>(/\ i.st = "Completed" /\ r.st = "Completed"
       /\ i.folded /\ r.folded /\ i.conv /\ r.conv)

=============================================================================
