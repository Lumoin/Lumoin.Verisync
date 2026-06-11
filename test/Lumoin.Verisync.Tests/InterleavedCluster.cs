using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// A deterministic interleaving test bench: every request and reply between a proposer and an acceptor
/// node becomes an in-flight message, and messages are delivered one at a time in a seeded pseudo-random
/// order. Concurrent protocol runs are thereby explored across delivery interleavings — reorderings the
/// synchronous <see cref="SimulatedCluster{TValue}"/> can never produce — and any run replays exactly
/// from its seed.
/// </summary>
/// <typeparam name="TValue">The register value type.</typeparam>
/// <remarks>
/// <para>
/// The bench is single-threaded: completing an in-flight message resumes the awaiting proposer inline on
/// the pump thread, which then enqueues its next messages before <see cref="Step"/> returns. Virtual time
/// (<see cref="Now"/>) advances per delivered message and per <see cref="Tick"/>, giving histories
/// real-time intervals without wall clocks.
/// </para>
/// <para>
/// A partitioned acceptor loses messages at delivery time — requests and replies alike — so a partition
/// that appears mid-flight also kills the replies already in the air, the way a real link failure does.
/// </para>
/// <para>
/// With <see cref="RequestDuplicationPercent"/> set, a delivered request may be re-enqueued as a stale
/// duplicate that hits the acceptor again at a later delivery point with its reply discarded — the way a
/// retransmission lands after the proposer has already moved on. Duplicates mutate acceptor state, so they
/// exercise the histories where an old request arrives after newer ballots have been accepted.
/// </para>
/// </remarks>
internal sealed class InterleavedCluster<TValue>
{
    private readonly List<string> deliveryTrace = [];
    private uint randomState;

    private ConsensusNode<TValue>[] Nodes { get; }
    private HashSet<int> Partitioned { get; } = [];
    private List<InFlightMessage> InFlight { get; } = [];


    public InterleavedCluster(int nodeCount, int seed)
    {
        Nodes = new ConsensusNode<TValue>[nodeCount];
        for(int i = 0; i < nodeCount; i++)
        {
            Nodes[i] = new ConsensusNode<TValue>();
        }

        //Xorshift32 instead of System.Random: the sequence is deterministic across runtimes and platforms,
        //so a seed printed by a failing run replays the identical interleaving anywhere. State must be nonzero.
        randomState = seed == 0 ? 2463534242u : (uint)seed;
    }


    /// <summary>The virtual clock: advances per delivered message and per <see cref="Tick"/>.</summary>
    public long Now { get; private set; }


    /// <summary>
    /// The percentage chance (0–100) that a delivered request is re-enqueued as a stale duplicate. The
    /// duplicate is handled by its acceptor at a later delivery point and its reply is discarded.
    /// </summary>
    public int RequestDuplicationPercent { get; set; }


    /// <summary>The number of in-flight messages awaiting delivery.</summary>
    public int PendingCount => InFlight.Count;


    /// <summary>The delivery order so far, one line per delivered or lost message, for replay comparison.</summary>
    public IReadOnlyList<string> DeliveryTrace => deliveryTrace;


    public ConsensusNode<TValue> Node(int index) => Nodes[index];


    public void Partition(int index) => Partitioned.Add(index);


    public void Heal(int index) => Partitioned.Remove(index);


    /// <summary>Advances the virtual clock by one, marking a history boundary such as an operation start.</summary>
    public long Tick() => ++Now;


    /// <summary>
    /// Creates a proposer whose endpoints enqueue in-flight messages instead of completing synchronously,
    /// putting every exchange under the bench's delivery-order control.
    /// </summary>
    public FastProposer<TValue> CreateProposer()
    {
        var endpoints = new ConsensusEndpointDelegate<TValue>[Nodes.Length];
        for(int i = 0; i < Nodes.Length; i++)
        {
            int index = i;
            endpoints[i] = (request, _) =>
            {
                var completion = new TaskCompletionSource<ConsensusReply<TValue>>();
                InFlight.Add(new InFlightMessage(index, request, null, completion));

                return new ValueTask<ConsensusReply<TValue>>(completion.Task);
            };
        }

        return new FastProposer<TValue>(endpoints);
    }


    /// <summary>
    /// Delivers one pseudo-randomly chosen in-flight message: a request is handled by its node and the
    /// reply becomes a new in-flight message; a reply resumes the awaiting proposer inline.
    /// </summary>
    /// <returns>Whether a message was delivered; <see langword="false"/> when nothing is in flight.</returns>
    public bool Step()
    {
        if(InFlight.Count == 0)
        {
            return false;
        }

        int picked = NextIndex(InFlight.Count);
        InFlightMessage message = InFlight[picked];
        InFlight.RemoveAt(picked);
        Now++;

        //A duplicate carries no completion: it mutates acceptor state but its reply goes nowhere.
        string kind = message.Completion is null ? $"{Describe(message)}:dup" : Describe(message);
        if(Partitioned.Contains(message.Acceptor))
        {
            deliveryTrace.Add($"{Now}:a{message.Acceptor}:{kind}:lost");
            message.Completion?.SetException(new IOException($"Acceptor {message.Acceptor} is partitioned."));

            return true;
        }

        if(message.Request is not null)
        {
            ConsensusReply<TValue> reply = Nodes[message.Acceptor].Handle(message.Request);
            deliveryTrace.Add($"{Now}:a{message.Acceptor}:{kind}");
            if(message.Completion is not null)
            {
                InFlight.Add(new InFlightMessage(message.Acceptor, null, reply, message.Completion));
            }

            if(RequestDuplicationPercent > 0 && NextIndex(100) < RequestDuplicationPercent)
            {
                InFlight.Add(new InFlightMessage(message.Acceptor, message.Request, null, null));
            }
        }
        else
        {
            deliveryTrace.Add($"{Now}:a{message.Acceptor}:{kind}");
            message.Completion!.SetResult(message.Reply!);
        }

        return true;
    }


    /// <summary>
    /// Pumps until nothing is in flight. Because completions resume proposers inline, the queue only
    /// drains once every started protocol task has run to completion (or exhausted its own retries).
    /// </summary>
    public void RunToQuiescence()
    {
        while(Step())
        {
        }
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
            ? message.Request is PrepareRequest<TValue> ? "prepare" : "accept"
            : message.Reply is PrepareReply<TValue> ? "prepare-reply" : "accept-reply";
    }


    private sealed record InFlightMessage(int Acceptor, ConsensusRequest<TValue>? Request, ConsensusReply<TValue>? Reply, TaskCompletionSource<ConsensusReply<TValue>>? Completion);
}
