using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one read-modify-write trial produced: what every writer's change cost, what the replicas were left
/// holding, and whether that value is the fold of the changes that committed.
/// </summary>
/// <typeparam name="TWriter">The arm's per-writer measurement.</typeparam>
/// <param name="Writers">One measurement per writer, in writer order.</param>
/// <param name="FinalValue">The value the replicas hold when the trial has drained, or <see langword="null"/> when nothing was ever committed.</param>
/// <param name="Fold">What the correctness oracle found.</param>
/// <remarks>
/// THE FINAL VALUE IS READ FROM THE REPLICAS AND NEVER FROM A CLIENT. A client reports what it believes it
/// committed, and an oracle fed that belief would agree with the protocol by construction. Reading the hosts'
/// own state instead makes the oracle an independent observer of the same trial.
/// </remarks>
internal sealed record RmwTrialOutcome<TWriter>(
    ImmutableArray<TWriter> Writers,
    string? FinalValue,
    RmwFoldVerdict Fold);
