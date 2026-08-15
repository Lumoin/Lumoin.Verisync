namespace Lumoin.Verisync.Core;

/// <summary>
/// Which rule refused a consensus operation.
/// </summary>
/// <remarks>
/// <para>
/// These are the refusals a running register and host raise, as
/// <see cref="StateRestoreRefusal"/> names the ones a restore raises. They are separated because the two
/// answer different questions: a restore refusal is about durable state a host was handed, and one of these is
/// about an operation a caller asked for against state that is already sound.
/// </para>
/// <para>
/// One rule is one member however many places raise it. <see cref="VersionRangeSpent"/> is reported by the
/// version's own successor and by a host that can serve no version because of it, and prose alone could not
/// tell those two sites apart at all — the same sentence stood at both.
/// </para>
/// <para>
/// The zero value claims nothing, as <see cref="QuePaxaWriteStatus.Undecided"/> does, so a default-valued
/// refusal cannot be read as a rule that fired.
/// </para>
/// </remarks>
public enum ConsensusRefusal
{
    /// <summary>No rule is named. Nothing raises this, and a refusal carrying it has not said which rule fired.</summary>
    Unspecified = 0,

    /// <summary>A second write was asked for while one was already in flight on the register.</summary>
    ConcurrentWrite = 1,

    /// <summary>The version range is spent, so the last representable version has no successor.</summary>
    VersionRangeSpent = 2,

    /// <summary>A reconfiguration was asked for on a register holding no committed record to carry forward.</summary>
    NothingCommittedToReconfigure = 3,

    /// <summary>A readiness report was asked for from a register built without a per-member version query.</summary>
    ReadinessWithoutMemberQuery = 4,

    /// <summary>A member's version probe was answered by a host asserting another member's identity.</summary>
    ProbeAnsweredByAnotherMember = 5,

    /// <summary>The round decided a record carrying a version other than the instance's own.</summary>
    MisroutedDecision = 6
}
