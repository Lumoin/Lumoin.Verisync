<img style="display: block; margin-inline-start: auto; margin-inline-end: auto;" src="resources/lumoin-verisync-github-logo.svg" width="800" height="400" alt="Verisync project logo: A circular emblem in blue hues, two interlocking arrows forming a circular motion around the center evoking eventual convergence of replicas, followed by the wordmark 'verisync'.">

# Lumoin.Verisync

**A .NET stack for distributed state: conflict-free replicated data types, causal contexts, and consensus registers that elect no leader.**

![Main build workflow](https://github.com/Lumoin/Lumoin.Verisync/actions/workflows/main.yml/badge.svg)

---

## What is Verisync?

Verisync is a .NET library for distributed state synchronization without a central coordinator: conflict-free replicated data types, causal context tracking, and leaderless consensus. The library is designed so that wallets, edge devices, and services can share, merge, and reconcile state with strong eventual consistency, while exposing causality and conflicts as first-class concepts rather than implementation details.

The core value proposition is local-first state that converges across replicas. Each replica can read and write without waiting on a quorum; merges are deterministic and side-effect-free; causal history is preserved so that downstream consumers can reason about happens-before relationships and concurrent updates. Where a subset of operations genuinely needs linearizability, a leaderless consensus register provides it without electing a leader.

Verisync is designed to be a peer of credential, identity, and graph stacks rather than a dependency of any one of them. Replica identifiers, version vectors, and causal contexts are semantic types; nothing in the public API is a raw integer counter or a stringly-typed identifier.

## Libraries

| Library | Purpose | NuGet |
|---------|---------|:-----:|
| **Lumoin.Verisync.Core** | Replica identity, causal contexts, CRDTs, the QuePaxa and Fast CASPaxos consensus registers, a Raft replicated log, authenticated log, transport seam | [![NuGet](https://img.shields.io/nuget/v/Lumoin.Verisync.Core.svg?style=flat)](https://www.nuget.org/packages/Lumoin.Verisync.Core/) |
| **Lumoin.Verisync.Json** | JSON serialization for channel messages, consensus protocol messages, and CRDT state records | [![NuGet](https://img.shields.io/nuget/v/Lumoin.Verisync.Json.svg?style=flat)](https://www.nuget.org/packages/Lumoin.Verisync.Json/) |
| **Lumoin.Verisync.Cbor** | CBOR serialization for channel messages | [![NuGet](https://img.shields.io/nuget/v/Lumoin.Verisync.Cbor.svg?style=flat)](https://www.nuget.org/packages/Lumoin.Verisync.Cbor/) |

## Key capabilities

**State-based CRDTs with property-tested merges.** Grow-only and positive-negative counters (`GCounter`, `PNCounter`), last-writer-wins and multi-value registers (`LwwRegister`, `MvRegister`), an observed-remove set (`OrSet`), and a replicated growable array for collaborative sequences (`Rga`). Every merge is a join-semilattice operation (commutative, associative, idempotent), verified with property-based tests.

**Causal contexts as first-class types.** Version vectors, dots, dotted version vectors, and dotted-version-vector sets are semantic types rather than dictionaries of opaque keys. Happens-before, concurrency, and observed-remove semantics are properties of these types, not ad-hoc comparisons in user code.

**Serializable state.** Every CRDT exposes `ToState`/`FromState` over plain records a host can persist (a database row, a message table, a blob) and reload and merge later. JSON codecs ship in `Lumoin.Verisync.Json`, CBOR in `Lumoin.Verisync.Cbor`.

**Set reconciliation.** A rateless anti-entropy protocol exchanges coded symbols to recover only the differences between two replicas; traffic scales with the divergence, not the set size. Runs over the transport seam.

**Leaderless consensus.** Classic CASPaxos and Fast CASPaxos registers: linearizable read-modify-write without leader election. The fast path commits in a single round trip when uncontended; contention falls back to a classic recovery round that tallies the fast-round winner. The protocol layer is message-driven, so the same proposer and acceptor run over in-process calls, in-memory channels, or sockets.

**QuePaxa consensus.** A QuePaxa versioned register ships beside the CASPaxos ones, because placement decides which protocol wins. Fast CASPaxos commits in one round trip from any replica when uncontended, but to a supermajority: on the probe's latency model that radius eats most of the saving on a spread placement and costs a co-located majority several times the classic round, and by quorum arithmetic at three replicas the fast quorum is unanimous and tolerates no failure. QuePaxa's per-instance leader — derived from the previous write, never elected — commits in one round trip to a simple majority, the shortest radius at every site of both placements, and by the same quorum arithmetic, at five replicas its fast path survives the single failure that leaves the Fast CASPaxos fast path needing every surviving replica; a non-leader pays three majority-radius trips. Under simultaneous contention neither fast path survives, and the difference is what remains: at three writers QuePaxa's writers adopt the leader's proposal and decide it deterministically in three steps, and more writers cost steps rather than recoveries, agreeing in every trial, while the register's writers fall back to recovery rounds. Which protocol is faster is therefore per writer and per placement, not global. The two are interchangeable only for updates that are idempotent, monotone, or abort-on-lose — CASPaxos applies the caller's change function inside the round, QuePaxa decides among proposed values, so a losing proposer re-reads and re-proposes. Linearizable read-modify-write remains the CASPaxos registers' claim.

**Dynamic membership.** A QuePaxa chain's membership is a field of the decided record rather than deployment configuration, so every host derives the membership it runs under from the record it has learned and no two hosts can be told different ones for one instance. Members are admitted and retired through the register, gated on a readiness report that names what each member has learned and separates a member that is behind from one that cannot be reached; a probe answer names the host that produced it, so a mis-wired endpoint map is refused rather than counted. [`docs/quepaxa.md`](docs/quepaxa.md) carries the operating procedures and the measured comparison between the two registers.

**Raft.** A replicated log beside the registers, for state machines that need an ordered command sequence rather than a single value: elections, log replication and commitment by majority, with `Term` and `LogIndex` as semantic types rather than bare integers and bounded so a value survives a reader that parses JSON numbers as doubles. Identities arriving from the wire are filtered against the configured membership before the term rule runs, and a node restores from durable state through the same persistence seam the consensus hosts use.

**Authenticated register and log replay.** A layer above consensus for cryptographically accumulated history: entry classification, chain-integrity verification, proof validation, and state folding are injected by the application, so the layer carries any proof scheme without baking one in.

**Pluggable transport.** The channel seam is `System.IO.Pipelines` with serialization injected as delegates: length-prefixed frames over any duplex byte stream. JSON and CBOR implementations are provided; the library does not own sockets, schedulers, or clocks.

## Architecture principles

Verisync follows the same data-oriented principles as the rest of the family: code is separate from immutable data, CRDTs are values rather than entities with hidden identity, and merges are pure functions. Domain types are agnostic to serialization format; encoding lives at serialization boundaries in the dedicated `Lumoin.Verisync.Json` and `Lumoin.Verisync.Cbor` packages.

Transport is wired through delegates rather than interfaces, and so is persistence wherever a run loop sequences it; clocks enter as injected `TimeProvider` instances, and a host that drives its node directly makes the state durable itself before it replies. The same CRDT or consensus register is tested against a synthetic in-memory transport (including a deterministic interleaving bench that explores message reorderings from a seed) and deployed against a real transport without changes at the call site.

## Getting started

Install the packages relevant to your use case:

```bash
# Core primitives: CRDTs, causal contexts, consensus.
dotnet add package Lumoin.Verisync.Core

# JSON serialization for state records and protocol messages.
dotnet add package Lumoin.Verisync.Json

# CBOR serialization for channel messages.
dotnet add package Lumoin.Verisync.Cbor
```

## Development

The codebase runs on Windows, Linux, and macOS.

Protocol designs are model-checked with TLA+ before implementation. The modules and the re-runnable model configurations are in the `tla/` directory.

Press **.** on the repository page to open the codebase in VS Code web editor for quick exploration.

## Vulnerability disclosure

Please report suspected security vulnerabilities privately through [GitHub security advisories](https://github.com/Lumoin/Lumoin.Verisync/security/advisories), not through public issues.

## Contributing

Open issues for bugs, suggestions, or improvements, or create pull requests. Especially welcome:

- Convergence and commutativity tests using property-based testing.
- New CRDT shapes or transport adapters.
- Improvements to causal-history reasoning and delivery-interleaving test scenarios.

## Acknowledgements

Work of **Reuben Bond on [Fast CASPaxos](https://github.com/ReubenBond/fast-caspaxos)** and the [CASPaxos write-up](https://reubenbond.github.io/posts/caspaxos/) has influenced the design of this library. Verisync's leaderless consensus follows the shape laid out there: a value-agnostic, rewritable register; the fast-round optimization that lets any proposer commit in one round trip when uncontended, recovering through a classic ballot under contention; and a message-driven acceptor/proposer split with the transport injected. The reference implementation, paper, and TLA+ model in that repository were the load-bearing guide for the ballot ordering and recovery logic.

## License

See the LICENSE file for details.

---

> **Note:** The 0.0.x line is published and under active development. APIs may change between versions.
