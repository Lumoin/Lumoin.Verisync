namespace Lumoin.Verisync.Core;

/// <summary>
/// Centralised string constants for OpenTelemetry metric, activity, and tag names used across
/// Lumoin.Verisync components.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="VerisyncMetrics"/> holds the <c>Meter</c> instrument instances, this class
/// centralises their names alongside the activity names and activity-tag names that leaf types
/// stamp onto OTel spans. The shape follows OTel naming conventions: lowercase, dot-separated,
/// namespaced under <c>verisync.</c>.
/// </para>
/// </remarks>
public static class VerisyncTelemetry
{
    /// <summary>Meter name for the library. Matches <see cref="VerisyncActivitySource.Name"/>.</summary>
    public const string MeterName = "Lumoin.Verisync";


    /// <summary>Metric name for the distribution of allocated buffer sizes in bytes.</summary>
    public const string MemoryAllocatedBytes = "verisync.memory.allocated_bytes";

    /// <summary>Metric name for the distribution of tagged-memory lifetimes in milliseconds.</summary>
    public const string MemoryLifetimeMs = "verisync.memory.lifetime_ms";


    /// <summary>Metric name for the count of versioned-register writes, dimensioned by what they established.</summary>
    public const string ConsensusWrites = "verisync.consensus.writes";

    /// <summary>Metric name for the distribution of consensus attempts one write spent.</summary>
    public const string ConsensusWriteAttempts = "verisync.consensus.write.attempts";

    /// <summary>Metric name for the size of the membership a register's next write runs under.</summary>
    public const string ConsensusMembershipSize = "verisync.consensus.membership.size";

    /// <summary>Metric name for the quorum that membership implies.</summary>
    public const string ConsensusMembershipQuorum = "verisync.consensus.membership.quorum";

    /// <summary>Metric name for the count of per-member version probes, dimensioned by how each answered.</summary>
    public const string ConsensusProbes = "verisync.consensus.probes";


    /// <summary>Metric and activity tag name for the chain a consensus measurement belongs to.</summary>
    public const string TagCluster = "verisync.consensus.cluster";

    /// <summary>Metric and activity tag name for the replica a consensus measurement is about.</summary>
    public const string TagMember = "verisync.consensus.member";

    /// <summary>Metric and activity tag name for what a write established, which is a <see cref="QuePaxaWriteStatus"/> name.</summary>
    public const string TagWriteStatus = "verisync.consensus.write.status";

    /// <summary>Metric and activity tag name for whether a decision was taken on the leader's one-round-trip path.</summary>
    public const string TagFastPath = "verisync.consensus.write.fast_path";

    /// <summary>Metric tag name for how a version probe answered.</summary>
    public const string TagProbeOutcome = "verisync.consensus.probe.outcome";

    /// <summary>Activity tag name for the number of consensus attempts a write spent.</summary>
    public const string ActivityWriteAttempts = "verisync.consensus.write.attempts";

    /// <summary>Activity tag name for how many members of a measured membership answered.</summary>
    public const string ActivityReachableMembers = "verisync.consensus.readiness.reachable";

    /// <summary>Activity tag name for how many members a readiness report was measured over.</summary>
    public const string ActivityMeasuredMembers = "verisync.consensus.readiness.measured";


    /// <summary>The value <see cref="TagProbeOutcome"/> carries for a member that answered.</summary>
    public const string ProbeAnswered = "answered";

    /// <summary>The value <see cref="TagProbeOutcome"/> carries for a member whose probe faulted.</summary>
    public const string ProbeFaulted = "faulted";

    /// <summary>The value <see cref="TagProbeOutcome"/> carries for a member that answered nothing before its deadline.</summary>
    public const string ProbeTimedOut = "timed_out";


    /// <summary>Activity name for one versioned-register write, whatever it establishes.</summary>
    public const string ActivityNameConsensusWrite = "verisync.consensus.write";

    /// <summary>Activity name for one readiness report.</summary>
    public const string ActivityNameConsensusReadiness = "verisync.consensus.readiness";


    /// <summary>Activity tag name for the size of a tagged buffer in bytes.</summary>
    public const string TagBufferSize = "verisync.buffer.size";

    /// <summary>Activity tag name for the <see cref="VerisyncKind"/> of a tagged buffer.</summary>
    public const string TagKind = "verisync.kind";

    /// <summary>Activity tag name for the lifetime of a value in milliseconds, set when the lifetime span is stopped.</summary>
    public const string ActivityLifetimeMs = "verisync.lifetime_ms";


    /// <summary>Activity name for the lifetime span of a tagged-memory instance.</summary>
    public const string ActivityNameMemoryLifetime = "verisync.memory.lifetime";
}
