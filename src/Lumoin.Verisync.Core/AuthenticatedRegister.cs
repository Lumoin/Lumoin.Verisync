using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The authenticated register wrapper (Layer 2): an immutable value holding the application state and the
/// cryptographic accumulator, that commits one operation at a time by driving the commit pipeline and
/// extending a tamper-evident chain.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TContext">The caller-defined proof validation context type.</typeparam>
/// <typeparam name="TAccumulator">The accumulator type.</typeparam>
/// <remarks>
/// <para>
/// The wrapper knows the value's shape — <c>(state, accumulator)</c> — but nothing about the authorisation
/// model or the fold realisation; those are the injected delegates in the <see cref="LogCommitContext{TState, TOperation, TProof, TContext, TAccumulator}"/>.
/// A commit builds the next entry (canonicalize, digest), classifies it, verifies chain integrity, validates
/// its proofs, applies it to the state, and folds it into the accumulator. If any stage rejects the entry the
/// register is returned unchanged and the result carries the error.
/// </para>
/// <para>
/// Linearizing concurrent proposals is the consensus layer's job; this wrapper is the per-step writer that
/// sits above it. Canonicalization and digest computation are injected so the serialization and hashing
/// boundaries stay outside the core.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
[SuppressMessage("Design", "CA1005:Avoid excessive parameters on generic types", Justification = "The five type parameters (state, operation, proof, validation context, accumulator) are the irreducible axes of the layered authenticated-wrapper design.")]
public sealed class AuthenticatedRegister<TState, TOperation, TProof, TContext, TAccumulator>
{
    private LogCommitContext<TState, TOperation, TProof, TContext, TAccumulator> Context { get; }
    private CanonicalizeEntryDelegate<TOperation, TProof> Canonicalize { get; }
    private ComputeDigestDelegate ComputeDigest { get; }


    private AuthenticatedRegister(
        LogCommitContext<TState, TOperation, TProof, TContext, TAccumulator> context,
        CanonicalizeEntryDelegate<TOperation, TProof> canonicalize,
        ComputeDigestDelegate computeDigest,
        LogState<TState> state,
        TAccumulator accumulator,
        ReadOnlyMemory<byte>? headDigest,
        ulong nextIndex)
    {
        Context = context;
        Canonicalize = canonicalize;
        ComputeDigest = computeDigest;
        State = state;
        Accumulator = accumulator;
        HeadDigest = headDigest;
        NextIndex = nextIndex;
    }


    /// <summary>The current log state — <see cref="EmptyLogState{TState}"/> before genesis.</summary>
    public LogState<TState> State { get; }

    /// <summary>The current accumulator value.</summary>
    public TAccumulator Accumulator { get; }

    /// <summary>The digest of the most recently committed entry, or <see langword="null"/> before genesis.</summary>
    public ReadOnlyMemory<byte>? HeadDigest { get; }

    /// <summary>The index the next committed entry will take.</summary>
    public ulong NextIndex { get; }


    /// <summary>
    /// Creates an empty authenticated register positioned before the genesis entry.
    /// </summary>
    /// <param name="context">The commit pipeline delegates.</param>
    /// <param name="canonicalize">The canonical-bytes encoder for entry content.</param>
    /// <param name="computeDigest">The digest function over canonical bytes.</param>
    /// <param name="initialAccumulator">The accumulator's seed value.</param>
    /// <returns>A new register with an <see cref="EmptyLogState{TState}"/> state and index zero.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="context"/>, <paramref name="canonicalize"/>, or <paramref name="computeDigest"/> is <see langword="null"/>.</exception>
    public static AuthenticatedRegister<TState, TOperation, TProof, TContext, TAccumulator> Create(
        LogCommitContext<TState, TOperation, TProof, TContext, TAccumulator> context,
        CanonicalizeEntryDelegate<TOperation, TProof> canonicalize,
        ComputeDigestDelegate computeDigest,
        TAccumulator initialAccumulator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(canonicalize);
        ArgumentNullException.ThrowIfNull(computeDigest);

        return new AuthenticatedRegister<TState, TOperation, TProof, TContext, TAccumulator>(
            context, canonicalize, computeDigest, new EmptyLogState<TState>(), initialAccumulator, null, 0);
    }


    /// <summary>
    /// Attempts to commit <paramref name="operation"/> with <paramref name="proofs"/> as the next entry.
    /// </summary>
    /// <param name="operation">The operation to commit, or <see langword="null"/> for a heartbeat entry.</param>
    /// <param name="proofs">The proofs authorizing the entry.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The register after the attempt and the commit result.</returns>
    public async ValueTask<(AuthenticatedRegister<TState, TOperation, TProof, TContext, TAccumulator> Register, CommitResult<TOperation, TProof> Result)> CommitAsync(
        TOperation? operation,
        ImmutableArray<TProof> proofs,
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> canonicalBytes = Canonicalize(NextIndex, HeadDigest, operation, proofs);
        ReadOnlyMemory<byte> digest = ComputeDigest(canonicalBytes);

        LogEntry<TOperation, TProof> entry = new()
        {
            Index = NextIndex,
            PreviousDigest = HeadDigest,
            Digest = digest,
            CanonicalBytes = canonicalBytes,
            Operation = operation,
            Proofs = proofs
        };

        LogEntryClassification classification = Context.Classify(entry);

        string? integrityError = await Context.VerifyChainIntegrity(entry, HeadDigest, cancellationToken).ConfigureAwait(false);
        if(integrityError is not null)
        {
            return (this, new CommitResult<TOperation, TProof>(false, null, integrityError));
        }

        string? proofError = await Context.ValidateProof(entry, State, Context.ValidationContext, cancellationToken).ConfigureAwait(false);
        if(proofError is not null)
        {
            return (this, new CommitResult<TOperation, TProof>(false, null, proofError));
        }

        (LogState<TState> newState, string? applyError) = await Context.Apply(classification, State, entry, cancellationToken).ConfigureAwait(false);
        if(applyError is not null)
        {
            return (this, new CommitResult<TOperation, TProof>(false, null, applyError));
        }

        TAccumulator newAccumulator = await Context.FoldStep(entry, Accumulator, cancellationToken).ConfigureAwait(false);

        var committed = new AuthenticatedRegister<TState, TOperation, TProof, TContext, TAccumulator>(
            Context, Canonicalize, ComputeDigest, newState, newAccumulator, digest, NextIndex + 1);

        return (committed, new CommitResult<TOperation, TProof>(true, entry, null));
    }


    private string DebuggerDisplay => $"AuthenticatedRegister: index {NextIndex}, state {State.GetType().Name}";
}
