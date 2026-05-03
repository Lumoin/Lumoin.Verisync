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
