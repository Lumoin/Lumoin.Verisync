using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A hybrid sequence container: a pluggable sequence CRDT accumulates collaborative edits and merges
/// between checkpoints, and the converged sequence is periodically promoted into a Fast/classic CASPaxos
/// checkpoint that becomes the canonical "sequence as of ballot N" anchor.
/// </summary>
/// <typeparam name="TSequence">The sequence CRDT state type.</typeparam>
/// <typeparam name="TValue">The element type.</typeparam>
/// <typeparam name="TAnchor">The stable addressing type elements are referred to by.</typeparam>
/// <remarks>
/// <para>
/// This is the hybrid archetype: replicas exchange CRDT edits and converge coordination-free between
/// checkpoints, then one replica promotes the converged state through consensus when a canonical, ordered
/// anchor is needed. The container is an immutable value; edits and merges return new containers.
/// </para>
/// <para>
/// The sequence design itself — addressing, merge, ordering — is injected through a
/// <see cref="SequenceCrdtContext{TSequence, TValue, TAnchor}"/>, so the container owns only the
/// checkpoint protocol. The strategy is part of the document's replication contract:
/// <see cref="Merge(CheckpointedSequence{TSequence, TValue, TAnchor})"/> refuses to merge containers
/// carrying different <see cref="StrategyId"/> values, because replicas running different strategies do
/// not degrade — they silently diverge. Pin the identifier in the document's genesis entry or first seal.
/// </para>
/// <para>
/// Edits delegate to the live <see cref="Live"/> sequence.
/// <see cref="Merge(CheckpointedSequence{TSequence, TValue, TAnchor})"/> merges the live sequences for
/// convergence and keeps the checkpoint committed at the higher ballot, since checkpoints are linearized
/// by consensus. <see cref="Promote(CasPaxosRegister{CheckpointCommitment}, Ballot)"/> proposes the
/// <em>commitment</em> of the current live snapshot — its canonical-bytes digest — through the register,
/// never the snapshot itself: consensus payloads stay metadata-sized regardless of sequence length,
/// while the content travels the CRDT plane (here, inside the container as <see cref="Checkpoint"/>)
/// and is verifiable against <see cref="Commitment"/>. A commitment is consistent with its content by
/// construction at promotion; anything rehydrating a container from storage or wire must re-derive and
/// compare, the same fail-closed rule the seal codecs follow.
/// </para>
/// <para>
/// Promotion does not trim the live sequence: it retains the full edit history, including everything
/// already captured by the checkpoint, because retained anchors address later inserts and merges from
/// replicas that have not observed the checkpoint. Bounding the live structure requires waterline
/// compaction with re-anchoring below the latest sealed checkpoint — a planned per-strategy capability
/// of the sealed-segments design, not yet implemented. Until then the live sequence grows with the edit
/// history.
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
        ImmutableArray<TValue> checkpoint,
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
    /// The live, mergeable sequence. It carries the full edit history — not just edits since the last
    /// checkpoint — so that element anchors remain available as insert targets and merge inputs.
    /// </summary>
    public TSequence Live { get; }

    /// <summary>The canonical sequence content as of the last promotion, or empty before the first. Local content; consensus carries only <see cref="Commitment"/>.</summary>
    public ImmutableArray<TValue> Checkpoint { get; }

    /// <summary>The consensus-agreed commitment to <see cref="Checkpoint"/>, or <see langword="null"/> before the first promotion.</summary>
    public CheckpointCommitment? Commitment { get; }

    /// <summary>The ballot the current checkpoint was committed at, or <see langword="null"/> if none.</summary>
    public Ballot? CheckpointBallot { get; }

    /// <summary>The identifier of the sequence strategy this container operates under.</summary>
    public string StrategyId => Context.StrategyId;

    /// <summary>The visible values of the live sequence, in sequence order.</summary>
    public IReadOnlyList<TValue> Values => Context.Values(Live);


    /// <summary>
    /// Creates an empty container operating under <paramref name="context"/>: the strategy's empty
    /// sequence and no checkpoint.
    /// </summary>
    /// <param name="context">The sequence strategy.</param>
    /// <param name="canonicalizeCheckpoint">The deterministic canonical encoder for checkpoint snapshots.</param>
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

        return new CheckpointedSequence<TSequence, TValue, TAnchor>(context, canonicalizeCheckpoint, computeDigest, context.Empty, ImmutableArray<TValue>.Empty, null, null);
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
    /// <returns>The new container.</returns>
    public CheckpointedSequence<TSequence, TValue, TAnchor> Remove(TAnchor anchor)
    {
        return WithLive(Context.Remove(Live, anchor));
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
    /// Promotes the current live sequence as the canonical checkpoint under <paramref name="ballot"/>:
    /// the snapshot's <em>commitment</em> — never the snapshot — is proposed through
    /// <paramref name="register"/>, and on success the container records the snapshot locally alongside
    /// the agreed commitment.
    /// </summary>
    /// <param name="register">The CASPaxos register holding the canonical checkpoint commitments.</param>
    /// <param name="ballot">The proposing ballot.</param>
    /// <returns>The container after promotion, the register after the change, and the change outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="register"/> is <see langword="null"/>.</exception>
    public (CheckpointedSequence<TSequence, TValue, TAnchor> Sequence, CasPaxosRegister<CheckpointCommitment> Register, ChangeOutcome<CheckpointCommitment> Outcome) Promote(
        CasPaxosRegister<CheckpointCommitment> register,
        Ballot ballot)
    {
        ArgumentNullException.ThrowIfNull(register);

        ImmutableArray<TValue> snapshot = Context.Values(Live).ToImmutableArray();
        var commitment = new CheckpointCommitment(ComputeDigest(CanonicalizeCheckpoint(snapshot)));
        (CasPaxosRegister<CheckpointCommitment> nextRegister, ChangeOutcome<CheckpointCommitment> outcome) =
            register.Change(ballot, _ => commitment);

        CheckpointedSequence<TSequence, TValue, TAnchor> sequence = outcome.IsChosen
            ? new CheckpointedSequence<TSequence, TValue, TAnchor>(Context, CanonicalizeCheckpoint, ComputeDigest, Live, snapshot, outcome.Value, ballot)
            : this;

        return (sequence, nextRegister, outcome);
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


    /// <summary>
    /// Compacts the live sequence below the waterline: state both captured by the current
    /// <see cref="Checkpoint"/> and below <paramref name="stabilityFrontier"/> is reclaimed. A no-op
    /// when the strategy does not compact.
    /// </summary>
    /// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>.</param>
    /// <returns>The container with the compacted live sequence; <c>this</c> when the strategy has no compaction.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    public CheckpointedSequence<TSequence, TValue, TAnchor> Compact(VectorClock stabilityFrontier)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);
        if(Context.Compact is null)
        {
            return this;
        }

        return WithLive(Context.Compact(Live, stabilityFrontier, Checkpoint));
    }


    private CheckpointedSequence<TSequence, TValue, TAnchor> WithLive(TSequence live)
    {
        return new CheckpointedSequence<TSequence, TValue, TAnchor>(Context, CanonicalizeCheckpoint, ComputeDigest, live, Checkpoint, Commitment, CheckpointBallot);
    }


    private string DebuggerDisplay => $"CheckpointedSequence[{StrategyId}]: {Context.Values(Live).Count} live, checkpoint {Checkpoint.Length} @ {(CheckpointBallot?.ToString() ?? "(none)")}";
}
