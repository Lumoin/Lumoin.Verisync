namespace Lumoin.Verisync.Core;

/// <summary>
/// Classifies a <see cref="LogEntry{TOperation, TProof}"/> by inspecting it and returning a
/// <see cref="LogEntryClassification"/>.
/// </summary>
/// <typeparam name="TOperation">The domain operation type.</typeparam>
/// <typeparam name="TProof">The proof type.</typeparam>
/// <param name="entry">The entry to classify.</param>
/// <returns>The classification of the entry, which drives which branch of the apply logic runs.</returns>
/// <remarks>
/// This is the writer's copy of the classification delegate. It is shape-aligned with its sibling project
/// event-log reader so a chain the writer commits is replayable by the reader.
/// </remarks>
public delegate LogEntryClassification ClassifyOperationDelegate<TOperation, TProof>(LogEntry<TOperation, TProof> entry);
