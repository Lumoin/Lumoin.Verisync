using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Groups the reader-side delegates and configuration required to replay an authenticated log: classify each
/// entry, verify its chain integrity, validate its proofs, and apply it to the running state.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TContext">The caller-defined proof validation context type.</typeparam>
/// <remarks>
/// This is the read-side counterpart to the writer's <see cref="LogCommitContext{TState, TOperation, TProof, TContext, TAccumulator}"/>.
/// It shares the classify / verify-integrity / validate-proof / apply delegate shapes so a chain the writer
/// committed replays here unchanged; it has no fold step (the reader does not accumulate) and adds an optional
/// per-entry notification hook.
/// </remarks>
public sealed class LogReplayContext<TState, TOperation, TProof, TContext>
{
    /// <summary>The delegate that classifies each entry before dispatch.</summary>
    public required ClassifyOperationDelegate<TOperation, TProof> Classify { get; init; }

    /// <summary>The delegate that verifies chain integrity against the previous entry's digest.</summary>
    public required VerifyChainIntegrityDelegate<TOperation, TProof> VerifyChainIntegrity { get; init; }

    /// <summary>The delegate that validates the proofs carried by each entry.</summary>
    public required ValidateProofDelegate<TState, TOperation, TProof, TContext> ValidateProof { get; init; }

    /// <summary>The caller-defined context passed to <see cref="ValidateProof"/>.</summary>
    public required TContext ValidationContext { get; init; }

    /// <summary>The delegate that applies a classified entry to the current state.</summary>
    public required ApplyDelegate<TState, TOperation, TProof> Apply { get; init; }

    /// <summary>An optional hook called after each entry is processed successfully, or <see langword="null"/> when none is attached.</summary>
    public OnEntryProcessedDelegate<TState, TOperation, TProof>? OnEntryProcessed { get; init; }

    /// <summary>The time source used for temporal validation during replay.</summary>
    public required TimeProvider TimeProvider { get; init; }
}
