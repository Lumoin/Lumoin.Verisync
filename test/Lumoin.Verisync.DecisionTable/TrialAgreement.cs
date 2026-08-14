using System.Collections.Immutable;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The agreement gate every measured row is held to: one instance decides one value and one owner in every
/// trial, on both protocols.
/// </summary>
/// <remarks>
/// <para>
/// AGREEMENT IS A GATE RATHER THAN A COLUMN. A configuration that fails it is void rather than slow, so the
/// predicate is stated once here and applied identically wherever a row is built, and the two protocols are
/// held to the same two halves of it.
/// </para>
/// <para>
/// The DECIDE half requires the instance to have decided at all. The VALUE half requires every writer that
/// saw a decision to have seen the same one. A bounded recovery ladder makes one writer's exhaustion a
/// censored write rather than a broken register, so a writer that gave up while another writer committed
/// fails neither half and travels in the row's censored count instead; a trial in which nobody committed
/// fails the decide half and voids the configuration.
/// </para>
/// </remarks>
internal static class TrialAgreement
{
    /// <summary>Whether one Fast CASPaxos trial agreed.</summary>
    /// <param name="measurements">One measurement per writer, in writer order.</param>
    /// <returns>Whether the trial decided one value.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="measurements"/> is empty.</exception>
    public static bool Fast(ImmutableArray<FastWriterMeasurement> measurements)
    {
        if(measurements.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A trial with no writer cannot be held to the agreement gate.", nameof(measurements));
        }

        string? decided = null;
        bool agreed = true;
        foreach(FastWriterMeasurement measurement in measurements)
        {
            if(measurement.CommittedValue is not { } committed)
            {
                continue;
            }

            //Every writer that committed must have committed the same value: a recovery adopts what it
            //recovered and never overwrites it, so a divergence here would be a broken register.
            decided ??= committed;
            agreed &= EqualityComparer<string>.Default.Equals(committed, decided);
        }

        //And the instance must have decided at all, which is the half the QuePaxa predicate carries in its
        //own decided conjunct. A trial where every writer spent its ladder decided nothing.
        return agreed && decided is not null;
    }


    /// <summary>Whether one QuePaxa trial agreed.</summary>
    /// <param name="measurements">One measurement per writer, in writer order.</param>
    /// <returns>Whether the trial decided one value and one owner.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="measurements"/> is empty.</exception>
    public static bool QuePaxa(ImmutableArray<QuePaxaWriterMeasurement> measurements)
    {
        if(measurements.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A trial with no writer cannot be held to the agreement gate.", nameof(measurements));
        }

        bool agreed = true;
        foreach(QuePaxaWriterMeasurement measurement in measurements)
        {
            agreed &= measurement.Outcome.IsDecided
                && EqualityComparer<string>.Default.Equals(measurement.Outcome.Value, measurements[0].Outcome.Value)
                && measurement.Outcome.DecidedBy == measurements[0].Outcome.DecidedBy;
        }

        return agreed;
    }
}
