using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Drives one <see cref="QuePaxaVersionedNode{TValue}"/> over a single-consumer work queue, answering
/// every call on its own completion, so a declined instance faults that call alone while the host keeps
/// serving.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <remarks>
/// <para>
/// All inbound work is enqueued and <see cref="RunAsync"/> is the sole consumer and the only code that
/// touches the node, so the node's one-at-a-time contract holds even though <see cref="RecordAsync"/>,
/// <see cref="LearnAsync"/>, <see cref="MakeDurableAsync"/> and <see cref="ReadCommittedAsync"/> are
/// thread-safe producers callable from any connection. Exactly one runner owns a node and the node
/// enforces it: <see cref="RunAsync"/> claims the node for the life of its loop, a second runner's
/// <see cref="RunAsync"/> over a claimed node throws, and the node's own mutating members throw while
/// the claim is held. <see cref="RunAsync"/> runs once, and a host that must restart after a failed
/// write builds a fresh runner over the same node — the claim is released when the loop ends on any
/// path and before <see cref="RunAsync"/>'s task completes, so the fresh claim succeeds — which is what
/// keeps the durability baseline on the node rather than in the loop.
/// </para>
/// <para>
/// <see cref="RecordAsync"/> is a <see cref="VersionedRecorderEndpointDelegate{TValue}"/> over
/// <see cref="VersionedValue{TValue}"/> and needs no reply sink, because a reply's destination is the
/// call that asked for it, which is also what makes correlation per call as that delegate requires. A
/// host that cannot serve the addressed request faults the call and nothing else, and the fault is the
/// exception <see cref="QuePaxaVersionedNode{TValue}.Handle"/> threw, unwrapped and unwrapped alone: a
/// decline is the host's own act, so the caller receives exactly what the host raised and the runner
/// introduces no type, no reply and no version of its own. A wire host must reduce it to an opaque fault
/// carrying the call's correlation and nothing else, because the exception names the live version only
/// in prose and no protocol field carries it. Which throws are declines is
/// <see cref="QuePaxaVersionedNode{TValue}.Declines"/>'s answer and not this loop's, so a rule the host
/// gains — a chain it refuses, a membership it is outside of — arrives here as a decline rather than as a
/// defect that ends the loop.
/// </para>
/// <para>
/// The reply is handed to its call only after the state it rests on is durable, so a first proposal or
/// a committed record a proposer has read is never unpersisted. A persist failure ends the loop, the
/// reply computed for the call whose write failed is discarded and never delivered on any path,
/// abandonment included, and every call not yet answered is completed before the exception leaves
/// <see cref="RunAsync"/> — faulted with an <see cref="InvalidOperationException"/> wrapping the loop
/// failure, because the call did not fail on its own account, or cancelled under the runner's token
/// when that is the cause — since a proposer's attempt budget acts on faults and hangs on silence.
/// </para>
/// <para>
/// A learn is queued beside the requests rather than applied from outside, because the node is not safe
/// for concurrent calls. <see cref="LearnAsync"/>'s result reports adoption in memory — applied, not
/// durable — under <see cref="LearnDurability.InMemory"/>; what such a learn changes becomes durable
/// with the first reply that depends on it, or with <see cref="MakeDurableAsync"/>, which sequences a
/// checkpoint through the same queue. Under <see cref="LearnDurability.Durable"/> the host's state is
/// made durable before the call completes. A learn that moved
/// <see cref="QuePaxaVersionedNode{TValue}.ActiveConfiguration"/> is the one exception: it is checkpointed
/// under either durability, because the record that installs a membership may be the only copy of it inside
/// the membership it installs. A record of another chain faults its own learn and leaves the loop serving, on
/// the rule the request path follows: <see cref="QuePaxaVersionedNode{TValue}.DeclinesLearn"/> is what the
/// filter reads there, as <see cref="QuePaxaVersionedNode{TValue}.Declines"/> is on the request path.
/// </para>
/// <para>
/// A catch-up read goes through <see cref="ReadCommittedAsync"/>, which makes the state durable before
/// it reports the record. A read served beside this queue rather than through it may republish a record
/// the host learned and has not persisted; that was judged safe under crash faults — the record was
/// decided by a quorum, and losing the learn costs availability, never safety — but a peer that adopts
/// the republished record moves to the next version on it, so the sequenced read persists first and the
/// gate makes that free whenever the host owes no write.
/// </para>
/// <para>
/// A call cancelled by its own token completes cancelled even though its queued work still runs,
/// because an endpoint that ignored its token would block a cancelled proposal indefinitely.
/// Continuations are asynchronous, so a resumed proposer's next send cannot re-enter this queue on the
/// loop's own thread.
/// </para>
/// </remarks>
public sealed class QuePaxaVersionedRunner<TValue>
{
    private readonly Channel<WorkItem> work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private int started;


    /// <summary>
    /// Initializes a runner over <paramref name="node"/>.
    /// </summary>
    /// <param name="node">The host this runner drives. Exactly one runner owns a node at a time, which <see cref="RunAsync"/> claims and the node enforces.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="node"/> is <see langword="null"/>.</exception>
    public QuePaxaVersionedRunner(QuePaxaVersionedNode<TValue> node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node = node;
    }


    /// <summary>
    /// The host this runner drives. Its documented off-loop reads —
    /// <see cref="QuePaxaVersionedNode{TValue}.Committed"/>, <see cref="QuePaxaVersionedNode{TValue}.LiveVersion"/>,
    /// <see cref="QuePaxaVersionedNode{TValue}.Serves"/>, <see cref="QuePaxaVersionedNode{TValue}.Instance"/>,
    /// <see cref="QuePaxaVersionedNode{TValue}.Recorder"/>, and the immutable
    /// <see cref="QuePaxaVersionedNode{TValue}.Genesis"/> and <see cref="QuePaxaVersionedNode{TValue}.Self"/> —
    /// stay open beside the running loop, which is what a wire host serves an operations endpoint from
    /// without keeping a second reference to the node. Every mutating member throws under the loop's
    /// ownership claim, so the export widens what a host can read and nothing it can interleave.
    /// </summary>
    public QuePaxaVersionedNode<TValue> Node { get; }


    /// <summary>
    /// Queues a record request and completes with its reply once the loop has served it and the state
    /// the reply rests on is durable.
    /// </summary>
    /// <param name="request">The request to serve.</param>
    /// <param name="cancellationToken">The caller's token; the returned task completes when it signals, though the queued work still runs.</param>
    /// <returns>The reply, after the state it rests on is durable.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the loop has ended.</exception>
    /// <remarks>
    /// This method is a <see cref="VersionedRecorderEndpointDelegate{TValue}"/> over
    /// <see cref="VersionedValue{TValue}"/>: assign it where that delegate is expected. A declined
    /// instance faults the returned task with the exception the host threw; the queued work of a
    /// cancelled call is left to run, because recording it twice is the identity.
    /// </remarks>
    public ValueTask<VersionedRecordReply<VersionedValue<TValue>>> RecordAsync(VersionedRecordRequest<VersionedValue<TValue>> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = new TaskCompletionSource<VersionedRecordReply<VersionedValue<TValue>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask write = work.Writer.WriteAsync(new RecordItem(request, source), cancellationToken);
        if(write.IsCompletedSuccessfully)
        {
            return new ValueTask<VersionedRecordReply<VersionedValue<TValue>>>(source.Task.WaitAsync(cancellationToken));
        }

        return AwaitEnqueued(write, source.Task, cancellationToken);
    }


    /// <summary>
    /// Queues a committed record for adoption and completes with whether it advanced the host.
    /// </summary>
    /// <param name="committed">A decided record.</param>
    /// <param name="durability">How far the learn must get before this call completes.</param>
    /// <param name="cancellationToken">The caller's token; the returned task completes when it signals, though the queued work still runs.</param>
    /// <returns><see langword="true"/> when the record advanced the host in memory.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="durability"/> is not a defined <see cref="LearnDurability"/>.</exception>
    /// <exception cref="ArgumentException">Thrown through the returned task when the record names a chain other than the host's, which faults this call alone.</exception>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the loop has ended.</exception>
    /// <remarks>
    /// <para>
    /// This method is a <see cref="ReceiveCommittedRecordDelegate{TValue}"/> by method-group conversion:
    /// assign it where that delegate is expected, which is the receiving end a
    /// <see cref="PublishCommittedRecordDelegate{TValue}"/> implementation delivers into.
    /// </para>
    /// <para>
    /// Under <see cref="LearnDurability.InMemory"/> what the learn changes becomes durable with the first
    /// reply that depends on it or with <see cref="MakeDurableAsync"/>, which stays the caller-driven
    /// checkpoint. Under <see cref="LearnDurability.Durable"/> the host's state is made durable through
    /// the same gate before this call completes, so a failed write faults the learn rather than reporting
    /// an adoption a crash could lose, and a non-advancing learn still runs the gate because the caller
    /// asked for a checkpoint and the gate turns it into no write whenever nothing is owed.
    /// </para>
    /// <para>
    /// A learn that moved <see cref="QuePaxaVersionedNode{TValue}.ActiveConfiguration"/> is made durable
    /// under either durability, and that asymmetry is deliberate. Eager persistence per disseminated record
    /// is the demotion the durability knob exists to avoid, while the record that installs a membership may
    /// be the only copy of it inside the membership it installs, so a host that adopted it in memory and
    /// crashed would come back serving under the membership the change replaced. A learn the host did not
    /// adopt installs nothing and owes nothing, and a store the loop runs without still keeps the whole gate
    /// vacuous.
    /// </para>
    /// <para>
    /// The result reports adoption in memory whichever durability was asked for. After a
    /// <see cref="LearnDurability.Durable"/> call that completed the state IS durable, and after one that
    /// faulted nothing is promised, so there is nothing a second flag could say.
    /// </para>
    /// <para>
    /// A record of another chain faults this call with the host's own refusal and leaves the loop serving,
    /// exactly as a declined request does at <see cref="RecordAsync"/>. The refusal is a fault rather than a
    /// value the result carries, because the result reports adoption and a caller told <see langword="false"/>
    /// would read a wiring defect as the ordinary record that did not advance, while the two ask for opposite
    /// acts: one is a record already held and the other is a publisher to rewire.
    /// </para>
    /// <para>
    /// A <see cref="LearnDurability.Durable"/> learn to <see cref="RegisterVersion.MaxValue"/> fires the
    /// gate on a host that serves no version and ends the loop through the snapshot's documented throw,
    /// with the learn faulted. That is the same terminal-by-design contract
    /// <see cref="MakeDurableAsync"/> carries, reached one call earlier: such a host declines every call
    /// without a write, and a deployment retires a spent key.
    /// </para>
    /// <para>
    /// A runner running without a persist delegate completes either durability successfully, reproducing
    /// the in-memory behavior as every other producer does.
    /// </para>
    /// </remarks>
    public ValueTask<bool> LearnAsync(VersionedValue<TValue> committed, LearnDurability durability, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(committed);
        if(durability is not (LearnDurability.InMemory or LearnDurability.Durable))
        {
            throw new ArgumentOutOfRangeException(nameof(durability), durability, "A learn is either applied in memory or made durable before it completes, and no other durability is defined. An undefined value taking the in-memory arm would silently lose the crash safety a caller asked for.");
        }

        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask write = work.Writer.WriteAsync(new LearnItem(committed, durability, source), cancellationToken);
        if(write.IsCompletedSuccessfully)
        {
            return new ValueTask<bool>(source.Task.WaitAsync(cancellationToken));
        }

        return AwaitEnqueued(write, source.Task, cancellationToken);
    }


    /// <summary>
    /// Queues a catch-up read and completes with the host's committed record once that record is durable.
    /// </summary>
    /// <param name="cancellationToken">The caller's token; the returned task completes when it signals, though the queued work still runs.</param>
    /// <returns>The committed record the host has learned, or <see langword="null"/> when it has learned none.</returns>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the loop has ended.</exception>
    /// <remarks>
    /// <para>
    /// This method is a <see cref="ReadCommittedRecordDelegate{TValue}"/>: assign it where that delegate
    /// is expected, as <see cref="QuePaxaVersionedRegister{TValue}"/> does for its catch-up. It carries no
    /// durability knob deliberately, both because the knob would break that conversion and because a
    /// republish is not permitted to be unpersisted: a replica adopts what a host reports and moves to the
    /// next version on it, so a record republished before it was written is one a crash loses while a peer
    /// has already built on it.
    /// </para>
    /// <para>
    /// The record is read after the gate has returned, so what leaves is a value the store holds. The
    /// write costs nothing whenever the host owes none, which is every host that has answered a request
    /// since its last learn; a host that owes one owes it because it learned and has not replied since, or
    /// because a write failed, which are the states where republishing is the hazard.
    /// </para>
    /// <para>
    /// A sequenced <see cref="ObserveCommittedVersionDelegate"/> is built from this rather than from a
    /// second unsequenced reader:
    /// <c>async token =&gt; (await runner.ReadCommittedAsync(token).ConfigureAwait(false))?.Version ?? RegisterVersion.Unwritten</c>.
    /// </para>
    /// <para>
    /// A read on a host that learned its way to <see cref="RegisterVersion.MaxValue"/> without an
    /// intervening write ends the loop through the snapshot's documented throw, with the read faulted,
    /// which is the same terminal-by-design contract <see cref="MakeDurableAsync"/> carries.
    /// </para>
    /// </remarks>
    public ValueTask<VersionedValue<TValue>?> ReadCommittedAsync(CancellationToken cancellationToken = default)
    {
        var source = new TaskCompletionSource<VersionedValue<TValue>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask write = work.Writer.WriteAsync(new ReadItem(source), cancellationToken);
        if(write.IsCompletedSuccessfully)
        {
            return new ValueTask<VersionedValue<TValue>?>(source.Task.WaitAsync(cancellationToken));
        }

        return AwaitEnqueued(write, source.Task, cancellationToken);
    }


    /// <summary>
    /// Queues a durability checkpoint and completes once the host's state is durable.
    /// </summary>
    /// <param name="cancellationToken">The caller's token; the returned task completes when it signals, though the queued work still runs.</param>
    /// <returns>A task that completes once the state is durable, or immediately successful when the loop runs without a persist delegate.</returns>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the loop has ended.</exception>
    /// <remarks>
    /// This is one of the four paths on which the committed arm of the durability gate fires alone,
    /// beside a <see cref="LearnDurability.Durable"/> learn, a learn that moved
    /// <see cref="QuePaxaVersionedNode{TValue}.ActiveConfiguration"/>, and
    /// <see cref="ReadCommittedAsync"/>: a
    /// learned record whose recorder reference did not move — the shared leaderless singleton across a
    /// learn — becomes durable through one of the four or with the next dependent reply, whichever
    /// comes first. None of the four is a reply path, which is why no reply vector reaches that arm. A checkpoint on a
    /// host that learned its way to <see cref="RegisterVersion.MaxValue"/> without an intervening write
    /// ends the loop through the snapshot's documented throw, which is terminal by design: such a host
    /// declines every call without a write, and a deployment retires a spent key. While a runner owns the
    /// node, <see cref="QuePaxaVersionedNode{TValue}.MakeDurableAsync"/> refuses a direct call; this
    /// method is the checkpoint there.
    /// </remarks>
    public ValueTask MakeDurableAsync(CancellationToken cancellationToken = default)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask write = work.Writer.WriteAsync(new DurableItem(source), cancellationToken);
        if(write.IsCompletedSuccessfully)
        {
            return new ValueTask(source.Task.WaitAsync(cancellationToken));
        }

        return AwaitEnqueuedPlain(write, source.Task, cancellationToken);
    }


    /// <summary>
    /// Stops accepting work; the loop drains what was already queued and returns.
    /// </summary>
    /// <remarks>
    /// A producer that runs after this faults fast with <see cref="ChannelClosedException"/> instead of
    /// hanging on a loop that will never dispatch it.
    /// </remarks>
    public void Complete()
    {
        _ = work.Writer.TryComplete();
    }


    /// <summary>
    /// Dispatches queued work against the node, one item at a time, until the queue is completed or the
    /// token is signalled.
    /// </summary>
    /// <param name="persistNode">
    /// An optional durability hook. When supplied, the node's state is made durable before any reply
    /// that rests on it is handed to its call, through the gate on
    /// <see cref="QuePaxaVersionedNode{TValue}.MakeDurableAsync"/>. When <see langword="null"/>, replies
    /// leave immediately and checkpoints complete successfully, reproducing the in-memory behavior
    /// suitable for tests and ephemeral clusters.
    /// </param>
    /// <param name="cancellationToken">The runner's token; signalling it ends the loop and cancels every unanswered call under it.</param>
    /// <returns>A task that completes when the queue is drained after <see cref="Complete"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when called a second time — a restart after a failure takes a fresh runner over the same node — and when another runner already owns the node.</exception>
    /// <remarks>
    /// <para>
    /// A throwing persist delegate ends the loop: the call whose write failed is faulted and its
    /// computed reply discarded, every queued call is faulted with the loop failure as inner exception,
    /// the writer is completed so later producers fail fast, and the exception then propagates out —
    /// the fail-closed posture the sibling loops share, since a host whose durable store is gone cannot
    /// keep promising state it may lose.
    /// </para>
    /// <para>
    /// The once-only guard precedes the claim, so a second call on this runner faults before it can
    /// touch a claim the first call holds and can neither steal nor drop it. The claim is released in a
    /// <c>finally</c>, so ownership ends with the loop on every path and before this task completes,
    /// which is what lets a fresh runner over the same node claim it.
    /// </para>
    /// </remarks>
    public async Task RunAsync(PersistVersionedNodeDelegate<TValue>? persistNode = null, CancellationToken cancellationToken = default)
    {
        if(Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The loop runs once per runner; a restart after a failure takes a fresh runner over the same node.");
        }

        Node.ClaimForRunner();
        try
        {
            WorkItem? inFlight = null;
            try
            {
                await foreach(WorkItem item in work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    inFlight = item;
                    await DispatchAsync(item, persistNode, cancellationToken).ConfigureAwait(false);
                    inFlight = null;
                }
            }
            catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
            {
                Abandon(inFlight, null, cancellationToken);

                throw;
            }
            catch(Exception exception)
            {
                Abandon(inFlight, exception, cancellationToken);

                throw;
            }
        }
        finally
        {
            Node.ReleaseFromRunner();
        }
    }


    private async ValueTask DispatchAsync(WorkItem item, PersistVersionedNodeDelegate<TValue>? persistNode, CancellationToken cancellationToken)
    {
        switch(item)
        {
            case RecordItem record:
                VersionedRecordReply<VersionedValue<TValue>> reply;
                try
                {
                    reply = Node.HandleForOwner(record.Request);
                }
                catch(Exception refusal) when(Node.Declines(record.Request))
                {
                    //Every documented refusal precedes any mutation on the host, so the loop keeps serving;
                    //a throw at a request the host does serve is a defect and ends the loop instead. The
                    //filter reads the host's own classifier rather than a version test of its own, so a
                    //membership or chain refusal is a decline here and not a defect.
                    _ = record.Source.TrySetException(refusal);

                    return;
                }

                if(persistNode is not null)
                {
                    await Node.MakeDurableForOwnerAsync(persistNode, cancellationToken).ConfigureAwait(false);
                }

                _ = record.Source.TrySetResult(reply);
                break;
            case LearnItem learn:
                //The adoption is computed before the write and handed over after it, so a failed write
                //leaves the loop without ever reporting an adoption the crash would lose.
                QuePaxaConfiguration held = Node.ActiveConfiguration;
                bool advanced;
                try
                {
                    advanced = Node.LearnForOwner(learn.Committed);
                }
                catch(Exception refusal) when(Node.DeclinesLearn(learn.Committed))
                {
                    //A record of another chain is a defect at the publisher that pushed it and not a failure
                    //of this host, and the refusal precedes any mutation, so it faults its own call and the
                    //loop keeps serving. The filter reads the host's own classifier, as the record arm's
                    //does, so the refusal reaches the caller as a fault while a record that merely did not
                    //advance still reports false.
                    _ = learn.Source.TrySetException(refusal);

                    return;
                }

                //A learn that installed a membership is checkpointed under either durability, because the
                //record carrying that membership may be the only copy of it inside the membership it
                //installs, and a host that lost it would come back serving an instance under the membership
                //the change replaced. The comparison is between what the host held before the learn and what
                //it holds after, so a record the host did not adopt installs nothing and owes nothing, and it
                //reads the host's own memo rather than deriving the membership a second time out here.
                if(persistNode is not null && (learn.Durability == LearnDurability.Durable || !Node.ActiveConfiguration.Equals(held)))
                {
                    await Node.MakeDurableForOwnerAsync(persistNode, cancellationToken).ConfigureAwait(false);
                }

                _ = learn.Source.TrySetResult(advanced);
                break;
            case DurableItem durable:
                if(persistNode is not null)
                {
                    await Node.MakeDurableForOwnerAsync(persistNode, cancellationToken).ConfigureAwait(false);
                }

                _ = durable.Source.TrySetResult();
                break;
            case ReadItem read:
                if(persistNode is not null)
                {
                    await Node.MakeDurableForOwnerAsync(persistNode, cancellationToken).ConfigureAwait(false);
                }

                _ = read.Source.TrySetResult(Node.Committed);
                break;
        }
    }


    private void Abandon(WorkItem? inFlight, Exception? failure, CancellationToken cancellationToken)
    {
        //Completing the writer first makes a later producer fail fast with ChannelClosedException, and
        //lets the drain below observe every write that had already succeeded.
        _ = work.Writer.TryComplete();

        InvalidOperationException? fault = failure is null
            ? null
            : new InvalidOperationException("The runner loop ended before the call completed; the inner exception is the loop failure.", failure);

        if(inFlight is not null)
        {
            CompleteAbandoned(inFlight, fault, cancellationToken);
        }

        while(work.Reader.TryRead(out WorkItem? item))
        {
            CompleteAbandoned(item, fault, cancellationToken);
        }
    }


    //TrySet keeps this a no-op for an item whose dispatch already answered or already faulted with its
    //decline, so a declined call keeps its decline through a later loop end.
    private static void CompleteAbandoned(WorkItem item, InvalidOperationException? fault, CancellationToken cancellationToken)
    {
        switch(item)
        {
            case RecordItem record when fault is null:
                _ = record.Source.TrySetCanceled(cancellationToken);
                break;
            case RecordItem record:
                _ = record.Source.TrySetException(fault);
                break;
            case LearnItem learn when fault is null:
                _ = learn.Source.TrySetCanceled(cancellationToken);
                break;
            case LearnItem learn:
                _ = learn.Source.TrySetException(fault);
                break;
            case DurableItem durable when fault is null:
                _ = durable.Source.TrySetCanceled(cancellationToken);
                break;
            case DurableItem durable:
                _ = durable.Source.TrySetException(fault);
                break;
            case ReadItem read when fault is null:
                _ = read.Source.TrySetCanceled(cancellationToken);
                break;
            case ReadItem read:
                _ = read.Source.TrySetException(fault);
                break;
        }
    }


    private static async ValueTask<TResult> AwaitEnqueued<TResult>(ValueTask write, Task<TResult> completion, CancellationToken cancellationToken)
    {
        await write.ConfigureAwait(false);

        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }


    private static async ValueTask AwaitEnqueuedPlain(ValueTask write, Task completion, CancellationToken cancellationToken)
    {
        await write.ConfigureAwait(false);
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }


    private abstract record WorkItem;

    private sealed record RecordItem(VersionedRecordRequest<VersionedValue<TValue>> Request, TaskCompletionSource<VersionedRecordReply<VersionedValue<TValue>>> Source): WorkItem;

    private sealed record LearnItem(VersionedValue<TValue> Committed, LearnDurability Durability, TaskCompletionSource<bool> Source): WorkItem;

    private sealed record DurableItem(TaskCompletionSource Source): WorkItem;

    private sealed record ReadItem(TaskCompletionSource<VersionedValue<TValue>?> Source): WorkItem;
}
