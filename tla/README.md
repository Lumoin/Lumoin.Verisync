# TLA+ workspace: the session pair and the seal protocol

Model-checked specifications for the two protocol tiers this repository ships: the anti-entropy
session pair with the completion frame, and the consensus-anchored checkpoint seal. The specs
model the code as it is; the abstraction decisions are documented in each module's header comment.
The Fast CASPaxos register is out of scope here and is collapsed to a linearizable cell with the
seal's monotone dominate-or-refuse change function.

## Negative models

A negative model is a configuration that enables a forbidden behavior (or removes a shipped
guard) and whose TLC run MUST report a violation. A green negative run means the model is too
abstract to trust and its positive runs prove nothing. The runner (`Run-SessionAndSeal.ps1`)
enforces the expected outcome of every configuration and fails on any deviation.

## Toolchain

`C:\tools\tlaplus` with a portable Temurin 21 JRE, current `tla2tools.jar`, and `sany.cmd` /
`tlc.cmd` wrappers (heap-bounded; override with `TLA_JAVA_HEAP`). Run from this directory:

    C:\tools\tlaplus\sany.cmd .\SessionPair.tla
    C:\tools\tlaplus\tlc.cmd -workers 4 -checkpoint 0 -config .\MCSessionPairSafety.cfg .\SessionPair.tla
    .\Run-SessionAndSeal.ps1

## The matrix

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

## Scope notes (what a green run does not certify)

- The fail-closed rejection layer (misordered frames, wrong-role frames, the completion
  transfer-count check) is structurally unreachable here: the honest FIFO exactly-once transport
  the protocol contracts for never produces the inputs those guards reject. Rejection behavior is
  owned by the unit suite.
- The liveness property is the recurrence form (always-eventually): a once-ever form would stay
  satisfied by any single early convergence and go blind to a converge-then-wedge regression.
- The session model treats sends into a closed channel as silent loss (the socket transport's
  view); the in-process transport's sender-side throw is covered by the separately-enabled crash
  action.
