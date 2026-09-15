using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LogReplayerTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ImmutableArray<string> Proof { get; } = ["controller"];


    [TestMethod]
    public async Task RoundTripsAuthenticatedRegisterOutput()
    {
        //Commit a chain through the writer, then verify the exact entries replay cleanly through the reader.
        AuthenticatedRegister<string, string, string, string, string> writer = NewWriter();
        (AuthenticatedRegister<string, string, string, string, string> afterGenesis, CommitResult<string, string> genesis) =
            await writer.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);
        (_, CommitResult<string, string> update) =
            await afterGenesis.CommitAsync("edit", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        List<LogEntry<string, string>> chain = [genesis.Entry!, update.Entry!];
        List<LogReplayResult<string, string, string>> results = await ReplayAll(chain).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsTrue(results[0].IsSuccess);
        Assert.IsTrue(results[1].IsSuccess);
        Assert.AreEqual("create;edit", ((ActiveLogState<string>)results[1].State).Value);
    }


    [TestMethod]
    public async Task ReplayStopsOnIntegrityError()
    {
        LogEntry<string, string> genesis = MakeEntry(0, null, "create", Proof);
        LogEntry<string, string> tampered = MakeEntry(1, Encoding.UTF8.GetBytes("wrong-previous"), "edit", Proof);

        List<LogReplayResult<string, string, string>> results = await ReplayAll([genesis, tampered]).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsTrue(results[0].IsSuccess);
        Assert.IsFalse(results[1].IsSuccess);
        Assert.AreEqual("chain broken", results[1].Error);
    }


    [TestMethod]
    public async Task ReplayStopsOnInvalidProof()
    {
        LogEntry<string, string> genesis = MakeEntry(0, null, "create", Proof);
        LogEntry<string, string> unproven = MakeEntry(1, Digest(0, "create"), "edit", ImmutableArray<string>.Empty);

        List<LogReplayResult<string, string, string>> results = await ReplayAll([genesis, unproven]).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsTrue(results[0].IsSuccess);
        Assert.AreEqual("no proof", results[1].Error);
    }


    [TestMethod]
    public async Task EmptyStreamProducesNoResults()
    {
        List<LogReplayResult<string, string, string>> results = await ReplayAll([]).ConfigureAwait(false);

        Assert.IsEmpty(results);
    }


    private async Task<List<LogReplayResult<string, string, string>>> ReplayAll(List<LogEntry<string, string>> entries)
    {
        LogReplayer<string, string, string, string> replayer = new();
        LogReplayContext<string, string, string, string> context = NewReaderContext();

        var results = new List<LogReplayResult<string, string, string>>();
        await foreach(LogReplayResult<string, string, string> result in
            replayer.ReplayAsync(ToAsync(entries, TestContext.CancellationToken), context, TestContext.CancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        return results;
    }


    private static async IAsyncEnumerable<LogEntry<string, string>> ToAsync(
        List<LogEntry<string, string>> entries,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach(LogEntry<string, string> entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }


    private static LogReplayContext<string, string, string, string> NewReaderContext()
    {
        return new LogReplayContext<string, string, string, string>
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = (entry, previousEntryDigest, _) =>
                ValueTask.FromResult<string?>(NullableEqual(entry.PreviousDigest, previousEntryDigest) ? null : "chain broken"),
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            ValidationContext = "trust-anchors",
            Apply = (classification, state, entry, _) => ValueTask.FromResult(ApplyEntry(classification, state, entry)),
            TimeProvider = TimeProvider.System
        };
    }


    private static AuthenticatedRegister<string, string, string, string, string> NewWriter()
    {
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = (entry, previousEntryDigest, _) =>
                ValueTask.FromResult<string?>(NullableEqual(entry.PreviousDigest, previousEntryDigest) ? null : "chain broken"),
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult(ApplyEntry(classification, state, entry)),
            FoldStep = (entry, accumulator, _) => ValueTask.FromResult(accumulator + "|" + (entry.Operation ?? "heartbeat")),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };

        CanonicalizeEntryDelegate<string, string> canonicalize = (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;

        return AuthenticatedRegister<string, string, string, string, string>.Create(context, canonicalize, computeDigest, "seed");
    }


    private static LogEntry<string, string> MakeEntry(ulong index, ReadOnlyMemory<byte>? previousDigest, string operation, ImmutableArray<string> proofs)
    {
        return new LogEntry<string, string>
        {
            Index = index,
            PreviousDigest = previousDigest,
            Digest = Digest(index, operation),
            CanonicalBytes = Digest(index, operation),
            Operation = operation,
            Proofs = proofs
        };
    }


    private static byte[] Digest(ulong index, string operation) => Encoding.UTF8.GetBytes($"{index}:{operation}");


    private static (LogState<string> State, string? Error) ApplyEntry(LogEntryClassification classification, LogState<string> state, LogEntry<string, string> entry)
    {
        if(classification == LogEntryClassification.Genesis)
        {
            return (new ActiveLogState<string>(entry.Operation!), null);
        }

        if(state is ActiveLogState<string> active)
        {
            string next = entry.Operation is null ? active.Value : active.Value + ";" + entry.Operation;

            return (new ActiveLogState<string>(next), null);
        }

        return (state, "register is not active");
    }


    private static bool NullableEqual(ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right)
    {
        if(left is null && right is null)
        {
            return true;
        }

        if(left is null || right is null)
        {
            return false;
        }

        return left.Value.Span.SequenceEqual(right.Value.Span);
    }


    /// <summary>
    /// Pins that enumerating <see cref="LogReplayer{TState,TOperation,TProof,TContext}.ReplayFromAsync"/> throws
    /// <see cref="ArgumentNullException"/> naming "entries" when the entry source is <see langword="null"/>. The
    /// check only fires once enumeration starts, since the method is a `yield`-based async iterator.
    /// </summary>
    [TestMethod]
    public async Task ReplayFromThrowsWhenEntriesIsNull()
    {
        LogReplayer<string, string, string, string> replayer = new();
        LogReplayContext<string, string, string, string> context = NewReaderContext();

        IAsyncEnumerable<LogReplayResult<string, string, string>> stream = replayer.ReplayFromAsync(
            null!, new EmptyLogState<string>(), null, context, TestContext.CancellationToken);

        ArgumentNullException exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
        {
            await foreach(LogReplayResult<string, string, string> _ in stream.WithCancellation(TestContext.CancellationToken).ConfigureAwait(false))
            {
            }
        }).ConfigureAwait(false);

        Assert.AreEqual("entries", exception.ParamName);
    }


    /// <summary>
    /// Pins that <see cref="LogReplayContext{TState,TOperation,TProof,TContext}.OnEntryProcessed"/>, when set, is
    /// invoked once per successfully processed entry with that entry's own result.
    /// </summary>
    [TestMethod]
    public async Task ReplayInvokesOnEntryProcessedForEachSuccessfulEntry()
    {
        LogEntry<string, string> genesis = MakeEntry(0, null, "create", Proof);
        LogEntry<string, string> update = MakeEntry(1, Digest(0, "create"), "edit", Proof);

        List<LogReplayResult<string, string, string>> notified = [];
        LogReplayContext<string, string, string, string> context = new()
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = (entry, previousEntryDigest, _) =>
                ValueTask.FromResult<string?>(NullableEqual(entry.PreviousDigest, previousEntryDigest) ? null : "chain broken"),
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            ValidationContext = "trust-anchors",
            Apply = (classification, state, entry, _) => ValueTask.FromResult(ApplyEntry(classification, state, entry)),
            OnEntryProcessed = (result, _) =>
            {
                notified.Add(result);

                return ValueTask.CompletedTask;
            },
            TimeProvider = TimeProvider.System
        };

        LogReplayer<string, string, string, string> replayer = new();
        List<LogReplayResult<string, string, string>> results = [];
        await foreach(LogReplayResult<string, string, string> result in
            replayer.ReplayAsync(ToAsync([genesis, update], TestContext.CancellationToken), context, TestContext.CancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        Assert.HasCount(2, results);
        Assert.HasCount(2, notified);
        Assert.AreSame(results[0], notified[0]);
        Assert.AreSame(results[1], notified[1]);
    }


    /// <summary>
    /// Pins that enumerating <see cref="LogReplayer{TState,TOperation,TProof,TContext}.ReplayFromAsync"/> throws
    /// <see cref="ArgumentNullException"/> naming "startState" when the checkpoint state is <see langword="null"/>.
    /// </summary>
    [TestMethod]
    public async Task ReplayFromThrowsWhenStartStateIsNull()
    {
        LogReplayer<string, string, string, string> replayer = new();
        LogReplayContext<string, string, string, string> context = NewReaderContext();

        IAsyncEnumerable<LogReplayResult<string, string, string>> stream = replayer.ReplayFromAsync(
            ToAsync([], TestContext.CancellationToken), null!, null, context, TestContext.CancellationToken);

        ArgumentNullException exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
        {
            await foreach(LogReplayResult<string, string, string> _ in stream.WithCancellation(TestContext.CancellationToken).ConfigureAwait(false))
            {
            }
        }).ConfigureAwait(false);

        Assert.AreEqual("startState", exception.ParamName);
    }


    /// <summary>
    /// Pins that when <see cref="LogReplayContext{TState,TOperation,TProof,TContext}.Apply"/> reports an error,
    /// replay yields exactly one error result for the failing entry and then stops immediately — no further
    /// entries are processed and no extra success result is produced for the failing entry.
    /// </summary>
    [TestMethod]
    public async Task ReplayStopsOnApplyError()
    {
        LogEntry<string, string> genesis = MakeEntry(0, null, "create", Proof);
        LogEntry<string, string> failing = MakeEntry(1, Digest(0, "create"), "fail", Proof);

        LogReplayContext<string, string, string, string> context = new()
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = (entry, previousEntryDigest, _) =>
                ValueTask.FromResult<string?>(NullableEqual(entry.PreviousDigest, previousEntryDigest) ? null : "chain broken"),
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            ValidationContext = "trust-anchors",
            Apply = (classification, state, entry, _) =>
                ValueTask.FromResult(entry.Operation == "fail"
                    ? (state, "apply failed")
                    : ApplyEntry(classification, state, entry)),
            TimeProvider = TimeProvider.System
        };

        LogReplayer<string, string, string, string> replayer = new();
        List<LogReplayResult<string, string, string>> results = [];
        await foreach(LogReplayResult<string, string, string> result in
            replayer.ReplayAsync(ToAsync([genesis, failing], TestContext.CancellationToken), context, TestContext.CancellationToken).ConfigureAwait(false))
        {
            results.Add(result);
        }

        Assert.HasCount(2, results);
        Assert.IsTrue(results[0].IsSuccess);
        Assert.IsFalse(results[1].IsSuccess);
        Assert.AreEqual("apply failed", results[1].Error);
    }


    /// <summary>
    /// Pins that enumerating <see cref="LogReplayer{TState,TOperation,TProof,TContext}.ReplayFromAsync"/> throws
    /// <see cref="ArgumentNullException"/> naming "context" when the replay context is <see langword="null"/>.
    /// </summary>
    [TestMethod]
    public async Task ReplayFromThrowsWhenContextIsNull()
    {
        LogReplayer<string, string, string, string> replayer = new();

        IAsyncEnumerable<LogReplayResult<string, string, string>> stream = replayer.ReplayFromAsync(
            ToAsync([], TestContext.CancellationToken), new EmptyLogState<string>(), null, null!, TestContext.CancellationToken);

        ArgumentNullException exception = await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
        {
            await foreach(LogReplayResult<string, string, string> _ in stream.WithCancellation(TestContext.CancellationToken).ConfigureAwait(false))
            {
            }
        }).ConfigureAwait(false);

        Assert.AreEqual("context", exception.ParamName);
    }
}
