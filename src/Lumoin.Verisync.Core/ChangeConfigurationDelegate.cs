namespace Lumoin.Verisync.Core;

/// <summary>
/// Computes the membership a reconfiguration proposes from the membership the instance it runs at is under.
/// </summary>
/// <param name="current">The membership the reconfiguring attempt captured, which is what the change applies to.</param>
/// <returns>The membership to install, which is the same chain with members added or removed.</returns>
/// <remarks>
/// <para>
/// A delta and never an absolute set. A reconfiguration that lost its version is re-applied against the
/// membership that won, so "add this replica" survives a rival's change and "set the membership to exactly
/// this list" silently undoes it: an operator adding a fourth replica while another operator removes a
/// failed one would, under an absolute set, reinstate the replica that was removed. An implementation that
/// ignores <paramref name="current"/> and returns a fixed configuration is that defect written down, and it
/// is a defect rather than a supported use.
/// </para>
/// <para>
/// <see cref="QuePaxaConfiguration.With(ReplicaId)"/> and
/// <see cref="QuePaxaConfiguration.Without(ReplicaId)"/> are idempotent for exactly this reason: re-applying
/// a change against a membership that already carries it returns that membership, so the re-application
/// after a superseded attempt is a no-op rather than a second edit.
/// </para>
/// <para>
/// It runs outside the consensus round and once per attempt, as the value update does, and it must be a
/// function of its argument alone. A change reading state that moves between attempts proposes something
/// other than what it was re-applied to.
/// </para>
/// </remarks>
public delegate QuePaxaConfiguration ChangeConfigurationDelegate(QuePaxaConfiguration current);
