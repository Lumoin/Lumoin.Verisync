using System;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Groups the writer-side delegates and configuration required to commit one operation into an
/// authenticated log: classify the operation, verify chain integrity, validate the proof, apply the
/// operation, and fold it into the accumulator.
/// </summary>
/// <typeparam name="TState">The domain state type.</typeparam>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <typeparam name="TContext">The caller-defined proof validation context type.</typeparam>
/// <typeparam name="TAccumulator">The accumulator type.</typeparam>
/// <remarks>
/// <para>
/// This is the commit-side counterpart to the reader's replay context. The reader iterates a chain and
/// verifies it; the writer commits one step — validate, apply, extend, fold — and the
/// <see cref="FoldStep"/> delegate is the slot the reader does not have. Both sides share the
/// classify / verify-integrity / validate-proof / apply shapes so a committed chain is replayable.
/// </para>
/// <para>
/// The delegates form a coherent behavioural unit, so grouping them in a single immutable context makes
/// the caller's intent explicit and lets one instance serve many commits. <see cref="TimeProvider"/> is
/// a first-class member rather than an ambient dependency so temporal validation is testable without
/// touching the system clock.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1005:Avoid excessive parameters on generic types", Justification = "The five type parameters (state, operation, proof, validation context, accumulator) are the irreducible axes of the layered authenticated-wrapper design; collapsing any of them would erase a distinction the architecture requires.")]
public sealed class LogCommitContext<TState, TOperation, TProof, TContext, TAccumulator>
{
    /// <summary>The delegate that classifies an operation before it is committed.</summary>
    public required ClassifyOperationDelegate<TOperation, TProof> Classify { get; init; }

    /// <summary>The delegate that verifies chain integrity against the previous committed digest.</summary>
    public required VerifyChainIntegrityDelegate<TOperation, TProof> VerifyChainIntegrity { get; init; }

    /// <summary>The delegate that validates the proofs carried by the entry.</summary>
    public required ValidateProofDelegate<TState, TOperation, TProof, TContext> ValidateProof { get; init; }

    /// <summary>The delegate that applies the operation to the current state.</summary>
    public required ApplyDelegate<TState, TOperation, TProof> Apply { get; init; }

    /// <summary>The delegate that folds the committed entry into the accumulator.</summary>
    public required FoldStepDelegate<TOperation, TProof, TAccumulator> FoldStep { get; init; }

    /// <summary>The caller-defined context passed to <see cref="ValidateProof"/>.</summary>
    public required TContext ValidationContext { get; init; }

    /// <summary>The time source used for temporal validation during commit.</summary>
    public required TimeProvider TimeProvider { get; init; }
}
