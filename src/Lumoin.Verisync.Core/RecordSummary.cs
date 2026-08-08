namespace Lumoin.Verisync.Core;

/// <summary>
/// What a recorder answers a record request with: its step, the first proposal it took at that step, and the
/// aggregate it accumulated at the step before. This is the triple the constant-space interval summary
/// register returns.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <param name="Step">
/// The recorder's step after the record. Lemma C.2 makes it at least the requested step, because the
/// recorder advances to that step before replying, which leaves catching up as the only alternative to
/// advancing.
/// </param>
/// <param name="First">
/// The first proposal recorded at <paramref name="Step"/>, or <see langword="null"/> for a register that was
/// never written. A register above step zero always holds one.
/// </param>
/// <param name="PriorAggregate">
/// The aggregate accumulated at the step immediately below <paramref name="Step"/>, or
/// <see langword="null"/> when the recorder skipped that step.
/// </param>
/// <remarks>
/// The aggregate currently being accumulated is absent from this summary. The constant-space contract is that
/// a proposer reads an aggregate only one step after it was accumulated, and exposing the current one would
/// let a proposer read a half-formed step.
/// </remarks>
public sealed record RecordSummary<TValue>(RecorderStep Step, PrioritizedProposal<TValue>? First, PrioritizedProposal<TValue>? PriorAggregate);
