using Lumoin.Verisync.Core;
using System.Collections.Immutable;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A discrete-event bench for the versioned register: record requests, record replies, dissemination and
/// committed-version observations are all scheduled events on one virtual clock, and the hedging delay the
/// leader schedule imposes is driven by the same clock rather than by a test advancing a fake one.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <remarks>
/// <para>
/// It is a third bench rather than a mode of either existing one.
/// <see cref="InterleavedQuePaxaCluster{TValue}"/> is typed to the bare message family and drives a proposer
/// against recorder nodes; <see cref="VersionedQuePaxaCluster{TValue}"/> is typed to the envelope and serves
/// synchronously so that a test observes exactly what a write put on the wire. This one is typed to the
/// envelope and serves through a queue, which is the one shape neither of the others can take without
/// changing what its own laws are pinned against.
/// </para>
/// <para>
/// THERE IS ONE CLOCK AND TWO READINGS OF IT. <see cref="Now"/> is a virtual instant in ticks: delivering a
/// message advances it by <see cref="HopLatency"/>, firing a timer advances it to that timer's deadline, and
/// <see cref="Tick"/> advances it by a single tick to mark a history boundary. The same instant is what
/// <see cref="TimeProvider"/> reports, so a hedging delay denominated in milliseconds and transport progress
/// denominated in hops are comparable quantities rather than two unreconciled currencies.
/// </para>
/// <para>
/// QUIESCENCE INCLUDES ARMED TIMERS, and it has to. Everything a write puts on the transport happens after
/// its hedging delay, so a writer parked on that delay has enqueued nothing at all; a pump that stopped at an
/// empty queue would return with the writer still parked and hand the checker a history missing that
/// writer's operations, which passes vacuously. <see cref="RunToQuiescence"/> therefore stops only when no
/// message is in flight, no timer is armed, and every client task it was given has completed, and it throws
/// rather than returning when the last of those does not hold.
/// </para>
/// <para>
/// THE BENCH IS SINGLE-THREADED AND CHECKS THAT IT IS. Completing a message or firing a timer resumes the
/// awaiting client inline on the pump's own thread, which is what makes a woken writer's first sends land
/// before the delivery call returns. Every mutation asserts it is on the thread that constructed the
/// cluster, so a continuation that escaped to the thread pool is reported rather than silently producing a
/// short history.
/// </para>
/// <para>
/// HOSTS AND MEMBERS ARE TWO SEPARATE FACTS. The hosts are the replicas this bench was told to run, and the
/// membership is what the genesis configuration and the records decided on its chain name, so a replica may
/// be a host that is up, reachable and answering while standing outside the configuration entirely. That is
/// the joiner before the change that admits it and the leaver after the change that removed it, and a bench
/// whose every array is sized to its host list makes both unstateable by construction.
/// </para>
/// </remarks>
internal sealed class InterleavedVersionedQuePaxaCluster<TValue>
{
    /// <summary>
    /// A livelock is the one failure mode a synchronous pump turns into a hung suite rather than a red test, so
    /// the pump is bounded well above any behaviour the attempt budget admits and reports rather than spins.
    /// </summary>
    private const int MaxEvents = 200_000;

    private uint randomState;

    /// <summary>
    /// Creates a cluster running one host per replica in <paramref name="replicas"/> on the chain
    /// <paramref name="genesis"/> begins, each having learned <paramref name="committed"/>.
    /// </summary>
    /// <param name="genesis">The chain's genesis membership, which every host of this bench is handed and which need not name every host.</param>
    /// <param name="replicas">The replicas this bench runs a host for, in host order.</param>
    /// <param name="baseDelay">The hedging delay increment per position the registers this bench creates wait.</param>
    /// <param name="seed">The delivery-order seed. It is printed by every test that uses one.</param>
    /// <param name="committed">The committed record every host starts from, or <see langword="null"/> when none has been written.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="genesis"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="replicas"/> is default or empty, or names one replica twice.</exception>
    /// <remarks>
    /// The genesis membership is taken rather than derived from the host list, which is what lets a host
    /// stand outside the configuration. Every host is handed the same genesis, as every host of a deployed
    /// chain is, so a replica the genesis does not name declines record requests until it learns a record
    /// that names it, and answers everything else the whole time.
    /// </remarks>
    public InterleavedVersionedQuePaxaCluster(
        QuePaxaConfiguration genesis,
        ImmutableArray<ReplicaId> replicas,
        TimeSpan baseDelay,
        int seed,
        VersionedValue<TValue>? committed = null)
    {
        ArgumentNullException.ThrowIfNull(genesis);
        if(replicas.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A bench runs at least one host.", nameof(replicas));
        }

        for(int index = 0; index < replicas.Length; index++)
        {
            for(int other = index + 1; other < replicas.Length; other++)
            {
                if(replicas[index].Equals(replicas[other]))
                {
                    throw new ArgumentException("A bench runs one host per replica and cannot name one replica twice; two hosts answering as one replica is a wiring error rather than a topology.", nameof(replicas));
                }
            }
        }

        Genesis = genesis;
        Replicas = replicas;
        BaseDelay = baseDelay;
        Seed = seed;
        OwnerThreadId = Environment.CurrentManagedThreadId;
        Clock = new PumpTimeProvider(this);

        Hosts = new QuePaxaVersionedNode<TValue>[replicas.Length];
        Partitioned = new bool[replicas.Length];
        DisseminationHeld = new bool[replicas.Length];
        RecordRequestCounts = new int[replicas.Length];
        for(int index = 0; index < replicas.Length; index++)
        {
            Hosts[index] = new QuePaxaVersionedNode<TValue>(genesis, Membership.Member(replicas[index]), committed);
            if(committed is not null)
            {
                Adoptions.Add(new AdoptedRecord(replicas[index], committed));
            }
        }

        //Xorshift32 instead of System.Random: the sequence is deterministic across runtimes and platforms, so
        //a seed printed by a failing run replays the identical interleaving anywhere. State must be nonzero.
        randomState = seed == 0 ? 2463534242u : (uint)seed;
    }


    /// <summary>
    /// Creates a cluster of <paramref name="hostCount"/> hosts on the chain <paramref name="schedule"/>'s
    /// whole agreed order founds, each having learned <paramref name="committed"/>.
    /// </summary>
    /// <param name="schedule">The agreed order the chain is founded on and the hedging increment its registers wait.</param>
    /// <param name="hostCount">The number of recorder hosts, taken from the front of that order.</param>
    /// <param name="seed">The delivery-order seed. It is printed by every test that uses one.</param>
    /// <param name="committed">The committed record every host starts from, or <see langword="null"/> when none has been written.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="hostCount"/> is less than one or above the order's length.</exception>
    public InterleavedVersionedQuePaxaCluster(QuePaxaLeaderSchedule schedule, int hostCount, int seed, VersionedValue<TValue>? committed = null)
        : this(GenesisOver(schedule), LeadingReplicasOf(schedule, hostCount), BaseDelayOf(schedule), seed, committed)
    {
    }


    /// <summary>
    /// The genesis membership this cluster's chain begins under, which every host was handed and which a
    /// register over the same chain stamps its records with until a change moves it.
    /// </summary>
    public QuePaxaConfiguration Genesis { get; }

    /// <summary>
    /// The replicas this cluster runs a host for, in host order, whether or not the membership names them.
    /// </summary>
    public ImmutableArray<ReplicaId> Replicas { get; }

    /// <summary>The hedging delay increment per position the registers this cluster creates wait.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>The delivery-order seed this cluster was created with.</summary>
    public int Seed { get; }

    /// <summary>The number of recorder hosts, which is not the number of members.</summary>
    public int HostCount => Hosts.Length;

    /// <summary>The virtual instant, in ticks.</summary>
    public long Now { get; private set; }

    /// <summary>
    /// What one message delivery costs the clock, which is the rate that makes a hedging delay and a number
    /// of hops comparable. A delay of four hops at the default is what the schedule's base delay buys.
    /// </summary>
    public TimeSpan HopLatency { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>The clock the registers this cluster creates run their hedging delay against.</summary>
    public TimeProvider Clock { get; }

    /// <summary>The number of messages awaiting delivery.</summary>
    public int PendingCount => InFlight.Count;

    /// <summary>The number of timers awaiting expiry, which a parked writer's hedging delay is one of.</summary>
    public int ArmedTimerCount => Timers.Count;

    /// <summary>The number of timers the pump has fired, which is how a test sees that a writer parked at all.</summary>
    public int TimersFired { get; private set; }

    /// <summary>
    /// The number of timers fired while messages were still in flight, which is the situation the ordering
    /// between a due timer and the next delivery governs. A run where it is zero pins nothing about that
    /// ordering, whatever else it asserts.
    /// </summary>
    public int TimersFiredUnderTraffic { get; private set; }

    /// <summary>
    /// The number of timers fired at an instant already past their own deadline. It is zero while a timer due
    /// no later than the next delivery runs first, and a pump that drained its queue before firing anything
    /// would overshoot instead, which makes a hedging delay longer than the schedule asked for.
    /// </summary>
    public int TimersFiredLate { get; private set; }

    /// <summary>The number of dissemination messages delivered to a host that had not learned the record.</summary>
    public int DisseminationsLearned { get; private set; }

    /// <summary>Every committed record a host of this cluster has adopted, in adoption order.</summary>
    /// <remarks>
    /// A safety witness reads this beside the records the hosts hold now. A version whose record has been
    /// superseded by a later one is still a version every host that held it had to agree about, and a reading
    /// taken when a run has quiesced can no longer see it.
    /// </remarks>
    public IReadOnlyList<AdoptedRecord> AdoptedRecords => Adoptions;

    /// <summary>The delivery order so far, one line per delivered or lost event, for replay comparison.</summary>
    public IReadOnlyList<string> DeliveryTrace => TraceLines;

    /// <summary>
    /// Every record request delivered to a host, in delivery order, with the replica whose register sent it.
    /// This is what a reach pin reads to show that two writers' requests genuinely interleaved at one host
    /// rather than running one after the other.
    /// </summary>
    public IReadOnlyList<DeliveredRecordCall> DeliveredCalls => RecordCalls;

    /// <summary>
    /// The highest committed record any host holds, which is the linearizability witness. It is read from the
    /// hosts rather than through <see cref="QuePaxaVersionedRegister{TValue}.ReadAsync"/>, which is explicitly
    /// not a linearizable read and would make the witness a local belief.
    /// </summary>
    public VersionedValue<TValue>? HighestCommitted
    {
        get
        {
            VersionedValue<TValue>? highest = null;
            RegisterVersion reached = RegisterVersion.Unwritten;
            foreach(QuePaxaVersionedNode<TValue> host in Hosts)
            {
                if(host.Committed is { } committed && committed.Version > reached)
                {
                    highest = committed;
                    reached = committed.Version;
                }
            }

            return highest;
        }
    }


    private QuePaxaVersionedNode<TValue>[] Hosts { get; }

    private bool[] Partitioned { get; }

    /// <summary>Which hosts lose an offered record at delivery while answering everything else.</summary>
    private bool[] DisseminationHeld { get; }

    /// <summary>How many record requests each host has been delivered.</summary>
    private int[] RecordRequestCounts { get; }

    private List<Message> InFlight { get; } = [];

    private List<PumpTimer> Timers { get; } = [];

    private List<string> TraceLines { get; } = [];

    private List<DeliveredRecordCall> RecordCalls { get; } = [];

    /// <summary>Every record a host adopted, which is the safety witness's history.</summary>
    private List<AdoptedRecord> Adoptions { get; } = [];

    private int OwnerThreadId { get; }


    /// <summary>Returns the host at <paramref name="index"/>.</summary>
    /// <param name="index">The host index.</param>
    /// <returns>The host.</returns>
    public QuePaxaVersionedNode<TValue> Host(int index) => Hosts[index];


    /// <summary>Returns the host that is <paramref name="member"/>.</summary>
    /// <param name="member">The replica whose host to return.</param>
    /// <returns>That host.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is that replica.</exception>
    /// <remarks>
    /// Reading a host by the replica it is rather than by its position is what keeps a scenario legible once
    /// the membership has moved, because a position means nothing after a change and a replica means the same
    /// thing at every version.
    /// </remarks>
    public QuePaxaVersionedNode<TValue> HostFor(ReplicaId member) => Hosts[HostIndexOf(member)];


    /// <summary>The committed record the host that is <paramref name="member"/> holds.</summary>
    /// <param name="member">The replica whose host to read.</param>
    /// <returns>That host's committed record, or <see langword="null"/> when it has learned none.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is that replica.</exception>
    public VersionedValue<TValue>? CommittedAt(ReplicaId member) => Hosts[HostIndexOf(member)].Committed;


    /// <summary>How many record requests have reached the host that is <paramref name="member"/>.</summary>
    /// <param name="member">The replica whose host to read.</param>
    /// <returns>The number of record requests delivered to it.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is that replica.</exception>
    /// <remarks>
    /// It counts record requests and nothing else: a dissemination, a catch-up read and a version
    /// observation all leave it alone, and a request lost to a partition never reached the host and is not
    /// counted either. A host whose count is zero has therefore answered no request at all, which is what
    /// makes an eager push the only route a record it holds can have arrived by.
    /// </remarks>
    public int RecordRequestsAt(ReplicaId member) => RecordRequestCounts[HostIndexOf(member)];


    /// <summary>Partitions the host at <paramref name="index"/>, so its messages are lost at delivery.</summary>
    /// <param name="index">The host index.</param>
    public void Partition(int index) => Partitioned[index] = true;


    /// <summary>Heals the host at <paramref name="index"/>.</summary>
    /// <param name="index">The host index.</param>
    public void Heal(int index) => Partitioned[index] = false;


    /// <summary>
    /// Stops the host that is <paramref name="member"/> from taking an offered record, while it keeps
    /// answering requests, reads and version observations.
    /// </summary>
    /// <param name="member">The replica whose host to hold back.</param>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is that replica.</exception>
    /// <remarks>
    /// A held host is behind and not unreachable, and the two are what a readiness report separates. A
    /// partition collapses them, because a host nobody can reach also reports nothing, so a scenario that
    /// needs a member which answers and has not learned holds its dissemination instead.
    /// </remarks>
    public void HoldDissemination(ReplicaId member) => DisseminationHeld[HostIndexOf(member)] = true;


    /// <summary>Lets the host that is <paramref name="member"/> take offered records again.</summary>
    /// <param name="member">The replica whose host to release.</param>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is that replica.</exception>
    /// <remarks>
    /// Releasing the hold delivers nothing by itself. A record offered while the hold was on was lost at
    /// delivery and is gone, so a held host catches up when something offers it the record again.
    /// </remarks>
    public void ResumeDissemination(ReplicaId member) => DisseminationHeld[HostIndexOf(member)] = false;


    /// <summary>
    /// Offers <paramref name="committed"/> to the hosts <paramref name="members"/> names, as scheduled
    /// deliveries.
    /// </summary>
    /// <param name="committed">The record to offer.</param>
    /// <param name="members">The replicas whose hosts to offer it to.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> or <paramref name="members"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is one of those replicas.</exception>
    /// <remarks>
    /// This is the catch-up a deployment owns beside a writer's own push, and it is deliberately not the
    /// register's publish: nothing here is decided, offered to an audience or reported anywhere, so a
    /// scenario that uses it is saying a straggler was fed by its operator rather than by the protocol.
    /// </remarks>
    public void Disseminate(VersionedValue<TValue> committed, IEnumerable<ReplicaId> members)
    {
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(members);

        foreach(ReplicaId member in members)
        {
            TaskCompletionSource<bool> completion = new();
            Enqueue(new Dissemination(HostIndexOf(member), committed, completion));
            Absorb(completion.Task);
        }
    }


    /// <summary>Advances the clock by one tick, marking a history boundary such as an operation start.</summary>
    /// <returns>The new instant.</returns>
    /// <remarks>
    /// A single tick rather than a hop, so a history boundary orders strictly against transport progress
    /// without being mistaken for it.
    /// </remarks>
    public long Tick()
    {
        AssertOwnerThread();

        return ++Now;
    }


    /// <summary>
    /// Creates a register for <paramref name="self"/> whose transport, clock, catch-up and dissemination are
    /// all this cluster's, so a whole run replays from <see cref="Seed"/> alone.
    /// </summary>
    /// <param name="self">The replica the register writes as, which must appear in the schedule's order.</param>
    /// <param name="attemptsPerRecorder">How many times one step may send to one host before abandoning it for that step.</param>
    /// <returns>The register.</returns>
    /// <remarks>
    /// <para>
    /// The priority draw is seeded from this cluster's seed and the replica rather than taken from
    /// <see cref="ProposalPriority.Cryptographic"/>, because a bench whose priorities are drawn from the
    /// system source replays its delivery order and not its decisions.
    /// </para>
    /// <para>
    /// BOTH THE STAND-DOWN AND THE CATCH-UP ARE SUPPLIED, and one without the other does not work. The
    /// stand-down keeps a writer woken after a rival closed its version from spending a round on a closed
    /// instance, but it returns before the proposer runs and so skips the point at which a losing attempt
    /// adopts the winner. A writer given only the stand-down never advances past the version it stood down
    /// at. The catch-up is what lets it advance, and it is the same query a deployed replica makes.
    /// </para>
    /// </remarks>
    public QuePaxaVersionedRegister<TValue> CreateRegister(ReplicaId self, int attemptsPerRecorder)
    {
        //The sender is captured here rather than read off the proposal key, because a proposer carrying
        //another lane's template names the adopted owner and would under-report contention.
        VersionedRecorderEndpointDelegate<VersionedValue<TValue>> Resolve(ReplicaId member)
        {
            int host = HostIndexOf(member);

            return (request, _) =>
            {
                TaskCompletionSource<VersionedRecordReply<VersionedValue<TValue>>> completion = new();
                Enqueue(new RecordCall(host, self, request, completion));

                return new ValueTask<VersionedRecordReply<VersionedValue<TValue>>>(completion.Task);
            };
        }

        ReadCommittedRecordDelegate<TValue> ResolveReader(ReplicaId member)
        {
            int host = HostIndexOf(member);

            return _ =>
            {
                TaskCompletionSource<VersionedValue<TValue>?> completion = new();
                Enqueue(new ReadCall(host, self, completion));

                return new ValueTask<VersionedValue<TValue>?>(completion.Task);
            };
        }

        return new QuePaxaVersionedRegister<TValue>(
            Genesis,
            self,
            BaseDelay,
            Resolve,
            SeededPrioritySource.For(Seed, self),
            attemptsPerRecorder,
            Clock,
            ObserveAsync,
            ResolveReader,
            PublishAsync,
            ObserveMemberAsync);
    }


    /// <summary>The index of the host that is <paramref name="member"/>.</summary>
    /// <param name="member">The member to look for.</param>
    /// <returns>That host's index.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no host of this bench is that member, which is how a resolver reports one it cannot resolve.</exception>
    private int HostIndexOf(ReplicaId member)
    {
        int index = TryHostIndexOf(member);
        if(index < 0)
        {
            throw new InvalidOperationException($"No host of this bench is {member}.");
        }

        return index;
    }


    /// <summary>The index of the host that is <paramref name="member"/>, or a negative value when none is.</summary>
    /// <param name="member">The member to look for.</param>
    /// <returns>That host's index, or a negative value.</returns>
    /// <remarks>
    /// A membership may name a replica this bench runs no host for, which is the deployment whose endpoint
    /// map does not reach one of its members, so the paths that tolerate an unreachable member ask this and
    /// the ones that report an unresolvable member ask <see cref="HostIndexOf"/>.
    /// </remarks>
    private int TryHostIndexOf(ReplicaId member)
    {
        for(int index = 0; index < Hosts.Length; index++)
        {
            if(Hosts[index].Self.Replica.Equals(member))
            {
                return index;
            }
        }

        return -1;
    }


    /// <summary>
    /// Pumps until no message is in flight, no timer is armed, and every task in <paramref name="clients"/>
    /// has completed.
    /// </summary>
    /// <param name="clients">The client tasks this run is driving.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="clients"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the pump exceeds its event bound, which reports a livelock instead of hanging the suite, and
    /// if the schedule empties while a client is still incomplete, which reports a client parked on a seam
    /// this cluster does not own instead of returning a history that is silently short.
    /// </exception>
    public void RunToQuiescence(IReadOnlyList<Task> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        AssertOwnerThread();

        int events = 0;
        while(Step())
        {
            events++;
            if(events > MaxEvents)
            {
                throw new InvalidOperationException($"The bench ran more than {MaxEvents} events without quiescing, so a write is not terminating.");
            }
        }

        int unfinished = clients.Count(client => !client.IsCompleted);
        if(unfinished > 0)
        {
            throw new InvalidOperationException($"The schedule is empty with {unfinished} of {clients.Count} clients incomplete, so a client is parked on something this cluster does not drive and its operations would be missing from the history.");
        }
    }


    /// <summary>
    /// Runs the next scheduled event: the earliest armed timer when it is due no later than the next
    /// delivery, and otherwise one pseudo-randomly chosen in-flight message.
    /// </summary>
    /// <returns>Whether an event ran; <see langword="false"/> when the schedule is empty.</returns>
    public bool Step()
    {
        AssertOwnerThread();

        long? earliestDeadline = EarliestDeadline();
        if(InFlight.Count == 0)
        {
            return earliestDeadline is { } deadline && FireAt(deadline);
        }

        //A timer due before the next delivery would land in the past if the delivery ran first, so the
        //earlier event runs first and the clock stays monotone in both readings.
        if(earliestDeadline is { } due && due <= Now + HopLatency.Ticks)
        {
            return FireAt(due);
        }

        return Deliver(NextIndex(InFlight.Count));
    }


    private bool FireAt(long deadline)
    {
        PumpTimer timer = Timers.Where(candidate => candidate.Deadline == deadline).OrderBy(candidate => candidate.Ordinal).First();
        _ = Timers.Remove(timer);

        if(InFlight.Count > 0)
        {
            TimersFiredUnderTraffic++;
        }

        if(deadline < Now)
        {
            TimersFiredLate++;
        }

        Now = Math.Max(Now, deadline);
        TimersFired++;
        TraceLines.Add($"{Now}:timer{timer.Ordinal}");

        //The callback resumes the parked client inline, so whatever it sends is in flight before this
        //returns and the schedule is never observed empty while that client has work outstanding.
        timer.Fire();

        return true;
    }


    private bool Deliver(int index)
    {
        Message message = InFlight[index];
        InFlight.RemoveAt(index);
        Now += HopLatency.Ticks;

        if(Partitioned[message.Host])
        {
            TraceLines.Add($"{Now}:h{message.Host}:{message.Kind}:lost");
            message.Lose(new IOException($"Host {message.Host} is partitioned."));

            return true;
        }

        //A held host is reached and takes nothing, so the offer fails exactly as an unreachable host's does
        //while every other call to it still answers.
        if(message.OffersARecord && DisseminationHeld[message.Host])
        {
            TraceLines.Add($"{Now}:h{message.Host}:{message.Kind}:held");
            message.Lose(new IOException($"Host {message.Host} is not taking offered records."));

            return true;
        }

        TraceLines.Add($"{Now}:h{message.Host}:{message.Kind}");
        switch(message)
        {
            case RecordCall call:
            {
                RecordRequestCounts[call.Host]++;
                RecordCalls.Add(new DeliveredRecordCall(call.Host, call.Sender, call.Request.Request.Proposal.Key.Owner.Replica, call.Request.Version, call.Request.Request.Step));

                //A host declines by throwing, which a transport reports as a fault like any other, so the
                //decline travels back as a failed reply rather than as a reply the protocol can read.
                try
                {
                    VersionedRecordReply<VersionedValue<TValue>> reply = Hosts[call.Host].Handle(call.Request);
                    Enqueue(new RecordAnswer(call.Host, call.Sender, reply, call.Completion));
                }
                catch(ArgumentOutOfRangeException declined)
                {
                    Enqueue(new RecordFault(call.Host, call.Sender, declined, call.Completion));
                }

                break;
            }

            case RecordAnswer answer:
            {
                answer.Completion.SetResult(answer.Reply);

                break;
            }

            case RecordFault fault:
            {
                fault.Completion.SetException(fault.Declined);

                break;
            }

            case Dissemination dissemination:
            {
                if(Hosts[dissemination.Host].Learn(dissemination.Committed))
                {
                    DisseminationsLearned++;
                    Adoptions.Add(new AdoptedRecord(Hosts[dissemination.Host].Self.Replica, dissemination.Committed));
                }

                dissemination.Completion.SetResult(true);

                break;
            }

            case ReadCall read:
            {
                read.Completion.SetResult(Hosts[read.Host].Committed);

                break;
            }

            case ObserveCall observe:
            {
                observe.Completion.SetResult(Hosts[observe.Host].Committed?.Version ?? RegisterVersion.Unwritten);

                break;
            }

            default:
                throw new InvalidOperationException($"The bench has no delivery for a {message.GetType().Name}.");
        }

        return true;
    }


    /// <summary>
    /// Offers a decided record to the hosts its audience names, as scheduled deliveries, which is the
    /// dissemination a deployment owes and the reason the next version becomes servable at all.
    /// </summary>
    /// <param name="committed">The decided record.</param>
    /// <param name="audience">The replicas the register computed the offer for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once every offer has landed or failed.</returns>
    /// <remarks>
    /// The audience is what this offers to, and never the host list. At a configuration boundary the two
    /// coincide for a cluster whose hosts are all members, and they part at the ordinary decide after it,
    /// where a host the change removed is still up and must be offered nothing.
    /// </remarks>
    private async ValueTask PublishAsync(VersionedValue<TValue> committed, ImmutableArray<ReplicaId> audience, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pending = new List<Task<bool>>(audience.Length);
        foreach(ReplicaId member in audience)
        {
            int host = TryHostIndexOf(member);
            if(host < 0)
            {
                //A member this bench runs no host for cannot be offered anything, and a push that could not
                //be made to one member is not a reason to skip the rest of the audience.
                continue;
            }

            TaskCompletionSource<bool> completion = new();
            Enqueue(new Dissemination(host, committed, completion));
            pending.Add(completion.Task);
        }

        //A host that cannot be reached simply does not learn. Faulting here would fault an attempt that has
        //already decided, which would report a committed write as a failure.
        foreach(Task<bool> delivery in pending)
        {
            try
            {
                _ = await delivery.ConfigureAwait(false);
            }
            catch(IOException)
            {
            }
        }
    }


    /// <summary>
    /// Reports what the host that is <paramref name="member"/> has committed, which is what a readiness
    /// report is assembled from.
    /// </summary>
    /// <param name="member">The member to ask.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>That member's highest committed version, which is unwritten when it has learned nothing.</returns>
    /// <remarks>
    /// A member this bench runs no host for faults rather than answering unwritten, because a report cannot
    /// tell a member that has learned nothing from one nothing reaches unless the two answer differently.
    /// </remarks>
    private ValueTask<MemberVersionReport> ObserveMemberAsync(ReplicaId member, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int host = TryHostIndexOf(member);
        if(host < 0)
        {
            return ValueTask.FromException<MemberVersionReport>(new InvalidOperationException($"No host of this bench is {member}."));
        }

        TaskCompletionSource<RegisterVersion> completion = new();
        Enqueue(new ObserveCall(host, completion));

        return AttributeAsync(Hosts[host].Self, completion.Task);
    }


    /// <summary>
    /// Labels a version answer with the identity of the host that produced it, which this bench resolved the
    /// probe to: the routing is the bench's own, so the assertion is honest by construction.
    /// </summary>
    private static async ValueTask<MemberVersionReport> AttributeAsync(HostId recorder, Task<RegisterVersion> answer)
    {
        return new MemberVersionReport(recorder, await answer.ConfigureAwait(false));
    }


    /// <summary>
    /// Reports the highest version any reachable host has committed, which is what lets a writer woken after
    /// a rival closed its version stand down instead of running a closed instance.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest version any host answered with.</returns>
    /// <remarks>
    /// It reads every host and not the membership's members, because a stand-down signal is a deployment-wide
    /// aggregate rather than a statement about who records: a host outside the configuration that already
    /// holds a later record is as good a reason to stand down as a member holding it.
    /// </remarks>
    private async ValueTask<RegisterVersion> ObserveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pending = new List<Task<RegisterVersion>>(Hosts.Length);
        for(int index = 0; index < Hosts.Length; index++)
        {
            TaskCompletionSource<RegisterVersion> completion = new();
            Enqueue(new ObserveCall(index, completion));
            pending.Add(completion.Task);
        }

        RegisterVersion highest = RegisterVersion.Unwritten;
        foreach(Task<RegisterVersion> answer in pending)
        {
            try
            {
                RegisterVersion reported = await answer.ConfigureAwait(false);
                if(reported > highest)
                {
                    highest = reported;
                }
            }
            catch(IOException)
            {
            }
        }

        return highest;
    }


    private void Enqueue(Message message)
    {
        AssertOwnerThread();
        InFlight.Add(message);
    }


    /// <summary>Observes an offer no caller awaits.</summary>
    /// <param name="offer">The delivery to observe.</param>
    /// <remarks>
    /// A delivery lost at a partitioned or held host faults its task, and a faulted task nobody read
    /// resurfaces later on a finalizer thread as a failure belonging to whatever test happened to be running
    /// then. Reading it here keeps a lost offer's report inside the run that produced it.
    /// </remarks>
    private static void Absorb(Task offer)
    {
        _ = offer.ContinueWith(static delivered => _ = delivered.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }


    /// <summary>The genesis membership a cluster over <paramref name="schedule"/>'s whole agreed order founds.</summary>
    /// <param name="schedule">The agreed order.</param>
    /// <returns>The genesis membership.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> is <see langword="null"/>.</exception>
    private static QuePaxaConfiguration GenesisOver(QuePaxaLeaderSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return QuePaxaConfiguration.CreateGenesis(Membership.Of([.. schedule.Schedule.Order]));
    }


    /// <summary>The first <paramref name="hostCount"/> replicas of <paramref name="schedule"/>'s agreed order.</summary>
    /// <param name="schedule">The agreed order.</param>
    /// <param name="hostCount">How many of its replicas run a host.</param>
    /// <returns>Those replicas, in the order's own order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="hostCount"/> is less than one or above the order's length.</exception>
    private static ImmutableArray<ReplicaId> LeadingReplicasOf(QuePaxaLeaderSchedule schedule, int hostCount)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentOutOfRangeException.ThrowIfLessThan(hostCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hostCount, schedule.Schedule.Order.Length);

        return ImmutableArray.Create(schedule.Schedule.Order, 0, hostCount);
    }


    /// <summary>The hedging increment <paramref name="schedule"/> stands on.</summary>
    /// <param name="schedule">The schedule.</param>
    /// <returns>Its base delay.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="schedule"/> is <see langword="null"/>.</exception>
    private static TimeSpan BaseDelayOf(QuePaxaLeaderSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.Schedule.BaseDelay;
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


    private int NextIndex(int count)
    {
        randomState ^= randomState << 13;
        randomState ^= randomState >> 17;
        randomState ^= randomState << 5;

        return (int)(randomState % (uint)count);
    }


    private void AssertOwnerThread()
    {
        if(Environment.CurrentManagedThreadId != OwnerThreadId)
        {
            throw new InvalidOperationException($"The bench was driven from thread {Environment.CurrentManagedThreadId} and owns thread {OwnerThreadId}. A continuation that left the pump's thread makes the schedule and the clock race, and a history built under that race is not the run the seed replays.");
        }
    }


    /// <summary>One record request as it reached a host, which is what a reach pin reads.</summary>
    /// <param name="Host">The host that served it.</param>
    /// <param name="Sender">The replica whose register sent it, captured at the endpoint.</param>
    /// <param name="KeyOwner">
    /// The replica owning the proposal key it carried, which is NOT the sender in general: a proposer that
    /// adopted another lane's template carries that lane's owner, and the key is never restamped.
    /// </param>
    /// <param name="Version">The version it was addressed to.</param>
    /// <param name="Step">The step it carried.</param>
    internal sealed record DeliveredRecordCall(int Host, ReplicaId Sender, ReplicaId KeyOwner, RegisterVersion Version, RecorderStep Step);


    /// <summary>One committed record as a host adopted it, which is what a safety witness folds.</summary>
    /// <param name="Member">The replica whose host adopted it.</param>
    /// <param name="Record">The record adopted.</param>
    internal sealed record AdoptedRecord(ReplicaId Member, VersionedValue<TValue> Record);


    private abstract record Message(int Host)
    {
        public abstract string Kind { get; }

        /// <summary>Whether this message offers a record to its host, which is what a hold stops.</summary>
        public virtual bool OffersARecord => false;

        public abstract void Lose(Exception cause);
    }


    private sealed record RecordCall(
        int Host,
        ReplicaId Sender,
        VersionedRecordRequest<VersionedValue<TValue>> Request,
        TaskCompletionSource<VersionedRecordReply<VersionedValue<TValue>>> Completion): Message(Host)
    {
        /// <summary>
        /// The priority is part of the kind so that the delivery trace records what a request proposed and not
        /// only that one was sent.
        /// </summary>
        /// <remarks>
        /// A replay comparison over a trace without it cannot tell a seeded draw from an unpredictable one,
        /// because the message schedule is the same either way.
        /// </remarks>
        public override string Kind => $"record:p{Request.Request.Proposal.Key.Priority.Value}";

        public override void Lose(Exception cause) => Completion.SetException(cause);
    }


    private sealed record RecordAnswer(
        int Host,
        ReplicaId Sender,
        VersionedRecordReply<VersionedValue<TValue>> Reply,
        TaskCompletionSource<VersionedRecordReply<VersionedValue<TValue>>> Completion): Message(Host)
    {
        public override string Kind => "record-reply";

        public override void Lose(Exception cause) => Completion.SetException(cause);
    }


    private sealed record RecordFault(
        int Host,
        ReplicaId Sender,
        Exception Declined,
        TaskCompletionSource<VersionedRecordReply<VersionedValue<TValue>>> Completion): Message(Host)
    {
        public override string Kind => "record-declined";

        public override void Lose(Exception cause) => Completion.SetException(cause);
    }


    private sealed record Dissemination(int Host, VersionedValue<TValue> Committed, TaskCompletionSource<bool> Completion): Message(Host)
    {
        public override string Kind => "learn";

        public override bool OffersARecord => true;

        public override void Lose(Exception cause) => Completion.SetException(cause);
    }


    private sealed record ReadCall(int Host, ReplicaId Sender, TaskCompletionSource<VersionedValue<TValue>?> Completion): Message(Host)
    {
        public override string Kind => "read";

        public override void Lose(Exception cause) => Completion.SetException(cause);
    }


    private sealed record ObserveCall(int Host, TaskCompletionSource<RegisterVersion> Completion): Message(Host)
    {
        public override string Kind => "observe";

        public override void Lose(Exception cause) => Completion.SetException(cause);
    }


    /// <summary>
    /// The clock the pump owns. Every reading comes from <see cref="Now"/>, so a delay expressed in
    /// milliseconds and a number of hops are the same quantity measured two ways.
    /// </summary>
    private sealed class PumpTimeProvider(InterleavedVersionedQuePaxaCluster<TValue> cluster): TimeProvider
    {
        private static DateTimeOffset Epoch { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private int Ordinals { get; set; }


        public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(cluster.Now);

        public override long GetTimestamp() => cluster.Now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;


        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);

            if(period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("The bench schedules one-shot timers only, which is the shape a hedging delay takes. A periodic timer would arm once here and never repeat, which is a silent loss rather than a refusal.");
            }

            cluster.AssertOwnerThread();
            var timer = new PumpTimer(cluster, callback, state, ++Ordinals);
            timer.Arm(dueTime);

            return timer;
        }
    }


    /// <summary>A one-shot timer the pump fires, rather than one a platform timer queue fires.</summary>
    private sealed class PumpTimer(InterleavedVersionedQuePaxaCluster<TValue> cluster, TimerCallback callback, object? state, int ordinal): ITimer
    {
        public int Ordinal => ordinal;

        public long Deadline { get; private set; }


        public void Arm(TimeSpan dueTime)
        {
            _ = cluster.Timers.Remove(this);
            if(dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            Deadline = cluster.Now + Math.Max(dueTime.Ticks, 0);
            cluster.Timers.Add(this);
        }


        public void Fire() => callback(state);


        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if(period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("The bench schedules one-shot timers only.");
            }

            Arm(dueTime);

            return true;
        }


        public void Dispose() => cluster.Timers.Remove(this);


        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }


    /// <summary>
    /// A distinct priority stream per replica per seed, so a run is reproducible and two writers never draw
    /// the identical sequence.
    /// </summary>
    /// <remarks>
    /// Xorshift64 rather than the cryptographic source: every priority in a run is reproducible from its
    /// seed, so a failing interleaving replays the identical draws on any runtime.
    /// </remarks>
    private sealed class SeededPrioritySource
    {
        private ulong state;

        private SeededPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        public static ProposalPrioritySourceDelegate For(int seed, ReplicaId replica)
        {
            ulong mixed = ((ulong)(uint)seed * 1_000_003UL) ^ Fingerprint(replica);
            SeededPrioritySource source = new(mixed);

            return source.Next;
        }


        private static ulong Fingerprint(ReplicaId replica)
        {
            Span<byte> buffer = stackalloc byte[ReplicaId.Size];
            replica.CopyTo(buffer);

            ulong value = 1469598103934665603UL;
            foreach(byte octet in buffer)
            {
                value = (value ^ octet) * 1099511628211UL;
            }

            return value;
        }


        private ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;

            //The two reserved endpoints are excluded, so the source honours the delegate's contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }
}
