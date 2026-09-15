using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned envelope's own guards: the version it refuses and the request it refuses, each pinned from
/// the constructor and from a <c>with</c> expression.
/// </summary>
/// <remarks>
/// The with vectors are not duplicates of the construction ones: a positional record's initializer
/// writes the backing field directly while a <c>with</c> expression runs the <c>init</c> accessor, so the two
/// are separate paths and a field validated on one of them alone accepts through the other.
/// </remarks>
[TestClass]
internal sealed class VersionedRecordRequestTests
{
    /// <summary>The lane every request in this class is proposed on.</summary>
    private static ProposerLane LaneA { get; } = ProposerLane.For(Replica(1));
    /// <summary>The step every request in this class names.</summary>
    private static RecorderStep Four { get; } = RecorderStep.RoundOnePhaseZero;


    /// <summary>The test context, supplying the hang-guard token.</summary>
    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// Pins that the unwritten version is refused both when the envelope is constructed and when a <c>with</c>
    /// expression rewrites it, because the initializer and the <c>init</c> accessor are separate paths to the
    /// same backing field and only the accessor runs on a <c>with</c> expression.
    /// </summary>
    [TestMethod]
    public void TheUnwrittenVersionIsRefusedOnConstructionAndOnAWithExpression()
    {
        ArgumentOutOfRangeException constructed = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = new VersionedRecordRequest<string>(RegisterVersion.Unwritten, Request()));

        Assert.AreEqual("Version", constructed.ParamName);

        VersionedRecordRequest<string> valid = new(new RegisterVersion(3UL), Request());

        ArgumentOutOfRangeException rewritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = valid with { Version = RegisterVersion.Unwritten });

        Assert.AreEqual("Version", rewritten.ParamName);
    }


    /// <summary>
    /// Pins that a missing request is refused both when the envelope is constructed and when a <c>with</c>
    /// expression rewrites it. The constructor vector trips the null guard inside <c>ValidateRequest</c>
    /// directly (the initializer bypasses the <c>init</c> accessor), and the <c>with</c> vector is the only
    /// path that runs the accessor's own body at all.
    /// </summary>
    [TestMethod]
    public void AMissingRequestIsRefusedOnConstructionAndOnAWithExpression()
    {
        ArgumentNullException constructed = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = new VersionedRecordRequest<string>(new RegisterVersion(3UL), null!));

        Assert.AreEqual("Request", constructed.ParamName);

        VersionedRecordRequest<string> valid = new(new RegisterVersion(3UL), Request());

        ArgumentNullException rewritten = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = valid with { Request = null! });

        Assert.AreEqual("Request", rewritten.ParamName);
    }


    /// <summary>A valid request for the envelope under test.</summary>
    private static RecordRequest<string> Request()
    {
        return new RecordRequest<string>(Four, new PrioritizedProposal<string>(new ProposalKey(ProposalPriority.Lowest, LaneA), "v"));
    }


    /// <summary>A replica identity whose first byte is the given id.</summary>
    private static ReplicaId Replica(byte id)
    {
        Span<byte> buffer = stackalloc byte[ReplicaId.Size];
        buffer[0] = id;

        return ReplicaId.FromSpan(buffer);
    }
}
