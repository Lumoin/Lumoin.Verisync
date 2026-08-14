namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// How the recorders of a QuePaxa configuration are led, which is a configured fact at every recorder rather
/// than a client-side choice.
/// </summary>
/// <remarks>
/// This is the axis a table cell prescribes when it reads "QuePaxa, leadered": a recorder configuration. The
/// Fast CASPaxos mode axis is not symmetric with it, being one base delay on a hedging schedule.
/// </remarks>
internal enum LeadershipMode
{
    /// <summary>Writer zero's lane leads every recorder and believes it leads.</summary>
    WriterZeroLeads,

    /// <summary>Every recorder is led by a lane that never writes, so every writer runs the ordinary path.</summary>
    AbsentLeader,

    /// <summary>The recorders are leaderless and no writer claims leadership.</summary>
    Leaderless
}
