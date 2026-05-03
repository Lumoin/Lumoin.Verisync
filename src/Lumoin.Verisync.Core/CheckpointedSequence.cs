using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A hybrid sequence container: an <see cref="Rga{TValue}"/> accumulates collaborative edits and merges
/// between checkpoints, and the converged sequence is periodically promoted into a Fast/classic CASPaxos
/// checkpoint that becomes the canonical "sequence as of ballot N" anchor.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <remarks>
/// <para>
/// This is the hybrid archetype: replicas exchange CRDT edits and converge coordination-free between
/// checkpoints, then one replica promotes the converged state through consensus when a canonical, ordered
/// anchor is needed. The container is an immutable value; edits and merges return new containers.
/// </para>
/// <para>
/// Edits delegate to the live <see cref="Live"/> array. <see cref="Merge(CheckpointedSequence{TValue})"/>
/// merges the live arrays for convergence and keeps the checkpoint committed at the higher ballot, since
/// checkpoints are linearized by consensus. <see cref="Promote(CasPaxosRegister{ImmutableArray{TValue}}, Ballot)"/>
/// commits the current live sequence into a register; edits made after a promotion remain in the live array
/// until the next promotion.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class CheckpointedSequence<TValue>: IEquatable<CheckpointedSequence<TValue>>
{
    private CheckpointedSequence(Rga<TValue> live, ImmutableArray<TValue> checkpoint, Ballot? checkpointBallot)
    {
        Live = live;
        Checkpoint = checkpoint;
        CheckpointBallot = checkpointBallot;
    }


    /// <summary>An empty container: an empty live array and no checkpoint.</summary>
    public static CheckpointedSequence<TValue> Empty { get; } = new(Rga<TValue>.Empty, ImmutableArray<TValue>.Empty, null);


    /// <summary>The live, mergeable sequence accumulating edits since the last checkpoint.</summary>
    public Rga<TValue> Live { get; }

    /// <summary>The canonical sequence as of the last promotion, or empty before the first.</summary>
    public ImmutableArray<TValue> Checkpoint { get; }

    /// <summary>The ballot the current checkpoint was committed at, or <see langword="null"/> if none.</summary>
    public Ballot? CheckpointBallot { get; }


    /// <summary>Inserts <paramref name="value"/> at the head of the live sequence.</summary>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new container and the identity assigned to the inserted element.</returns>
    public (CheckpointedSequence<TValue> Sequence, Dot InsertedId) InsertAtHead(TValue value, ReplicaId replica)
    {
        (Rga<TValue> live, Dot id) = Live.InsertAtHead(value, replica);

        return (new CheckpointedSequence<TValue>(live, Checkpoint, CheckpointBallot), id);
    }


    /// <summary>Inserts <paramref name="value"/> after <paramref name="after"/> in the live sequence.</summary>
    /// <param name="after">The identity of the element to insert after.</param>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new container and the identity assigned to the inserted element.</returns>
    public (CheckpointedSequence<TValue> Sequence, Dot InsertedId) InsertAfter(Dot after, TValue value, ReplicaId replica)
    {
        (Rga<TValue> live, Dot id) = Live.InsertAfter(after, value, replica);

        return (new CheckpointedSequence<TValue>(live, Checkpoint, CheckpointBallot), id);
    }


    /// <summary>Removes the element identified by <paramref name="id"/> from the live sequence.</summary>
    /// <param name="id">The identity of the element to remove.</param>
    /// <returns>The new container.</returns>
    public CheckpointedSequence<TValue> Remove(Dot id)
    {
        return new CheckpointedSequence<TValue>(Live.Remove(id), Checkpoint, CheckpointBallot);
    }


    /// <summary>
    /// Merges the live sequences for convergence and keeps the checkpoint committed at the higher ballot.
    /// </summary>
    /// <param name="other">The container to merge with.</param>
    /// <returns>A new container; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public CheckpointedSequence<TValue> Merge(CheckpointedSequence<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Rga<TValue> mergedLive = Live.Merge(other.Live);
        bool keepThis = CheckpointBallot is { } mine
            && (other.CheckpointBallot is not { } theirs || mine >= theirs);

        return keepThis
            ? new CheckpointedSequence<TValue>(mergedLive, Checkpoint, CheckpointBallot)
            : new CheckpointedSequence<TValue>(mergedLive, other.Checkpoint, other.CheckpointBallot);
    }


    /// <summary>
    /// Promotes the current live sequence into <paramref name="register"/> as the canonical checkpoint under
    /// <paramref name="ballot"/>.
    /// </summary>
    /// <param name="register">The CASPaxos register holding the canonical sequence snapshots.</param>
    /// <param name="ballot">The proposing ballot.</param>
    /// <returns>The container after promotion, the register after the change, and the change outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="register"/> is <see langword="null"/>.</exception>
    public (CheckpointedSequence<TValue> Sequence, CasPaxosRegister<ImmutableArray<TValue>> Register, ChangeOutcome<ImmutableArray<TValue>> Outcome) Promote(
        CasPaxosRegister<ImmutableArray<TValue>> register,
        Ballot ballot)
    {
        ArgumentNullException.ThrowIfNull(register);

        ImmutableArray<TValue> snapshot = Live.Values.ToImmutableArray();
        (CasPaxosRegister<ImmutableArray<TValue>> nextRegister, ChangeOutcome<ImmutableArray<TValue>> outcome) =
            register.Change(ballot, _ => snapshot);

        CheckpointedSequence<TValue> sequence = outcome.IsChosen
            ? new CheckpointedSequence<TValue>(Live, outcome.Value, ballot)
            : this;

        return (sequence, nextRegister, outcome);
    }


    /// <inheritdoc/>
    public bool Equals(CheckpointedSequence<TValue>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Live.Equals(other.Live)
            && Checkpoint.SequenceEqual(other.Checkpoint)
            && Nullable.Equals(CheckpointBallot, other.CheckpointBallot);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CheckpointedSequence<TValue> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Live, Checkpoint.Length, CheckpointBallot);


    private string DebuggerDisplay => $"CheckpointedSequence: {Live.Count} live, checkpoint {Checkpoint.Length} @ {(CheckpointBallot?.ToString() ?? "(none)")}";
}
