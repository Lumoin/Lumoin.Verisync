namespace Lumoin.Verisync.Core;

/// <summary>
/// What one member of a versioned register's membership reported when it was asked how far it had caught up.
/// </summary>
/// <param name="Member">The member that was asked.</param>
/// <param name="Version">The highest version it reported, or <see langword="null"/> when it did not answer.</param>
/// <remarks>
/// An absent answer is <see langword="null"/> rather than <see cref="RegisterVersion.Unwritten"/>, because a
/// host that has learned nothing and a host that could not be reached are different situations and only one
/// of them is silence. An operator gating a decommission on a report that collapsed the two would clear the
/// gate against a cluster that answered nothing at all.
/// </remarks>
public readonly record struct MemberReadiness(ReplicaId Member, RegisterVersion? Version)
{
    /// <summary>Whether this member answered at all.</summary>
    public bool Reachable => Version is not null;


    /// <summary>Whether this member reported having learned <paramref name="version"/> or a later one.</summary>
    /// <param name="version">The version to test against.</param>
    /// <returns><see langword="true"/> when the member answered and its answer is at or above that version.</returns>
    public bool HasLearned(RegisterVersion version) => Version is { } reported && reported >= version;
}
