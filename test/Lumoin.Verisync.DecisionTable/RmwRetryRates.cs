using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The workload gate's input, built from the read-modify-write rider's own rows.
/// </summary>
/// <remarks>
/// <para>
/// THE RATE IS KEYED BY PROTOCOL, RUNG AND ARRIVAL SPREAD, and not by the configuration's mode. The gate
/// removes configurations, so a rate that could not tell one rung from another would remove every QuePaxa
/// configuration of a cell or none of them, and staggering is precisely what moves the rate: the whole point
/// of reading the gate per configuration is that a ladder rung which eliminates the conflicts survives it.
/// </para>
/// <para>
/// THE MODE IS DELIBERATELY OUTSIDE THE KEY. QuePaxa's read-modify-write path is the versioned register, whose
/// leader is derived from the committed record rather than configured at the recorder, so the rider has no
/// leaderless arm to measure and could report a rate for only half of the plain grid's QuePaxa
/// configurations. Leaving the mode out of the key gives a leaderless row the rate measured at its own rung
/// instead of leaving it silently ungated, on the argument that the quantity is how often committed state
/// moved under a writer and that a recorder's leadership setting changes which proposal wins rather than
/// whether the loser must re-read.
/// </para>
/// <para>
/// A configuration the rider holds no figure for is left in the cell, which is the gate's own rule: it removes
/// on a measured excess and never on an absence.
/// </para>
/// </remarks>
internal static class RmwRetryRates
{
    /// <summary>
    /// The gate's input over <paramref name="rows"/>.
    /// </summary>
    /// <param name="rows">The rider's rows, at every configuration and every arrival spread it measured.</param>
    /// <returns>The rate lookup the verdict reducer reads.</returns>
    /// <exception cref="ArgumentException">Thrown if two rows of <paramref name="rows"/> carry one protocol, rung and spread, because a rate that two measurements disagreed on is not a rate.</exception>
    public static RetryRateDelegate For(ImmutableArray<RmwRow> rows)
    {
        var byConfiguration = new Dictionary<(ProtocolKind Protocol, double Rung, double Spread), double>();
        foreach(RmwRow row in rows)
        {
            (ProtocolKind Protocol, double Rung, double Spread) key = (row.Row.Protocol, row.Row.Rung, row.Row.Spread);
            if(!byConfiguration.TryAdd(key, row.ConflictRetryRate))
            {
                throw new ArgumentException($"The rider measured {row.Row.Key} at spread {row.Row.Spread} more than once, so the gate would read whichever rate happened to be stored last.", nameof(rows));
            }
        }

        return measured => byConfiguration.TryGetValue((measured.Protocol, measured.Rung, measured.Spread), out double rate) ? rate : null;
    }
}
