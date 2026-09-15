using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Text;

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


    /// <summary>Pins that <see cref="AuthenticatedRegister{TState, TOperation, TProof, TContext, TAccumulator}.Create"/> rejects a null canonicalize delegate.</summary>
    [TestMethod]
    public void CreateThrowsWhenCanonicalizeIsNull()
    {
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)null)),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() =>
            AuthenticatedRegister<string, string, string, string, string>.Create(context, null!, computeDigest, "seed"));

        Assert.AreEqual("canonicalize", exception.ParamName);
    }


    /// <summary>Pins that <see cref="AuthenticatedRegister{TState, TOperation, TProof, TContext, TAccumulator}.Create"/> rejects a null commit context.</summary>
    [TestMethod]
    public void CreateThrowsWhenContextIsNull()
    {
        CanonicalizeEntryDelegate<string, string> canonicalize = (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() =>
            AuthenticatedRegister<string, string, string, string, string>.Create(null!, canonicalize, computeDigest, "seed"));

        Assert.AreEqual("context", exception.ParamName);
    }


    /// <summary>Pins that <see cref="AuthenticatedRegister{TState, TOperation, TProof, TContext, TAccumulator}.Create"/> rejects a null computeDigest delegate.</summary>
    [TestMethod]
    public void CreateThrowsWhenComputeDigestIsNull()
    {
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)null)),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };
        CanonicalizeEntryDelegate<string, string> canonicalize = (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");

        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() =>
            AuthenticatedRegister<string, string, string, string, string>.Create(context, canonicalize, null!, "seed"));

        Assert.AreEqual("computeDigest", exception.ParamName);
    }


    /// <summary>Pins that a chain-integrity failure reports an uncommitted result carrying that error and leaves the register unchanged.</summary>
    [TestMethod]
    public async Task CommitReportsFailureWhenChainIntegrityFails()
    {
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>("chain broken"),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)null)),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };
        CanonicalizeEntryDelegate<string, string> canonicalize = (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;
        AuthenticatedRegister<string, string, string, string, string> register =
            AuthenticatedRegister<string, string, string, string, string>.Create(context, canonicalize, computeDigest, "seed");

        (AuthenticatedRegister<string, string, string, string, string> after, CommitResult<string, string> result) =
            await register.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsFalse(result.IsCommitted);
        Assert.AreEqual("chain broken", result.Error);
        Assert.IsNull(result.Entry);
        Assert.AreEqual(0UL, after.NextIndex);
        Assert.IsInstanceOfType<EmptyLogState<string>>(after.State);
    }


    /// <summary>Pins that an apply failure reports an uncommitted result carrying that error, leaves the register unchanged, and never reaches FoldStep.</summary>
    [TestMethod]
    public async Task CommitReportsFailureWhenApplyFails()
    {
        bool folded = false;
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)"register is not active")),
            FoldStep = (_, accumulator, _) => { folded = true; return ValueTask.FromResult(accumulator); },
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };
        CanonicalizeEntryDelegate<string, string> canonicalize = (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;
        AuthenticatedRegister<string, string, string, string, string> register =
            AuthenticatedRegister<string, string, string, string, string>.Create(context, canonicalize, computeDigest, "seed");

        (AuthenticatedRegister<string, string, string, string, string> after, CommitResult<string, string> result) =
            await register.CommitAsync("create", Proof, TestContext.CancellationToken).ConfigureAwait(false);

        Assert.IsFalse(result.IsCommitted);
        Assert.AreEqual("register is not active", result.Error);
        Assert.IsNull(result.Entry);
        Assert.AreEqual(0UL, after.NextIndex);
        Assert.IsInstanceOfType<EmptyLogState<string>>(after.State);
        Assert.AreEqual("seed", after.Accumulator);
        Assert.IsFalse(folded);
    }


    /// <summary>Pins that a chain-integrity check that parks resumes off the thread pool, not the caller's captured context, matching the ConfigureAwait(false) on the first delegate await.</summary>
    [TestMethod]
    public async Task APendingChainIntegrityCheckResumesOffTheCallersSynchronizationContext()
    {
        TaskCompletionSource<string?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => new ValueTask<string?>(gate.Task),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)null)),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };

        await AssertOnlyTheSuspendingDelegateResumesOffContext(context, () => gate.SetResult(null)).ConfigureAwait(false);
    }


    /// <summary>Pins that a proof validation that parks resumes off the thread pool, not the caller's captured context, matching the ConfigureAwait(false) on that delegate await.</summary>
    [TestMethod]
    public async Task APendingProofValidationResumesOffTheCallersSynchronizationContext()
    {
        TaskCompletionSource<string?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => new ValueTask<string?>(gate.Task),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)null)),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };

        await AssertOnlyTheSuspendingDelegateResumesOffContext(context, () => gate.SetResult(null)).ConfigureAwait(false);
    }


    /// <summary>Pins that an apply that parks resumes off the thread pool, not the caller's captured context, matching the ConfigureAwait(false) on that delegate await.</summary>
    [TestMethod]
    public async Task APendingApplyResumesOffTheCallersSynchronizationContext()
    {
        TaskCompletionSource<(LogState<string> State, string? Error)> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => new ValueTask<(LogState<string> State, string? Error)>(gate.Task),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };

        await AssertOnlyTheSuspendingDelegateResumesOffContext(context, () => gate.SetResult((new ActiveLogState<string>("committed"), null))).ConfigureAwait(false);
    }


    /// <summary>Pins that a fold step that parks resumes off the thread pool, not the caller's captured context, matching the ConfigureAwait(false) on that delegate await.</summary>
    [TestMethod]
    public async Task APendingFoldStepResumesOffTheCallersSynchronizationContext()
    {
        TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = entry => LogEntryClassification.Genesis,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (classification, state, entry, _) => ValueTask.FromResult((state, (string?)null)),
            FoldStep = (_, accumulator, _) => new ValueTask<string>(gate.Task),
            ValidationContext = "trust-anchors",
            TimeProvider = TimeProvider.System
        };

        await AssertOnlyTheSuspendingDelegateResumesOffContext(context, () => gate.SetResult("folded")).ConfigureAwait(false);
    }


    /// <summary>
    /// Starts a commit under a counting synchronization context with one commit delegate parked and the rest
    /// synchronous, so the parked delegate's await is the one reached with the context current; releases it and
    /// asserts the resumed commit came back on neither the context's post nor its send path.
    /// </summary>
    private async Task AssertOnlyTheSuspendingDelegateResumesOffContext(LogCommitContext<string, string, string, string, string> context, Action release)
    {
        CanonicalizeEntryDelegate<string, string> canonicalize = (index, _, operation, _) => Encoding.UTF8.GetBytes($"{index}:{operation}");
        ComputeDigestDelegate computeDigest = canonicalBytes => canonicalBytes;
        AuthenticatedRegister<string, string, string, string, string> register =
            AuthenticatedRegister<string, string, string, string, string>.Create(context, canonicalize, computeDigest, "seed");

        PostCountingSynchronizationContext syncContext = new();
        Task commit = syncContext.Start(() => register.CommitAsync("create", Proof, TestContext.CancellationToken).AsTask());

        Assert.IsFalse(commit.IsCompleted, "The delegate under test completed synchronously, so a captured continuation could not be observed.");

        release();

        await commit.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

        Assert.AreEqual(0, syncContext.Posts, "The resumed commit posted its continuation back to the caller's synchronization context instead of completing off it.");
        Assert.AreEqual(0, syncContext.Sends);
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
