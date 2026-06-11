using Lumoin.Verisync.Core;
using Microsoft.Extensions.Time.Testing;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class LogCommitContextTests
{
    [TestMethod]
    public async Task DelegatesDriveAOneStepCommit()
    {
        FakeTimeProvider clock = new();
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = _ => LogEntryClassification.Update,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>(null),
            Apply = (_, _, entry, _) => ValueTask.FromResult<(LogState<string>, string?)>((new ActiveLogState<string>(entry.Operation!), null)),
            FoldStep = (entry, accumulator, _) => ValueTask.FromResult(accumulator + ":" + entry.Operation),
            ValidationContext = "trust-anchors",
            TimeProvider = clock
        };

        LogEntry<string, string> entry = new()
        {
            Index = 0,
            PreviousDigest = null,
            Digest = new byte[] { 1 },
            CanonicalBytes = new byte[] { 1 },
            Operation = "write-a",
            Proofs = ["controller-signature"]
        };
        LogState<string> state = new EmptyLogState<string>();

        LogEntryClassification classification = context.Classify(entry);
        Assert.AreEqual(LogEntryClassification.Update, classification);

        string? integrityError = await context.VerifyChainIntegrity(entry, null, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(integrityError);

        string? proofError = await context.ValidateProof(entry, state, context.ValidationContext, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(proofError);

        (LogState<string> newState, string? applyError) = await context.Apply(classification, state, entry, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNull(applyError);
        Assert.IsInstanceOfType<ActiveLogState<string>>(newState);
        Assert.AreEqual("write-a", ((ActiveLogState<string>)newState).Value);

        string accumulator = await context.FoldStep(entry, "genesis", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("genesis:write-a", accumulator);

        Assert.AreSame(clock, context.TimeProvider);
        Assert.AreEqual("trust-anchors", context.ValidationContext);
    }


    [TestMethod]
    public async Task ValidateProofErrorIsSurfaced()
    {
        FakeTimeProvider clock = new();
        LogCommitContext<string, string, string, string, string> context = new()
        {
            Classify = _ => LogEntryClassification.Update,
            VerifyChainIntegrity = (_, _, _) => ValueTask.FromResult<string?>(null),
            ValidateProof = (_, _, _, _) => ValueTask.FromResult<string?>("invalid signature"),
            Apply = (_, state, _, _) => ValueTask.FromResult<(LogState<string>, string?)>((state, null)),
            FoldStep = (_, accumulator, _) => ValueTask.FromResult(accumulator),
            ValidationContext = string.Empty,
            TimeProvider = clock
        };

        LogEntry<string, string> entry = new()
        {
            Index = 0,
            PreviousDigest = null,
            Digest = new byte[] { 1 },
            CanonicalBytes = new byte[] { 1 },
            Operation = "write-a",
            Proofs = ["bad-signature"]
        };

        string? proofError = await context.ValidateProof(entry, new EmptyLogState<string>(), context.ValidationContext, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("invalid signature", proofError);
    }
}
