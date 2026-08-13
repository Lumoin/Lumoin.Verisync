using System.Globalization;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// A single-threaded discrete-event clock denominated in microseconds, which both protocol arms and every
/// shipped <see cref="TimeProvider"/> seam under measurement run on.
/// </summary>
/// <remarks>
/// <para>
/// THE CLOCK IS IN MICROSECONDS BECAUSE A CO-LOCATED PLACEMENT CANNOT BE EXPRESSED IN MILLISECONDS. A
/// rack-scale one-way delay truncates to zero on a whole-millisecond clock, every message then arrives at
/// once, arrival order collapses onto enqueue order, and the contention question disappears. Milliseconds are
/// a derived reading of this clock and never its unit.
/// </para>
/// <para>
/// WALL-CLOCK TIME APPEARS NOWHERE. Every instant a measurement reports is this counter, which is a function
/// of the seed and the topology alone, so a run reproduces digit for digit on any machine and a number
/// measured beside a build is the same number as one measured on an idle machine.
/// </para>
/// <para>
/// The pump is single-threaded and checks that it is. Completing a message or firing a timer resumes the
/// awaiting client inline on the pump's own thread, so whatever that client sends next is scheduled before
/// the pump looks at its schedule again; a continuation that escaped to the thread pool is reported rather
/// than silently producing a short run. Timers are the seam <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// uses, which is how a shipped hedging delay becomes a measured wait rather than a restated one.
/// </para>
/// <para>
/// THIS CLOCK RUNS A THOUSAND TIMES SLOWER THAN REAL TIME, and it must.
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> truncates its delay to whole
/// milliseconds and returns an already-completed task for anything below one, so a delay denominated in real
/// microseconds would never be waited at all. One microsecond of this clock is therefore one millisecond of
/// the <see cref="TimeSpan"/> the shipped types see, which puts every delay the campaign uses above the
/// platform's floor. The scale is invisible to the protocols, which only ever compare durations this same
/// clock produced, and without it every co-located and availability-zone hedged row - whose entire stagger
/// ladder is under a real millisecond - would silently measure its own unhedged row.
/// </para>
/// </remarks>
internal sealed class VirtualTimePump
{
    /// <summary>
    /// How many <see cref="TimeSpan"/> ticks one microsecond of this clock is worth, which is the conversion
    /// every reading shares.
    /// </summary>
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond;

    /// <summary>How many microseconds of this clock pass in one of its seconds.</summary>
    private const long MicrosecondsPerSecond = TimeSpan.TicksPerSecond / TicksPerMicrosecond;


    /// <summary>
    /// Initializes a pump bounded by <paramref name="eventBudget"/> dispatches.
    /// </summary>
    /// <param name="eventBudget">The dispatch bound. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="eventBudget"/> is not positive.</exception>
    /// <remarks>
    /// The budget turns a run that cannot drain into a report rather than into a hung harness, which is the
    /// one failure mode a synchronous pump would otherwise convert into a wedged campaign.
    /// </remarks>
    public VirtualTimePump(long eventBudget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(eventBudget, 1);

        EventBudget = eventBudget;
        OwnerThreadId = Environment.CurrentManagedThreadId;
        Clock = new PumpTimeProvider(this);
    }


    /// <summary>The virtual instant, in microseconds since the run began.</summary>
    public long Now { get; private set; }

    /// <summary>The dispatch bound this pump reports rather than exceeds.</summary>
    public long EventBudget { get; }

    /// <summary>The number of events this pump has dispatched, which a run prints to calibrate the budget.</summary>
    public long Dispatched { get; private set; }

    /// <summary>The number of timers this pump has fired, which is how a run sees that a writer parked at all.</summary>
    public int TimersFired { get; private set; }

    /// <summary>
    /// The clock the shipped types under measurement run their delays against, reading this same instant.
    /// </summary>
    public TimeProvider Clock { get; }


    private PriorityQueue<PumpEventDelegate, (long Time, long Sequence)> Queue { get; } = new();

    private List<PumpTimer> Timers { get; } = [];

    private int OwnerThreadId { get; }

    private long Sequence { get; set; }

    private int Ordinals { get; set; }


    /// <summary>Converts an instant or a duration in microseconds to the milliseconds a report is denominated in.</summary>
    /// <param name="microseconds">The value in microseconds.</param>
    /// <returns>The value in milliseconds.</returns>
    public static double ToMilliseconds(long microseconds) => microseconds / 1000.0;


    /// <summary>Converts a duration in microseconds to the <see cref="TimeSpan"/> a shipped policy type takes.</summary>
    /// <param name="microseconds">The duration in microseconds.</param>
    /// <returns>The duration.</returns>
    public static TimeSpan ToTimeSpan(long microseconds) => TimeSpan.FromTicks(microseconds * TicksPerMicrosecond);


    /// <summary>Converts a <see cref="TimeSpan"/> a shipped policy type produced back to whole microseconds.</summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The duration in microseconds.</returns>
    public static long ToMicroseconds(TimeSpan duration) => duration.Ticks / TicksPerMicrosecond;


    /// <summary>Schedules <paramref name="action"/> at the absolute instant <paramref name="instant"/>.</summary>
    /// <param name="instant">The instant, in microseconds. Must not be in the past.</param>
    /// <param name="action">The work to perform.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="action"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the pump is driven from a thread that does not own it, or if <paramref name="instant"/> is in the past.</exception>
    public void ScheduleAt(long instant, PumpEventDelegate action)
    {
        ArgumentNullException.ThrowIfNull(action);
        AssertOwnerThread();
        if(instant < Now)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"An event was scheduled at {instant}us while the clock stands at {Now}us. A schedule that admits the past is not a clock, and the arrival order every measurement rests on would stop being a function of the seed."));
        }

        Queue.Enqueue(action, (instant, Sequence++));
    }


    /// <summary>Schedules <paramref name="action"/> <paramref name="delay"/> microseconds from now.</summary>
    /// <param name="delay">The delay in microseconds. Must not be negative.</param>
    /// <param name="action">The work to perform.</param>
    public void ScheduleAfter(long delay, PumpEventDelegate action)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delay);

        ScheduleAt(Now + delay, action);
    }


    /// <summary>
    /// Runs the schedule to exhaustion and then requires every task in <paramref name="clients"/> to have
    /// completed.
    /// </summary>
    /// <param name="clients">The client tasks this run is driving.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="clients"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the run exceeds <see cref="EventBudget"/>, which reports a run that cannot drain rather than
    /// spinning; if the schedule empties while a client is still incomplete, which reports a lost message
    /// rather than returning a run whose measurements are silently missing a writer; and if a client did not
    /// complete successfully, which reports the exception rather than letting it read as a write that never
    /// finished.
    /// </exception>
    /// <remarks>
    /// A FAULTED CLIENT IS A COMPLETED CLIENT, so completion alone is not quiescence. A client that threw
    /// leaves a half-filled state behind, and an arm that read it would report an exception in the same
    /// column a spent recovery ladder lands in. Every client's exception is observed before anything is
    /// reported, so a second faulted client cannot resurface later on a finalizer thread.
    /// </remarks>
    public void Run(IReadOnlyList<Task> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        AssertOwnerThread();

        while(Step())
        {
            Dispatched++;
            if(Dispatched > EventBudget)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The pump exceeded its event budget of {EventBudget}. A run that cannot drain is a defect rather than a slow trial."));
            }
        }

        int parked = -1;
        int faulted = -1;
        Exception? cause = null;
        for(int index = 0; index < clients.Count; index++)
        {
            Task client = clients[index];
            if(!client.IsCompleted)
            {
                if(parked < 0)
                {
                    parked = index;
                }

                continue;
            }

            //Reading the property is what marks the exception observed, so it is read for every client and
            //not only for the one this run goes on to report.
            AggregateException? aggregate = client.Exception;
            if(client.IsCompletedSuccessfully)
            {
                continue;
            }

            if(faulted < 0)
            {
                faulted = index;
                cause = aggregate is { InnerExceptions.Count: 1 } ? aggregate.InnerExceptions[0] : aggregate;
            }
        }

        if(faulted >= 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Client {faulted} did not complete successfully after the schedule drained at {Now}us; its status is {clients[faulted].Status}. A client that threw is a defect rather than a write that never finished."), cause);
        }

        if(parked >= 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Client {parked} is parked after the schedule drained at {Now}us. An empty schedule with an incomplete client is a lost message, not quiescence."));
        }
    }


    /// <summary>Runs the next scheduled event.</summary>
    /// <returns>Whether an event ran; <see langword="false"/> when the schedule is empty.</returns>
    /// <remarks>
    /// A timer due no later than the next queued event runs first, so a delay never lands in the past and the
    /// two readings of the clock stay monotone against each other.
    /// </remarks>
    private bool Step()
    {
        long? deadline = EarliestDeadline();
        bool hasEvent = Queue.TryPeek(out _, out (long Time, long Sequence) key);

        if(deadline is { } due && (!hasEvent || due <= key.Time))
        {
            FireAt(due);

            return true;
        }

        if(!hasEvent)
        {
            return false;
        }

        PumpEventDelegate action = Queue.Dequeue();
        Now = key.Time;
        action();

        return true;
    }


    private void FireAt(long deadline)
    {
        PumpTimer timer = Timers.Where(candidate => candidate.Deadline == deadline).OrderBy(candidate => candidate.Ordinal).First();
        _ = Timers.Remove(timer);

        Now = Math.Max(Now, deadline);
        TimersFired++;

        //The callback resumes the parked client inline, so whatever it sends is scheduled before this returns
        //and the schedule is never observed empty while that client has work outstanding.
        timer.Fire();
    }


    private long? EarliestDeadline()
    {
        long? earliest = null;
        foreach(PumpTimer timer in Timers)
        {
            if(earliest is null || timer.Deadline < earliest)
            {
                earliest = timer.Deadline;
            }
        }

        return earliest;
    }


    private void AssertOwnerThread()
    {
        if(Environment.CurrentManagedThreadId != OwnerThreadId)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The pump owns thread {OwnerThreadId} and was driven from thread {Environment.CurrentManagedThreadId}. A continuation that left the pump's thread makes the schedule and the clock race, and a measurement taken under that race is not the run its seed replays."));
        }
    }


    /// <summary>
    /// The clock the pump owns. Every reading comes from <see cref="Now"/>, so a delay a shipped type
    /// expresses in milliseconds and a transport instant expressed in microseconds are one quantity read two
    /// ways.
    /// </summary>
    private sealed class PumpTimeProvider(VirtualTimePump pump): TimeProvider
    {
        /// <summary>
        /// A fixed epoch rather than a real instant: a reading of this clock must not carry the day the run
        /// happened, or two runs of one seed would differ in a field a report could print.
        /// </summary>
        private static DateTimeOffset Epoch { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);


        public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(pump.Now * TicksPerMicrosecond);

        public override long GetTimestamp() => pump.Now;

        public override long TimestampFrequency => MicrosecondsPerSecond;


        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if(period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("The pump schedules one-shot timers only, which is the shape a hedging delay takes. A periodic timer would arm once here and never repeat, which is a silent loss rather than a refusal.");
            }

            pump.AssertOwnerThread();
            var timer = new PumpTimer(pump, callback, state, ++pump.Ordinals);
            timer.Arm(dueTime);

            return timer;
        }
    }


    /// <summary>A one-shot timer the pump fires, rather than one a platform timer queue fires.</summary>
    private sealed class PumpTimer(VirtualTimePump pump, TimerCallback callback, object? state, int ordinal): ITimer
    {
        public int Ordinal => ordinal;

        public long Deadline { get; private set; }


        public void Arm(TimeSpan dueTime)
        {
            _ = pump.Timers.Remove(this);
            if(dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            Deadline = pump.Now + Math.Max(ToMicroseconds(dueTime), 0);
            pump.Timers.Add(this);
        }


        public void Fire() => callback(state);


        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if(period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("The pump schedules one-shot timers only.");
            }

            Arm(dueTime);

            return true;
        }


        public void Dispose() => pump.Timers.Remove(this);


        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
