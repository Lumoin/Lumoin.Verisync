namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// What one write from one site costs on each protocol path, computed from quorum sizes and distances alone.
/// </summary>
/// <param name="Site">The replica index the write originates at.</param>
/// <param name="Region">The region that replica sits in.</param>
/// <param name="FastQuorumRoundTrip">
/// One round trip to the nearest replica that completes a Fast CASPaxos fast quorum, which is the radius the
/// distance arithmetic prices the fast path at and what a first-quorum proposer would get.
/// </param>
/// <param name="FastShippedRoundTrip">
/// One round trip to the FARTHEST replica, which is what the shipped proposer actually pays: it gathers every
/// phase over all acceptors and does not act on the first quorum, so its fast write completes when the last
/// acceptor has answered.
/// </param>
/// <param name="MajorityRoundTrip">One round trip to the nearest replica that completes a strict majority.</param>
/// <param name="ClassicRoundTrip">A Fast CASPaxos classic recovery, which is two round trips at the majority radius.</param>
/// <param name="QuePaxaLeaderRoundTrip">
/// A QuePaxa believed leader's uncontended commit, which is one step and therefore one round trip at the
/// majority radius.
/// </param>
/// <param name="QuePaxaNonLeaderRoundTrip">
/// A QuePaxa non-leader's uncontended commit, which is three steps at the same radius.
/// </param>
/// <remarks>
/// Every field is exact and free. Nothing here is simulated, and nothing here is contention-shaped: these are
/// the cells the campaign computes rather than measures, and they are reported separately from the measured
/// ones for exactly that reason.
/// </remarks>
internal sealed record ComputedSiteCost(
    int Site,
    string Region,
    long FastQuorumRoundTrip,
    long FastShippedRoundTrip,
    long MajorityRoundTrip,
    long ClassicRoundTrip,
    long QuePaxaLeaderRoundTrip,
    long QuePaxaNonLeaderRoundTrip)
{
    /// <summary>
    /// The fast path's cost as a fraction of a classic round. Above one the ordering has inverted and the
    /// leaderless fast path is the slower mode at this site. Both legs are first-quorum radii, so the ratio
    /// prices neither path's shipped pacing: the shipped proposer gathers every phase over all acceptors on
    /// the recovery path as well as on the fast one, and <see cref="FastShippedRoundTrip"/> is where that
    /// cost is carried.
    /// </summary>
    public double FastOverClassic => (double)FastQuorumRoundTrip / ClassicRoundTrip;


    /// <summary>
    /// What the shipped gather costs over a proposer that acted on its first quorum, as a fraction. It is one
    /// where the fast quorum already reaches the farthest replica and grows with the spread of the placement.
    /// </summary>
    public double ShippedOverQuorum => (double)FastShippedRoundTrip / FastQuorumRoundTrip;
}
