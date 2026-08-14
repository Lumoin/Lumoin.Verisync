using System.Collections.Immutable;
using System.Globalization;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The QuePaxa half of the read-modify-write rider: the shipped versioned register, its recorder hosts and its
/// dissemination path driven end to end over the virtual clock.
/// </summary>
/// <remarks>
/// <para>
/// THE RETRY LOOP IS THE SHIPPED ONE. <see cref="QuePaxaVersionedRegister{TValue}.WriteAsync"/> is the surface
/// that recomputes: an attempt whose version another replica closed comes back superseded carrying the
/// winner's record, the register adopts it, and the next attempt runs the change function against that winner
/// at the version after it. Nothing here re-implements that loop, so what the rider measures is the cost a
/// deployment pays rather than the cost of a model of it.
/// </para>
/// <para>
/// THE CHANGE FUNCTION RUNS OUTSIDE THE ROUND, WHICH IS THE WHOLE POINT OF THE RIDER. QuePaxa decides among
/// whole proposals, so a losing proposal is discarded rather than composed and the loser must run another
/// consensus instance to apply its change at all. Every conflict therefore costs a version, and the cost
/// compounds with the number of writers still holding an unapplied change.
/// </para>
/// <para>
/// A WRITER IS A MEMBER. The register writes as one replica of the chain and a non-member reports
/// <see cref="QuePaxaWriteStatus.OutsideConfiguration"/> without proposing, so the writer count cannot exceed
/// the replica count. The plain grid places writer w at site w modulo the replica count and this arm places it
/// at the same site, which is the same placement wherever the two are comparable at all.
/// </para>
/// <para>
/// DISSEMINATION IS A MESSAGE AND IS PRICED AS ONE. A recorder host serves the one version after the record it
/// has learned, so nothing can be written at the next version until the current one has reached the hosts. The
/// publish path schedules a delivery per audience member at the placement's own delay and returns without
/// waiting for it, which is the early return the shipped delegate documents; the writer therefore pays for
/// dissemination in the next version's availability rather than in its own commit latency.
/// </para>
/// <para>
/// The transport is lossless. A host still refuses a request naming a version it does not serve, and that
/// refusal reaches the proposer as an unreachable recorder, which is the shipped path a stale writer takes and
/// not an injected fault.
/// </para>
/// </remarks>
internal static class RmwQuePaxaArm
{
    /// <summary>The pump bound one read-modify-write trial runs under.</summary>
    /// <remarks>
    /// It is above the plain arm's, because one trial here runs one consensus instance per writer rather than
    /// one for the whole trial, and each instance carries its own dissemination round.
    /// </remarks>
    public const long DefaultEventBudget = 400_000;

    /// <summary>How many consensus attempts one write may spend before the trial gives up on it.</summary>
    /// <remarks>
    /// A write can lose its version once per rival still holding an unapplied change, and can spend an
    /// additional attempt learning that a version it addressed had already closed, so the budget is well above
    /// the writer counts the grid runs rather than tight against them: a budget a contended write reaches
    /// would report the harness's bound where the measurement wants the protocol's cost.
    /// </remarks>
    public const int DefaultMaxAttempts = 24;

    /// <summary>How many times one step may send to one recorder before abandoning it for that step.</summary>
    public const int DefaultAttemptsPerRecorder = 1;


    /// <summary>
    /// Runs one trial in which every writer applies one change, and reports what each change cost.
    /// </summary>
    /// <param name="request">The trial's arguments.</param>
    /// <returns>One measurement per writer, the value the replicas were left holding, and the oracle's verdict on it.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the writer count and the arrival count disagree, or if there are more writers than replicas.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the run cannot drain, drains with a writer still parked, or leaves a writer without an outcome.</exception>
    public static RmwTrialOutcome<RmwQuePaxaWriterMeasurement> RunTrial(RmwQuePaxaTrialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Topology);
        ArgumentNullException.ThrowIfNull(request.Jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.WriterCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.AttemptsPerRecorder, 1);
        if(request.ArrivalMicroseconds.IsDefault || request.ArrivalMicroseconds.Length != request.WriterCount)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trial has {request.WriterCount} writers and {(request.ArrivalMicroseconds.IsDefault ? 0 : request.ArrivalMicroseconds.Length)} arrivals."), nameof(request));
        }

        Topology topology = request.Topology;
        int replicaCount = topology.SiteCount;
        if(request.WriterCount > replicaCount)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trial runs {request.WriterCount} writers over {replicaCount} replicas. A versioned register writes as one member of its chain, so a writer beyond the membership proposes nothing at all and would be measured as a refusal rather than as contention."), nameof(request));
        }

        var pump = new VirtualTimePump(request.EventBudget);
        ImmutableArray<ReplicaId> members = [.. Enumerable.Range(0, replicaCount).Select(HarnessIdentity.Replica)];
        QuePaxaConfiguration genesis = QuePaxaConfiguration.CreateGenesis(members);

        var nodes = new QuePaxaVersionedNode<string>[replicaCount];
        for(int site = 0; site < replicaCount; site++)
        {
            nodes[site] = new QuePaxaVersionedNode<string>(genesis, members[site]);
        }

        var registers = new QuePaxaVersionedRegister<string>[request.WriterCount];
        var states = new WriterState[request.WriterCount];
        var sources = new HarnessPrioritySource[request.WriterCount];
        var clients = new Task[request.WriterCount];

        for(int index = 0; index < request.WriterCount; index++)
        {
            int writer = index;
            int site = writer % replicaCount;
            states[writer] = new WriterState(RmwFold.Token(writer));
            sources[writer] = new HarnessPrioritySource(SeedMixer.PriorityStreamSeed(request.TrialSeed, writer));

            registers[writer] = new QuePaxaVersionedRegister<string>(
                genesis,
                members[site],
                VirtualTimePump.ToTimeSpan(request.BaseDelayMicroseconds),
                member => Resolve(pump, topology, nodes, members, request, writer, site, member),
                sources[writer].Next,
                request.AttemptsPerRecorder,
                pump.Clock,
                null,
                null,
                (record, audience, _) => Publish(pump, topology, nodes, registers, members, site, record, audience),
                null);

            //A writer whose arrival has not been dispatched yet has no task, and a pump that failed to
            //dispatch one would otherwise be reported as a parked client rather than as the missing arrival it
            //is. The completed placeholder keeps the parked-client check reading only what it is for.
            clients[writer] = Task.CompletedTask;
            pump.ScheduleAt(request.ArrivalMicroseconds[writer], () => clients[writer] = WriteAsync(pump, registers[writer], states[writer], request.MaxAttempts));
        }

        pump.Run(clients);

        ImmutableArray<RmwQuePaxaWriterMeasurement>.Builder measurements = ImmutableArray.CreateBuilder<RmwQuePaxaWriterMeasurement>(request.WriterCount);
        for(int writer = 0; writer < request.WriterCount; writer++)
        {
            WriterState state = states[writer];
            if(state.Outcome is not { } outcome)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Writer {writer} produced no outcome after the schedule drained at seed {request.TrialSeed}."));
            }

            long activation = state.ArrivalMicroseconds + state.AddedWaitMicroseconds;
            bool committed = outcome.Status == QuePaxaWriteStatus.Committed;
            measurements.Add(new RmwQuePaxaWriterMeasurement(
                writer,
                writer % replicaCount,
                state.Token,
                outcome,
                state.ArrivalMicroseconds,
                state.AddedWaitMicroseconds,
                committed ? state.SettledMicroseconds - activation : null,
                committed ? null : state.SettledMicroseconds - activation,
                state.ConflictRecomputes,
                state.UndecidedRecomputes,
                state.ApplyOnceTokenFirings,
                state.RecomposedAgainstAnotherWriter,
                state.LastConflictBase,
                outcome.Value));
        }

        ImmutableArray<RmwQuePaxaWriterMeasurement> writers = measurements.ToImmutable();
        string? finalValue = HighestCommitted(nodes)?.Value;
        ImmutableArray<char> committedTokens = [.. writers.Where(measurement => measurement.IsCommitted).Select(measurement => measurement.Token)];

        return new RmwTrialOutcome<RmwQuePaxaWriterMeasurement>(writers, finalValue, RmwFold.Check(finalValue, committedTokens, request.WriterCount));
    }


    /// <summary>The highest record any host of <paramref name="nodes"/> has learned.</summary>
    /// <param name="nodes">The recorder hosts.</param>
    /// <returns>The record, or <see langword="null"/> when no host has learned one.</returns>
    /// <remarks>
    /// The oracle reads the hosts rather than the writers, so a client that believes something the cluster
    /// does not hold cannot make the fold agree with itself.
    /// </remarks>
    private static VersionedValue<string>? HighestCommitted(QuePaxaVersionedNode<string>[] nodes)
    {
        VersionedValue<string>? highest = null;
        RegisterVersion reached = RegisterVersion.Unwritten;
        foreach(QuePaxaVersionedNode<string> node in nodes)
        {
            if(node.Committed is { } held && held.Version > reached)
            {
                reached = held.Version;
                highest = held;
            }
        }

        return highest;
    }


    /// <summary>Runs one writer's whole read-modify-write and records what it cost.</summary>
    /// <param name="pump">The clock every instant is read from.</param>
    /// <param name="register">The writer's register.</param>
    /// <param name="state">The writer's record of its own write.</param>
    /// <param name="maxAttempts">How many consensus attempts the write may spend.</param>
    /// <returns>A task that completes when the write has settled.</returns>
    /// <remarks>
    /// The delay is read before the write rather than reported by it, because the register waits its position's
    /// delay once per attempt and the row's added-wait column is the wait a writer paid before it first sent.
    /// The continuation resumes inline on the pump's thread at the instant the settling reply landed, so the
    /// settled instant is the write's own rather than the instant the pump next happens to look.
    /// </remarks>
    private static async Task WriteAsync(VirtualTimePump pump, QuePaxaVersionedRegister<string> register, WriterState state, int maxAttempts)
    {
        state.ArrivalMicroseconds = pump.Now;
        state.AddedWaitMicroseconds = register.Delay is { } delay ? VirtualTimePump.ToMicroseconds(delay) : 0;

        QuePaxaWriteOutcome<string> outcome = await register.WriteAsync(state.Update, maxAttempts, CancellationToken.None).ConfigureAwait(false);

        state.Outcome = outcome;
        state.SettledMicroseconds = pump.Now;
    }


    /// <summary>The endpoint that reaches <paramref name="member"/> from <paramref name="site"/>.</summary>
    /// <param name="pump">The clock every delivery is scheduled on.</param>
    /// <param name="topology">The placement the delays come from.</param>
    /// <param name="nodes">The recorder hosts, in site order.</param>
    /// <param name="members">The membership, in site order.</param>
    /// <param name="request">The trial's arguments.</param>
    /// <param name="writer">The writer the endpoint belongs to.</param>
    /// <param name="site">The site the writer sends from.</param>
    /// <param name="member">The member to reach.</param>
    /// <returns>The endpoint.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no host of this trial is that member, which is how a resolver reports one it cannot resolve.</exception>
    private static VersionedRecorderEndpointDelegate<VersionedValue<string>> Resolve(
        VirtualTimePump pump,
        Topology topology,
        QuePaxaVersionedNode<string>[] nodes,
        ImmutableArray<ReplicaId> members,
        RmwQuePaxaTrialRequest request,
        int writer,
        int site,
        ReplicaId member)
    {
        int host = HostOf(members, member);
        if(host < 0)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No host of this trial is {member}, so the member cannot be resolved to a transport."));
        }

        return (versionedRequest, _) => Send(pump, topology, nodes, request, writer, site, host, versionedRequest);
    }


    /// <summary>Carries one request to one host and its reply back, at the placement's own delays.</summary>
    /// <param name="pump">The clock the legs are scheduled on.</param>
    /// <param name="topology">The placement the delays come from.</param>
    /// <param name="nodes">The recorder hosts, in site order.</param>
    /// <param name="request">The trial's arguments.</param>
    /// <param name="writer">The writer the call belongs to.</param>
    /// <param name="site">The site the call is sent from.</param>
    /// <param name="host">The host the call is addressed to.</param>
    /// <param name="versionedRequest">The request.</param>
    /// <returns>The host's reply.</returns>
    /// <remarks>
    /// A host refuses a request naming a version it does not serve by throwing, and the refusal travels the
    /// reply leg like an answer would: a proposer learns of it when it would have learned of the reply, and
    /// treats it as an unreachable recorder. Completing it at the send instant instead would make a refusal
    /// cheaper than an answer and let a stale writer discover its staleness for free.
    /// </remarks>
    private static ValueTask<VersionedRecordReply<VersionedValue<string>>> Send(
        VirtualTimePump pump,
        Topology topology,
        QuePaxaVersionedNode<string>[] nodes,
        RmwQuePaxaTrialRequest request,
        int writer,
        int site,
        int host,
        VersionedRecordRequest<VersionedValue<string>> versionedRequest)
    {
        //Correlation is per call and never per recorder, which is what the endpoint delegate's contract
        //demands: consecutive steps overlap, so one recorder can hold an abandoned call from the previous
        //step and a live one from this step at the same moment.
        var completion = new TaskCompletionSource<VersionedRecordReply<VersionedValue<string>>>();
        long outbound = topology.OneWay(site, host);
        long inbound = topology.OneWay(host, site);
        int step = versionedRequest.Request.Step.Value;

        pump.ScheduleAfter(outbound + request.Jitter.Draw(request.TrialSeed, writer, host, step, 0, outbound), () =>
        {
            VersionedRecordReply<VersionedValue<string>>? reply = null;
            Exception? refusal = null;
            try
            {
                reply = nodes[host].Handle(versionedRequest);
            }
            catch(Exception declined)
            {
                refusal = declined;
            }

            pump.ScheduleAfter(inbound + request.Jitter.Draw(request.TrialSeed, writer, host, step, 1, inbound), () =>
            {
                if(reply is { } answer)
                {
                    completion.SetResult(answer);

                    return;
                }

                completion.SetException(refusal ?? new InvalidOperationException("A host neither answered nor refused, which is a defect of this transport rather than a protocol outcome."));
            });
        });

        return new ValueTask<VersionedRecordReply<VersionedValue<string>>>(completion.Task);
    }


    /// <summary>Offers a decided record to its audience, one scheduled delivery per host.</summary>
    /// <param name="pump">The clock the deliveries are scheduled on.</param>
    /// <param name="topology">The placement the delays come from.</param>
    /// <param name="nodes">The recorder hosts, in site order.</param>
    /// <param name="registers">The writers' registers, in writer order.</param>
    /// <param name="members">The membership, in site order.</param>
    /// <param name="fromSite">The site the record is published from.</param>
    /// <param name="committed">The decided record.</param>
    /// <param name="audience">The hosts to offer it to.</param>
    /// <returns>A completed operation, because the offer is made rather than awaited.</returns>
    /// <remarks>
    /// A host feeds both roles from one record: the recorder derives the next instance's leader from it and
    /// the register computes the next version and its update's input from it, so a delivery that reached only
    /// one of the two would leave a replica able to serve a version it cannot write at.
    /// </remarks>
    private static ValueTask Publish(
        VirtualTimePump pump,
        Topology topology,
        QuePaxaVersionedNode<string>[] nodes,
        QuePaxaVersionedRegister<string>[] registers,
        ImmutableArray<ReplicaId> members,
        int fromSite,
        VersionedValue<string> committed,
        ImmutableArray<ReplicaId> audience)
    {
        foreach(ReplicaId member in audience)
        {
            int host = HostOf(members, member);
            if(host < 0)
            {
                continue;
            }

            pump.ScheduleAfter(topology.OneWay(fromSite, host), () =>
            {
                _ = nodes[host].Learn(committed);
                if(host < registers.Length)
                {
                    _ = registers[host].Learn(committed);
                }
            });
        }

        return ValueTask.CompletedTask;
    }


    /// <summary>The host index of <paramref name="member"/>, which is negative when no host is that member.</summary>
    /// <param name="members">The membership, in site order.</param>
    /// <param name="member">The member to look for.</param>
    /// <returns>The host index, or a negative value.</returns>
    private static int HostOf(ImmutableArray<ReplicaId> members, ReplicaId member)
    {
        for(int site = 0; site < members.Length; site++)
        {
            if(members[site].Equals(member))
            {
                return site;
            }
        }

        return -1;
    }


    /// <summary>
    /// What one writer's change function and its write jointly record while the trial runs.
    /// </summary>
    /// <param name="token">The token this writer's change appends.</param>
    /// <remarks>
    /// THE CONFLICT COUNT COMES FROM THE CHANGE FUNCTION'S OWN ARGUMENT. The register hands the update the
    /// value it believes committed, once per attempt and outside the round, so an argument that differs from
    /// the previous attempt's is committed state having moved under this write and an argument that repeats it
    /// is a retry that learned nothing. A count taken from the attempt number instead would price the two the
    /// same, and only one of them is the conflict the settled rule names.
    /// </remarks>
    private sealed class WriterState(char token)
    {
        /// <summary>The token this writer's change appends.</summary>
        public char Token => token;

        /// <summary>The instant this writer's client started.</summary>
        public long ArrivalMicroseconds { get; set; }

        /// <summary>The hedging delay the first attempt waited before sending.</summary>
        public long AddedWaitMicroseconds { get; set; }

        /// <summary>The instant the write settled, whether it committed or spent its budget.</summary>
        public long SettledMicroseconds { get; set; }

        /// <summary>The shipped outcome of the whole write, or <see langword="null"/> while it is still running.</summary>
        public QuePaxaWriteOutcome<string>? Outcome { get; set; }

        /// <summary>How many times the change function ran against a value it had not been handed before.</summary>
        public int ConflictRecomputes { get; private set; }

        /// <summary>How many times it ran against the value it had already been handed.</summary>
        public int UndecidedRecomputes { get; private set; }

        /// <summary>How many times it found this writer's own token already applied.</summary>
        public int ApplyOnceTokenFirings { get; private set; }

        /// <summary>Whether at least one recompute ran against a value another writer had already committed.</summary>
        public bool RecomposedAgainstAnotherWriter { get; private set; }

        /// <summary>The value the last conflict recompute ran against, which is the winner this write rebuilt on top of.</summary>
        public string? LastConflictBase { get; private set; }


        /// <summary>Whether the change function has run at least once.</summary>
        private bool HasRun { get; set; }

        /// <summary>The value the change function was handed last.</summary>
        private string? PreviousBase { get; set; }


        /// <summary>The value this writer proposes, computed from the value the register believes committed.</summary>
        /// <param name="current">The value the register believes committed, or <see langword="null"/> when it believes none is.</param>
        /// <returns>The value to propose.</returns>
        public string Update(string? current)
        {
            if(HasRun)
            {
                if(EqualityComparer<string?>.Default.Equals(current, PreviousBase))
                {
                    UndecidedRecomputes++;
                }
                else
                {
                    ConflictRecomputes++;
                    LastConflictBase = current;
                    RecomposedAgainstAnotherWriter |= CarriesAnother(current);
                }
            }

            HasRun = true;
            PreviousBase = current;
            if(RmwFold.Carries(current, token))
            {
                ApplyOnceTokenFirings++;
            }

            return RmwFold.Apply(current, token);
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


    /// <summary>
    /// One writer's phase-zero priority stream.
    /// </summary>
    /// <remarks>
    /// Xorshift64 rather than the cryptographic source: every priority is reproducible from its seed, so a
    /// failing configuration replays the identical draws. Each writer owns a stream, because a proposer that
    /// believes it leads draws nothing at its first step and a shared stream would couple the writers through
    /// dispatch order.
    /// </remarks>
    private sealed class HarnessPrioritySource
    {
        private ulong state;

        /// <summary>Initializes a stream at <paramref name="seed"/>.</summary>
        /// <param name="seed">The stream's seed.</param>
        public HarnessPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        /// <summary>Draws the next ordinary priority.</summary>
        /// <returns>The priority.</returns>
        public ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;

            //The two excluded endpoints, none and reserved, are mapped away so the source honours the
            //delegate's ordinary-priority contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }
}
