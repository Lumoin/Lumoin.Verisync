using System.Collections.Immutable;
using System.Globalization;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The Fast CASPaxos half of the bridge: the shipped proposer, the shipped hedged writer and the shipped
/// acceptor nodes driven end to end over the same virtual clock the QuePaxa arm runs on.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ARM THAT PUTS THE TWO PROTOCOLS IN ONE CURRENCY. The existing evidence prices a Fast CASPaxos
/// fallback from two constants over an oracle predicate and has no per-writer millisecond at all. Here the
/// fallback is executed: a writer that missed its fast quorum runs
/// <see cref="FastProposer{TValue}.RecoverAsync"/> under a classic ballot, recoveries contend with each other
/// as they would in a deployment, and every instant is measured.
/// </para>
/// <para>
/// THE SHIPPED PROPOSER IS PACED BY THE FARTHEST ACCEPTOR. <see cref="FastProposer{TValue}"/> gathers every
/// phase over all acceptors and does not act on the first quorum, so its fast write completes when the last
/// reply has landed rather than when the fast-quorum-th did. Both instants are measured and both are
/// reported: the shipped one is what a deployment gets, the quorum one is what the distance arithmetic
/// prices, and reporting either alone misstates the comparison.
/// </para>
/// <para>
/// THE STAGGER IS THE SHIPPED POLICY RATHER THAN A RESTATEMENT. <see cref="HedgedFastWriter{TValue}"/> takes
/// the pump's <see cref="TimeProvider"/> and awaits its delay against it, so the rotation, the position
/// arithmetic and the documented zero-delay degenerate case under measurement are
/// <see cref="HedgingSchedule"/>'s own.
/// </para>
/// <para>
/// EVERY LATENCY IS MEASURED FROM THE WRITER'S OWN ACTIVATION, which is its arrival and its own hedging delay
/// added together and is the origin the QuePaxa arm already reports from. The two arms' latency columns are
/// argmin'd against each other, so one origin is what makes that column one currency; the delay itself is
/// reported separately as the cost side of the ladder's ledger.
/// </para>
/// </remarks>
internal static class FastCasPaxosArm
{
    /// <summary>The pump bound one trial runs under.</summary>
    public const long DefaultEventBudget = 200_000;

    /// <summary>How many classic ballots one writer may spend before a trial gives up on it.</summary>
    public const int DefaultMaxRecoveryAttempts = 8;


    /// <summary>
    /// Runs one trial and reports what every writer's write cost.
    /// </summary>
    /// <param name="request">The trial's arguments.</param>
    /// <returns>One measurement per writer, in writer order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the writer count and the arrival count disagree.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the run cannot drain, or drains with a writer still parked.</exception>
    public static ImmutableArray<FastWriterMeasurement> RunTrial(FastTrialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Topology);
        ArgumentNullException.ThrowIfNull(request.Jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.WriterCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxRecoveryAttempts, 1);
        if(request.ArrivalMicroseconds.IsDefault || request.ArrivalMicroseconds.Length != request.WriterCount)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trial has {request.WriterCount} writers and {(request.ArrivalMicroseconds.IsDefault ? 0 : request.ArrivalMicroseconds.Length)} arrivals."), nameof(request));
        }

        Topology topology = request.Topology;
        int replicaCount = topology.SiteCount;
        int fastQuorum = QuorumDistance.FastQuorum(replicaCount);
        var pump = new VirtualTimePump(request.EventBudget);

        var nodes = new ConsensusNode<string>[replicaCount];
        for(int site = 0; site < replicaCount; site++)
        {
            nodes[site] = new ConsensusNode<string>();
        }

        ImmutableArray<ReplicaId> order = [.. Enumerable.Range(0, request.WriterCount).Select(HarnessIdentity.Replica)];
        HedgingSchedule schedule = HedgingSchedule.Create(order, request.HedgingBaseDelay);

        var states = new WriterState[request.WriterCount];
        var writers = new HedgedFastWriter<string>[request.WriterCount];
        var proposers = new FastProposer<string>[request.WriterCount];
        var clients = new Task[request.WriterCount];

        for(int index = 0; index < request.WriterCount; index++)
        {
            int writer = index;
            int site = writer % replicaCount;
            states[writer] = new WriterState(fastQuorum);

            var endpoints = new ConsensusEndpointDelegate<string>[replicaCount];
            for(int acceptorIndex = 0; acceptorIndex < replicaCount; acceptorIndex++)
            {
                int acceptor = acceptorIndex;
                endpoints[acceptor] = (consensusRequest, _) => Send(pump, topology, nodes, request, states[writer], writer, site, acceptor, consensusRequest);
            }

            proposers[writer] = new FastProposer<string>(endpoints);
            writers[writer] = new HedgedFastWriter<string>(proposers[writer], schedule, HarnessIdentity.Replica(writer), pump.Clock, request.LearnSignal?.Invoke(writer));

            clients[writer] = Task.CompletedTask;
            pump.ScheduleAt(request.ArrivalMicroseconds[writer], () => clients[writer] = WriteAsync(pump, writers[writer], proposers[writer], states[writer], writer, request.MaxRecoveryAttempts));
        }

        pump.Run(clients);

        ImmutableArray<FastWriterMeasurement>.Builder measurements = ImmutableArray.CreateBuilder<FastWriterMeasurement>(request.WriterCount);
        for(int writer = 0; writer < request.WriterCount; writer++)
        {
            WriterState state = states[writer];
            long arrival = request.ArrivalMicroseconds[writer];

            //Every reading is measured from this writer's own ACTIVATION, which is its arrival and the delay
            //the shipped writer reported waiting added together. The arrival and the wait travel in the
            //record beside them, so the client-visible currency is exactly reconstructable from the row.
            long activation = arrival + state.AddedWaitMicroseconds;
            measurements.Add(new FastWriterMeasurement(
                writer,
                writer % replicaCount,
                state.Activated,
                arrival,
                state.AddedWaitMicroseconds,
                state.FastAcceptedCount,
                state.FastWriteReturnedInstant - activation,
                state.FastQuorumInstant is { } quorumInstant ? quorumInstant - activation : null,
                state.FastAcceptedCount >= fastQuorum,
                state.RecoveryEntered,
                state.RecoveryAttempts,
                state.Phases.Count,
                state.IsCommitted,
                state.CommitInstant is { } commitInstant ? commitInstant - activation : null,
                state.GiveUpInstant is { } giveUpInstant ? giveUpInstant - activation : null,
                state.CommittedValue));
        }

        return measurements.ToImmutable();
    }


    private static async Task WriteAsync(
        VirtualTimePump pump,
        HedgedFastWriter<string> writerClient,
        FastProposer<string> proposer,
        WriterState state,
        int writer,
        int maxRecoveryAttempts)
    {
        string value = HarnessIdentity.Value(writer);
        ReplicaId self = HarnessIdentity.Replica(writer);

        HedgedFastWriteOutcome hedged = await writerClient.TryWriteAsync(FastBallot.InitialFast(), value, CancellationToken.None).ConfigureAwait(false);

        //The continuation resumes inline on the pump's thread at the instant the last reply landed, so this
        //reads the shipped gather's own instant rather than the instant the pump next happens to look.
        state.Activated = hedged.Activated;
        state.AddedWaitMicroseconds = VirtualTimePump.ToMicroseconds(hedged.Delay);
        state.FastAcceptedCount = hedged.AcceptedCount;
        state.FastWriteReturnedInstant = pump.Now;

        if(hedged.IsCommitted)
        {
            state.IsCommitted = true;
            state.CommitInstant = pump.Now;
            state.CommittedValue = value;

            return;
        }

        //A writer that stood down sent nothing at all, which is distinct from a fast write that failed to
        //reach its quorum, and it owes no recovery.
        if(!hedged.Activated)
        {
            return;
        }

        state.RecoveryEntered = true;
        for(int round = 1; round <= maxRecoveryAttempts && !state.IsCommitted; round++)
        {
            state.RecoveryAttempts++;
            ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(round, self), previous => previous ?? value, CancellationToken.None).ConfigureAwait(false);
            if(outcome.IsChosen)
            {
                state.IsCommitted = true;
                state.CommitInstant = pump.Now;
                state.CommittedValue = outcome.Value;
            }
        }

        //A write that exhausted its ladder is censored rather than absent, and what it cost before it was
        //abandoned is the instant that makes the censoring visible in a percentile rather than merely counted.
        if(!state.IsCommitted)
        {
            state.GiveUpInstant = pump.Now;
        }
    }


    private static ValueTask<ConsensusReply<string>> Send(
        VirtualTimePump pump,
        Topology topology,
        ConsensusNode<string>[] nodes,
        FastTrialRequest request,
        WriterState state,
        int writer,
        int site,
        int acceptor,
        ConsensusRequest<string> consensusRequest)
    {
        //ONE PHASE SENDS ONE REQUEST OBJECT TO EVERY ACCEPTOR, so counting the distinct requests a writer put
        //on the transport counts the phases it executed, whatever the fan-out. That is what makes the step
        //column a measurement rather than a lookup on the outcome.
        int step = state.ObservePhase(consensusRequest);

        var completion = new TaskCompletionSource<ConsensusReply<string>>();
        long outbound = topology.OneWay(site, acceptor);
        long inbound = topology.OneWay(acceptor, site);

        pump.ScheduleAfter(outbound + request.Jitter.Draw(request.TrialSeed, writer, acceptor, step, 0, outbound), () =>
        {
            ConsensusReply<string> reply = nodes[acceptor].Handle(consensusRequest);
            pump.ScheduleAfter(inbound + request.Jitter.Draw(request.TrialSeed, writer, acceptor, step, 1, inbound), () =>
            {
                state.ObserveReply(pump.Now, consensusRequest, reply);
                completion.SetResult(reply);
            });
        });

        return new ValueTask<ConsensusReply<string>>(completion.Task);
    }


    /// <summary>
    /// What the pump and the client jointly record about one writer while its trial runs.
    /// </summary>
    /// <remarks>
    /// The quorum instant cannot come from the client, because the shipped proposer never observes it: it
    /// gathers all acceptors and reports the total. The transport is where the fast-quorum-th accepting reply
    /// is visible, so that is where it is stamped.
    /// </remarks>
    private sealed class WriterState(int fastQuorum)
    {
        public bool Activated { get; set; }

        public long AddedWaitMicroseconds { get; set; }

        public int FastAcceptedCount { get; set; }

        public long FastWriteReturnedInstant { get; set; }

        public long? FastQuorumInstant { get; private set; }

        public bool RecoveryEntered { get; set; }

        public int RecoveryAttempts { get; set; }

        public bool IsCommitted { get; set; }

        public long? CommitInstant { get; set; }

        public long? GiveUpInstant { get; set; }

        public string? CommittedValue { get; set; }

        /// <summary>
        /// Reference identity rather than the record's own value equality: two phases that happened to carry
        /// equal ballots and values are still two phases, and a count that collapsed them would under-report
        /// the very fallback this arm exists to measure.
        /// </summary>
        public HashSet<object> Phases { get; } = new(ReferenceEqualityComparer.Instance);


        private int FastAcceptsSeen { get; set; }


        /// <summary>Records that <paramref name="request"/> went on the transport and returns the phase it belongs to.</summary>
        /// <param name="request">The request being sent.</param>
        /// <returns>The one-based phase index, which is what a jitter draw is keyed on.</returns>
        public int ObservePhase(ConsensusRequest<string> request)
        {
            _ = Phases.Add(request);

            return Phases.Count;
        }


        /// <summary>Stamps the instant the fast-quorum-th accepting reply landed.</summary>
        /// <param name="instant">The instant the reply landed.</param>
        /// <param name="request">The request it answers.</param>
        /// <param name="reply">The reply.</param>
        public void ObserveReply(long instant, ConsensusRequest<string> request, ConsensusReply<string> reply)
        {
            if(request is not AcceptRequest<string> { Ballot.IsFast: true } || reply is not AcceptReply<string> { Accepted: true })
            {
                return;
            }

            FastAcceptsSeen++;
            if(FastAcceptsSeen == fastQuorum)
            {
                FastQuorumInstant = instant;
            }
        }
    }
}
