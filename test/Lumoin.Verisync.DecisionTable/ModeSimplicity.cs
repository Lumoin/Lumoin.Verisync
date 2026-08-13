namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// How simple a configuration is to operate, which is the order the verdict's tie-break prefers in.
/// </summary>
/// <remarks>
/// A mode carrying a policy knob is an operational liability and must not win a cell on noise, so the values
/// are declared in the preference order the tie-break reads them in: leaderless above leadered above
/// staggered. A rung is the knob, so a nonzero rung outranks the mode it was configured on.
/// </remarks>
internal enum ModeSimplicity
{
    /// <summary>No leader and no ladder: QuePaxa leaderless at rung zero, or Fast CASPaxos unhedged.</summary>
    Leaderless,

    /// <summary>A recorder configuration and no ladder: QuePaxa leadered at rung zero.</summary>
    Leadered,

    /// <summary>Any configuration whose rung is nonzero, on either protocol.</summary>
    Staggered
}
