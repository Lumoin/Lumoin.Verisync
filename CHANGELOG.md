# Change Log

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).

<!-- Available types of changes:
### Added
### Changed
### Fixed
### Deprecated
### Removed
### Security
-->

## [Unreleased]

### Changed

- `CheckpointedSequence.Promote` proposes a `CheckpointCommitment` — the digest of the snapshot's
  canonical bytes — through the CASPaxos register instead of the snapshot itself. Consensus payloads
  stay metadata-sized regardless of sequence length; the content stays local and travels the CRDT
  plane, verifiable against the agreed commitment. `Create` takes the canonicalize and digest
  delegates; the register type is `CasPaxosRegister<CheckpointCommitment>`.

- `ConsensusNode.RunAsync` takes the reply sink as the named `SendReplyDelegate<TValue>` instead of
  a naked `Func`, matching the named-seam convention. Source-compatible: lambdas and method groups
  at existing call sites convert unchanged.

### Fixed

- Fast CASPaxos acceptor safety: accepting now raises the promise to the accepted ballot, so a stale
  lower-ballot accept can no longer regress acceptor state past a possibly-chosen value; fast ballots
  are accepted only while they equal the acceptor's promise and prepares are classic-only, so only the
  pre-promised initial fast round is blind-writable. Retry a contended fast write with the same fast
  ballot and value, or complete it through classic recovery.
- `Rga` insert identities are assigned Lamport-style (`VectorClock.IncrementPastAll`), restoring
  intention preservation: an insert-after now lands immediately after its predecessor instead of
  potentially behind older sibling subtrees observed from other replicas. Sibling orders involving
  pre-existing concurrent inserts may differ from earlier versions.

### Security

- Counter and clock arithmetic is checked: `GCounter.Increment`, `VectorClock.Increment`, and
  `VectorClock.IncrementPastAll` throw `OverflowException` instead of wrapping (a wrapped counter is
  silently rejected by max-merge forever); classic `Ballot` validates its round at construction.
  `FromState` rejects what no honest history produces: negative counts (counters, clocks), zero-count
  entries are filtered (restoring `Equals`/`GetHashCode` consistency), dotted-version-vector-set dots
  must sit within their causal context, and RGA states with missing predecessors or predecessor
  cycles are rejected rather than silently dropping vertices from traversal. Tombstones for absent
  dots remain accepted and documented.
- The JSON channel deserializer is strict: a payload that is the JSON literal `null` and any payload
  with trailing data after the message are rejected with `JsonException` — many-bytes-to-one-message
  laxity breaks canonical-bytes assumptions near digest-relevant content.
- `TaggedMemory` equality members fail closed after dispose; the memory pool validates slab sizing
  before allocation (no more wrapped products), disposes its rental activity on a throwing rent,
  makes owner double-dispose idempotent, and keeps the active-rentals gauge paired across pool
  shutdown races.
- `MessageChannelReader` rejects frames whose declared payload exceeds a configurable maximum
  (16 MiB default) instead of buffering toward a hostile four-byte length prefix;
  `MessageChannelWriter` fails fast on oversized payloads. The reader completes its pipe even when
  deserialization throws.
- The JSON CRDT-state deserializers validate untrusted input: replica ids must decode to exactly
  `ReplicaId.Size` bytes, replica counters cannot be negative, and dot counters start at one.

### Added

- `OffsetAnchoredSequence<TValue>` and `OffsetAnchor`, registered as `verisync.sequence.offset.v1`:
  the checkpoint-offset sequence strategy — collaborative edits over an immutable, consensus-agreed
  base snapshot, with anchors as stable base offsets or live dot identities and the same Lamport
  intention-preservation rule as the RGA strategy. Merging is generation-aligned and fails closed
  across differing bases. Passes the full shared law harness.
- Waterline compaction for the checkpoint-offset strategy: `OffsetAnchoredSequence<TValue>.Compact`
  collapses stable visible vertices into the next base generation against an agreed
  (frontier, checkpoint) pair and fails closed on a misaligned pair. Stable tombstones with surviving
  descendants persist as position ghosts and removed base entries are always carried, so visible
  order survives compaction; every survivor outside a surviving subtree re-anchors at its gap anchor
  (the nearest preceding new-base entry), which the Lamport counter invariant keeps order-preserving
  and replica-independent. `TranslateAnchor` serves previous-generation anchors from dot- and
  offset-translation maps that compose across generations, so dot anchors stay translatable
  indefinitely while base anchors serve exactly the one-generation window the stability rule permits.
  The shared law harness gains the two remaining compaction laws — compact/merge commutation at or
  above the frontier and anchor servability across compaction — and the
  `TranslateAnchorDelegate` now receives the sequence, since translation maps are strategy state.
- `verisync.sequence.rga-rle.v1` (`WellKnownSequenceStrategies.CreateRgaRle`): the compactable RGA
  strategy — identical RGA semantics and identifiers, plus ghost-based waterline compaction with no
  re-anchoring of any kind. A stable tombstone persists as a ghost while any descendant survives;
  only recursively childless stable tombstones drop (head-anchored ones never do, since no element
  exists to translate to), so the visible order provably never changes. `Rga<TValue>.Compact`
  verifies the agreed checkpoint against the stable visible content fail-closed, and
  `TranslateAnchor` serves dropped dots from a translation map that composes across generations.
  `ToRunState`/`FromRunState` add the run-length serialized state — maximal same-replica
  predecessor-chained vertex runs, per-replica tombstone spans, and translation entries, validated
  fail-closed on reconstruction — while `ToState` now refuses to serialize an instance carrying
  translations rather than silently dropping them. Both compaction strategies pass the same four
  shared compaction laws.
- Serialization for both compaction strategies: `OffsetAnchoredSequence<TValue>.ToState`/`FromState`
  with the `OffsetAnchoredSequenceState` record family (vertices with anchors, removed offsets, and
  both translation maps; deterministic replica-major output; reconstruction validates anchors,
  ranges, duplicates, and anchor-graph acyclicity fail-closed), and `CrdtStateJson` codecs for
  `OffsetAnchoredSequenceState` and `RgaRunState` — hand-written and AOT-safe like the existing
  state codecs, with codec-level shape validation (`JsonException`) layered under the state-level
  relational validation (`ArgumentException`).
- Fast CASPaxos next-ballot piggybacking: a successful accept may carry the next fast ballot, raising
  the acceptor's promise to it and establishing the next fast round coordinator-free — recurring
  one-round-trip fast commits, as in the original design. All reject rules ignore the piggyback, the
  promise only ever rises, and an acceptor that never saw the piggyback keeps rejecting that fast
  round via the equality rule, so blind writes at un-established rounds remain impossible. The wire
  encoding's `next` field is optional and back-compatible.
- A naive, safety-correct, in-memory Raft node (`RaftNode<TCommand>` and its message records): the
  complete Figure 2 safety core — election restriction, log matching with conflict truncation and
  idempotent re-delivery, and the current-term commit rule — with liveness explicitly external
  (host-triggered elections, no timers). The within-trust-domain log-replication primitive
  complementing the register (metadata-grade anchors) and the CRDT plane (no ordering). The test
  suite was authored blind against the same specification and includes the Figure 8 scenario.
- `StabilityFrontier`, `CompactSequenceDelegate`, `TranslateAnchorDelegate`, and a `Compact` step on
  `CheckpointedSequence`: the waterline seams — the frontier (element-wise group minimum, in-library
  because a silent member must pin the floor) is the WHEN of compaction, the agreed checkpoint the
  WHAT; the first two compaction laws wait in the shared harness for the first compacting strategy.
- A shared sequence-strategy law harness in the test suite: every registered strategy inherits the
  join-semilattice laws, merge-order convergence, and local intention preservation as property tests
  exercised through the strategy context — the contract future strategies (including the compaction
  strategies) are built against. The RGA strategy is the first registration.
- `PersistAcceptorDelegate<TValue>` and an optional durability hook on `ConsensusNode.RunAsync`:
  when supplied, the acceptor state is made durable after every state-changing request and before the
  matching reply is sent, so an unpersisted promise is never observable — the documented crash-safety
  contract is now a seam instead of a caller obligation. A throwing hook fail-closes the reply.
- `SequenceCrdtContext<TSequence, TValue, TAnchor>` with named operation delegates and
  `WellKnownSequenceStrategies`: the sequence CRDT behind `CheckpointedSequence` is now pluggable.
  A strategy (addressing model, merge, ordering) is injected as a delegate bundle and named by a
  stable identifier that is part of the document's replication contract — merging containers carrying
  different strategy identifiers fails closed, since mismatched strategies silently diverge.
  `CheckpointedSequence<TValue>` became `CheckpointedSequence<TSequence, TValue, TAnchor>` created
  via `Create(context)`; the RGA-backed strategy (`verisync.sequence.rga.v1`) is the first
  registration and preserves the previous behavior exactly.
- `FramePadding`: opt-in padded framing for the message channel. Frames are padded up to size
  buckets (powers-of-two or fixed ladders) with an inner real-length prefix, so a network observer
  cannot distinguish message types or content sizes within a bucket — a metadata-privacy measure
  from the sealed-segments design. The unpadded wire format is unchanged byte for byte; both
  endpoints must share the policy, and the attacker-influenced inner length is never trusted past
  the frame bounds.
- `LogCommitmentJson`: hand-written AOT-safe JSON codecs for `LogHead`, `MerkleInclusionProof`,
  `MerkleConsistencyProof`, and `SegmentSeal<TProof>`, so heads, proofs, and seals can cross a
  message channel. The seal deserializer re-derives the canonical bytes and digest through
  `SegmentSeal.Create` and fails closed on a digest mismatch, so a tampered seal is rejected at the
  codec.
- `MerkleFrontier`: the bounded-state companion to `MerkleLogTree` — an append-only root tracker
  holding only the O(log n) frontier peaks, producing roots byte-identical to the full tree (verified
  by an exhaustive size sweep against it). Proofs still come from `MerkleLogTree` over archived
  leaves; the frontier is how a live replica tracks its root without holding history.
- `LogHead` and `LogHeadConsistency`: the log-plane anti-equivocation exchange. A head claims a tree
  size and root; `LogHeadConsistency.Verify` binds a `MerkleConsistencyProof` to exactly the two
  heads before trusting it and reduces a failure between authentic heads to portable fork evidence.
  In the single-tree composition a `SegmentSeal` whose commitment is the tree root at its boundary is
  an attested head, so consecutive seals prove their own consistency.
- `SegmentSeal<TProof>` with `AttestSealDelegate` and `VerifySealAttestationDelegate`: sealed log
  segments — a versioned, byte-for-byte pinned commitment to a contiguous entry range, chained by seal
  digest with in-library link verification (digest linkage, index continuity, genesis rules).
  Attestation evidence covers the seal digest and rides outside the digested bytes. Combined with an
  `AuthenticatedRegister` whose accumulator is a `MerkleLogTree` folding entry digests, sealing a
  segment is reading the accumulator root, and per-entry membership in a sealed segment is provable
  with a `MerkleInclusionProof` alone.
- `MerkleLogTree`, `MerkleInclusionProof`, and `MerkleConsistencyProof`: an RFC 9162 (Certificate
  Transparency v2) append-only Merkle log tree with inclusion and consistency proofs, hash-agnostic
  through `ComputeDigestDelegate` with structural `0x00`/`0x01` domain separation. The documented
  byte layout is a cross-stack contract; the test suite pins the canonical RFC 6962 SHA-256 vectors.
  This is the foundation for sealed log segments and bounded logs.
- `VectorClock.IncrementPastAll`: a Lamport-style advance whose new counter exceeds every observed
  counter.
- Documented contracts: `ConsensusNode` durability obligations across restarts, the CASPaxos
  "a failed change may still be chosen" retry caveat on `ChangeOutcome`, the full verification
  obligations of `VerifyChainIntegrityDelegate`, and the non-associativity of the
  `DottedVersionVector` dot component.

## [0.1.0] - 2026-06-09

### Added

- Initial public packages: `Lumoin.Verisync.Core`, `Lumoin.Verisync.Json`, `Lumoin.Verisync.Cbor`.
- CRDT primitives (`GCounter`, `PNCounter`, `LwwRegister`, `MvRegister`, `OrSet`, `Rga`) with
  property-tested join-semilattice merges, causal contexts (`VectorClock`, `Dot`,
  `DottedVersionVector`, `DottedVersionVectorSet`), and gossip digests.
- Classic and Fast CASPaxos consensus (leaderless linearizable registers) with a
  transport-agnostic, message-driven protocol layer over `System.IO.Pipelines`.
- Authenticated register and log replay (layer-2) with application-injected
  classification, proof validation, and fold delegates.
- Serializable CRDT state codecs (`ToState`/`FromState`) for host-persisted synchronization.
- Exact-size slab memory pool (`VerisyncMemoryPool`) wired to OpenTelemetry metrics and traces.
