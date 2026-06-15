<img style="display: block; margin-inline-start: auto; margin-inline-end: auto;" src="resources/lumoin-verisync-github-logo.svg" width="800" height="400" alt="Verisync project logo: A circular emblem in blue hues, two interlocking arrows forming a circular motion around the center evoking eventual convergence of replicas, followed by the wordmark 'verisync'.">

# Lumoin.Verisync

**A .NET stack for distributed state: conflict-free replicated data types, causal contexts, and leaderless consensus.**

![Main build workflow](https://github.com/Lumoin/Lumoin.Verisync/actions/workflows/main.yml/badge.svg)

---

## What is Verisync?

Verisync is a .NET library for distributed state synchronization without a central coordinator: conflict-free replicated data types, causal context tracking, and leaderless consensus. The library is designed so that wallets, edge devices, and services can share, merge, and reconcile state with strong eventual consistency, while exposing causality and conflicts as first-class concepts rather than implementation details.

The core value proposition is local-first state that converges across replicas. Each replica can read and write without waiting on a quorum; merges are deterministic and side-effect-free; causal history is preserved so that downstream consumers can reason about happens-before relationships and concurrent updates. Where a subset of operations genuinely needs linearizability, a leaderless consensus register provides it without electing a leader.

Verisync is designed to be a peer of credential, identity, and graph stacks rather than a dependency of any one of them. Replica identifiers, version vectors, and causal contexts are semantic types; nothing in the public API is a raw integer counter or a stringly-typed identifier.

## Libraries

| Library | Purpose | NuGet |
|---------|---------|:-----:|
| **Lumoin.Verisync.Core** | Replica identity, causal contexts, CRDTs, Fast CASPaxos consensus, authenticated log, transport seam | [![NuGet](https://img.shields.io/nuget/v/Lumoin.Verisync.Core.svg?style=flat)](https://www.nuget.org/packages/Lumoin.Verisync.Core/) |
| **Lumoin.Verisync.Json** | JSON serialization for channel messages, consensus protocol messages, and CRDT state records | [![NuGet](https://img.shields.io/nuget/v/Lumoin.Verisync.Json.svg?style=flat)](https://www.nuget.org/packages/Lumoin.Verisync.Json/) |
| **Lumoin.Verisync.Cbor** | CBOR serialization for channel messages | [![NuGet](https://img.shields.io/nuget/v/Lumoin.Verisync.Cbor.svg?style=flat)](https://www.nuget.org/packages/Lumoin.Verisync.Cbor/) |

## Key capabilities

**State-based CRDTs with property-tested merges.** Grow-only and positive-negative counters (`GCounter`, `PNCounter`), last-writer-wins and multi-value registers (`LwwRegister`, `MvRegister`), an observed-remove set (`OrSet`), and a replicated growable array for collaborative sequences (`Rga`). Every merge is a join-semilattice operation (commutative, associative, idempotent), verified with property-based tests.

**Causal contexts as first-class types.** Version vectors, dots, dotted version vectors, and dotted-version-vector sets are semantic types rather than dictionaries of opaque keys. Happens-before, concurrency, and observed-remove semantics are properties of these types, not ad-hoc comparisons in user code.

**Serializable state.** Every CRDT exposes `ToState`/`FromState` over plain records a host can persist (a database row, a message table, a blob) and reload and merge later. JSON codecs ship in `Lumoin.Verisync.Json`, CBOR in `Lumoin.Verisync.Cbor`.

**Set reconciliation.** A rateless anti-entropy protocol exchanges coded symbols to recover only the differences between two replicas; traffic scales with the divergence, not the set size. Runs over the transport seam.

**Leaderless consensus.** Classic CASPaxos and Fast CASPaxos registers: linearizable read-modify-write without leader election. The fast path commits in a single round trip when uncontended; contention falls back to a classic recovery round that tallies the fast-round winner. The protocol layer is message-driven, so the same proposer and acceptor run over in-process calls, in-memory channels, or sockets.

**Authenticated register and log replay.** A layer above consensus for cryptographically accumulated history: entry classification, chain-integrity verification, proof validation, and state folding are injected by the application, so the layer carries any proof scheme without baking one in.

**Pluggable transport.** The channel seam is `System.IO.Pipelines` with serialization injected as delegates: length-prefixed frames over any duplex byte stream. JSON and CBOR implementations are provided; the library does not own sockets, schedulers, or clocks.

## Architecture principles

Verisync follows the same data-oriented principles as the rest of the family: code is separate from immutable data, CRDTs are values rather than entities with hidden identity, and merges are pure functions. Domain types are agnostic to serialization format; encoding lives at serialization boundaries in the dedicated `Lumoin.Verisync.Json` and `Lumoin.Verisync.Cbor` packages.

Transport, persistence, and clock sources are wired through delegates rather than interfaces. The same CRDT or consensus register is tested against a synthetic in-memory transport (including a deterministic interleaving bench that explores message reorderings from a seed) and deployed against a real transport without changes at the call site.

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

> **Note:** This library is under active development ahead of its first release. APIs may change between versions.
