using System;
using System.Collections.Generic;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The result of processing a single <see cref="LogEntry{TOperation, TProof}"/> during replay.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <remarks>
/// The replayer emits one result per entry. When <see cref="Error"/> is non-null the stream has terminated:
/// <see cref="State"/> reflects the last successfully applied state and <see cref="Entry"/> is the failing entry.
/// </remarks>
public sealed class LogReplayResult<TState, TOperation, TProof>: IEquatable<LogReplayResult<TState, TOperation, TProof>>
{
    /// <summary>The entry that was processed to produce this result.</summary>
    public required LogEntry<TOperation, TProof> Entry { get; init; }

    /// <summary>The log state after the entry was applied, or the last applied state when <see cref="Error"/> is non-null.</summary>
    public required LogState<TState> State { get; init; }

    /// <summary>The classification of <see cref="Entry"/>.</summary>
    public required LogEntryClassification Classification { get; init; }

    /// <summary>The error message when processing failed, or <see langword="null"/> on success.</summary>
    public required string? Error { get; init; }

    /// <summary>Whether this result represents successful processing.</summary>
    public bool IsSuccess => Error is null;


    /// <inheritdoc/>
    public bool Equals(LogReplayResult<TState, TOperation, TProof>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Entry == other.Entry
            && EqualityComparer<LogState<TState>>.Default.Equals(State, other.State)
            && Classification == other.Classification
            && string.Equals(Error, other.Error, StringComparison.Ordinal);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LogReplayResult<TState, TOperation, TProof> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Entry, State, Classification, Error);


    /// <summary>Determines whether two results are equal.</summary>
    public static bool operator ==(LogReplayResult<TState, TOperation, TProof>? left, LogReplayResult<TState, TOperation, TProof>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two results are not equal.</summary>
    public static bool operator !=(LogReplayResult<TState, TOperation, TProof>? left, LogReplayResult<TState, TOperation, TProof>? right) =>
        !(left == right);
}
