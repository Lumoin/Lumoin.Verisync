using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A proposer's lane counter for one register version: the version it counts within, and the next lane
/// number unused at that version.
/// </summary>
/// <param name="Version">The version the count belongs to.</param>
/// <param name="NextLane">The next unused lane number at <paramref name="Version"/>.</param>
/// <remarks>
/// The two mean nothing apart. A lane number without the version it counts within cannot say whether it is
/// still unused, which is what keeps one proposal key to one value: a second proposal at one version needs a
/// second lane, and a version nothing has proposed at starts again at zero.
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct LaneAllocation(RegisterVersion Version, int NextLane)
{
    /// <summary>The allocation of a proposer that has proposed at no version yet.</summary>
    public static LaneAllocation None { get; } = new(RegisterVersion.Unwritten, 0);


    /// <summary>
    /// This allocation moved to <paramref name="version"/>, which restarts the count where that is a version
    /// this allocation has not counted within.
    /// </summary>
    /// <param name="version">The version the next proposal is made at.</param>
    /// <returns>The allocation for that version.</returns>
    public LaneAllocation At(RegisterVersion version) => version == Version ? this : new LaneAllocation(version, 0);


    /// <summary>This allocation with its next lane consumed.</summary>
    /// <returns>The advanced allocation.</returns>
    public LaneAllocation Advanced() => this with { NextLane = NextLane + 1 };


    private string DebuggerDisplay => $"LaneAllocation: version={Version.Value}, next={NextLane}";
}
