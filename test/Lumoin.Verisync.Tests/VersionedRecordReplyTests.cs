using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// The versioned record reply's own guards: the reply it refuses, pinned from the constructor and from a
/// <c>with</c> expression.
/// </summary>
/// <remarks>
/// <para>
/// The with vector is not a duplicate of the construction one: a positional record's initializer
/// writes the backing field directly while a <c>with</c> expression runs the <c>init</c> accessor, so the two
/// are separate paths and a field validated on one of them alone accepts through the other. Constructing
/// leaves an ordinary auto-property indistinguishable from a validated one.
/// </para>
/// </remarks>
[TestClass]
internal sealed class VersionedRecordReplyTests
{
    /// <summary>The test context, supplying the hang-guard token.</summary>
    public TestContext TestContext { get; set; } = null!;


    /// <summary>
    /// Pins that <see cref="VersionedRecordReply{TValue}.Reply"/> refuses <see langword="null"/> on construction
    /// and again on a <c>with</c> expression, for the same reason the version is.
    /// </summary>
    [TestMethod]
    public void ANullReplyIsRefusedOnConstructionAndOnAWithExpression()
    {
        Span<byte> replicaBytes = stackalloc byte[ReplicaId.Size];
        replicaBytes[0] = 1;
        ReplicaId replica = ReplicaId.FromSpan(replicaBytes);
        HostId recorder = new(replica, StoreIncarnation.Generate());

        //Version and Recorder are otherwise valid here, so the null reply is the only field that can trip a guard.
        ArgumentNullException constructed = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = new VersionedRecordReply<string>(new RegisterVersion(3UL), recorder, null!));

        Assert.AreEqual("Reply", constructed.ParamName);

        RecordReply<string> reply = new(
            RecorderStep.RoundOnePhaseZero,
            new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(1), ProposerLane.For(replica)), "v"),
            null);
        VersionedRecordReply<string> valid = new(new RegisterVersion(3UL), recorder, reply);

        //The initializer wrote the backing field directly, so this is the only path the init accessor runs on
        //and the only vector an unvalidated auto-property would be visible from.
        ArgumentNullException rewritten = Assert.ThrowsExactly<ArgumentNullException>(
            () => _ = valid with { Reply = null! });

        Assert.AreEqual("Reply", rewritten.ParamName);
    }


    /// <summary>
    /// Pins that <see cref="VersionedRecordReply{TValue}.Version"/> refuses the unwritten sentinel on
    /// construction and again on a <c>with</c> expression, for the same reason the reply refuses null.
    /// </summary>
    [TestMethod]
    public void TheUnwrittenVersionIsRefusedOnConstructionAndOnAWithExpression()
    {
        Span<byte> replicaBytes = stackalloc byte[ReplicaId.Size];
        replicaBytes[0] = 1;
        ReplicaId replica = ReplicaId.FromSpan(replicaBytes);
        HostId recorder = new(replica, StoreIncarnation.Generate());
        RecordReply<string> reply = new(
            RecorderStep.RoundOnePhaseZero,
            new PrioritizedProposal<string>(new ProposalKey(new ProposalPriority(1), ProposerLane.For(replica)), "v"),
            null);

        //The reply and recorder are valid here, so the unwritten version is the only field that can trip a guard.
        ArgumentOutOfRangeException constructed = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = new VersionedRecordReply<string>(RegisterVersion.Unwritten, recorder, reply));

        Assert.AreEqual("Version", constructed.ParamName);

        VersionedRecordReply<string> valid = new(new RegisterVersion(3UL), recorder, reply);

        //The initializer wrote the backing field directly, so the with expression is the only path the init
        //accessor runs on and the only vector an unvalidated auto-property would be visible from.
        ArgumentOutOfRangeException rewritten = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = valid with { Version = RegisterVersion.Unwritten });

        Assert.AreEqual("Version", rewritten.ParamName);
    }
}
