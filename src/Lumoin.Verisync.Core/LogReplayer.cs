using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Replays an authenticated append-only log, emitting one <see cref="LogReplayResult{TState, TOperation, TProof}"/>
/// per entry. This is Verisync's own reader: a chain its authenticated wrapper commits is verifiable using
/// only Verisync.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TContext">The caller-defined proof validation context type.</typeparam>
/// <remarks>
/// <para>
/// The replayer holds no policy — it drives the <see cref="LogReplayContext{TState, TOperation, TProof, TContext}"/>
/// delegates in order for each entry: classify, verify chain integrity against the digest it observed from the
/// preceding entry (not the value the entry claims), validate proofs, apply. It threads the authoritative
/// previous digest forward so tampering is detectable at the point it occurs.
/// </para>
/// <para>
/// The source is an <see cref="IAsyncEnumerable{T}"/> so the same replayer handles historical replay and live
/// streaming. Replay stops when the source ends, the token is signalled, or a stage returns an error; on
/// failure the final emitted result carries a non-null <see cref="LogReplayResult{TState, TOperation, TProof}.Error"/>.
/// </para>
/// </remarks>
public sealed class LogReplayer<TState, TOperation, TProof, TContext>
{
    /// <summary>
    /// Replays <paramref name="entries"/> from genesis, emitting one result per entry.
    /// </summary>
    /// <param name="entries">The source entry stream.</param>
    /// <param name="context">The replay context supplying all delegates.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of results, one per entry, terminating on source end, cancellation, or error.</returns>
    public IAsyncEnumerable<LogReplayResult<TState, TOperation, TProof>> ReplayAsync(
        IAsyncEnumerable<LogEntry<TOperation, TProof>> entries,
        LogReplayContext<TState, TOperation, TProof, TContext> context,
        CancellationToken cancellationToken) =>
        ReplayFromAsync(entries, new EmptyLogState<TState>(), null, context, cancellationToken);


    /// <summary>
    /// Replays <paramref name="entries"/> starting from a known checkpoint state and digest.
    /// </summary>
    /// <param name="entries">The source entry stream, starting at the checkpoint.</param>
    /// <param name="startState">The log state at the checkpoint; pass <see cref="EmptyLogState{TState}"/> to start from genesis.</param>
    /// <param name="startDigest">
    /// The digest of the last entry processed before the checkpoint, or <see langword="null"/> for genesis. A digest
    /// that does not match what was recorded makes the first resumed entry fail its integrity check, so checkpoint
    /// forgery is detectable.
    /// </param>
    /// <param name="context">The replay context supplying all delegates.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An async stream of results, one per entry, terminating on source end, cancellation, or error.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="entries"/>, <paramref name="startState"/>, or <paramref name="context"/> is <see langword="null"/>.</exception>
    public async IAsyncEnumerable<LogReplayResult<TState, TOperation, TProof>> ReplayFromAsync(
        IAsyncEnumerable<LogEntry<TOperation, TProof>> entries,
        LogState<TState> startState,
        ReadOnlyMemory<byte>? startDigest,
        LogReplayContext<TState, TOperation, TProof, TContext> context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(startState);
        ArgumentNullException.ThrowIfNull(context);

        LogState<TState> currentState = startState;
        ReadOnlyMemory<byte>? previousEntryDigest = startDigest;

        await foreach(LogEntry<TOperation, TProof> entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            LogEntryClassification classification = context.Classify(entry);

            string? integrityError = await context.VerifyChainIntegrity(entry, previousEntryDigest, cancellationToken).ConfigureAwait(false);
            if(integrityError is not null)
            {
                yield return ErrorResult(entry, currentState, classification, integrityError);
                yield break;
            }

            string? proofError = await context.ValidateProof(entry, currentState, context.ValidationContext, cancellationToken).ConfigureAwait(false);
            if(proofError is not null)
            {
                yield return ErrorResult(entry, currentState, classification, proofError);
                yield break;
            }

            (LogState<TState> nextState, string? applyError) = await context.Apply(classification, currentState, entry, cancellationToken).ConfigureAwait(false);
            if(applyError is not null)
            {
                yield return ErrorResult(entry, currentState, classification, applyError);
                yield break;
            }

            LogReplayResult<TState, TOperation, TProof> result = new()
            {
                Entry = entry,
                State = nextState,
                Classification = classification,
                Error = null
            };

            if(context.OnEntryProcessed is not null)
            {
                await context.OnEntryProcessed(result, cancellationToken).ConfigureAwait(false);
            }

            yield return result;

            currentState = nextState;
            previousEntryDigest = entry.Digest;
        }
    }


    private static LogReplayResult<TState, TOperation, TProof> ErrorResult(
        LogEntry<TOperation, TProof> entry,
        LogState<TState> state,
        LogEntryClassification classification,
        string error) =>
        new()
        {
            Entry = entry,
            State = state,
            Classification = classification,
            Error = error
        };
}
