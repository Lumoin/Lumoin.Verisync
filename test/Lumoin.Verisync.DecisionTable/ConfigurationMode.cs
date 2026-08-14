namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// One configuration of the grid's mode axis, naming the protocol and the mode inside it as a single value.
/// </summary>
/// <remarks>
/// The two protocols' mode axes are not symmetric, so the axis is enumerated whole rather than assembled from a
/// protocol beside a mode name. A QuePaxa mode is a recorder configuration, a safety input decided before any
/// write; a Fast CASPaxos mode is one base delay on a hedging schedule, whose zero value the shipped type
/// documents as reproducing unhedged behaviour exactly. An operator reading the two as the same kind of
/// instruction would be wrong, and a verdict that carried them in one string could not say so.
/// </remarks>
internal enum ConfigurationMode
{
    /// <summary>QuePaxa with writer zero's lane leading every recorder.</summary>
    QuePaxaLeadered,

    /// <summary>QuePaxa with leaderless recorders, where no writer claims leadership.</summary>
    QuePaxaLeaderless,

    /// <summary>Fast CASPaxos at a hedging base delay of zero, which is the unhedged path exactly.</summary>
    FastUnhedged,

    /// <summary>Fast CASPaxos at a nonzero hedging base delay.</summary>
    FastHedged
}
