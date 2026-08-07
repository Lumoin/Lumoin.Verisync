using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A delayed-activation order over the replicas that may write a register: the replica first in the order
/// writes immediately, and every later replica waits its position times a base delay before writing at all.
/// </summary>
/// <remarks>
/// <para>
/// A hedging delay is not a timeout. A timeout detects a failure after the fact and triggers a recovery that
/// interferes with normal progress, so it can never be set to zero and must be tuned conservatively above
/// the round-trip time. A hedging delay only staggers when replicas start, so it gates neither safety nor
/// liveness: zero is a legal setting and simply means every replica writes at once, which is the unhedged
/// behaviour. A badly chosen delay costs redundant fast rounds or delayed writes, never agreement.
/// </para>
/// <para>
/// The order must be an <em>agreed</em> fact, identical on every replica: a configured static order, a
/// committed decision, or a deterministic function of committed state such as <see cref="RotateTo(ReplicaId)"/>
/// over the previously committed writer. It must never be derived from locally observed latency or load,
/// because two replicas that rank the candidates differently each believe they are first and write
/// concurrently — which is the contention hedging exists to remove.
/// </para>
/// <para>
/// The schedule is a value with no clock of its own; <see cref="HedgedFastWriter{TValue}"/> applies the
/// delay against an injected <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class HedgingSchedule
{
    private HedgingSchedule(ImmutableArray<ReplicaId> order, TimeSpan baseDelay)
    {
        Order = order;
        BaseDelay = baseDelay;
    }


    /// <summary>
    /// Creates a schedule over an agreed replica order.
    /// </summary>
    /// <param name="agreedOrder">The agreed activation order. The first replica writes without delay.</param>
    /// <param name="baseDelay">The delay increment per position. Zero means every replica activates at once.</param>
    /// <returns>A new schedule.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="agreedOrder"/> is default, empty, or contains a duplicate replica.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="baseDelay"/> is negative, or is large enough that the last position's delay
    /// would not fit in a <see cref="TimeSpan"/>.
    /// </exception>
    public static HedgingSchedule Create(ImmutableArray<ReplicaId> agreedOrder, TimeSpan baseDelay)
    {
        if(agreedOrder.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A hedging schedule requires at least one replica.", nameof(agreedOrder));
        }

        for(int i = 0; i < agreedOrder.Length; i++)
        {
            for(int j = i + 1; j < agreedOrder.Length; j++)
            {
                if(agreedOrder[i].Equals(agreedOrder[j]))
                {
                    throw new ArgumentException("A hedging schedule cannot list the same replica twice.", nameof(agreedOrder));
                }
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);

        long lastPosition = agreedOrder.Length - 1;
        if(lastPosition > 0 && baseDelay.Ticks > long.MaxValue / lastPosition)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), baseDelay, "The last position's hedging delay does not fit in a TimeSpan.");
        }

        return new HedgingSchedule(agreedOrder, baseDelay);
    }


    /// <summary>The agreed activation order.</summary>
    public ImmutableArray<ReplicaId> Order { get; }

    /// <summary>The delay increment per position.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>The replica first in the order, which activates without delay.</summary>
    public ReplicaId Leader => Order[0];


    /// <summary>Whether <paramref name="replica"/> appears in this schedule.</summary>
    /// <param name="replica">The replica to look for.</param>
    public bool Contains(ReplicaId replica) => IndexOf(replica) >= 0;


    /// <summary>
    /// The zero-based activation position of <paramref name="replica"/>.
    /// </summary>
    /// <param name="replica">The replica to look for.</param>
    /// <returns>The position.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="replica"/> is not in the schedule.</exception>
    public int PositionOf(ReplicaId replica)
    {
        int index = IndexOf(replica);
        if(index < 0)
        {
            throw new ArgumentException("The replica is not in this hedging schedule.", nameof(replica));
        }

        return index;
    }


    /// <summary>
    /// The delay <paramref name="replica"/> waits before writing: its position times <see cref="BaseDelay"/>.
    /// </summary>
    /// <param name="replica">The replica to look for.</param>
    /// <returns>The activation delay, which is zero for <see cref="Leader"/> and for a zero base delay.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="replica"/> is not in the schedule.</exception>
    public TimeSpan DelayFor(ReplicaId replica)
    {
        return TimeSpan.FromTicks(BaseDelay.Ticks * PositionOf(replica));
    }


    /// <summary>
    /// Returns the schedule rotated so that <paramref name="replica"/> is first, preserving the cyclic order
    /// of the rest.
    /// </summary>
    /// <param name="replica">The replica to place first.</param>
    /// <returns>The rotated schedule.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="replica"/> is not in the schedule.</exception>
    /// <remarks>
    /// Rotating to the previously committed writer keeps a single-writer-mostly register on its one-round-trip
    /// path without any replica choosing a leader for itself: the previous writer is a committed fact every
    /// replica derives the same answer from. Rotating to anything a replica merely observed locally breaks the
    /// agreement the order depends on.
    /// </remarks>
    public HedgingSchedule RotateTo(ReplicaId replica)
    {
        int offset = PositionOf(replica);
        if(offset == 0)
        {
            return this;
        }

        ImmutableArray<ReplicaId>.Builder rotated = ImmutableArray.CreateBuilder<ReplicaId>(Order.Length);
        for(int i = 0; i < Order.Length; i++)
        {
            rotated.Add(Order[(offset + i) % Order.Length]);
        }

        return new HedgingSchedule(rotated.ToImmutable(), BaseDelay);
    }


    /// <summary>Returns this schedule with a different base delay.</summary>
    /// <param name="baseDelay">The new delay increment per position.</param>
    /// <returns>The adjusted schedule.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="baseDelay"/> is negative, or is large enough that the last position's delay
    /// would not fit in a <see cref="TimeSpan"/>.
    /// </exception>
    public HedgingSchedule WithBaseDelay(TimeSpan baseDelay) => Create(Order, baseDelay);


    private int IndexOf(ReplicaId replica)
    {
        for(int i = 0; i < Order.Length; i++)
        {
            if(Order[i].Equals(replica))
            {
                return i;
            }
        }

        return -1;
    }


    private string DebuggerDisplay => $"HedgingSchedule: {Order.Length} replicas, base delay {BaseDelay}";
}
