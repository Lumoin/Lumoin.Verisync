namespace Lumoin.Verisync.Core;

/// <summary>
/// What a versioned register's write attempt established.
/// </summary>
/// <remarks>
/// A failed write is two different states and a caller acts on them differently. A write another replica's
/// value beat is a conflict: the version closed, its value is known, and the caller re-reads and re-proposes.
/// A write that reached no decision established nothing, because its own value may yet be chosen by another
/// proposer carrying it. Treating that as a conflict abandons a write that is still live, and treating it as
/// a failure risks applying a non-idempotent update twice.
/// </remarks>
public enum QuePaxaWriteStatus
{
    /// <summary>
    /// The attempt reached no decision, through a missed quorum or a spent step budget. It is the default so
    /// that a zero value claims nothing.
    /// </summary>
    /// <remarks>
    /// Not decided means not known decided. Every recorder the attempt reached still recorded, so this
    /// proposal may be carried by another proposer and decided later, and a caller must not read this as
    /// evidence that its value was rejected.
    /// </remarks>
    Undecided = 0,

    /// <summary>The version was decided, and it carries another writer's record rather than this one's.</summary>
    Superseded = 1,

    /// <summary>The version was decided and it carries this writer's record.</summary>
    Committed = 2,

    /// <summary>
    /// The attempt was not made, because this replica is outside the membership the instance runs under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a third failure and not a variant of the first two. Nothing was proposed, nothing was recorded
    /// anywhere, and no later proposer can carry this value, so a caller reads it as a definite refusal
    /// where <see cref="Undecided"/> is a definite ignorance.
    /// </para>
    /// <para>
    /// A register reports it rather than throwing, because membership is a per-version fact and not a
    /// construction error. A replica removed by a configuration change reaches this at the first version
    /// after the change and a joiner reaches it at every version before the one that admits it, and both are
    /// states the protocol arrives at rather than misuse a caller can be blamed for.
    /// </para>
    /// </remarks>
    OutsideConfiguration = 3
}
