using System;
using System.Collections.Immutable;
using System.Text;
using System.Threading.Tasks;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class AuthenticatedRegisterTests
{
    public TestContext TestContext { get; set; } = null!;


    private static ImmutableArray<string> Proof { get; } = ["controller"];


    [TestMethod]
    public async Task GenesisCommitActivatesState()
    {
        AuthenticatedRegister<string, string, string, string, string> register = NewRegister();

        (AuthenticatedRegister<string, string, string, string, string> committed, CommitResult<string, string> result) =
            await register.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(result.IsCommitted);
        Assert.IsInstanceOfType<ActiveLogState<string>>(committed.State);
        Assert.AreEqual("create", ((ActiveLogState<string>)committed.State).Value);
        Assert.AreEqual(1UL, committed.NextIndex);
        Assert.IsNotNull(committed.HeadDigest);
    }


    [TestMethod]
    public async Task SecondCommitChainsToFirst()
    {
        AuthenticatedRegister<string, string, string, string, string> register = NewRegister();
        (AuthenticatedRegister<string, string, string, string, string> afterGenesis, _) =
            await register.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        (AuthenticatedRegister<string, string, string, string, string> afterUpdate, CommitResult<string, string> result) =
            await afterGenesis.CommitAsync("edit", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(result.IsCommitted);
        Assert.AreEqual("create;edit", ((ActiveLogState<string>)afterUpdate.State).Value);
        Assert.AreEqual(2UL, afterUpdate.NextIndex);
        Assert.IsNotNull(result.Entry);
        Assert.IsNotNull(result.Entry!.PreviousDigest);
        Assert.IsTrue(result.Entry.PreviousDigest!.Value.Span.SequenceEqual(afterGenesis.HeadDigest!.Value.Span));
    }


    [TestMethod]
    public async Task AccumulatorFoldsAcrossCommits()
    {
        AuthenticatedRegister<string, string, string, string, string> register = NewRegister();
        (AuthenticatedRegister<string, string, string, string, string> afterGenesis, _) =
            await register.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);
        (AuthenticatedRegister<string, string, string, string, string> afterUpdate, _) =
            await afterGenesis.CommitAsync("edit", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual("seed|create|edit", afterUpdate.Accumulator);
    }


    [TestMethod]
    public async Task RejectedProofLeavesRegisterUnchanged()
    {
        AuthenticatedRegister<string, string, string, string, string> register = NewRegister();

        (AuthenticatedRegister<string, string, string, string, string> after, CommitResult<string, string> result) =
            await register.CommitAsync("create", ImmutableArray<string>.Empty, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsFalse(result.IsCommitted);
        Assert.AreEqual("no proof", result.Error);
        Assert.AreEqual(0UL, after.NextIndex);
        Assert.IsInstanceOfType<EmptyLogState<string>>(after.State);
    }


    [TestMethod]
    public async Task HeartbeatCommitKeepsStateValue()
    {
        AuthenticatedRegister<string, string, string, string, string> register = NewRegister();
        (AuthenticatedRegister<string, string, string, string, string> afterGenesis, _) =
            await register.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        (AuthenticatedRegister<string, string, string, string, string> afterHeartbeat, CommitResult<string, string> result) =
            await afterGenesis.CommitAsync(null, Proof, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsTrue(result.IsCommitted);
        Assert.AreEqual("create", ((ActiveLogState<string>)afterHeartbeat.State).Value);
        Assert.AreEqual(2UL, afterHeartbeat.NextIndex);
    }


    private static AuthenticatedRegister<string, string, string, string, string> NewRegister()
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

        CanonicalizeEntryDelegate<string, string> canonicalize =
            (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;

        return AuthenticatedRegister<string, string, string, string, string>.Create(context, canonicalize, computeDigest, "seed");
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
