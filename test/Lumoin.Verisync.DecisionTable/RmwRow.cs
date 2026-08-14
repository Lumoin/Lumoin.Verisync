namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One read-modify-write configuration's row: the measured row every other part of the harness already reads,
/// beside the columns only a read-modify-write workload has.
/// </summary>
/// <param name="Row">The measured row, whose latency columns are each writer's own change committing and whose agreement column is the fold oracle.</param>
/// <param name="ConflictRetryRate">
/// The fraction of writes that had to re-propose because another writer's write got in first. THIS IS THE
/// COLUMN THE WORKLOAD GATE READS, and the two arms count different events for it because the two protocols
/// re-propose for different reasons: a QuePaxa write recomputes against the winner and runs another consensus
/// instance, while a Fast CASPaxos write absorbs the winner inside the round it is already running and pays
/// another round only when its ballot was pre-empted.
/// </param>
/// <param name="MeanConflictRetries">The mean number of those re-proposals per write, which is where contention compounds rather than merely occurring.</param>
/// <param name="RecomposedRate">The fraction of writes whose change function ran against a value another writer had already committed.</param>
/// <param name="ApplyOnceRate">
/// The fraction of writes whose change function found this writer's own change already applied. It is the
/// observable of the semantic difference: Fast CASPaxos recovers a writer's own partially accepted value back
/// into that writer's own round whenever its blind round was split, where QuePaxa discards a superseded
/// proposal whole and reaches the same state only through an attempt that decided nothing and was carried by
/// another proposer afterwards.
/// </param>
/// <param name="Censored">How many writes spent their attempt budget without their own change committing.</param>
/// <param name="FoldBreaches">How many trials the correctness oracle rejected, which is what the row's agreement column reports as a gate.</param>
/// <param name="SampleFinalValue">The value the replicas held at the end of the row's last trial, printed so that the fold is legible rather than only summarised.</param>
/// <remarks>
/// The measured row is carried whole rather than extended, so the verdict reducer reads exactly the record it
/// reads everywhere else and the retry rate reaches it through the gate's own delegate. A row type the reducer
/// had to know about would make the gate's input a special case of the reduction rather than an input to it.
/// </remarks>
internal sealed record RmwRow(
    MeasuredRow Row,
    double ConflictRetryRate,
    double MeanConflictRetries,
    double RecomposedRate,
    double ApplyOnceRate,
    int Censored,
    int FoldBreaches,
    string SampleFinalValue);
