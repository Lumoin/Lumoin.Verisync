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

## [0.0.13] - 2026-09-02

### Changed

- The packages now depend on `Lumoin.Base` 0.0.12.

### Fixed

- Every member raising `StateRestoreException` or `ConsensusRefusedException` documents that type and the refusal member a caller switches on, in place of the base type it named before, so the typed refusal surface is readable from the shipped API documentation. A test fails when a file raises a library exception no tag in it names, which the compiler cannot check because it proves only that a cref resolves.

## [0.0.12] - 2026-09-02

### Changed

- The packages now depend on `Lumoin.Base` 0.0.11.

## [0.0.11] - 2026-09-02

### Changed

- Dependency refresh; no library code or wire-format changes. The packages now depend on `Lumoin.Base` 0.0.10 (from 0.0.9), which adds the process-wide shared UTF-8 interner. `SIL.ReleaseTasks` moves to 3.2.1, which no longer depends on `System.Security.Cryptography.Xml` at all, so the transitive override pin for its formerly vulnerable reference is removed rather than bumped. The CI `step-security/harden-runner` pin moves to v2.21.1. Everything else was already current: .NET SDK 10.0.400, MSTest.Sdk 4.3.3, the analyzer and testing packages, and the remaining action pins.

## [0.0.10] - 2026-08-24

### Added

- `StoreIncarnation` and `HostId`. A replica id names a role and does not name the store answering for it, so a store wiped and restarted under one identity, or one identity provisioned onto two hosts, both answer as that member while holding divergent state, and a quorum counted over distinct replicas counts them once. An incarnation is minted with a store and carried for as long as that store's contents survive, and a host is the pair. A configuration lists at most one host per replica, `IncarnationOf` reports the store admitted for one, and `With` refuses an addition naming a listed replica under another incarnation, because replacing a member's store is a retirement and an admission rather than an addition.

- `ConsensusRefusal.StoreNotAdmittedForMember`, which a host raises when its replica is a member and the store it holds is not the admitted one, and `ConsensusRefusal.ProbeAnsweredByAnotherStore`, which a readiness report raises for an answer carrying the right replica from another store. Both stand beside the existing refusals for a replica no membership lists and a probe answered by another member, because a mis-wired endpoint map and a replaced store are different situations and only one of them is a wiring error.

### Changed

- **Breaking.** `VersionedRecordReply<TValue>.Recorder` and `MemberVersionReport.Recorder` are a `HostId` rather than a `ReplicaId`, and the register compares an answer against the whole admitted host. An answer carrying the admitted replica under another incarnation came from a store the membership never admitted, and counting it would put two stores that have agreed on nothing into one slot of a quorum.

- **Breaking.** `QuePaxaVersionedNode<TValue>.Self` is a `HostId`, and the constructor takes one. The membership filter then refuses a host whose store the active membership does not admit, which is the same rule as the one that refuses a host no membership lists, one dimension over.

- **Breaking.** `QuePaxaVersionedNodeState<TValue>` carries the host that wrote it, and `QuePaxaVersionedNode<TValue>.FromState` takes a `HostId` and refuses a snapshot written by another host with `StateRestoreRefusal.HostIdentityMismatch`. That refuses a store attached to the wrong machine, a store restored under a replica it never served, and a deployment restating its store's incarnation as the value the membership admits rather than the one its store holds — which is the claim the membership filter would otherwise be testing instead of a fact. A store that came back empty reaches the constructor and not the restore, because there is no snapshot to restore from, so the constructor is the one path on which a deployment's word about its own store is taken.

- **Breaking.** `StateRestoreRefusal` gains `HostIdentityMismatch`, and the acceptor and Raft members that followed it move up by one so the host family stays contiguous.

- **Breaking.** The chain identity's digest covers each genesis member's store incarnation as well as its replica, so a genesis over the same replicas under different stores is a different chain.

- **Breaking.** A host is encoded as an object carrying `replica` and `incarnation` wherever one appears: a configuration member, a reply's recorder, and a durable node state's `host`.

- Provisioning a host is two-phase, and `docs/quepaxa.md` states it: create the store, read the incarnation it minted, form the genesis list from the pairs, then start the hosts under that list.

### Fixed

- A JSON payload whose member is written as a bare identity, or any required field read off an element that is not an object, is refused as malformed input rather than surfacing the accessor's wrong-kind exception.

## [0.0.9] - 2026-08-15

### Added

- `StateRestoreException` and `StateRestoreRefusal`. Every durable-state restore now names the rule it refused on, across the QuePaxa recorder, the versioned host, the Fast CASPaxos acceptor and the Raft node, so a caller switches on the refusal instead of matching the sentence beside it. It derives from `ArgumentException` and keeps `ParamName`, which names which argument was refused and is a separate question from which rule refused it: the chain check reports `HostForeignChain` whether it is raised against a record handed to a constructor or a snapshot handed to a restore.

- `ConsensusRefusedException` and `ConsensusRefusal`, the same for the refusals a running register and host raise: a concurrent write, a spent version range, a reconfiguration with nothing committed, a readiness report without a member query, a probe answered by another member, and a misrouted decision. The two families stay apart because they answer different questions — durable state a host was handed, against an operation asked for on state that is already sound. `VersionRangeSpent` is reported by both sites that raise it, which carried the same sentence and so could not be told apart at all.

- Metrics and spans for the consensus surface, on the meter and activity source the library already publishes: membership size and quorum, per-member probe outcomes, the write status distribution and the fast-path rate.

- A stated guarantee for deployments that pace, admit, deny or drop Verisync traffic: delay, denial and loss cost availability and never agreement, misdelivery of one call's reply to another is the boundary that guarantee does not cover, dissemination is the path to protect rather than shed, an interfered-with readiness probe makes a gate refuse rather than clear, and the rateless tier is loss- and order-tolerant with no operation-level size to declare.

### Changed

- **Breaking.** `ReadReadinessAsync` and `ReadAsync` take a per-member deadline. A member that answers nothing at all is reported unreachable, which is the entry a member that faults already produces, and the members after it are still asked. The probe is raced against the deadline rather than only told about it, because no delegate contract obliges a query to honour its token; a query that ignores it is bounded all the same and is then abandoned still running. The deadline is a required argument rather than construction, since an operator sweep and an automated gate want different patience. `Timeout.InfiniteTimeSpan` asks for the previous behaviour; zero and negative spans are refused, because a report in which nothing answered is what a silent cluster reports.

- **Breaking.** `QuePaxaWriteOutcome<TValue>` carries `Record`, the record the version was decided at, and its `Value` and `Writer` are read off that record rather than passed beside it. A caller learns the membership a reconfiguration installed from `Record.NextConfiguration` instead of re-reading `ActiveConfiguration`, which any learn arriving meanwhile has already moved.

- **Breaking.** Restore refusals throw `StateRestoreException` where they threw `ArgumentException`, and the register's and host's running refusals throw `ConsensusRefusedException` where they threw `InvalidOperationException`. Catching the base types still catches both.

### Fixed

- A recorder whose replies carry another host's identity is refused on a retransmission as well as on a first send. Without that, a register counts replies gathered on retransmissions and can commit on fewer distinct replicas than the quorum names.

## [0.0.8] - 2026-08-15

### Added

- QuePaxa consensus, as a transport-free safety core and a message-driven layer over it: the interval summary register, the recorder that enforces the round-one leader's reserved priority, the round rules, the proposer that acts on the first quorum to answer, and JSON codecs for the message family.

- `QuePaxaVersionedRegister<TValue>`, a consensus register where every write is a fresh instance at the next version. The instance's leader is derived from the previous version's writer and never elected, writers activate on a hedging schedule, and a write reports `Committed`, `Superseded`, `Undecided` or `OutsideConfiguration`.

- Dynamic membership. `ClusterId` and `QuePaxaConfiguration` carry the chain identity and an ordered member list; the membership governing a version is a field of the record decided at the version before it, so hosts derive it rather than being told it. `ReconfigureAsync` installs a change while carrying the committed value forward. A record naming another chain is refused on every path a record enters a host by.

- Readiness reporting. `ReadReadinessAsync` reports what each member has learned, as `RegisterReadiness` and `MemberReadiness`, and separates a member that is behind from one that cannot be reached. An overload measures over a supplied membership, so the incoming side of a change is observable before the change commits. `QuePaxaConfiguration.Joining` and `Leaving` name the delta at a boundary.

- Durable restore for the QuePaxa recorder, the versioned host and the Fast CASPaxos acceptor, each as a `ToState`/`FromState` pair with codecs, refusing every state its protocol cannot hold.

- `QuePaxaVersionedRunner<TValue>`, a single-consumer loop that drives one host: it serves record requests, makes state durable before a reply leaves the process, learns disseminated records under a caller-named `LearnDurability`, and answers catch-up reads.

- Dissemination has both legs. `PublishCommittedRecordDelegate<TValue>` carries the audience the register computed, which at a membership boundary is the union of the deciding and installed memberships, and `ReceiveCommittedRecordDelegate<TValue>` names the receiving side.

- `HedgingSchedule` and `HedgedFastWriter<TValue>` put the Fast CASPaxos fast round on a delayed-activation schedule. No ballot, quorum rule or acceptor state changes; a delay of zero reproduces the unhedged behaviour.

- `docs/quepaxa.md`: how to choose between the two registers, and the procedures for operating a changing membership.

### Changed

- The value-codec seam is one named, format-agnostic pair, `WriteValueDelegate<TWriter, TValue>` and `ReadValueDelegate<TSource, TValue>`, taken by every JSON and CBOR factory where each took a naked `Action` or `Func`. **Breaking** for a caller that stored a factory argument or result in an explicitly typed `Action`/`Func` variable; lambdas and method groups bind unchanged.

- The CASPaxos change function is the named `ChangeRegisterValueDelegate<TValue>`, kept distinct from `ComputeRegisterValueDelegate<TValue>` because one composes inside the round and the other recomputes outside it. **Breaking** for a caller passing an explicitly typed `Func<TValue?, TValue>`.

- A member version probe answers a `MemberVersionReport`, the version beside the identity the answering host asserts, and a readiness report refuses an answer naming another member. Two probe routes reaching one host would otherwise let one replica fill two slots of the report a decommission is gated on. The identity is the host's own claim and is not authentication. **Breaking** for every implementation of `ObserveMemberVersionDelegate`.

- A versioned reply carries the host that produced it, and a register checks that the endpoint it aimed at one member was answered by that member. **Breaking** on the reply's shape and wire.

- A versioned host is owned by at most one runner at a time and enforces it: while a runner holds the claim, mutating the host from outside throws. **Breaking** for a host that snapshotted or mutated a node beside its own runner.

- The Raft surface carries `Term` and `LogIndex` where it carried bare `long` values, both bounded one below two to the fifty-third so a value survives a reader that parses JSON numbers as doubles. **Breaking** for every caller that constructs or reads these members, and for a peer that sent a value above the bound.

- `RaftNode<TCommand>` filters every identity arriving from the wire against the configured membership before the term rule runs, so a non-member can neither complete an election nor raise a member's term.

- `ChangeOutcome<TValue>` gains `AcceptedCount`, reporting how many acceptors accepted, which is what the fast-ballot piggyback rule needs. **Breaking** for positional construction, deconstruction or pattern matching.

- The CBOR deserializer refuses a payload carrying data after the message. **Breaking** for a sender that padded its frames.

- Dependency refresh across the toolchain; no library code or wire-format changes.

### Fixed

- The write documentation promised an exception for a recorder answering for another instance or member; those replies are absorbed as that recorder's unavailability and surface as an undecided outcome. The documented throw is now the one the register makes: a round that decided a record carrying another version, refused before adoption.

- Concurrent writers are checked for linearizability over real sockets, including across a minority partition, and socket assertions count answerers against a quorum floor rather than requiring every host to answer, which the protocol does not owe.

- The channel readers gain `CancelPendingRead` and end gracefully on a canceled read, so a consumer can stop a call already in flight.

- A catch-up read skips a host whose runner has stopped instead of abandoning the round at every host after it.

- `ConsensusNode<TValue>.RunAsync` gates its durable write on what was last persisted rather than on whether the current request changed the acceptor, closing a window where a retransmission was answered from state that never reached the disk.

- `FastCasPaxosRegister<TValue>.ProposeFastReaching` refuses a repeated acceptor index instead of counting it toward a quorum. **Breaking** for a caller that passed a list with repeats.

- A versioned register allocates the proposer lane per proposal rather than per call, so a caller cannot put two values under one proposal key by retrying at a version it left undecided.

- The packed SBOM records the packaged version, and the packages ship their XML documentation.

## [0.0.7] - 2026-07-29

### Changed

- Dependency refresh across the toolchain; no library code or wire-format changes. The packages now depend on `Lumoin.Base` 0.0.8 (from 0.0.6), and `Lumoin.Verisync.Cbor` on `System.Formats.Cbor` 10.0.10. The build moves to .NET SDK 10.0.302 and MSTest.Sdk 4.3.3; because MSTest.Sdk 4.3.x injects its own central `PackageVersion` items, the MSTest and `Microsoft.Testing.Extensions.*` pins move out of `Directory.Packages.props` and are steered through the SDK version properties in the test project. `Microsoft.CodeAnalysis.BannedApiAnalyzers` moves from the 3.12 beta line to the stable 5.6.0, the `Microsoft.Extensions.*.Testing` packages to 10.8.0, the code-coverage extension and `dotnet-coverage` tool to 18.9.0, and the CI action pins to their current releases (`actions/checkout` v7.0.1, `actions/setup-dotnet` v6.0.0, `step-security/harden-runner` v2.20.0, `NuGet/login` v1.2.0). The test suite adopts the MSTest 4.3.3 assertion surface (`Assert.AreSequenceEqual`/`AreNotSequenceEqual`/`HasCount`) in place of `CollectionAssert`.

### Security

- The pinned `System.Security.Cryptography.Xml` transitive override rises from 10.0.9 to 10.0.10, clearing the high-severity advisories published against 10.0.9 (GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f, GHSA-mmjf-rqrv-855v) that `NuGetAudit` fails the restore on.

## [0.0.6] - 2026-07-13

### Changed

- `CheckpointedSequence.Promote` and the container's standalone `Compact` are removed; `Seal` is now the sole compaction entry point, so consensus checkpointing requires a certification-capable strategy. `CheckpointCommitment` carries its frontier, and `CompactSequenceDelegate` and `CanonicalizeCheckpointDelegate` now take the dotted checkpoint. The compactable RGA run state serializes dotted removes: tombstone spans carry two aligned counter ranges so a contiguous single-replica deletion coalesces to one span, irregular tombstones fall back to explicit entries, and the translation map coalesces into spans under a predicate that never covers a dot currently present as a vertex, so a resurrected ghost's witness entry always survives verbatim.

- The RGA strategy identifiers are replaced: `verisync.sequence.rga.v2` and `verisync.sequence.rga-rle.v2` supersede the deleted v1 identifiers, because the dotted-remove semantics change both strategies' replication contracts; the superseded `offset.v1` identifier is also deleted. `SequenceRemoveDelegate` and `CheckpointedSequence.Remove` now carry the removing `ReplicaId`. Flat `RgaState` tombstones become `RgaTombstoneEntry` records (`target` plus `removeDots`, empty for legacy); both deserializers throw on duplicate identities, validate that the context covers every vertex and remove-dot, and reject a default (absent) array. `ToRunState` fails closed on a state carrying dotted removes, while legacy-only states still round-trip. The translation-map merge is no longer last-writer-wins: a dropped dot resolves to the maximum-counter merged vertex reachable through the union of both maps, making the merge commutative across states compacted at different frontiers. Retention classification is now an explicit-stack post-order walk, so long stable deleted runs compact without call-stack growth.

- The public `ReconciliationContract` constructor now enforces a checksum-width production floor of four bytes (`MinimumProductionChecksumWidth`): a width of one, two, or three is rejected, because below four bytes the per-decode masquerade bound becomes material at realistic difference sizes. **Breaking** for a caller constructing a width-one-through-three contract through the public constructor: move to a width of four or more (eight for a difference past the tens of thousands). The per-decode union-bound documentation on the decoder and on `ReconciliationContract.ChecksumWidth` now states that scaling explicitly and points at the decoder's new count and bound members.

- Verisync's tagging and pooled-memory primitives now come from `Lumoin.Base` rather than `Lumoin.Verisync.Core`. `VerisyncTags` and `TaggedMemory` build on and expose `Lumoin.Base.Tag`, the typed `Create<T>`/`With<T>`/`Get<T>` API with content-based, order-independent equality, in place of the former bespoke `Tag` record. The reconciliation cell buffer, item arena, dotted projection, encoder, decoder, and anti-entropy session now require an injected `MemoryPool<byte>`: the pool parameter is non-null, and the former pool-less convenience constructors and private heap-backed fallback owner are gone. Wire formats and reconciliation behavior are unchanged. Requires `Lumoin.Base` 0.0.4.

- Deserialization fails closed under one type across encodings: every `DeserializeMessageDelegate` the JSON and CBOR codecs build now throws a single new `MessageDeserializationException` (in `Lumoin.Verisync.Core`), with the encoding-native cause (a `JsonException`, a `CborContentException`, or a wrapped argument exception) preserved as its inner exception, so a channel consumer catches one type whether the wire is JSON or CBOR. Serializers are unchanged and still throw their native exceptions. This supersedes the surfaced exception type in the `JsonException` entries below; those now describe the inner cause a handler can still read off `InnerException`.

- The reconciliation decoder now peels in near-linear time. The previous decode re-scanned the whole decoded set on every absorbed symbol and re-scanned all cells to a fixpoint on every absorb, quadratic in the difference size, so throughput collapsed as differences grew. It now applies already-decoded items to incoming cells through an incremental cursor heap and finds newly pure cells through a work-list seeded by each modified cell. The decoded set, the completion point, the soundness rules, and the wire format are unchanged. Measured: a 16,384-item difference that took about 54 s now reconciles in about 0.2 s, and per-symbol throughput rises with the difference instead of falling.

- The encoder and decoder store coded symbols in a single flat, contiguous `ReconciliationCellBuffer` rather than parallel lists of per-cell arrays, so the XOR fold walks contiguous memory and the per-cell allocation is gone; the two duplicated content-keyed-bytes helpers are unified into one `ContentAddress`. `ReconciliationSymbol` gains an additive `ReadOnlySpan<byte>` constructor so a symbol can be snapshotted from a cell in a single copy. Behavior, wire format, and the pinned vectors are unchanged; the gain is locality and modest decode throughput.

- The encoder and decoder reuse their walk cursors instead of reallocating one per fold. The cursors that carry pending item contributions through the produce and peel priority queues were reference-type records cloned on every re-enqueue; they are now mutable structs stored inline in the queue and advanced in place, and the encoder cursor carries its checksum as a value re-materialized at fold time rather than a copied array. Behavior is byte-identical, and steady-state per-session allocation is down about 47% (about 287 KB to about 151 KB) with the live heap still flat.

- The reconciliation encoder, decoder, and `AntiEntropySession` are now `IDisposable` and rent their coded-cell backing from a caller-supplied `MemoryPool<byte>`, so the kernel's largest scoped buffer is accountable through the pool's rental metrics rather than a naked array. The constructors thread the required pool and a capacity hint; the session pre-sizes from its projected snapshot, disposes the encoder and decoder it owns, but never the injected pool. Pooled segments are cleared on rent, preserving the all-zero-fresh-cell contract the fold relies on, and the paired rent is exception-safe so a failed second rent returns the first rather than leaking it. A `ReconciliationSymbol` stays an owned-copy value: the documented exception for bytes that escape the kernel to callers and the wire.

### Removed

- The bespoke `Lumoin.Verisync.Core.Tag` type, superseded by `Lumoin.Base.Tag`. **Breaking** for any consumer that referenced `Lumoin.Verisync.Core.Tag` directly: the type moves to the `Lumoin.Base` namespace and drops the `(Type, object)` tuple factories, the `Data` property, and the `Type` indexer in favor of the typed `Create<T>`/`With<T>`/`Get<T>`/`TryGet<T>`/`Contains<T>` API. The internal heap-backed reconciliation fallback owner (`ReconciliationHeapMemoryOwner`) is removed in the same move.

- The pool-less and nullable-pool constructors on `ReconciliationCellBuffer`, `ReconciliationItemArena`, `DottedReconciliationProjection`, `ReconciliationEncoder`, `ReconciliationDecoder`, and `AntiEntropySession`. **Breaking**: these types now take a required, non-null `MemoryPool<byte>`, so a caller that previously omitted the pool (or passed `null`) must now name one explicitly, for example `BaseMemoryPool.Shared`.

### Fixed

- A remove-aware anti-entropy session can no longer poison its causal context on an interrupted exchange. The drain path used to fold the peer's context whenever the host wound the session down with `Complete()` and no apply had folded, but the fold could cover the dots of entries never transferred, and the next session would then classify those entries observed-and-removed: a permanent, cluster-wide false drop of live data. The drain now folds nothing on either role; a remove-aware initiator additionally defers its local drops while its fetch is outstanding, and a drop dispatched on a running initiator fails closed as an order violation. A wind-down before the exchange finishes is now observable as the new terminal `AntiEntropySessionState.Interrupted`, previously indistinguishable from `Completed`, and an interrupted session has folded no peer context at all. `Interrupted` and `IsConverged` are complementary: `IsConverged` stays false on any interrupted wind-down and true only through the reconciliation path. As a deliberate consequence, the responder's context advances only through applies during the exchange; the completion frame (see Added) converges both contexts in one session.
- The initiator's deferred local drops now apply after the fetch answer's elements, not before. Every applier folds the full peer context, and only the elements apply carries the entries that context covers, so the old order left a one-await window (the elements hook throwing, or the process dying between the two applies) where the folded context durably covered entries that never applied, and the next session would classify them observed-and-removed: the same permanent false drop the deferral itself exists to prevent, one handler step later. With the elements first, the fold always rides the hook call that carries the entries it covers, and a fault before the drops apply merely re-classifies them in the next session.
- A remove-aware responder now fails closed when the peer never ships its causal context: a done signal without a prior context faults the run, instead of the responder silently classifying later applies against the empty clock and completing as if the remove-aware exchange had happened. `SubmitAsync` and `TriggerBatchAsync` now document the `ChannelClosedException` their returned tasks fault with after `Complete()`.
- `RaftRunner` no longer strands proposals when its loop ends. A non-cancellation hook failure (the fail-closed path for a broken transport, durable store, or state machine) used to complete nothing: every queued, in-flight, and later-issued `ProposeAsync` task hung forever, and cancellation orphaned the proposal being dispatched between its dequeue and its result. On every early exit the runner now completes the work channel first and then cancels (on cancellation) or faults (on any other failure, with an `InvalidOperationException` carrying the loop failure as its inner exception) every pending and in-flight proposal, so a later `ProposeAsync`, `SubmitAsync`, or trigger fails fast with the now-documented `ChannelClosedException`. The `ProposeAsync` contract also states plainly that a faulted or cancelled proposal may already be appended and persisted, and may later commit, so a host that retries must tolerate or deduplicate a possible duplicate command.
- `RaftRunner.RunAsync` given a null `send` delegate now fails closed instead of hanging pre-enqueued proposals. The argument validation threw before the loop started and left the work channel writer open, so a proposal already queued on a runner that would never run hung forever; the null-send path now completes the writer and faults every enqueued proposal, with the validation failure as the inner exception, exactly as an early loop exit does.
- A `RaftRunner` proposal cancelled when the runner token ends the loop now carries that token as its cancellation cause. The abandonment used a token-less `TrySetCanceled`, so the resulting `TaskCanceledException` lost the attribution; it now passes the runner's cancellation token.
- `RaftRunner` no longer misreads a hook's own cancellation as a clean stop. The loop's `catch (OperationCanceledException)` is narrowed with `when (cancellationToken.IsCancellationRequested)`, so an `OperationCanceledException` a persist, send, or apply hook throws for its own reasons, while the runner token is not signalled, flows to the fault path and faults the pending proposals rather than cancelling them as if the runner had stopped cleanly.
- An add-only `AntiEntropySession` handed local drops by its difference resolver now fails closed with `InvalidOperationException` instead of dereferencing a missing drop applier. An add-only session wires no drop path, so a resolution carrying local drops used to `NullReferenceException` on the null-forgiving apply; it is now rejected at decode completion, mirroring the add-only rejection of the remove-aware context and drop frames.

### Security

- The reconciliation envelope JSON deserializer now fails closed as `JsonException` on every present-but-malformed field: a JSON-null hex field (which had leaked `ArgumentNullException`), a wrong-kind value where a string or hex field was expected (`InvalidOperationException`), a fractional or `Int32`-overflowing integer (`FormatException`), and a non-object or non-array carrier where the shape required one. Every `JsonElement` accessor is now kind-guarded before use, so a hostile or buggy peer can no longer drive the verifying deserializer to throw an exception type the contract promises it never will. A structurally missing required property fails closed as well: every per-field read across the JSON codecs (reconciliation, CRDT state, Raft, log commitment, consensus) now goes through a `RequireProperty` guard that throws rather than leaking the framework's `KeyNotFoundException`.

### Added

- The anti-entropy session closes its one-directional context gap with a wire-level completion frame. A remove-aware initiator whose exchange work completed sends `ReconciliationCompletion` as its final frame, carrying the count of transfer envelopes it sent; the responder, protected by add-only, role, and phase guards and a count check that fails closed on any transfer-count drift (a lost, truncated, or duplicated transfer envelope), runs its one terminal fold of the initiator's exchanged context and reaches `Completed`. One session now converges both members' contexts, where convergence previously required a second, reverse-direction session. An interrupted exchange emits no frame and keeps the landed semantics verbatim (`Interrupted`, zero folds, not converged). Remove-aware mode only; a 0.0.5 peer receiving the unknown frame tears down loudly through the uniform fail-closed deserialization contract.

- Sealing is group-quiescent-aware and its diagnostics are actionable. The offset strategy exposes an insert-quiescence probe: `OffsetAnchoredSequence.UnstableInserts(frontier)` returns the vertex insert-dots the frontier does not cover, in deterministic `(Replica, Counter)` order, surfaced through a new optional `SequenceUnstableInsertsDelegate` slot on the strategy context and as `CheckpointedSequence.UnstableInserts` (null for strategies whose compaction imposes no quiescence precondition). The container now refuses with two distinct diagnostics: `Seal` fails closed before any consensus round when the frontier leaves inserts uncovered, and `ApplyCommittedSeal`, after the digest check, tells a straggling writer the recovery: the committed frontier can never cover its in-flight inserts, so it adopts a healthy member's state, inherits the checkpoint and commitment by merging containers, and re-applies its edits as fresh inserts. Because a base-changing compaction re-identifies converted elements, offset appliers apply each base-changing committed seal exactly once (re-application fails closed; a drop-only seal re-applies idempotently exactly as RGA), and an offset member re-sealing after its own base-changing compaction is refused harmlessly at the equal-frontier arm. `OffsetAnchoredSequence.Merge` and `Rga.Merge` now fail closed when the operands carry conflicting vertices under one insert identity, since a dot mints exactly one immutable vertex; the check compares element values, so `TValue` must carry value equality under `EqualityComparer<T>.Default` (now a documented contract on both strategies). A drop-only offset compaction now keeps the prior base-offset translation map instead of replacing it, `TranslateAnchor` gains a matching identity fallback for unmapped in-range base offsets, and `FromState` rejects base-offset translations that target live anchors. The container docs state plainly that a member without fresh group-wide digests must never seal, and that the group's recovery from an island is wholesale adoption from it.

- The offset-anchored sequence strategy certifies removals on both of its axes, closing the same causal-invisibility defect the RGA strategies closed: `offset.v2` removes are dotted events that tick the context (live-tombstone removals as in the RGA mechanism, and base-offset removals through a per-offset remove-dot map), so gossip digests see remove divergence, the stability frontier certifies removals group-wide, and the strategy exposes the causal context and certified projection that make an offset container sealable. Compaction follows a four-way taxonomy: a stable tombstoned element whose remove is not yet certified converts into the new base as a pending-removed entry, never ghost-retained, carrying its remove-dots onto its new offset. The base carries a generation identity (the frontier it was materialized at, stamped only by base-changing compactions) and merges are fenced on it, because certified reclamation would let base value arrays repeat across genuinely different generations. Removed base entries are certified but not yet reclaimed: they stay as hidden ordering placeholders, and reclamation awaits a follow-on that carries a consensus-agreed reclamation set through the seal. Compaction, and therefore sealing an offset container, requires an insert-quiescent frontier that covers every vertex's insert-dot; an unstable vertex fails closed. Deserialization gains full parity validation, including one cross-axis dot pool, so a forged dot aliased between the live and base axes cannot turn an honest live certification into the reclamation of an unremoved base slot.

- The offset strategy's base addressing is generation-exact. `OffsetAddress`, the structural `OffsetAnchor` paired with the base generation it was read at, is the public addressing type on both sides of the surface: `InsertAfter` and `InsertAtHead` return it, and `Remove`, `TranslateAnchor`, and `VisibleElements` carry it, while `OffsetAnchor` stays internal to the strategy and its state model. The state gains a `baseGeneration` ordinal beside `baseFrontier`, stamped only by base-changing compactions and required fail-closed in the JSON. `TranslateAnchor` serves a base address by its generation: identity at the current generation, the base-offset map at exactly the one preceding generation the map serves, and `null` fail-closed for anything older or newer, so a stale or forged generation is refused rather than silently mis-served. Edits at a stale-generation base address fail closed with `ArgumentException`, the generation checked before the range; merges fence on the ordinal alongside the frontier. Head and live-dot addresses stay exact across generations and carry the canonical generation zero. The strategy identifier does not change.

- Sequence removes are dotted events the group can certify. `Rga<TValue>.Remove` now takes the removing `ReplicaId`, ticks the causal context, and records the minted remove-dot in a tombstone map (`target -> remove-dots`), so a remove enters the gossip digest and the stability frontier like any other event. Waterline compaction's drop gate additionally requires at least one remove-dot at or below the frontier, so a tombstone can no longer be dropped, and later resurrected by a laggard's merge, before every member has seen the removal. The compaction checkpoint is now the certified projection (the visible order filtered to stable insert-dots, excluding only elements whose remove is certified at the frontier), which makes it a pure function of the frontier and identical on every honest member. `Merge` fails closed, in both argument orders, when an operand presents an element live that the other operand's lineage compacted after a universally observed remove: such a state is a stale pre-remove replay and must adopt a current state wholesale. A new `CausalContext` accessor on `Rga<TValue>` (with a `SequenceCausalContextDelegate` seam on the strategy context and `CheckpointedSequence`) serves the digest path, which `ToState` could not on a compacted array. Tombstones loaded from pre-dotted state carry no remove-dot and are retained forever.

- Waterline compaction is driven by a consensus seal. `CheckpointedSequence.Seal` computes the certified projection at a caller-supplied stability frontier through a new strategy seam (`CertifySequenceProjectionDelegate`), proposes a frontier-carrying commitment through the CASPaxos register with a monotone change function (the proposal replaces the recovered commitment only when it is the first seal, strictly dominates the recovered frontier, or repeats it byte-identically; anything behind, concurrent, or equal-with-a-divergent-digest re-proposes the recovered value unchanged), and compacts only when its own proposal was chosen (`Sealed`), returning the container unchanged otherwise so a losing sealer retries above the winner. Committed frontiers therefore form a strictly ascending chain. Non-sealers converge through `ApplyCommittedSeal`, which verifies the member's own certified projection against the committed digest (a distinct diagnostic tells a lagging member to catch up first) and compacts on match. Checkpoints are dotted (`SequenceCheckpointEntry` pairs each value with its element identity), so a commitment is unambiguous under repeated values.

- `AntiEntropySession<TElement>.IsConverged`: a convergence attestation distinct from the terminal state alone. A wind-down before the exchange finishes now lands `AntiEntropySessionState.Interrupted` rather than `Completed`, and `IsConverged` agrees with that terminal split while additionally reading through pre-terminally for a responder still resolving fetches. It is set only on the reconciliation path (for an initiator when the decoder recovered the whole symmetric difference and the resolution finished, for a responder when the peer's done signal attested a complete decode against this session's snapshot) and stays false for a session merely terminated. It is written only by the consumer loop and read with a volatile-safe read, so a host may poll it from another thread alongside `State` to assert the sets actually converged.

- The reconciliation decoder's per-decode false-peel bound is now observable. `ReconciliationDecoder.PurityCheckCount` counts every non-neutral purity evaluation a decode runs (the masquerade opportunities, each accepting a mixed cell as pure with probability `2^(-8 * ChecksumWidth)`), and `FalseDecodeProbabilityBound` is that count times the per-cell bound, clamped to one: the operative union-bound figure over the whole decode, distinct from the per-cell probability. A consumer acting on a decode, such as a repair path, should require the bound far below one before trusting the recovered difference; the bound is against random corruption, an adversary holding the checksum key being covered by the contract's key discipline.

- Pool-aware and item-stream wire deserialization, the read-side companions to the message channel. `OwnedMessageChannelReader<TMessage>` deserializes each framed message into an owned, disposable value whose bytes are rented from a required, injected `MemoryPool<byte>` through the new `DeserializeOwnedMessageDelegate<TMessage>` rather than copied to the GC heap; ownership of each yielded value transfers to the consumer, which disposes it. `ItemStreamChannelReader<TItem>` reads a length-prefixed flow of one structured type and drives a per-item handler (`DecodeItemDelegate<TItem>` plus the synchronous `ItemHandlerDelegate<TItem>`) materialising no collection: each item is borrowed for exactly one handler call and the pooled backing the decoder rented for it is released the moment the call returns. Both readers reuse the existing channel's framing, padding, and hostile-frame bounds, now centralised in one internal `FrameReader` (the outer-length cap, the padded inner real-length check, the up-front item-count bound, and the no-trailing-bytes rule). The plain `MessageChannelReader<TMessage>`, refactored onto the shared framing, and the entire write side are unchanged.

- Remove-aware (dot-cloud) reconciliation atop the anti-entropy session: an `OrSet` or `DottedVersionVectorSet` reconciles its observed removes as well as its adds, with reconcile-then-apply equal to `DottedVersionVectorSet.Merge` while bytes on the wire stay proportional to the divergence. The kernel, encoder, decoder, and symbol stay an unchanged generic digest-set engine; remove-awareness is host-side. The session takes an optional pinned local `VectorClockState`; the initiator projects present `(dot, value)` entries to digests through a new `DottedReconciliationProjection`, decodes the symmetric difference, and classifies each decoded dot against the peer's exchanged causal context by the merge rule (a held dot the peer's context covers is a local drop; an absent dot the initiator's own pre-session context covers is pushed as a drop rather than re-added, the resurrection guard). The genuinely new wire surface is a whole-context exchange (`ReconciliationContext`, shipped whole and never reconciled) and a remove push (`ReconciliationDrop`, dots only); a `null` local context keeps the add-only path byte-identical.

- SIMD acceleration for the reconciliation kernel's hot loops: the byte-wise XOR folds and neutrality scans behind encoding, peeling, and symbol combination now route through `ReconciliationXor`, a facade over per-width vector backends (`Vector128`, `Vector256`, `Vector512`, with a scalar reference) selected by a dispatch that the JIT folds to a direct call. The width tiers are cross-platform, lowering to SSE/AVX2/AVX-512, NEON, and WASM SIMD alike, and the wire contract is unchanged: every backend is pinned byte-identical to the scalar reference across edge lengths. The benchmarks project gains XOR and encoder throughput benchmarks plus `--reconciliation-overhead`, a seed-pinned measurement of bytes-on-wire against the information-theoretic floor and full-state and hash-list anchor rows (symbols per difference converge to about 1.37x at a thousand-item divergence, three orders of magnitude under either anchor at small differences).

- `AntiEntropySession`: the host-side runner for one point-to-point reconciliation session, in the `RaftRunner` production shape, where all inbound work flows through a single-consumer queue, so every state change and every outbound send happens on one loop and transport writes are serialized by construction. A session pins one set version (the item snapshot is copied and encoded at construction); the initiator decodes against its own lockstep encoder, signals done, and classifies the difference through `ResolveReconciliationDifferenceDelegate` into fetches and pushes; the responder streams batches only on host `TriggerBatchAsync` calls (liveness stays external, no timers, no entropy), serves fetches through `ServeReconciliationFetchDelegate` with exact-coverage verification, and applies elements through `ApplyReconciliationElementsDelegate`. Every protocol violation (mismatched offer, out-of-role frame, stream gap, partial fetch answer) fails the session closed. `ReconciliationEnvelope` gains the same exactly-one-payload dispatch guard as the Raft envelope. Element-level reconciliation is add-only at this base tier; remove-aware reconciliation over dot-cloud causal contexts is added separately in this release.

- The reconciliation wire layer: a `RaftEnvelope`-style one-of-five message family (`ReconciliationEnvelope` carrying offer, symbol batch, done, fetch, or elements) and `ReconciliationJson` codecs following the established fail-closed conventions. The deserializer is verifying: it pins the local `ReconciliationContract` and rejects an offer that does not match it, so a contract mismatch throws before any symbol is absorbed, and validates every hex field's width against that contract. The offer never carries key bytes; it carries a key check (a PRF tag over a fixed public input) so peers with differing checksum keys abort up front instead of failing to peel.

- The rateless set-reconciliation kernel: a replica encodes a set of fixed-width items into an unbounded coded-symbol stream (`ReconciliationEncoder`) whose symbol-wise XOR with a peer's stream is the stream of their symmetric difference, recovered by a peeling decoder (`ReconciliationDecoder`) from a prefix proportional to the difference size. Neither side ever sizes the divergence, and an equal-set reconciliation completes on the first symbol. The encoding is a group homomorphism from (sets, symmetric difference) to (streams, XOR). `ReconciliationContract` pins what peers must agree on before subtraction is meaningful (item domain, item width, checksum width and key; checksum width bounds the masquerade probability, and a secret key turns a poisoned stream into detected-and-aborted); injectivity enforcement is local-only (`ReconciliationInjectivityEnforcement`), and `ProjectReconciliationItemsDelegate` is the seam that projects a pinned state snapshot to reconcilable items. Wire codecs, the session runner, and SIMD XOR backends are later phases.

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
