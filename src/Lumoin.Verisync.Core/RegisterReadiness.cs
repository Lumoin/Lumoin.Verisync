using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// What every member of a versioned register's membership reported when it was asked how far it had caught
/// up: the operator's gate for adding a replica and for decommissioning one.
/// </summary>
/// <remarks>
/// <para>
/// It is an observation and never an act. Membership is operator-driven throughout, so the library reports
/// what the members say and does nothing on the strength of it; a liveness signal driving a
/// safety-adjacent change is the automatic-membership design this arc declined.
/// </para>
/// <para>
/// It is a snapshot of separate answers rather than a consistent cut. Each member answered at its own
/// instant, so a report showing a quorum at a version says that each of those members had learned it when it
/// answered, which is exactly what the gate needs: a host that has learned a version does not unlearn it.
/// Nothing here says anything about the members that did not answer.
/// </para>
/// <para>
/// This is a class rather than a record because it carries an <see cref="ImmutableArray{T}"/> and claims no
/// equality. A synthesized equality would compare that array by the identity of its backing array, which is
/// the defect <see cref="QuePaxaConfiguration"/> writes its own equality out by hand to avoid, and a report
/// nobody compares is better off claiming nothing than claiming something wrong.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class RegisterReadiness
{
    /// <summary>
    /// Initializes a report of <paramref name="members"/> measured over <paramref name="configuration"/>.
    /// </summary>
    /// <param name="configuration">The membership the report was measured over.</param>
    /// <param name="members">One entry per member, in the configuration's own member order.</param>
    /// <remarks>
    /// The register is the only producer, which is why nothing is validated here: the entries are built from
    /// the configuration's member list in that list's order, so the correspondence holds by construction
    /// rather than by a check.
    /// </remarks>
    internal RegisterReadiness(QuePaxaConfiguration configuration, ImmutableArray<MemberReadiness> members)
    {
        Configuration = configuration;
        Members = members;
    }


    /// <summary>The membership this report was measured over.</summary>
    public QuePaxaConfiguration Configuration { get; }

    /// <summary>What each member reported, in the configuration's own member order.</summary>
    public ImmutableArray<MemberReadiness> Members { get; }

    /// <summary>How many members answered at all.</summary>
    /// <remarks>
    /// Reachability is reported beside the versions because the two fail differently. A membership whose
    /// members all answer an old version is behind, and one whose members do not answer is unavailable, and
    /// an operator waiting out the first would wait forever on the second.
    /// </remarks>
    public int Reachable
    {
        get
        {
            int reachable = 0;
            foreach(MemberReadiness member in Members)
            {
                if(member.Reachable)
                {
                    reachable++;
                }
            }

            return reachable;
        }
    }


    /// <summary>
    /// Whether a quorum of the membership reported having learned <paramref name="version"/> or a later one.
    /// </summary>
    /// <param name="version">The version to test against.</param>
    /// <returns><see langword="true"/> when at least <see cref="QuePaxaConfiguration.Quorum"/> members reported at or above that version.</returns>
    /// <remarks>
    /// This is the condition a write at the version after <paramref name="version"/> needs in order to gather
    /// a quorum at all, because a host serves the one instance whose leader it can derive. An operator adding
    /// a replica waits for it to hold before writing again, and one decommissioning a replica waits for it to
    /// hold without counting the replica being retired.
    /// </remarks>
    public bool QuorumHasLearned(RegisterVersion version)
    {
        int learned = 0;
        foreach(MemberReadiness member in Members)
        {
            if(member.HasLearned(version))
            {
                learned++;
            }
        }

        return learned >= Configuration.Quorum;
    }


    private string DebuggerDisplay => $"RegisterReadiness: {Reachable} of {Members.Length} reachable, quorum {Configuration.Quorum}";
}
