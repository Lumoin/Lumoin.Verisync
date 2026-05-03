namespace Lumoin.Verisync.Core;

/// <summary>
/// The outcome of an attempt to commit one operation through the authenticated register wrapper.
/// </summary>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="IsCommitted">
/// <see langword="true"/> if the operation passed every commit-pipeline stage and the entry was committed;
/// <see langword="false"/> if a stage rejected it.
/// </param>
/// <param name="Entry">The committed entry when <see cref="IsCommitted"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
/// <param name="Error">The error reported by the rejecting stage when <see cref="IsCommitted"/> is <see langword="false"/>; otherwise <see langword="null"/>.</param>
public sealed record CommitResult<TOperation, TProof>(bool IsCommitted, LogEntry<TOperation, TProof>? Entry, string? Error);
