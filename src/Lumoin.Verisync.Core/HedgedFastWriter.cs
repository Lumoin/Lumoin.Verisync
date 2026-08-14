using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Writes a register's fast round on a hedging schedule: the writer waits its scheduled delay, optionally
/// checks whether an earlier-scheduled writer has already driven the round, and only then sends the fast
/// write through <see cref="FastProposer{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// Concurrent writers on one fast ballot split it: each acceptor keeps the first value it sees for that
/// ballot and rejects any other, so when the split leaves no value with a fast quorum every writer falls
/// back to a classic recovery round. Staggering activation makes the earliest-scheduled writer likely to
/// hold the whole quorum alone, which turns a round where nobody commits fast into one where one writer
/// commits in a single round trip and the others recover — recoveries that no longer contend with each
/// other either.
/// </para>
/// <para>
/// Nothing here touches the protocol. Ballots, quorum rules, and acceptor state are unchanged, and the delay
/// is never consulted for safety: it only decides <em>when</em> a writer sends. That is the difference from a
/// view-change timeout, which must be conservatively large because triggering it interferes with normal
/// progress. A hedging delay of zero reproduces the unhedged behaviour exactly.
/// </para>
/// <para>
/// The writer holds no clock: delays run against the injected <see cref="TimeProvider"/>, so a host drives
/// them from a test clock or from the system clock without changing this type.
/// </para>
/// </remarks>
public sealed class HedgedFastWriter<TValue>
{
    /// <summary>
    /// Initializes a hedged writer for <paramref name="self"/> over <paramref name="proposer"/>.
    /// </summary>
    /// <param name="proposer">The proposer that carries the fast round to the acceptors.</param>
    /// <param name="schedule">The agreed activation order and base delay.</param>
    /// <param name="self">This replica, which must appear in <paramref name="schedule"/>.</param>
    /// <param name="timeProvider">The clock the hedging delay runs against.</param>
    /// <param name="observeProgress">
    /// An optional signal that an earlier-scheduled writer already drove the round, letting this writer stand
    /// down instead of sending a round that would be rejected. When <see langword="null"/> every scheduled
    /// writer activates on its delay, which is the configuration available to a host with no learn path.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="proposer"/>, <paramref name="schedule"/>, or <paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="self"/> is not in <paramref name="schedule"/>.</exception>
    public HedgedFastWriter(FastProposer<TValue> proposer, HedgingSchedule schedule, ReplicaId self, TimeProvider timeProvider, FastRoundProgressDelegate? observeProgress = null)
    {
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if(!schedule.Contains(self))
        {
            throw new ArgumentException("The writing replica is not in the hedging schedule.", nameof(self));
        }

        Proposer = proposer;
        Schedule = schedule;
        Self = self;
        TimeProvider = timeProvider;
        ObserveProgress = observeProgress;
    }


    /// <summary>The proposer that carries the fast round to the acceptors.</summary>
    public FastProposer<TValue> Proposer { get; }

    /// <summary>The agreed activation order and base delay.</summary>
    public HedgingSchedule Schedule { get; }

    /// <summary>This replica.</summary>
    public ReplicaId Self { get; }

    /// <summary>The delay this replica waits before deciding whether to activate.</summary>
    public TimeSpan Delay => Schedule.DelayFor(Self);


    private TimeProvider TimeProvider { get; }

    private FastRoundProgressDelegate? ObserveProgress { get; }


    /// <summary>
    /// Waits this replica's hedging delay and then writes <paramref name="value"/> on the fast path, unless the
    /// round has meanwhile been driven by an earlier-scheduled writer.
    /// </summary>
    /// <param name="fastBallot">The fast-round ballot. Must be a fast ballot.</param>
    /// <param name="value">The value to propose.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="next">An optional fast ballot to piggyback on the accept, establishing the next fast round.</param>
    /// <returns>Whether the writer activated, the delay it waited, and the fast-write result when it did.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="fastBallot"/> is not a fast ballot.</exception>
    /// <remarks>
    /// The replica first in the schedule has a zero delay and never stands down: it sends immediately and is
    /// the writer the others are hedging behind. A writer whose outcome reports it did not activate has sent
    /// nothing at all, which is distinct from a fast write that failed to reach its quorum.
    /// </remarks>
    public async Task<HedgedFastWriteOutcome> TryWriteAsync(FastBallot fastBallot, TValue value, CancellationToken cancellationToken, FastBallot? next = null)
    {
        if(!fastBallot.IsFast)
        {
            throw new ArgumentException("A fast write requires a fast ballot.", nameof(fastBallot));
        }

        TimeSpan delay = Delay;
        if(delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, TimeProvider, cancellationToken).ConfigureAwait(false);

            if(ObserveProgress is not null && await ObserveProgress(fastBallot, cancellationToken).ConfigureAwait(false))
            {
                return new HedgedFastWriteOutcome(false, delay, 0, false);
            }
        }

        (int accepted, bool committed) = await Proposer.TryFastWriteAsync(fastBallot, value, cancellationToken, next).ConfigureAwait(false);

        return new HedgedFastWriteOutcome(true, delay, accepted, committed);
    }
}
