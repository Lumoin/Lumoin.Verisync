using System.Collections.Immutable;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The stagger a configuration imposes, taken from the shipped <see cref="HedgingSchedule"/> rather than
/// open-coded as a position times a delay.
/// </summary>
/// <remarks>
/// <para>
/// The existing probe open-codes the stagger and says so: neither the schedule nor the hedged writer is
/// exercised there, so the hedging half of every published row is a restatement of the policy rather than a
/// measurement of the shipped one. This harness need not inherit that. The Fast CASPaxos arm hands the
/// schedule to <see cref="HedgedFastWriter{TValue}"/> and the delay is awaited against the pump's clock; the
/// QuePaxa proposer exposes no clock at all, so its arm reads the same schedule's arithmetic and activates
/// its writers at those instants.
/// </para>
/// <para>
/// The delays are identical to the open-coded ones by construction, which is what lets the reproduction gate
/// pass while the policy under measurement is the shipped one.
/// </para>
/// </remarks>
internal static class StaggerSchedule
{
    /// <summary>
    /// The delay, in microseconds, each of <paramref name="writerCount"/> writers waits under a base delay of
    /// <paramref name="baseDelayMicroseconds"/>.
    /// </summary>
    /// <param name="writerCount">The number of writers in the schedule.</param>
    /// <param name="baseDelayMicroseconds">The delay increment per position. Zero staggers nobody.</param>
    /// <returns>The delays in writer order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="writerCount"/> is not positive or <paramref name="baseDelayMicroseconds"/> is negative.</exception>
    public static ImmutableArray<long> Delays(int writerCount, long baseDelayMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(writerCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(baseDelayMicroseconds);

        ImmutableArray<ReplicaId> order = [.. Enumerable.Range(0, writerCount).Select(HarnessIdentity.Replica)];
        HedgingSchedule schedule = HedgingSchedule.Create(order, VirtualTimePump.ToTimeSpan(baseDelayMicroseconds));

        return [.. order.Select(replica => VirtualTimePump.ToMicroseconds(schedule.DelayFor(replica)))];
    }
}
