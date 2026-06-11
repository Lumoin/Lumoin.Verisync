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

        Assert.HasCount(0, results);
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
}
