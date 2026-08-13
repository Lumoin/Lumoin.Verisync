using System.Collections.Immutable;
using System.Globalization;
using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.DecisionTable;

/// <summary>
/// The QuePaxa half of the bridge: the shipped proposer and recorders driven end to end over the virtual
/// clock, generalized in the replica count and denominated in microseconds.
/// </summary>
/// <remarks>
/// <para>
/// Every protocol decision here is the shipped one. Requests go through
/// <see cref="RecorderEndpointDelegate{TValue}"/> endpoints into a <see cref="QuePaxaNode{TValue}"/> per site
/// and are driven by <see cref="QuePaxaProposer{TValue}"/>, so the fold, the downgrade, the
/// request-to-reply mapping and the act-on-the-first-quorum rule are the protocol's and not a restatement.
/// The quorum is read from the shipped register through <see cref="QuorumDistance"/>, so no replica count can
/// drift from the shipped rules.
/// </para>
/// <para>
/// THE PROPOSER HOLDS NO CLOCK, so the stagger is applied by the host at call time: a writer's activation is
/// a scheduled instant rather than an awaited delay. The value of the stagger still comes from the shipped
/// <see cref="HedgingSchedule"/> wherever a caller builds it that way, which is the difference between
/// measuring the shipped policy's arithmetic and restating it.
/// </para>
/// <para>
/// The transport is lossless, so the proposer's fault machinery - the attempt budget, the quorum-unreachable
/// exit and the below-step reply filter - is deliberately unexercised here. Fault injection is a separate
/// campaign rather than a column of this one.
/// </para>
/// </remarks>
internal static class QuePaxaArm
{
    /// <summary>The pump bound one QuePaxa trial runs under.</summary>
    public const long DefaultEventBudget = 200_000;


    /// <summary>
    /// Runs one trial and reports what every writer's attempt cost.
    /// </summary>
    /// <param name="request">The trial's arguments.</param>
    /// <returns>One measurement per writer, in writer order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if the writer count and the activation count disagree.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the run cannot drain, or drains with a writer still parked.</exception>
    public static ImmutableArray<QuePaxaWriterMeasurement> RunTrial(QuePaxaTrialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Topology);
        ArgumentNullException.ThrowIfNull(request.Jitter);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.WriterCount, 1);
        if(request.ActivationsMicroseconds.IsDefault || request.ActivationsMicroseconds.Length != request.WriterCount)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trial has {request.WriterCount} writers and {(request.ActivationsMicroseconds.IsDefault ? 0 : request.ActivationsMicroseconds.Length)} activations."), nameof(request));
        }

        if(request.StaggerMicroseconds.IsDefault || request.StaggerMicroseconds.Length != request.WriterCount)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The trial has {request.WriterCount} writers and {(request.StaggerMicroseconds.IsDefault ? 0 : request.StaggerMicroseconds.Length)} stagger entries."), nameof(request));
        }

        Topology topology = request.Topology;
        int replicaCount = topology.SiteCount;
        var pump = new VirtualTimePump(request.EventBudget);

        //The lane leading the absent-leader configurations sits above both the replica count and the writer
        //count, so no writer holds it and every writer runs the ordinary path. A lane indexed by the replica
        //count alone would be writer number replicaCount's own lane wherever there are more writers than
        //replicas, which turns the configuration into a led one without saying so.
        ProposerLane? believedLeader = request.Leadership switch
        {
            LeadershipMode.WriterZeroLeads => HarnessIdentity.Lane(0),
            LeadershipMode.AbsentLeader => HarnessIdentity.Lane(Math.Max(replicaCount, request.WriterCount)),
            _ => null
        };

        var nodes = new QuePaxaNode<string>[replicaCount];
        for(int site = 0; site < replicaCount; site++)
        {
            nodes[site] = new QuePaxaNode<string>(believedLeader is null
                ? QuePaxaRecorder<string>.Leaderless
                : QuePaxaRecorder<string>.LedBy(believedLeader.Value));
        }

        var sources = new HarnessPrioritySource[request.WriterCount];
        var outcomes = new QuePaxaOutcome<string>?[request.WriterCount];
        var decisionInstants = new long[request.WriterCount];
        var clients = new Task[request.WriterCount];

        for(int index = 0; index < request.WriterCount; index++)
        {
            int writer = index;
            int site = writer % replicaCount;
            sources[writer] = new HarnessPrioritySource(SeedMixer.PriorityStreamSeed(request.TrialSeed, writer));

            var endpoints = new RecorderEndpointDelegate<string>[replicaCount];
            for(int recorderIndex = 0; recorderIndex < replicaCount; recorderIndex++)
            {
                int recorder = recorderIndex;
                endpoints[recorder] = (recordRequest, _) => Send(pump, topology, nodes, request, writer, site, recorder, recordRequest);
            }

            var proposer = new QuePaxaProposer<string>(endpoints, HarnessIdentity.Lane(writer), sources[writer].Next, attemptsPerRecorder: 1);

            pump.ScheduleAt(request.ActivationsMicroseconds[writer], () => clients[writer] = ProposeAsync(pump, proposer, believedLeader, writer, outcomes, decisionInstants));
        }

        //A writer whose activation has not been dispatched yet has no task, and a pump that failed to
        //dispatch one would otherwise be reported as a parked client rather than as the missing activation it
        //is. The completed placeholder keeps the parked-client check reading only what it is for.
        for(int writer = 0; writer < request.WriterCount; writer++)
        {
            clients[writer] = Task.CompletedTask;
        }

        pump.Run(clients);

        ImmutableArray<QuePaxaWriterMeasurement>.Builder measurements = ImmutableArray.CreateBuilder<QuePaxaWriterMeasurement>(request.WriterCount);
        for(int writer = 0; writer < request.WriterCount; writer++)
        {
            if(outcomes[writer] is not { } outcome)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Writer {writer} produced no outcome after the schedule drained at seed {request.TrialSeed}."));
            }

            long activation = request.ActivationsMicroseconds[writer];
            measurements.Add(new QuePaxaWriterMeasurement(
                writer,
                writer % replicaCount,
                outcome,
                activation,
                decisionInstants[writer] - activation,
                request.StaggerMicroseconds[writer],
                sources[writer].DrawCount));
        }

        return measurements.ToImmutable();
    }


    private static async Task ProposeAsync(
        VirtualTimePump pump,
        QuePaxaProposer<string> proposer,
        ProposerLane? believedLeader,
        int writer,
        QuePaxaOutcome<string>?[] outcomes,
        long[] decisionInstants)
    {
        QuePaxaOutcome<string> outcome = await proposer.ProposeAsync(believedLeader, HarnessIdentity.Value(writer), CancellationToken.None).ConfigureAwait(false);

        //The continuation resumes inline on the pump's thread at the instant the deciding reply landed, so
        //this reads the decision's own instant rather than the instant the pump next happens to look.
        outcomes[writer] = outcome;
        decisionInstants[writer] = pump.Now;
    }


    private static ValueTask<RecordReply<string>> Send(
        VirtualTimePump pump,
        Topology topology,
        QuePaxaNode<string>[] nodes,
        QuePaxaTrialRequest request,
        int writer,
        int site,
        int recorder,
        RecordRequest<string> recordRequest)
    {
        //Correlation is per call and never per recorder, which is what the endpoint delegate's contract
        //demands: consecutive steps overlap, so one recorder can hold an abandoned call from the previous
        //step and a live one from this step at the same moment.
        var completion = new TaskCompletionSource<RecordReply<string>>();
        long outbound = topology.OneWay(site, recorder);
        long inbound = topology.OneWay(recorder, site);
        int step = recordRequest.Step.Value;

        pump.ScheduleAfter(outbound + request.Jitter.Draw(request.TrialSeed, writer, recorder, step, 0, outbound), () =>
        {
            RecordReply<string> reply = nodes[recorder].Handle(recordRequest);
            pump.ScheduleAfter(inbound + request.Jitter.Draw(request.TrialSeed, writer, recorder, step, 1, inbound), () => completion.SetResult(reply));
        });

        return new ValueTask<RecordReply<string>>(completion.Task);
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

        public HarnessPrioritySource(ulong seed) => state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;


        /// <summary>How many priorities this stream has supplied.</summary>
        public int DrawCount { get; private set; }


        /// <summary>Draws the next ordinary priority.</summary>
        /// <returns>The priority.</returns>
        public ProposalPriority Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            DrawCount++;

            //The two excluded endpoints, none and reserved, are mapped away so the source honours the
            //delegate's ordinary-priority contract exactly.
            ulong value = state == 0 || state == ulong.MaxValue ? 0x0123_4567_89AB_CDEFUL : state;

            return new ProposalPriority(value);
        }
    }
}
