using System.Collections.Immutable;
using System.Globalization;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The Fast CASPaxos half of the read-modify-write rider: the shipped proposer, the shipped hedged writer and
/// the shipped acceptor nodes driven end to end over the same virtual clock the QuePaxa half runs on.
/// </summary>
/// <remarks>
/// <para>
/// THE CHANGE FUNCTION RUNS INSIDE THE ROUND, WHICH IS THE WHOLE POINT OF THE RIDER.
/// <see cref="FastProposer{TValue}.RecoverAsync"/> prepares a majority, recovers the value that round found,
/// applies the change to THAT value and accepts the result, all in one round. A writer whose blind fast round
/// was split therefore does not re-read and does not re-propose: it recovers the rival's change and composes
/// its own on top inside the round it is already running, and only a ballot another proposer pre-empted costs
/// it a further round.
/// </para>
/// <para>
/// THE BLIND FAST ROUND IS STILL RUN, AND IT IS AS BLIND AS A QUEPAXA PROPOSAL. A fast write carries a value
/// computed before any round, so under a read-modify-write it is sound only because the change carries an
/// apply-once token: a writer's own partially accepted fast value can be the value its own later recovery
/// tallies, and a plain append would then compose the change on top of itself. The firings are counted rather
/// than assumed away, and the rate they fire at is the observable difference between this arm's semantics and
/// the QuePaxa arm's, where a losing proposal is discarded whole and never composed.
/// </para>
/// <para>
/// THE STAGGER IS THE SHIPPED POLICY RATHER THAN A RESTATEMENT. <see cref="HedgedFastWriter{TValue}"/> takes
/// the pump's <see cref="TimeProvider"/> and awaits its delay against it, so the rotation, the position
/// arithmetic and the documented zero-delay degenerate case under measurement are
/// <see cref="HedgingSchedule"/>'s own.
/// </para>
/// <para>
/// EVERY LATENCY IS MEASURED FROM THE WRITER'S OWN ACTIVATION, which is its arrival and its own hedging delay
/// added together and is the origin the QuePaxa read-modify-write arm also reports from.
/// </para>
/// </remarks>
internal static class RmwFastCasPaxosArm
{
    /// <summary>The pump bound one read-modify-write trial runs under.</summary>
    public const long DefaultEventBudget = 400_000;

    /// <summary>How many classic ballots one writer may spend before a trial gives up on it.</summary>
    public const int DefaultMaxRecoveryRounds = 24;


    /// <summary>
    /// Runs one trial in which every writer applies one change, and reports what each change cost.
    /// </summary>
    /// <param name="request">The trial's arguments.</param>
    /// <returns>One measurement per writer, the value the acceptors were left holding, and the oracle's verdict on it.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the writer count and the arrival count disagree.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the run cannot drain, or drains with a writer still parked.</exception>
    public static RmwTrialOutcome<RmwFastWriterMeasurement> RunTrial(RmwFastTrialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Topology);
        ArgumentNullException.ThrowIfNull(request.Jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.WriterCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxRecoveryRounds, 1);
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
        var writerClients = new HedgedFastWriter<string>[request.WriterCount];
        var proposers = new FastProposer<string>[request.WriterCount];
        var clients = new Task[request.WriterCount];

        for(int index = 0; index < request.WriterCount; index++)
        {
            int writer = index;
            int site = writer % replicaCount;
            states[writer] = new WriterState(RmwFold.Token(writer), fastQuorum);

            var endpoints = new ConsensusEndpointDelegate<string>[replicaCount];
            for(int acceptorIndex = 0; acceptorIndex < replicaCount; acceptorIndex++)
            {
                int acceptor = acceptorIndex;
                endpoints[acceptor] = (consensusRequest, _) => Send(pump, topology, nodes, request, states[writer], writer, site, acceptor, consensusRequest);
            }

            proposers[writer] = new FastProposer<string>(endpoints);
            writerClients[writer] = new HedgedFastWriter<string>(proposers[writer], schedule, HarnessIdentity.Replica(writer), pump.Clock, null);

            clients[writer] = Task.CompletedTask;
            pump.ScheduleAt(request.ArrivalMicroseconds[writer], () => clients[writer] = WriteAsync(pump, writerClients[writer], proposers[writer], states[writer], writer, request.MaxRecoveryRounds));
        }

        pump.Run(clients);

        ImmutableArray<RmwFastWriterMeasurement>.Builder measurements = ImmutableArray.CreateBuilder<RmwFastWriterMeasurement>(request.WriterCount);
        for(int writer = 0; writer < request.WriterCount; writer++)
        {
            WriterState state = states[writer];
            long arrival = request.ArrivalMicroseconds[writer];
            long activation = arrival + state.AddedWaitMicroseconds;

            measurements.Add(new RmwFastWriterMeasurement(
                writer,
                writer % replicaCount,
                state.Token,
                state.Activated,
                arrival,
                state.AddedWaitMicroseconds,
                state.FastAcceptedCount,
                state.ReachedFastQuorum(),
                state.RecoveryEntered,
                state.RecoveryRounds,
                Math.Max(state.RecoveryRounds - 1, 0),
                state.Phases.Count,
                state.ComposeCalls,
                state.ApplyOnceTokenFirings,
                state.RecomposedAgainstAnotherWriter,
                state.LastRecoveredValue,
                state.IsCommitted,
                state.CommitInstant is { } commitInstant ? commitInstant - activation : null,
                state.GiveUpInstant is { } giveUpInstant ? giveUpInstant - activation : null,
                state.CommittedValue));
        }

        ImmutableArray<RmwFastWriterMeasurement> writers = measurements.ToImmutable();
        string? finalValue = HighestAccepted(nodes);
        ImmutableArray<char> committedTokens = [.. writers.Where(measurement => measurement.IsCommitted).Select(measurement => measurement.Token)];

        return new RmwTrialOutcome<RmwFastWriterMeasurement>(writers, finalValue, RmwFold.Check(finalValue, committedTokens, request.WriterCount));
    }


    /// <summary>The value accepted at the highest ballot any acceptor of <paramref name="nodes"/> holds.</summary>
    /// <param name="nodes">The acceptor hosts.</param>
    /// <returns>The value, or <see langword="null"/> when nothing was ever accepted.</returns>
    /// <remarks>
    /// The oracle reads the acceptors rather than the writers, so a client that believes something the register
    /// does not hold cannot make the fold agree with itself. The highest accepted ballot is the sound reading:
    /// a value chosen at a lower ballot was recovered by whoever proposed the higher one, so every committed
    /// change is inside it.
    /// </remarks>
    private static string? HighestAccepted(ConsensusNode<string>[] nodes)
    {
        FastBallot highest = FastBallot.Zero;
        string? accepted = null;
        foreach(ConsensusNode<string> node in nodes)
        {
            FastAcceptor<string> acceptor = node.Acceptor;
            if(acceptor.AcceptedBallot > highest)
            {
                highest = acceptor.AcceptedBallot;
                accepted = acceptor.AcceptedValue;
            }
        }

        return accepted;
    }


    /// <summary>Runs one writer's whole read-modify-write and records what it cost.</summary>
    /// <param name="pump">The clock every instant is read from.</param>
    /// <param name="writerClient">The shipped hedged writer that drives the blind fast round.</param>
    /// <param name="proposer">The shipped proposer that runs the classic rounds.</param>
    /// <param name="state">The writer's record of its own write.</param>
    /// <param name="writer">The writer index, which owns the classic ballots' proposer identity.</param>
    /// <param name="maxRecoveryRounds">How many classic ballots the write may spend.</param>
    /// <returns>A task that completes when the write has settled.</returns>
    /// <remarks>
    /// The blind fast value is the change applied to the value this writer knows of, which is nothing at all
    /// when its round is the register's first; that is the same knowledge a QuePaxa proposal is computed from,
    /// and it is what makes the two arms' first attempts the same kind of guess. What differs is the fallback:
    /// here the change is re-applied to the value the round recovered, there it is re-applied to the winner and
    /// proposed into a new instance.
    /// </remarks>
    private static async Task WriteAsync(
        VirtualTimePump pump,
        HedgedFastWriter<string> writerClient,
        FastProposer<string> proposer,
        WriterState state,
        int writer,
        int maxRecoveryRounds)
    {
        ReplicaId self = HarnessIdentity.Replica(writer);
        string blind = RmwFold.Apply(null, state.Token);

        HedgedFastWriteOutcome hedged = await writerClient.TryWriteAsync(FastBallot.InitialFast(), blind, CancellationToken.None).ConfigureAwait(false);

        //The continuation resumes inline on the pump's thread at the instant the last reply landed, so this
        //reads the shipped gather's own instant rather than the instant the pump next happens to look.
        state.Activated = hedged.Activated;
        state.AddedWaitMicroseconds = VirtualTimePump.ToMicroseconds(hedged.Delay);
        state.FastAcceptedCount = hedged.AcceptedCount;

        if(hedged.IsCommitted)
        {
            state.IsCommitted = true;
            state.CommitInstant = pump.Now;
            state.CommittedValue = blind;

            return;
        }

        //A writer that stood down sent nothing at all, which is distinct from a fast round that failed to
        //reach its quorum, and it owes no recovery.
        if(!hedged.Activated)
        {
            return;
        }

        state.RecoveryEntered = true;
        for(int round = 1; round <= maxRecoveryRounds && !state.IsCommitted; round++)
        {
            state.RecoveryRounds++;
            ChangeOutcome<string> outcome = await proposer.RecoverAsync(FastBallot.Classic(round, self), state.Compose, CancellationToken.None).ConfigureAwait(false);
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


    /// <summary>Carries one request to one acceptor and its reply back, at the placement's own delays.</summary>
    /// <param name="pump">The clock the legs are scheduled on.</param>
    /// <param name="topology">The placement the delays come from.</param>
    /// <param name="nodes">The acceptor hosts, in site order.</param>
    /// <param name="request">The trial's arguments.</param>
    /// <param name="state">The writer's record, which counts the phases at the transport.</param>
    /// <param name="writer">The writer the call belongs to.</param>
    /// <param name="site">The site the call is sent from.</param>
    /// <param name="acceptor">The acceptor the call is addressed to.</param>
    /// <param name="consensusRequest">The request.</param>
    /// <returns>The acceptor's reply.</returns>
    private static ValueTask<ConsensusReply<string>> Send(
        VirtualTimePump pump,
        Topology topology,
        ConsensusNode<string>[] nodes,
        RmwFastTrialRequest request,
        WriterState state,
        int writer,
        int site,
        int acceptor,
        ConsensusRequest<string> consensusRequest)
    {
        //ONE PHASE SENDS ONE REQUEST OBJECT TO EVERY ACCEPTOR, so counting the distinct requests a writer put
        //on the transport counts the phases it executed, whatever the fan-out. That is what makes the round
        //column a measurement rather than a lookup on the outcome.
        int step = state.ObservePhase(consensusRequest);

        var completion = new TaskCompletionSource<ConsensusReply<string>>();
        long outbound = topology.OneWay(site, acceptor);
        long inbound = topology.OneWay(acceptor, site);

        pump.ScheduleAfter(outbound + request.Jitter.Draw(request.TrialSeed, writer, acceptor, step, 0, outbound), () =>
        {
            ConsensusReply<string> reply = nodes[acceptor].Handle(consensusRequest);
            pump.ScheduleAfter(inbound + request.Jitter.Draw(request.TrialSeed, writer, acceptor, step, 1, inbound), () => completion.SetResult(reply));
        });

        return new ValueTask<ConsensusReply<string>>(completion.Task);
    }


    /// <summary>
    /// What the pump and the client jointly record about one writer while its trial runs.
    /// </summary>
    /// <param name="token">The token this writer's change appends.</param>
    /// <param name="fastQuorum">The fast quorum this trial's replica count implies.</param>
    /// <remarks>
    /// The composition counts come from the change function's own argument, because the shipped proposer does
    /// not report the value a round recovered and the argument is where that value is visible. A count taken
    /// from the outcome instead could not tell a round that composed on top of a rival's change from one that
    /// found the register untouched, and the difference between those two is the arm's whole subject.
    /// </remarks>
    private sealed class WriterState(char token, int fastQuorum)
    {
        /// <summary>The token this writer's change appends.</summary>
        public char Token => token;

        /// <summary>Whether the writer sent at all.</summary>
        public bool Activated { get; set; }

        /// <summary>The hedging delay the shipped writer reported waiting.</summary>
        public long AddedWaitMicroseconds { get; set; }

        /// <summary>How many acceptors accepted the blind fast round.</summary>
        public int FastAcceptedCount { get; set; }

        /// <summary>Whether the writer fell back to a classic round.</summary>
        public bool RecoveryEntered { get; set; }

        /// <summary>How many classic ballots the writer spent.</summary>
        public int RecoveryRounds { get; set; }

        /// <summary>Whether the change committed.</summary>
        public bool IsCommitted { get; set; }

        /// <summary>The instant the change committed.</summary>
        public long? CommitInstant { get; set; }

        /// <summary>The instant the writer abandoned its round budget.</summary>
        public long? GiveUpInstant { get; set; }

        /// <summary>The value the writer left committed.</summary>
        public string? CommittedValue { get; set; }

        /// <summary>How many times the change function ran against a value recovered inside a round.</summary>
        public int ComposeCalls { get; private set; }

        /// <summary>How many times it found this writer's own token already applied.</summary>
        public int ApplyOnceTokenFirings { get; private set; }

        /// <summary>Whether at least one in-round composition ran against a value another writer had already committed.</summary>
        public bool RecomposedAgainstAnotherWriter { get; private set; }

        /// <summary>The value the last in-round composition ran against.</summary>
        public string? LastRecoveredValue { get; private set; }

        /// <summary>
        /// Reference identity rather than the record's own value equality: two phases that happened to carry
        /// equal ballots and values are still two phases, and a count that collapsed them would under-report
        /// the very fallback this arm exists to measure.
        /// </summary>
        public HashSet<object> Phases { get; } = new(ReferenceEqualityComparer.Instance);


        /// <summary>The fast quorum this trial's replica count implies.</summary>
        private int FastQuorum => fastQuorum;


        /// <summary>Records that <paramref name="request"/> went on the transport and returns the phase it belongs to.</summary>
        /// <param name="request">The request being sent.</param>
        /// <returns>The one-based phase index, which is what a jitter draw is keyed on.</returns>
        public int ObservePhase(ConsensusRequest<string> request)
        {
            _ = Phases.Add(request);

            return Phases.Count;
        }


        /// <summary>Whether the blind fast round reached a fast quorum.</summary>
        /// <returns><see langword="true"/> when it did.</returns>
        public bool ReachedFastQuorum() => FastAcceptedCount >= FastQuorum;


        /// <summary>The value this writer accepts, computed from the value its round recovered.</summary>
        /// <param name="recovered">The value the round recovered, or <see langword="null"/> when it recovered none.</param>
        /// <returns>The value to accept.</returns>
        public string Compose(string? recovered)
        {
            ComposeCalls++;
            LastRecoveredValue = recovered;
            if(RmwFold.Carries(recovered, token))
            {
                ApplyOnceTokenFirings++;
            }

            RecomposedAgainstAnotherWriter |= CarriesAnother(recovered);

            return RmwFold.Apply(recovered, token);
        }


        /// <summary>Whether <paramref name="value"/> carries a token belonging to some other writer.</summary>
        /// <param name="value">The value to inspect.</param>
        /// <returns><see langword="true"/> when another writer's change is already in it.</returns>
        private bool CarriesAnother(string? value)
        {
            if(value is null)
            {
                return false;
            }

            foreach(char held in value)
            {
                if(held != token)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
