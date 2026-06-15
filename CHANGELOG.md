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

## [0.0.3] - 2026-06-16

### Changed

- Deserialization fails closed under one type across encodings: every `DeserializeMessageDelegate` the
  JSON and CBOR codecs build now throws a single new `MessageDeserializationException` (in
  `Lumoin.Verisync.Core`, documented on the delegate), with the encoding-native cause — a `JsonException`,
  a `CborContentException`, a wrapped argument exception — preserved as its inner exception, so a channel
  consumer catches one type whether the wire is JSON or CBOR instead of an encoding-specific exception. The
  conversion is a per-assembly guard wrapping every deserializer factory; serializers are unchanged and
  still throw their native exceptions, and a factory's argument-null guard stays at construction time. This
  supersedes the surfaced exception type in the `JsonException` entries below — those now describe the inner
  cause a handler can still read off `InnerException`.

- The reconciliation decoder peels in near-linear time. The previous decode re-scanned the whole
  decoded set on every absorbed symbol and re-scanned all cells to a fixpoint on every absorb —
  Θ(d²) in the difference size, so throughput collapsed as differences grew. It now applies
  already-decoded items to incoming cells through an incremental cursor heap (mirroring the encoder)
  and finds newly pure cells through a work-list seeded by each modified cell. The decoded set, the
  completion point, the soundness rules (peel only pure cells, stall on an already-decoded sum, never
  un-decode), and the wire format are unchanged — verified against the full law and vector suites and
  an independent adversarial model. Measured on the throughput soak: a 16,384-item difference that
  took ~54 s now reconciles in ~0.2 s, and per-symbol throughput rises with the difference instead of
  falling.

- The encoder and decoder store coded symbols in a single flat, contiguous `ReconciliationCellBuffer`
  rather than parallel lists of per-cell arrays, so the XOR fold walks contiguous memory and the
  per-cell allocation is gone; the two duplicated content-keyed-bytes helpers are unified into one
  `ContentAddress`. `ReconciliationSymbol` gains an additive `ReadOnlySpan<byte>` constructor so a
  symbol can be snapshotted from a cell in a single copy. Behavior, wire format, and the pinned
  vectors are unchanged (the full suite is the oracle); the gain is locality and modest decode
  throughput. Steady-state per-session allocation is unchanged — it is dominated by per-item
  encoding work, not cell storage — so a pooled cell buffer was measured and deliberately not taken.

- The encoder and decoder reuse their walk cursors instead of reallocating one per fold. The cursors
  that carry pending item contributions through the produce/peel priority queues were reference-type
  records cloned with a `with` expression on every re-enqueue — an object per fold, which the
  allocation soak identified as the dominant per-session allocation. They are now mutable structs
  stored inline in the queue and advanced in place, and the encoder cursor carries its checksum as a
  value re-materialized at fold time rather than a copied array. Behavior is byte-identical (the full
  suite plus an independent adversarial verification confirm the produced symbols, decoded set, and
  completion point are unchanged); the throughput soak shows steady-state per-session allocation down
  about 47% (≈287 KB → ≈151 KB) with the live heap still flat.

- The reconciliation encoder, decoder, and `AntiEntropySession` are now `IDisposable` and rent their
  coded-cell backing from a caller-supplied `MemoryPool<byte>` — a tracked exact-size pool in
  production — so the kernel's largest scoped buffer is accountable through the pool's rental metrics
  rather than living in a naked array. New pool-bearing constructor overloads
  thread the pool and a capacity hint (the session pre-sizes from its projected snapshot); a `null`
  pool keeps the standalone, untracked heap-backed path, so direct construction is non-breaking. The
  session disposes the encoder and decoder it owns but never the injected pool. Pooled segments are
  cleared on rent — a pool does not zero recycled memory — preserving the all-zero-fresh-cell contract
  the fold relies on, and the paired rent is exception-safe so a failed second rent returns the first
  rather than leaking it. A `ReconciliationSymbol` stays an owned-copy value: the documented exception
  for bytes that escape the kernel to callers and the wire. A new accountability test asserts the
  rental ledger balances to zero after a full session (no leaked rentals), and a soak over two thousand
  pooled sessions confirms it at scale.

### Security

- The reconciliation envelope JSON deserializer now fails closed as `JsonException` on every
  present-but-malformed field, closing a contract-stability gap found by an adversarial audit of the
  tier: a JSON-null hex field (which had leaked `ArgumentNullException` from the hex decode), a
  wrong-kind value where a string or hex field was expected (`InvalidOperationException`), a
  fractional or `Int32`-overflowing integer (`FormatException`), and a non-object/non-array carrier
  where the shape required one. Every `JsonElement` accessor is now kind-guarded before use, so a
  hostile or buggy peer can no longer drive the verifying deserializer to throw an exception type the
  contract promises it never will — a handler catching `JsonException` to treat a frame as a protocol
  fault sees the fault it expects. A structurally missing required property fails closed as well: every
  per-field read across the JSON codecs (reconciliation, CRDT state, Raft, log commitment, consensus) now
  goes through a `RequireProperty` guard that throws rather than leaking the framework's
  `KeyNotFoundException`.

### Added

- Remove-aware (dot-cloud) reconciliation atop the anti-entropy session: an `OrSet` /
  `DottedVersionVectorSet` reconciles its observed removes as well as its adds, with reconcile-then-apply
  proven equal to `DottedVersionVectorSet.Merge` while bytes on the wire stay proportional to the
  divergence. The kernel, encoder, decoder, and symbol stay an unchanged generic digest-set engine;
  remove-awareness is host-side. The session takes an optional pinned local `VectorClockState`; the
  initiator projects present `(dot, value)` entries to digests through a new
  `DottedReconciliationProjection`, decodes the symmetric difference, and classifies each decoded dot
  against the peer's exchanged causal context by the merge rule — a held dot the peer's context covers is
  a local drop, an absent dot the initiator's own pre-session context covers is pushed as a drop rather
  than re-added (the resurrection guard). The genuinely new wire surface is a whole-context exchange
  (`ReconciliationContext`, shipped whole and never reconciled) and a remove push (`ReconciliationDrop`,
  dots only); a `null` local context keeps the add-only path byte-identical. Proven against the merge
  oracle across add-only, remove-only, and mixed divergence including the resurrection case, and end to
  end over a real localhost socket; the log-plane anti-equivocation seal chain is now proven over a socket
  as well.

- SIMD acceleration for the reconciliation kernel's hot loops, phase 4 of the anti-entropy tier:
  the byte-wise XOR folds and neutrality scans behind encoding, peeling, and symbol combination
  now route through `ReconciliationXor`, a facade over per-width vector backends
  (`Vector128`/`Vector256`/`Vector512` with a scalar reference) selected by a dispatch that the
  JIT folds to a direct call. The width tiers are cross-platform — they lower to SSE/AVX2/AVX-512,
  NEON, and WASM SIMD alike — and the wire contract is provably unchanged: every backend is pinned
  byte-identical to the scalar reference across edge lengths, and a stream-level agreement test
  re-derives the encoder's emitted symbols from an independent scalar fold. The benchmarks project
  gains XOR and encoder throughput benchmarks plus `--reconciliation-overhead`, a seed-pinned
  measurement of bytes-on-wire against the information-theoretic floor and full-state/hash-list
  anchor rows (symbols per difference converge to ~1.37x at a thousand-item divergence,
  three orders of magnitude under either anchor at small differences).

- `AntiEntropySession`, phase 3 of the anti-entropy tier: the host-side runner for one
  point-to-point reconciliation session, in the `RaftRunner` production shape — all inbound work
  flows through a single-consumer queue, so every state change and every outbound send happens on
  one loop and transport writes are serialized by construction. A session pins one set version (the
  item snapshot is copied and encoded at construction); the initiator decodes against its own
  lockstep encoder, signals done, and classifies the difference through
  `ResolveReconciliationDifferenceDelegate` into fetches and pushes; the responder streams batches
  only on host `TriggerBatchAsync` calls (liveness stays external — no timers, no entropy), serves
  fetches through `ServeReconciliationFetchDelegate` with exact-coverage verification, and applies
  elements through `ApplyReconciliationElementsDelegate`. Every protocol violation — mismatched
  offer, out-of-role frame, stream gap, partial fetch answer — fails the session closed.
  `ReconciliationEnvelope` gains the same exactly-one-payload dispatch guard as the Raft envelope.
  Proven in memory and over a real localhost socket: convergence of diverged observed-remove sets,
  then quiescence on the first symbol of a second session. Element-level reconciliation is add-only at
  this base tier; remove-aware reconciliation over dot-cloud causal contexts is added separately in this
  release — see the dot-cloud reconciliation entry above.

- The reconciliation wire layer, phase 2 of the anti-entropy tier: a `RaftEnvelope`-style
  one-of-five message family (`ReconciliationEnvelope` carrying offer, symbol batch, done, fetch,
  or elements) and `ReconciliationJson` codecs following the established fail-closed conventions.
  The deserializer is verifying: it pins the local `ReconciliationContract` and rejects an offer
  that does not match it — so a contract mismatch throws before any symbol is absorbed — and
  validates every hex field's width against that contract. The offer never carries key bytes;
  it carries a key check (a PRF tag over a fixed public input) so peers with differing checksum
  keys abort up front instead of failing to peel. Proven end to end over a real localhost socket,
  plain and padded framing: two observed-remove sets pin contracts both ways, stream symbol
  batches, decode the difference, exchange the missing elements by digest, converge, and a second
  session completes on its first symbol with nothing decoded.

- The rateless set-reconciliation kernel, phase 1 of the anti-entropy tier: a replica encodes a set
  of fixed-width items into an unbounded coded-symbol stream (`ReconciliationEncoder`) whose
  symbol-wise XOR with a peer's stream is the stream of their symmetric difference, recovered by a
  peeling decoder (`ReconciliationDecoder`) from a prefix proportional to the difference size —
  neither side ever sizes the divergence, and an equal-set reconciliation completes on the first
  symbol. The encoding is a group homomorphism from (sets, symmetric difference) to (streams, XOR),
  property-tested as such alongside history erasure, decode exactness, quiescence, monotone
  knowledge, and bit-flip soundness; the index walk and SipHash-2-4 checksum primitives are pinned
  by byte-precise test vectors. `ReconciliationContract` pins what peers must agree on before
  subtraction is meaningful (item domain, item width, checksum width and key — checksum width
  bounds the masquerade probability, and a secret key turns a poisoned stream into
  detected-and-aborted); injectivity enforcement is local-only
  (`ReconciliationInjectivityEnforcement`), and `ProjectReconciliationItemsDelegate` is the seam
  that projects a pinned state snapshot to reconcilable items. Wire codecs, the session runner, and
  SIMD XOR backends are later phases.

## [0.0.2] - 2026-06-11

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
- The Raft production story around the safety core: `RaftNode.ToState`/`FromState` with
  `RaftNodeState` (the Figure 2 durable triple, validated fail-closed on restore; a restored node is
  a follower at commit zero rediscovering the volatile commit index), `RaftRunner` — a
  single-consumer message-driven loop preserving the node's single-threaded contract with the
  universal handle → persist → apply → send sequencing, named seams
  (`SendRaftEnvelopeDelegate`, `PersistRaftStateDelegate`, `ApplyCommittedDelegate`), host-triggered
  elections and heartbeats (still no timers or entropy in the library), propose with a faulting task
  on non-leaders, and self-quiescing follower catch-up off append replies — plus `RaftJson` wire
  codecs for the envelope and the durable state, mirroring the consensus message codecs' strictness.
  Apply is exactly-once per process lifetime and at-least-once across restarts, documented on the
  seam. Proven over real localhost sockets: election, replication, identical applied sequences on
  every node, and a follower restarted from its persisted state converging after reconnect.
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
