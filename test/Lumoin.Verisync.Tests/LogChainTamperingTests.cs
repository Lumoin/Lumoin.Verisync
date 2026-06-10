using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// Tamper-evidence tests over a real SHA-256 hash chain, with a verifier implementing the full
/// <see cref="VerifyChainIntegrityDelegate{TOperation, TProof}"/> contract: linkage against the
/// authoritative previous digest, digest recomputation over the canonical bytes, and the
/// correspondence of the canonical bytes to the typed fields replay applies.
/// </summary>
[TestClass]
internal sealed class LogChainTamperingTests
{
    public TestContext TestContext { get; set; } = null!;

    private static ImmutableArray<string> Proof { get; } = ["controller"];


    [TestMethod]
    public async Task HonestChainReplaysCleanly()
    {
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit", "close").ConfigureAwait(false);

        List<LogReplayResult<string, string, string>> results = await ReplayAll(chain).ConfigureAwait(false);

        Assert.HasCount(3, results);
        Assert.IsTrue(results[2].IsSuccess);
        Assert.AreEqual("create;edit;close", ((ActiveLogState<string>)results[2].State).Value);
    }


    [TestMethod]
    public async Task SwappingTheTypedOperationWhileKeepingBytesAndDigestIsDetected()
    {
        //The forgery the digest alone cannot see: canonical bytes and digest stay intact, only the
        //typed operation — the field replay actually applies — is replaced. The bytes-to-fields
        //correspondence check is what catches it.
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit").ConfigureAwait(false);
        chain[1] = Rebuild(chain[1], "grant-admin", chain[1].CanonicalBytes, chain[1].Digest);

        List<LogReplayResult<string, string, string>> results = await ReplayAll(chain).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsFalse(results[1].IsSuccess);
        Assert.AreEqual("canonical bytes do not match the entry fields", results[1].Error);
    }


    [TestMethod]
    public async Task TamperedCanonicalBytesAreDetected()
    {
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit").ConfigureAwait(false);
        byte[] tampered = chain[1].CanonicalBytes.ToArray();
        tampered[^1] ^= 0x01;
        chain[1] = Rebuild(chain[1], chain[1].Operation, tampered, chain[1].Digest);

        List<LogReplayResult<string, string, string>> results = await ReplayAll(chain).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsFalse(results[1].IsSuccess);
        Assert.AreEqual("digest does not match the canonical bytes", results[1].Error);
    }


    [TestMethod]
    public async Task RecomputingTheDigestOverTamperedBytesBreaksTheChainLink()
    {
        //A forger who recomputes the digest to match tampered bytes still cannot fix the next
        //entry's previous-digest link without rewriting the entire suffix.
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit", "close").ConfigureAwait(false);
        ReadOnlyMemory<byte> forgedBytes = Canonicalize(chain[1].Index, chain[0].Digest, "grant-admin", chain[1].Proofs);
        chain[1] = Rebuild(chain[1], "grant-admin", forgedBytes, SHA256.HashData(forgedBytes.Span));

        List<LogReplayResult<string, string, string>> results = await ReplayAll(chain).ConfigureAwait(false);

        //The forged entry itself verifies — it is internally consistent — but entry two's link to it fails.
        Assert.HasCount(3, results);
        Assert.IsTrue(results[1].IsSuccess);
        Assert.AreEqual("grant-admin", chain[1].Operation);
        Assert.IsFalse(results[2].IsSuccess);
        Assert.AreEqual("previous digest does not match the preceding entry", results[2].Error);
    }


    [TestMethod]
    public async Task ReorderedEntriesAreDetected()
    {
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit", "close").ConfigureAwait(false);
        (chain[1], chain[2]) = (chain[2], chain[1]);

        List<LogReplayResult<string, string, string>> results = await ReplayAll(chain).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsFalse(results[1].IsSuccess);
        Assert.AreEqual("previous digest does not match the preceding entry", results[1].Error);
    }


    [TestMethod]
    public async Task ResumeFromAnHonestCheckpointSucceeds()
    {
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit", "close").ConfigureAwait(false);

        //Resume from the state and digest recorded after the first entry, replaying only the tail.
        List<LogReplayResult<string, string, string>> results = await ReplayFrom(
            chain.GetRange(1, 2), new ActiveLogState<string>("create"), chain[0].Digest).ConfigureAwait(false);

        Assert.HasCount(2, results);
        Assert.IsTrue(results[1].IsSuccess);
        Assert.AreEqual("create;edit;close", ((ActiveLogState<string>)results[1].State).Value);
    }


    [TestMethod]
    public async Task ResumeFromAForgedCheckpointDigestIsDetected()
    {
        //The documented checkpoint-forgery guarantee: a start digest that does not match what was
        //recorded makes the first resumed entry fail its integrity check.
        List<LogEntry<string, string>> chain = await CommitChain("create", "edit", "close").ConfigureAwait(false);
        byte[] forged = chain[0].Digest.ToArray();
        forged[0] ^= 0x01;

        List<LogReplayResult<string, string, string>> results = await ReplayFrom(
            chain.GetRange(1, 2), new ActiveLogState<string>("create"), forged).ConfigureAwait(false);

        Assert.HasCount(1, results);
        Assert.IsFalse(results[0].IsSuccess);
        Assert.AreEqual("previous digest does not match the preceding entry", results[0].Error);
    }


    private async Task<List<LogEntry<string, string>>> CommitChain(params string[] operations)
    {
        AuthenticatedRegister<string, string, string, string, string> register =
            AuthenticatedRegister<string, string, string, string, string>.Create(NewWriterContext(), Canonicalize, ComputeDigest, "seed");

        var chain = new List<LogEntry<string, string>>(operations.Length);
        foreach(string operation in operations)
        {
            (register, CommitResult<string, string> result) = await register.CommitAsync(operation, Proof, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(result.IsCommitted, result.Error);
            chain.Add(result.Entry!);
        }

        return chain;
    }


    private Task<List<LogReplayResult<string, string, string>>> ReplayAll(List<LogEntry<string, string>> entries)
    {
        return ReplayFrom(entries, new EmptyLogState<string>(), null);
    }


    private async Task<List<LogReplayResult<string, string, string>>> ReplayFrom(
        List<LogEntry<string, string>> entries,
        LogState<string> startState,
        ReadOnlyMemory<byte>? startDigest)
    {
        LogReplayer<string, string, string, string> replayer = new();

        var results = new List<LogReplayResult<string, string, string>>();
        await foreach(LogReplayResult<string, string, string> result in
            replayer.ReplayFromAsync(ToAsync(entries, TestContext.CancellationToken), startState, startDigest, NewReaderContext(), TestContext.CancellationToken).ConfigureAwait(false))
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


    private static ReadOnlyMemory<byte> Canonicalize(ulong index, ReadOnlyMemory<byte>? previousDigest, string? operation, ImmutableArray<string> proofs)
    {
        string previous = previousDigest is null ? "genesis" : Convert.ToHexStringLower(previousDigest.Value.Span);
        string proofList = proofs.IsDefaultOrEmpty ? "" : string.Join(",", proofs);

        return Encoding.UTF8.GetBytes($"{index}|{previous}|{operation ?? "<heartbeat>"}|{proofList}");
    }


    private static ReadOnlyMemory<byte> ComputeDigest(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);


    /// <summary>
    /// The full verification contract: linkage, digest recomputation, and the correspondence of the
    /// canonical bytes to the typed fields — rebuilt with the <em>authoritative</em> previous digest,
    /// never the one the entry claims.
    /// </summary>
    private static ValueTask<string?> VerifyEntry(LogEntry<string, string> entry, ReadOnlyMemory<byte>? previousEntryDigest, CancellationToken cancellationToken)
    {
        if(!NullableEqual(entry.PreviousDigest, previousEntryDigest))
        {
            return ValueTask.FromResult<string?>("previous digest does not match the preceding entry");
        }

        if(!SHA256.HashData(entry.CanonicalBytes.Span).AsSpan().SequenceEqual(entry.Digest.Span))
        {
            return ValueTask.FromResult<string?>("digest does not match the canonical bytes");
        }

        ReadOnlyMemory<byte> expected = Canonicalize(entry.Index, previousEntryDigest, entry.Operation, entry.Proofs);
        if(!expected.Span.SequenceEqual(entry.CanonicalBytes.Span))
        {
            return ValueTask.FromResult<string?>("canonical bytes do not match the entry fields");
        }

        return ValueTask.FromResult<string?>(null);
    }


    private static LogCommitContext<string, string, string, string, string> NewWriterContext()
    {
        return new LogCommitContext<string, string, string, string, string>
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = VerifyEntry,
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult(ApplyEntry(classification, state, entry)),
            FoldStep = (entry, accumulator, _) => ValueTask.FromResult(accumulator + "|" + (entry.Operation ?? "heartbeat")),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };
    }


    private static LogReplayContext<string, string, string, string> NewReaderContext()
    {
        return new LogReplayContext<string, string, string, string>
        {
            Classify = entry => entry.Index == 0 ? LogEntryClassification.Genesis : LogEntryClassification.Update,
            VerifyChainIntegrity = VerifyEntry,
            ValidateProof = (entry, _, _, _) =>
                ValueTask.FromResult<string?>(entry.Proofs.IsDefaultOrEmpty ? "no proof" : null),
            ValidationContext = "trust-anchors",
            Apply = (classification, state, entry, _) => ValueTask.FromResult(ApplyEntry(classification, state, entry)),
            TimeProvider = TimeProvider.System
        };
    }


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


    private static LogEntry<string, string> Rebuild(LogEntry<string, string> entry, string? operation, ReadOnlyMemory<byte> canonicalBytes, ReadOnlyMemory<byte> digest)
    {
        return new LogEntry<string, string>
        {
            Index = entry.Index,
            PreviousDigest = entry.PreviousDigest,
            Digest = digest,
            CanonicalBytes = canonicalBytes,
            Operation = operation,
            Proofs = entry.Proofs
        };
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
