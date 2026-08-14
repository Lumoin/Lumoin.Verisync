using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A deterministic interleaving bench for the QuePaxa protocol: every record request and every record reply
/// between a proposer and a recorder node becomes an in-flight message, and messages are delivered one at a
/// time. Concurrent protocol runs are thereby explored across delivery interleavings that no synchronous
/// driver can produce, and any run replays exactly from its seed.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The bench is single-threaded: completing an in-flight message resumes the awaiting proposer inline on the
/// pump thread, which then enqueues its next messages before the delivery call returns. Virtual time
/// (<see cref="Now"/>) advances per delivered message and per <see cref="Tick"/>.
/// </para>
/// <para>
/// DELIVERY IS EITHER SAMPLED OR SCRIPTED, and both are needed. <see cref="Step"/> picks pseudo-randomly and
/// is what sweeps interleavings; <see cref="DeliverTo(int)"/> and <c>DeliverFirstMatching</c> pick a chosen
/// message, which is what makes a negative reachable as a pin rather than as a lottery — a scenario that
/// needs two proposers' first-step requests to reach two recorders in opposite orders cannot ask a random
/// pump for it.
/// </para>
/// <para>
/// A partitioned recorder loses messages at delivery time, requests and replies alike, so a partition that
/// appears mid-flight also kills the replies already in the air. TERMINATION UNDER A PARTITION DEPENDS ON THE
/// PROPOSER'S BOUNDED ATTEMPT BUDGET: a partitioned recorder's endpoint faults, the proposer re-sends, and the
/// new message enters the queue, so a proposal against a partitioned minority terminates in at most that many
/// sends per recorder per step.
/// </para>
/// <para>
/// With <see cref="RequestDuplicationPercent"/> set, a delivered request may be re-enqueued as a duplicate
/// that hits the recorder again at a later delivery point with its reply discarded. ONLY AN ORIGINAL REQUEST
/// SPAWNS A DUPLICATE, so the duplicate count is bounded by the request count and a hundred-percent setting
/// still terminates.
/// </para>
/// </remarks>
internal sealed class InterleavedQuePaxaCluster<TValue>
{
    /// <summary>
    /// A livelock is the one failure mode a synchronous pump turns into a hung suite rather than a red test, so
    /// the pump is bounded well above any behaviour the step budget admits and reports rather than spins.
    /// </summary>
    private const int MaxDeliveries = 200_000;

    private readonly List<string> deliveryTrace = [];
    private readonly List<DeliveredRequest> deliveredRequests = [];
    private uint randomState;

    private QuePaxaNode<TValue>[] Nodes { get; }
    private HashSet<int> Partitioned { get; } = [];
    private List<InFlightMessage> InFlight { get; } = [];


    /// <summary>
    /// Creates a cluster of nodes over <paramref name="recorders"/>, taken as they are so that a test can
    /// build a configured-leader cluster, a leaderless one, or a MISCONFIGURED one whose recorders disagree
    /// about who leads.
    /// </summary>
    /// <param name="recorders">The recorders, in index order.</param>
    /// <param name="seed">The delivery-order seed. It is printed by every test that uses one.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="recorders"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="recorders"/> is empty.</exception>
    public InterleavedQuePaxaCluster(IReadOnlyList<QuePaxaRecorder<TValue>> recorders, int seed)
    {
        ArgumentNullException.ThrowIfNull(recorders);
        if(recorders.Count == 0)
        {
            throw new ArgumentException("A cluster requires at least one recorder.", nameof(recorders));
        }

        Nodes = new QuePaxaNode<TValue>[recorders.Count];
        for(int i = 0; i < recorders.Count; i++)
        {
            Nodes[i] = new QuePaxaNode<TValue>(recorders[i]);
        }

        Seed = seed;

        //Xorshift32 instead of System.Random: the sequence is deterministic across runtimes and platforms, so
        //a seed printed by a failing run replays the identical interleaving anywhere. State must be nonzero.
        randomState = seed == 0 ? 2463534242u : (uint)seed;
    }


    /// <summary>The delivery-order seed this cluster was created with.</summary>
    public int Seed { get; }


    /// <summary>The virtual clock: advances per delivered message and per <see cref="Tick"/>.</summary>
    public long Now { get; private set; }


    /// <summary>The number of recorder nodes.</summary>
    public int RecorderCount => Nodes.Length;


    /// <summary>
    /// The percentage chance (0–100) that a delivered original request is re-enqueued as a duplicate. The
    /// duplicate is handled by its recorder at a later delivery point and its reply is discarded.
    /// </summary>
    public int RequestDuplicationPercent { get; set; }


    /// <summary>The number of in-flight messages awaiting delivery.</summary>
    public int PendingCount => InFlight.Count;


    /// <summary>
    /// The number of duplicates delivered to a recorder whose register was AT the duplicate's step. That is
    /// the only shape that exercises the same-step branch of the re-send argument: a duplicate landing below
    /// the recorder's step exercises the stale branch instead, so a duplication law without this counter can
    /// pass with every duplicate landing in the region the core already covered.
    /// </summary>
    public int SameStepDuplicatesDelivered { get; private set; }


    /// <summary>
    /// The number of same-step duplicates that left the recorder REFERENCE-IDENTICAL, which is the identity
    /// half of the re-send argument in its observable form: a second identical record folds into an aggregate
    /// that already dominates it, so no field would have changed and the register returns itself.
    /// </summary>
    public int IdempotentDuplicatesDelivered { get; private set; }


    /// <summary>The delivery order so far, one line per delivered or lost message, for replay comparison.</summary>
    public IReadOnlyList<string> DeliveryTrace => deliveryTrace;


    /// <summary>
    /// Every request delivered to a recorder, in delivery order, with the proposer that sent it. This is what
    /// a reach pin reads to show that two proposers' requests genuinely interleaved at one recorder rather
    /// than running one after the other.
    /// </summary>
    public IReadOnlyList<DeliveredRequest> DeliveredRequests => deliveredRequests;


    /// <summary>Returns the node at <paramref name="index"/>.</summary>
    /// <param name="index">The recorder index.</param>
    /// <returns>The node.</returns>
    public QuePaxaNode<TValue> Node(int index) => Nodes[index];


    /// <summary>Partitions the recorder at <paramref name="index"/>, so its messages are lost at delivery.</summary>
    /// <param name="index">The recorder index.</param>
    public void Partition(int index) => Partitioned.Add(index);


    /// <summary>Heals the recorder at <paramref name="index"/>.</summary>
    /// <param name="index">The recorder index.</param>
    public void Heal(int index) => Partitioned.Remove(index);


    /// <summary>Advances the virtual clock by one, marking a history boundary such as an operation start.</summary>
    /// <returns>The new clock value.</returns>
    public long Tick() => ++Now;


    /// <summary>
    /// Creates a proposer whose endpoints enqueue in-flight messages instead of completing synchronously,
    /// putting every exchange under the bench's delivery-order control.
    /// </summary>
    /// <param name="lane">The lane the proposer proposes on.</param>
    /// <param name="drawPriority">The source of the phase-zero priority draw.</param>
    /// <param name="attemptsPerRecorder">The per-step attempt budget, which is what bounds the run under a partition.</param>
    /// <returns>The proposer.</returns>
    public QuePaxaProposer<TValue> CreateProposer(ProposerLane lane, ProposalPrioritySourceDelegate drawPriority, int attemptsPerRecorder)
    {
        var endpoints = new RecorderEndpointDelegate<TValue>[Nodes.Length];
        for(int i = 0; i < Nodes.Length; i++)
        {
            int index = i;
            endpoints[i] = (request, _) =>
            {
                TaskCompletionSource<RecordReply<TValue>> completion = new();
                InFlight.Add(new InFlightMessage(index, lane, request, null, completion, false));

                return new ValueTask<RecordReply<TValue>>(completion.Task);
            };
        }

        return new QuePaxaProposer<TValue>(endpoints, lane, drawPriority, attemptsPerRecorder);
    }


    /// <summary>
    /// Delivers one pseudo-randomly chosen in-flight message: a request is handled by its node and the reply
    /// becomes a new in-flight message; a reply resumes the awaiting proposer inline.
    /// </summary>
    /// <returns>Whether a message was delivered; <see langword="false"/> when nothing is in flight.</returns>
    public bool Step()
    {
        if(InFlight.Count == 0)
        {
            return false;
        }

        return Deliver(NextIndex(InFlight.Count));
    }


    /// <summary>Delivers the oldest in-flight message addressed to <paramref name="recorder"/>.</summary>
    /// <param name="recorder">The recorder index.</param>
    /// <returns>Whether a message was delivered.</returns>
    public bool DeliverTo(int recorder)
    {
        for(int i = 0; i < InFlight.Count; i++)
        {
            if(InFlight[i].Recorder == recorder)
            {
                return Deliver(i);
            }
        }

        return false;
    }


    /// <summary>Delivers the oldest in-flight message satisfying <paramref name="predicate"/>.</summary>
    /// <param name="predicate">The message to look for.</param>
    /// <returns>Whether a message was delivered.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="predicate"/> is <see langword="null"/>.</exception>
    public bool DeliverFirstMatching(Func<InFlightMessage, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        for(int i = 0; i < InFlight.Count; i++)
        {
            if(predicate(InFlight[i]))
            {
                return Deliver(i);
            }
        }

        return false;
    }


    /// <summary>
    /// Pumps until nothing is in flight. Because completions resume proposers inline, the queue only drains
    /// once every started proposal has run to a conclusion or spent its attempt budget.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the pump exceeds its delivery bound, which reports a livelock instead of hanging the suite.</exception>
    public void RunToQuiescence()
    {
        int delivered = 0;
        while(Step())
        {
            delivered++;
            if(delivered > MaxDeliveries)
            {
                throw new InvalidOperationException($"The bench delivered more than {MaxDeliveries} messages without quiescing, so a proposal is not terminating.");
            }
        }
    }


    private bool Deliver(int index)
    {
        InFlightMessage message = InFlight[index];
        InFlight.RemoveAt(index);
        Now++;

        string kind = Describe(message);
        if(Partitioned.Contains(message.Recorder))
        {
            deliveryTrace.Add($"{Now}:r{message.Recorder}:{kind}:lost");
            message.Completion?.SetException(new IOException($"Recorder {message.Recorder} is partitioned."));

            return true;
        }

        if(message.Request is null)
        {
            deliveryTrace.Add($"{Now}:r{message.Recorder}:{kind}");
            message.Completion!.SetResult(message.Reply!);

            return true;
        }

        QuePaxaNode<TValue> node = Nodes[message.Recorder];
        QuePaxaRecorder<TValue> before = node.Recorder;
        bool atTheRequestedStep = before.Step == message.Request.Step;

        RecordReply<TValue> reply = node.Handle(message.Request);
        deliveryTrace.Add($"{Now}:r{message.Recorder}:{kind}");
        deliveredRequests.Add(new DeliveredRequest(message.Recorder, message.Proposer, message.Request.Step, message.IsDuplicate));

        if(message.IsDuplicate && atTheRequestedStep)
        {
            SameStepDuplicatesDelivered++;
            if(ReferenceEquals(node.Recorder, before))
            {
                IdempotentDuplicatesDelivered++;
            }
        }

        if(message.Completion is not null)
        {
            InFlight.Add(new InFlightMessage(message.Recorder, message.Proposer, null, reply, message.Completion, false));
        }

        //A duplicate of a duplicate would be unbounded, so only an original request spawns one; that is what
        //makes a hundred-percent duplication rate a deterministic doubling rather than a divergent queue.
        if(!message.IsDuplicate && RequestDuplicationPercent > 0 && NextIndex(100) < RequestDuplicationPercent)
        {
            InFlight.Add(new InFlightMessage(message.Recorder, message.Proposer, message.Request, null, null, true));
        }

        return true;
    }


    private int NextIndex(int count)
    {
        randomState ^= randomState << 13;
        randomState ^= randomState >> 17;
        randomState ^= randomState << 5;

        return (int)(randomState % (uint)count);
    }


    private static string Describe(InFlightMessage message)
    {
        return message.Request is not null
            ? message.IsDuplicate ? "record:dup" : "record"
            : "record-reply";
    }


    /// <summary>
    /// One message in flight between a proposer and a recorder node. A request carries a completion the reply
    /// will later satisfy; a DUPLICATE carries none, because it mutates recorder state and its reply goes
    /// nowhere.
    /// </summary>
    /// <param name="Recorder">The recorder index the message is addressed to.</param>
    /// <param name="Proposer">The lane of the proposer the exchange belongs to.</param>
    /// <param name="Request">The request, or <see langword="null"/> when this message is a reply.</param>
    /// <param name="Reply">The reply, or <see langword="null"/> when this message is a request.</param>
    /// <param name="Completion">The proposer's awaited completion, or <see langword="null"/> for a duplicate.</param>
    /// <param name="IsDuplicate">Whether this is a retransmission whose reply is discarded.</param>
    internal sealed record InFlightMessage(
        int Recorder,
        ProposerLane Proposer,
        RecordRequest<TValue>? Request,
        RecordReply<TValue>? Reply,
        TaskCompletionSource<RecordReply<TValue>>? Completion,
        bool IsDuplicate)
    {
        /// <summary>Whether this message is a request rather than a reply.</summary>
        public bool IsRequest => Request is not null;
    }


    /// <summary>One request as it reached a recorder, which is what a reach pin reads.</summary>
    /// <param name="Recorder">The recorder that served it.</param>
    /// <param name="Proposer">The lane that sent it.</param>
    /// <param name="Step">The step it carried.</param>
    /// <param name="IsDuplicate">Whether it was a retransmission.</param>
    internal sealed record DeliveredRequest(int Recorder, ProposerLane Proposer, RecorderStep Step, bool IsDuplicate);
}
