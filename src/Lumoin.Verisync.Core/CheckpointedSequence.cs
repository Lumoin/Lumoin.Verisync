using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A hybrid sequence container: a pluggable sequence CRDT accumulates collaborative edits and merges between
/// checkpoints, and the converged sequence is periodically sealed — through a Fast/classic CASPaxos register —
/// into a canonical "sequence as of frontier F" checkpoint that also reclaims the state below F.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type elements are referred to by.</typeparam>
/// <remarks>
/// <para>
/// This is the hybrid archetype: replicas exchange CRDT edits and converge coordination-free between
/// checkpoints, then one replica seals the converged state through consensus when a canonical, ordered anchor is
/// needed. The container is an immutable value; edits and merges return new containers.
/// </para>
/// <para>
/// The sequence design itself — addressing, merge, ordering, and how state below a sealed checkpoint is
/// reclaimed — is injected through a <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}"/>, so the
/// container owns only the checkpoint protocol. The strategy is part of the document's replication contract:
/// <see cref="Merge(CheckpointedSequence{TSequence, TValue, TAnchor})"/> refuses to merge containers carrying
/// different <see cref="StrategyId"/> values, because replicas running different strategies do not degrade —
/// they silently diverge. Pin the identifier in the document's genesis entry or first seal.
/// </para>
/// <para>
/// The seal lifecycle: members edit and gossip their causal-context digests; a host folds those digests into a
/// stability frontier F (see <see cref="StabilityFrontier"/>); one member calls
/// <see cref="Seal(CasPaxosRegister{CheckpointCommitment}, Ballot, VectorClock)"/> at F, which certifies the
/// dotted projection at F, proposes its <em>commitment</em> — the (frontier, digest) pair, never the snapshot —
/// through the register, and, only when its own proposal wins, compacts the live sequence at F and records the
/// checkpoint. Every other member then calls
/// <see cref="ApplyCommittedSeal(CheckpointCommitment, Ballot)"/> on learning the committed commitment, verifies
/// its own certified projection at F against the digest, and compacts identically — the determinism theorem end
/// to end. Consensus payloads stay metadata-sized regardless of sequence length; the content travels the CRDT
/// plane (here, inside the container as <see cref="Checkpoint"/>) and is verifiable against
/// <see cref="Commitment"/>.
/// </para>
/// <para>
/// Sealing REQUIRES a certifying strategy. Consensus-anchored checkpointing is tied to certification-capable
/// strategies: a container over a non-compacting or not-yet-certifying strategy — one whose context leaves
/// <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}.CertifyProjection"/> or
/// <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}.Compact"/> null — cannot be sealed at all, and
/// <see cref="Seal(CasPaxosRegister{CheckpointCommitment}, Ballot, VectorClock)"/> throws. That is intended: a
/// consensus anchor that certifies nothing was a footgun (a frontier-less full-snapshot commitment cannot
/// participate in the monotone refusal rule), so the register carries seal commitments only. A host that wants
/// the canonical-anchor archetype selects the certifying, compactable strategy — the same sequence type behind a
/// certifying identifier. Before the first seal <see cref="Checkpoint"/> is empty and both
/// <see cref="Commitment"/> and <see cref="CheckpointBallot"/> are <see langword="null"/>.
/// </para>
/// <para>
/// REJOIN / ADOPTION. Once any seal has committed, a replica that was evicted, restored from persistence that
/// may predate its acknowledged context, or replayed must NOT merge as a peer — merging a stale pre-remove state
/// is exactly what the strategy's grow-only merge detector throws on. Re-entry is WHOLESALE ADOPTION: the
/// rejoiner performs a QUORUM register read for the committed <see cref="CheckpointCommitment"/> and the ballot
/// it was learned at, takes a healthy member's FULL sequence state, seeds a fresh container around it with
/// <see cref="Adopt(SequenceCrdtContext{TSequence, TValue, TAnchor}, CanonicalizeCheckpointDelegate{TValue}, ComputeDigestDelegate, TSequence)"/>,
/// and calls <see cref="ApplyCommittedSeal(CheckpointCommitment, Ballot)"/> — whose digest verification IS the
/// adoption check for a donor still on the commitment's SOURCE generation: any donor for an identity-stable
/// strategy like RGA, and for a base-materializing strategy like offset any donor whose lineage has not
/// BASE-CHANGED at the committed frontier — a base-changing compaction re-identifies converted elements, so a
/// donor that applied one can never digest-match the pre-compaction commitment, while a drop-only compaction
/// stays on the source generation and still verifies. After the group applies a base-changing seal a rejoiner
/// therefore adopts a post-seal donor, inherits the checkpoint and commitment through container
/// <see cref="Merge(CheckpointedSequence{TSequence, TValue, TAnchor})"/> — the higher-ballot arm hands them
/// over — and is verified by the NEXT committed seal rather than the current one. The rejoiner converges the
/// above-frontier tail by NORMAL merging with other members; its own partition-time edits are gone (a host that
/// wants them back re-applies them as fresh inserts at the application layer). Run the recovery AT MOST ONCE
/// per lost context and persist its result before gossiping it: a fresh insert re-minted from the adopted
/// context reproduces the lost dot deterministically, so a recovery that runs twice with a different insertion
/// point or value forges two vertices under one identity — the strategies' merge detector fails that state
/// closed rather than let merge order choose.
/// </para>
/// <para>
/// GROUP-QUIESCENT SEALING. A strategy whose compaction requires insert-quiescence — it advertises this by
/// wiring the context's insert-quiescence probe, surfaced here as <see cref="UnstableInserts(VectorClock)"/> —
/// makes sealing a GROUP-QUIESCENT, stop-the-world checkpoint: a host must drive the group to insert-quiescence
/// at the committed frontier (stop accepting inserts, let digests advance, re-fold) or accept adoption for a
/// straggling writer whose edits the frontier cannot cover. The probe is both the readiness check — fold every
/// member's probe at a candidate frontier, all empty means the group can seal — and the recovery discriminator
/// — a non-empty probe at a committed frontier means adopt, not apply. That is why adoption is wholesale: the
/// seal was stop-the-world. For the offset strategy the GENERATION FENCE is the second adoption trigger
/// alongside the stale-pre-remove detector — a base-changing seal advances the generation identity, so a member
/// on the prior generation cannot merge as a peer and rejoins by adoption.
/// </para>
/// <para>
/// A STRAGGLER MUST NEVER SEAL. <see cref="Seal(CasPaxosRegister{CheckpointCommitment}, Ballot, VectorClock)"/>
/// probes only the SEALER'S OWN state — a sufficient local witness to refuse, never proof of group quiescence —
/// so a member that cannot fold fresh digests and probes from EVERY member must not seal at all. A partitioned
/// straggler that seals at its own locally-quiescent frontier commits an ISLAND the register accepts (it
/// strictly dominates the committed line): every other member is then wedged — applying the island demands a
/// catch-up its generation fence forbids, and every group seal quietly returns un-<c>Sealed</c> forever, because
/// no group-reachable frontier dominates the island's foreign axis. The group's only recovery is wholesale
/// adoption FROM the island (adopt plus container merge — the island is a post-compaction donor, so the
/// current-commitment digest check cannot verify it), re-applying its own above-frontier edits as fresh inserts.
/// The seal-progress guarantee below therefore holds for sealers on the committed generation with fresh
/// group-wide digests, not for stragglers.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class CheckpointedSequence<TSequence, TValue, TAnchor>: IEquatable<CheckpointedSequence<TSequence, TValue, TAnchor>>
{
    private SequenceCrdtContext<TSequence, TValue, TAnchor> Context { get; }
    private CanonicalizeCheckpointDelegate<TValue> CanonicalizeCheckpoint { get; }
    private ComputeDigestDelegate ComputeDigest { get; }


    private CheckpointedSequence(
        SequenceCrdtContext<TSequence, TValue, TAnchor> context,
        CanonicalizeCheckpointDelegate<TValue> canonicalizeCheckpoint,
        ComputeDigestDelegate computeDigest,
        TSequence live,
        ImmutableArray<SequenceCheckpointEntry<TValue>> checkpoint,
        CheckpointCommitment? commitment,
        Ballot? checkpointBallot)
    {
        Context = context;
        CanonicalizeCheckpoint = canonicalizeCheckpoint;
        ComputeDigest = computeDigest;
        Live = live;
        Checkpoint = checkpoint;
        Commitment = commitment;
        CheckpointBallot = checkpointBallot;
    }


    /// <summary>
    /// The live, mergeable sequence. It carries the full edit history above the last sealed checkpoint — so
    /// element anchors remain available as insert targets and merge inputs — and, for a compactable strategy,
    /// has had state below the last sealed frontier reclaimed.
    /// </summary>
    public TSequence Live { get; }

    /// <summary>The canonical dotted checkpoint content as of the last seal, or empty before the first. Local content; consensus carries only <see cref="Commitment"/>.</summary>
    public ImmutableArray<SequenceCheckpointEntry<TValue>> Checkpoint { get; }

    /// <summary>The consensus-agreed commitment to <see cref="Checkpoint"/>, or <see langword="null"/> before the first seal.</summary>
    public CheckpointCommitment? Commitment { get; }

    /// <summary>The ballot the current checkpoint was committed at, or <see langword="null"/> if none.</summary>
    public Ballot? CheckpointBallot { get; }

    /// <summary>The identifier of the sequence strategy this container operates under.</summary>
    public string StrategyId => Context.StrategyId;

    /// <summary>The visible values of the live sequence, in sequence order.</summary>
    public IReadOnlyList<TValue> Values => Context.Values(Live);

    /// <summary>The causal context the host advertises for this document as a gossip digest, or <see langword="null"/> when the strategy exposes none.</summary>
    public VectorClock? CausalContext => Context.CausalContext?.Invoke(Live);


    /// <summary>
    /// The seal-readiness and apply-vs-adopt diagnostic: the vertex insert-dots the strategy's
    /// insert-quiescence probe reports uncovered at <paramref name="stabilityFrontier"/>, or
    /// <see langword="null"/> when the strategy wires no probe (its compaction imposes no
    /// insert-quiescence precondition).
    /// </summary>
    /// <param name="stabilityFrontier">The candidate or committed stability frontier to probe.</param>
    /// <returns>
    /// The uncovered vertex insert-dots for a probe-wiring strategy — empty when the frontier is
    /// insert-quiescent — or <see langword="null"/> when the strategy wires no probe.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A host drives a group-quiescent seal by folding every member's probe at the candidate frontier — all
    /// empty means the group can seal there. At a COMMITTED frontier the probe is the recovery
    /// discriminator: a non-sealer whose probe is non-empty holds in-flight inserts the frontier can never
    /// cover and must adopt wholesale rather than apply. A <see langword="null"/> result says sealing this
    /// strategy is not group-quiescent at all.
    /// </remarks>
    public ImmutableArray<Dot>? UnstableInserts(VectorClock stabilityFrontier)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);

        return Context.UnstableInserts?.Invoke(Live, stabilityFrontier);
    }


    /// <summary>
    /// Creates an empty container operating under <paramref name="context"/>: the strategy's empty
    /// sequence and no checkpoint.
    /// </summary>
    /// <param name="context">The sequence strategy.</param>
    /// <param name="canonicalizeCheckpoint">The deterministic canonical encoder for dotted checkpoints.</param>
    /// <param name="computeDigest">The digest function over the canonical bytes; the digest is the consensus payload.</param>
    /// <returns>A new empty container.</returns>
    /// <exception cref="ArgumentNullException">Thrown if any argument is <see langword="null"/>.</exception>
    public static CheckpointedSequence<TSequence, TValue, TAnchor> Create(
        SequenceCrdtContext<TSequence, TValue, TAnchor> context,
        CanonicalizeCheckpointDelegate<TValue> canonicalizeCheckpoint,
        ComputeDigestDelegate computeDigest)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(canonicalizeCheckpoint);
        ArgumentNullException.ThrowIfNull(computeDigest);

        return new CheckpointedSequence<TSequence, TValue, TAnchor>(context, canonicalizeCheckpoint, computeDigest, context.Empty, ImmutableArray<SequenceCheckpointEntry<TValue>>.Empty, null, null);
    }


    /// <summary>
    /// Creates a container seeded with a transported <paramref name="live"/> sequence and no checkpoint —
    /// the rejoin-by-adoption entry point. A replica re-entering after eviction, restore, or replay wraps a
    /// healthy donor's full sequence state here and then calls
    /// <see cref="ApplyCommittedSeal(CheckpointCommitment, Ballot)"/> with the quorum-read commitment, whose
    /// digest verification is the adoption check; it must never merge its own stale pre-adoption state.
    /// </summary>
    /// <param name="context">The sequence strategy.</param>
    /// <param name="canonicalizeCheckpoint">The deterministic canonical encoder for dotted checkpoints.</param>
    /// <param name="computeDigest">The digest function over the canonical bytes; the digest is the consensus payload.</param>
    /// <param name="live">The donor's full sequence state to adopt wholesale.</param>
    /// <returns>A new container around <paramref name="live"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if any argument is <see langword="null"/>.</exception>
    public static CheckpointedSequence<TSequence, TValue, TAnchor> Adopt(
        SequenceCrdtContext<TSequence, TValue, TAnchor> context,
        CanonicalizeCheckpointDelegate<TValue> canonicalizeCheckpoint,
        ComputeDigestDelegate computeDigest,
        TSequence live)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(canonicalizeCheckpoint);
        ArgumentNullException.ThrowIfNull(computeDigest);
        ArgumentNullException.ThrowIfNull(live);

        return new CheckpointedSequence<TSequence, TValue, TAnchor>(context, canonicalizeCheckpoint, computeDigest, live, ImmutableArray<SequenceCheckpointEntry<TValue>>.Empty, null, null);
    }


    /// <summary>Inserts <paramref name="value"/> at the head of the live sequence.</summary>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new container and the anchor assigned to the inserted element.</returns>
    public (CheckpointedSequence<TSequence, TValue, TAnchor> Sequence, TAnchor InsertedId) InsertAtHead(TValue value, ReplicaId replica)
    {
        (TSequence live, TAnchor id) = Context.InsertAtHead(Live, value, replica);

        return (WithLive(live), id);
    }


    /// <summary>Inserts <paramref name="value"/> after <paramref name="after"/> in the live sequence.</summary>
    /// <param name="after">The anchor of the element to insert after.</param>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new container and the anchor assigned to the inserted element.</returns>
    public (CheckpointedSequence<TSequence, TValue, TAnchor> Sequence, TAnchor InsertedId) InsertAfter(TAnchor after, TValue value, ReplicaId replica)
    {
        (TSequence live, TAnchor id) = Context.InsertAfter(Live, after, value, replica);

        return (WithLive(live), id);
    }


    /// <summary>Removes the element anchored by <paramref name="anchor"/> from the live sequence.</summary>
    /// <param name="anchor">The anchor of the element to remove.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new container.</returns>
    public CheckpointedSequence<TSequence, TValue, TAnchor> Remove(TAnchor anchor, ReplicaId replica)
    {
        return WithLive(Context.Remove(Live, anchor, replica));
    }


    /// <summary>
    /// Merges the live sequences for convergence and keeps the checkpoint committed at the higher ballot.
    /// </summary>
    /// <param name="other">The container to merge with.</param>
    /// <returns>A new container; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if <paramref name="other"/> operates under a different <see cref="StrategyId"/> — replicas running different strategies silently diverge, so the mismatch fails closed.</exception>
    public CheckpointedSequence<TSequence, TValue, TAnchor> Merge(CheckpointedSequence<TSequence, TValue, TAnchor> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if(!string.Equals(StrategyId, other.StrategyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot merge sequences of different strategies: '{StrategyId}' and '{other.StrategyId}'. The strategy is part of the document's replication contract.");
        }

        TSequence mergedLive = Context.Merge(Live, other.Live);
        bool keepThis = CheckpointBallot is { } mine
            && (other.CheckpointBallot is not { } theirs || mine >= theirs);

        return keepThis
            ? new CheckpointedSequence<TSequence, TValue, TAnchor>(Context, CanonicalizeCheckpoint, ComputeDigest, mergedLive, Checkpoint, Commitment, CheckpointBallot)
            : new CheckpointedSequence<TSequence, TValue, TAnchor>(Context, CanonicalizeCheckpoint, ComputeDigest, mergedLive, other.Checkpoint, other.Commitment, other.CheckpointBallot);
    }


    /// <summary>
    /// Seals the current live sequence as the canonical checkpoint at <paramref name="stabilityFrontier"/>: the
    /// certified dotted projection's <em>commitment</em> — the (frontier, digest) pair, never the snapshot — is
    /// proposed through <paramref name="register"/> under a monotone refusal rule, and ONLY when the sealer's own
    /// proposal wins the register does the container compact the live sequence at the frontier and record the
    /// checkpoint.
    /// </summary>
    /// <param name="register">The CASPaxos register holding the canonical checkpoint commitments.</param>
    /// <param name="ballot">The proposing ballot.</param>
    /// <param name="stabilityFrontier">The group stability frontier the seal is computed at — see <see cref="StabilityFrontier"/>.</param>
    /// <returns>
    /// The container after the seal (compacted and recording the checkpoint when <c>Sealed</c>, otherwise
    /// <c>this</c> unchanged), the register after the change, the change outcome, and whether this sealer's own
    /// proposal won.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="register"/> or <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the strategy does not certify a projection or does not compact, so the container cannot be
    /// sealed; or, for a strategy that wires an insert-quiescence probe, when THIS member holds inserts
    /// <paramref name="stabilityFrontier"/> does not cover — a sufficient local witness that the group is not
    /// insert-quiescent — in which case the seal fails closed before any consensus round. The check is a local
    /// witness only: it cannot see other members' inserts, so group quiescence remains the host's probe-fold
    /// obligation before sealing.
    /// </exception>
    /// <remarks>
    /// <para>
    /// What certifies what: the digest covers the certified dotted projection at the frontier — the visible order
    /// filtered to stable insert-dots, excluding elements whose remove is certified — which is a pure function of
    /// the frontier for every member whose context dominates it, so two honest sealers at the same frontier
    /// propose byte-identical digests. The refusal rule (the register's change function) proposes MINE when the
    /// register is empty (the first seal — every clock dominates the absent frontier), when my frontier strictly
    /// dominates the recovered one, or when the frontiers are equal and the digests are byte-identical (an
    /// idempotent re-seal); otherwise — behind, concurrent, or an equal frontier with a divergent digest (the
    /// fail-safe against a buggy or forged sealer, unreachable for honest members sealing from states on the
    /// commitment's SOURCE generation) — it re-proposes the recovered commitment unchanged. An offset member
    /// re-sealing at the same frontier AFTER its own BASE-CHANGING compaction reaches the
    /// equal-frontier-divergent-digest arm harmlessly: its post-compaction projection is sentinel-re-keyed, so
    /// the digests differ and it aborts unchanged. A DROP-ONLY seal does not re-key the projection, so its
    /// re-seal takes the idempotent equal-digest arm exactly as RGA. The committed line is therefore a chain,
    /// each committed frontier strictly dominating its predecessor or repeating it byte-identically.
    /// </para>
    /// <para>
    /// <c>Sealed</c> is true only when the change was chosen AND the chosen value equals this sealer's own
    /// proposal. When it is false — a quorum failure or a competing commitment won — the container is returned
    /// UNCHANGED (the abort-on-lose): the host merges digests, recomputes at a frontier that dominates the chosen
    /// one (readable off <c>Outcome.Value.Frontier</c> when chosen), and retries. Every successful commit
    /// strictly ascends the chain, so competing sealers ON THE COMMITTED GENERATION with fresh group-wide digests
    /// make progress rather than livelock — a partitioned straggler that seals commits an island no group frontier
    /// can ever dominate (see the class remarks: a straggler must never seal). The recorded
    /// <c>(Checkpoint, Commitment)</c> pair is written only by a sealer whose own proposal won, so the commitment
    /// is consistent with its content by construction.
    /// </para>
    /// <para>
    /// An EMPTY certified projection is sealable: an all-removed document anchors on the digest of the empty
    /// canonical bytes. That requires the injected <see cref="ComputeDigestDelegate"/> to be total — a real hash
    /// digests empty input to a fixed non-empty value; a digest function returning empty bytes would fail the
    /// commitment's non-empty guard rather than fabricate a hollow anchor.
    /// </para>
    /// </remarks>
    public (CheckpointedSequence<TSequence, TValue, TAnchor> Sequence,
        CasPaxosRegister<CheckpointCommitment> Register,
        ChangeOutcome<CheckpointCommitment> Outcome,
        bool Sealed) Seal(
        CasPaxosRegister<CheckpointCommitment> register,
        Ballot ballot,
        VectorClock stabilityFrontier)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(stabilityFrontier);
        if(Context.CertifyProjection is null || Context.Compact is null)
        {
            throw new InvalidOperationException("The strategy does not certify a projection, so it cannot seal.");
        }

        //Insert-quiescence probe, before any projection, compaction, or consensus round: a probe-wiring
        //strategy (the base-materializing offset strategy) makes sealing group-quiescent, and THIS SEALER
        //holding uncovered inserts is a sufficient local witness that the group is not quiescent — the check
        //cannot see other members' inserts, so group quiescence stays the host's probe-fold obligation. This
        //precedes register.Change, so the refusal is structurally pre-consensus — nothing is proposed or
        //accepted before it fires; there is no direct register observable for the placement (an equal-frontier
        //divergent-digest re-seal discriminates it indirectly), so the ordering is pinned here and by the
        //synthetic-context ordering tests. The strategy's own guard inside compact-before-propose stays as
        //defense in depth.
        if(Context.UnstableInserts is { } unstableInserts && !unstableInserts(Live, stabilityFrontier).IsEmpty)
        {
            throw new InvalidOperationException("This member holds inserts the frontier does not cover, so the group is not insert-quiescent there and this strategy's compaction materializes only a fully-stable line. Drive the group quiescent — stop accepting inserts, let digests advance, and re-fold the frontier — then seal at a frontier that covers the inserts the UnstableInserts probe reports.");
        }

        ImmutableArray<SequenceCheckpointEntry<TValue>> projection = Context.CertifyProjection(Live, stabilityFrontier);

        //Compact BEFORE proposing: a strategy's compaction guard (the base-materializing strategy requires an
        //insert-quiescent frontier) must fail the seal before any consensus round, or a durable register
        //could choose a commitment no member can compact against. The result is pure and is used only when
        //this sealer's own proposal wins.
        TSequence compactedLive = Context.Compact(Live, stabilityFrontier, projection);
        var proposal = new CheckpointCommitment(stabilityFrontier, ComputeDigest(CanonicalizeCheckpoint(projection)));

        (CasPaxosRegister<CheckpointCommitment> nextRegister, ChangeOutcome<CheckpointCommitment> outcome) =
            register.Change(ballot, recovered =>
            {
                if(recovered is null)
                {
                    return proposal;
                }

                Causality order = proposal.Frontier.Compare(recovered.Frontier);
                if(order == Causality.After)
                {
                    return proposal;
                }

                if(order == Causality.Equal && proposal.Digest.Span.SequenceEqual(recovered.Digest.Span))
                {
                    return proposal;
                }

                return recovered;
            });

        bool isSealed = outcome.IsChosen && proposal.Equals(outcome.Value);
        if(!isSealed)
        {
            return (this, nextRegister, outcome, false);
        }

        var compacted = new CheckpointedSequence<TSequence, TValue, TAnchor>(
            Context,
            CanonicalizeCheckpoint,
            ComputeDigest,
            compactedLive,
            projection,
            proposal,
            ballot);

        return (compacted, nextRegister, outcome, true);
    }


    /// <summary>
    /// Applies a committed seal a non-sealer learned of: verifies this member's own certified projection at
    /// <paramref name="committed"/>'s frontier against its digest, then compacts the live sequence at that
    /// frontier and records the checkpoint. This is how every member — and a wholesale-adopting rejoiner —
    /// reclaims below-frontier state to match a seal it did not author.
    /// </summary>
    /// <param name="committed">The committed commitment learned from the register.</param>
    /// <param name="ballot">The ballot the commitment was learned at, kept so <see cref="Merge(CheckpointedSequence{TSequence, TValue, TAnchor})"/>'s higher-ballot checkpoint retention stays meaningful.</param>
    /// <returns>The container compacted at the committed frontier, recording the checkpoint and commitment.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the strategy does not certify a projection or does not compact; when this member has not yet
    /// observed everything below the committed frontier and must catch up before applying (its causal context
    /// does not dominate the committed frontier); when <paramref name="committed"/> sits behind or concurrent to
    /// a commitment this container already recorded (committed seals apply in chain order, so a stale earlier
    /// seal fails closed even when its digest would coincide); when its certified projection at the committed
    /// frontier does not byte-match the committed digest — with the preconditions satisfied, a mismatch is
    /// genuine divergence or a forged commitment, so it fails closed; or, for a strategy that wires an
    /// insert-quiescence probe, when this member still holds in-flight inserts the committed frontier can never
    /// cover — checked AFTER the digest so the graver divergence-or-forgery diagnosis surfaces first — in which
    /// case it must recover by wholesale adoption rather than applying.
    /// </exception>
    /// <remarks>
    /// The precondition — the applier's context must DOMINATE the committed frontier — is the scope of the
    /// determinism theorem: automatic for any member whose digest was folded into the frontier, a lagging member
    /// merges up first, and a restored or evicted replica adopts wholesale rather than applies-then-merges.
    /// Committed seals are applied in CHAIN ORDER; applying a stale earlier seal to a container already sealed
    /// above it fails the precondition-or-digest checks rather than regressing state. Re-applying an
    /// already-applied seal is idempotent when the compaction preserved projection identities — always for RGA,
    /// and for a base-materializing strategy (offset) when the seal was DROP-ONLY; a BASE-CHANGING seal is
    /// instead applied EXACTLY ONCE, because once it converted live content into sentinel-keyed base entries the
    /// re-application's projection no longer matches the committed digest and the digest check fails closed — a
    /// refusal, not corruption. A sealer that crashes between the register's acceptance and its
    /// own compaction leaves a committed checkpoint no member compacted against; that is harmless, because any
    /// member applies it on learning of it and the next seal strictly dominates it.
    /// </remarks>
    public CheckpointedSequence<TSequence, TValue, TAnchor> ApplyCommittedSeal(CheckpointCommitment committed, Ballot ballot)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if(Context.CertifyProjection is null || Context.Compact is null)
        {
            throw new InvalidOperationException("The strategy does not certify a projection, so it cannot seal.");
        }

        if(Context.CausalContext is { } causalContext)
        {
            Causality order = causalContext(Live).Compare(committed.Frontier);
            if(order is Causality.Before or Causality.Concurrent)
            {
                throw new InvalidOperationException("This member has not yet observed everything below the committed frontier and must catch up before applying the committed seal.");
            }
        }

        //Chain order: a commitment behind or concurrent to the one already recorded is a stale seal. Its
        //digest can coincide with this member's projection at that earlier frontier, so the digest check
        //alone would silently regress the recorded commitment — reject it by frontier order instead.
        if(Commitment is { } prior && committed.Frontier.Compare(prior.Frontier) is Causality.Before or Causality.Concurrent)
        {
            throw new InvalidOperationException("The committed seal sits behind or concurrent to the commitment this container already recorded; committed seals apply in chain order.");
        }

        ImmutableArray<SequenceCheckpointEntry<TValue>> projection = Context.CertifyProjection(Live, committed.Frontier);
        ReadOnlyMemory<byte> digest = ComputeDigest(CanonicalizeCheckpoint(projection));
        if(!digest.Span.SequenceEqual(committed.Digest.Span))
        {
            throw new InvalidOperationException("This member's certified projection at the committed frontier does not match the committed digest; the state has diverged or the commitment is forged.");
        }

        //Probe AFTER the digest check: the digest check runs first because a mismatch is divergence or
        //forgery — the graver diagnosis — and the certified projection is quiescence-independent, so the
        //digest check's meaning does not depend on the probe's outcome. With the digest verified, a
        //non-empty probe at the committed frontier means this member holds in-flight inserts the frontier can
        //never cover, so it fails closed and recovers by wholesale adoption rather than applying.
        if(Context.UnstableInserts is { } unstableInserts && !unstableInserts(Live, committed.Frontier).IsEmpty)
        {
            throw new InvalidOperationException("This member holds in-flight inserts the committed frontier can never cover, so it cannot apply this seal. Recover by wholesale adoption: adopt a healthy member's full sequence state with Adopt, inherit the current checkpoint and commitment by MERGING containers with that member — the higher-ballot arm hands them over — re-apply this member's in-flight edits as fresh inserts, and be verified by the NEXT committed seal it applies. Do not expect ApplyCommittedSeal at the current commitment to verify a post-compaction donor: a base-changing compaction re-identifies converted elements, so the current commitment's digest, computed over the pre-compaction projection, can never match a post-seal donor's projection.");
        }

        return new CheckpointedSequence<TSequence, TValue, TAnchor>(
            Context,
            CanonicalizeCheckpoint,
            ComputeDigest,
            Context.Compact(Live, committed.Frontier, projection),
            projection,
            committed,
            ballot);
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] CheckpointedSequence<TSequence, TValue, TAnchor>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(StrategyId, other.StrategyId, StringComparison.Ordinal)
            && EqualityComparer<TSequence>.Default.Equals(Live, other.Live)
            && Checkpoint.SequenceEqual(other.Checkpoint)
            && Equals(Commitment, other.Commitment)
            && Nullable.Equals(CheckpointBallot, other.CheckpointBallot);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CheckpointedSequence<TSequence, TValue, TAnchor> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(StrategyId, Live, Checkpoint.Length, CheckpointBallot);


    private CheckpointedSequence<TSequence, TValue, TAnchor> WithLive(TSequence live)
    {
        return new CheckpointedSequence<TSequence, TValue, TAnchor>(Context, CanonicalizeCheckpoint, ComputeDigest, live, Checkpoint, Commitment, CheckpointBallot);
    }


    private string DebuggerDisplay => $"CheckpointedSequence[{StrategyId}]: {Context.Values(Live).Count} live, checkpoint {Checkpoint.Length} @ {(CheckpointBallot?.ToString() ?? "(none)")}";
}
