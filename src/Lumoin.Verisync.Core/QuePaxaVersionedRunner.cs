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
/// <see cref="LearnAsync"/> and <see cref="MakeDurableAsync"/> are thread-safe producers callable from
/// any connection. Exactly one runner may own a node; a second runner over the same node is undefined.
/// <see cref="RunAsync"/> runs once, and a host that must restart after a failed write builds a fresh
/// runner over the same node, which is what keeps the durability baseline on the node rather than in
/// the loop.
/// </para>
/// <para>
/// <see cref="RecordAsync"/> is a <see cref="VersionedRecorderEndpointDelegate{TValue}"/> over
/// <see cref="VersionedValue{TValue}"/> and needs no reply sink, because a reply's destination is the
/// call that asked for it, which is also what makes correlation per call as that delegate requires. A
/// host that cannot serve the addressed instance faults the call and nothing else, and the fault is the
/// exception <see cref="QuePaxaVersionedNode{TValue}.Handle"/> threw, unwrapped: a decline is the
/// host's own act, indistinguishable from calling <see cref="QuePaxaVersionedNode{TValue}.Handle"/>
/// directly. A wire host must reduce it to an opaque fault carrying the call's correlation and nothing
/// else, because the exception names the live version only in prose and no protocol field carries it.
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
/// durable; what a learn changes becomes durable with the first reply that depends on it, or with
/// <see cref="MakeDurableAsync"/>, which sequences a checkpoint through the same queue. A catch-up read
/// served beside this queue may republish a learned record before it is durable, which is safe under
/// crash faults — the record was decided by a quorum, and losing the learn costs availability, never
/// safety — and is documented rather than closed.
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
    /// <param name="node">The host this runner drives. Exactly one runner may own a node.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="node"/> is <see langword="null"/>.</exception>
    public QuePaxaVersionedRunner(QuePaxaVersionedNode<TValue> node)
    {
        ArgumentNullException.ThrowIfNull(node);

        Node = node;
    }


    private QuePaxaVersionedNode<TValue> Node { get; }


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
    /// <param name="cancellationToken">The caller's token; the returned task completes when it signals, though the queued work still runs.</param>
    /// <returns><see langword="true"/> when the record advanced the host — applied, not durable.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="committed"/> is <see langword="null"/>.</exception>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the loop has ended.</exception>
    /// <remarks>
    /// What the learn changes becomes durable with the first reply that depends on it or with
    /// <see cref="MakeDurableAsync"/>; a host that must acknowledge a dissemination as durable
    /// checkpoints first.
    /// </remarks>
    public ValueTask<bool> LearnAsync(VersionedValue<TValue> committed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(committed);

        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask write = work.Writer.WriteAsync(new LearnItem(committed, source), cancellationToken);
        if(write.IsCompletedSuccessfully)
        {
            return new ValueTask<bool>(source.Task.WaitAsync(cancellationToken));
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
    /// This is the one path on which the committed arm of the durability gate fires alone: a learned
    /// record whose recorder reference did not move — the shared leaderless singleton across a learn —
    /// becomes durable here or with the next dependent reply, whichever comes first. A checkpoint on a
    /// host that learned its way to <see cref="RegisterVersion.MaxValue"/> without an intervening write
    /// ends the loop through the snapshot's documented throw, which is terminal by design: such a host
    /// declines every call without a write, and a deployment retires a spent key. While a runner owns the
    /// node, calling <see cref="QuePaxaVersionedNode{TValue}.MakeDurableAsync"/> directly is not safe;
    /// this method is the safe checkpoint there.
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
    /// <exception cref="InvalidOperationException">Thrown when called a second time; a restart after a failure takes a fresh runner over the same node.</exception>
    /// <remarks>
    /// A throwing persist delegate ends the loop: the call whose write failed is faulted and its
    /// computed reply discarded, every queued call is faulted with the loop failure as inner exception,
    /// the writer is completed so later producers fail fast, and the exception then propagates out —
    /// the fail-closed posture the sibling loops share, since a host whose durable store is gone cannot
    /// keep promising state it may lose.
    /// </remarks>
    public async Task RunAsync(PersistVersionedNodeDelegate<TValue>? persistNode = null, CancellationToken cancellationToken = default)
    {
        if(Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("The loop runs once per runner; a restart after a failure takes a fresh runner over the same node.");
        }

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


    private async ValueTask DispatchAsync(WorkItem item, PersistVersionedNodeDelegate<TValue>? persistNode, CancellationToken cancellationToken)
    {
        switch(item)
        {
            case RecordItem record:
                VersionedRecordReply<VersionedValue<TValue>> reply;
                try
                {
                    reply = Node.Handle(record.Request);
                }
                catch(Exception refusal) when(!Node.Serves(record.Request.Version))
                {
                    //Both documented refusals precede any mutation on the host, so the loop keeps serving;
                    //a throw at a version the host does serve is a defect and ends the loop instead.
                    _ = record.Source.TrySetException(refusal);

                    return;
                }

                if(persistNode is not null)
                {
                    await Node.MakeDurableAsync(persistNode, cancellationToken).ConfigureAwait(false);
                }

                _ = record.Source.TrySetResult(reply);
                break;
            case LearnItem learn:
                _ = learn.Source.TrySetResult(Node.Learn(learn.Committed));
                break;
            case DurableItem durable:
                if(persistNode is not null)
                {
                    await Node.MakeDurableAsync(persistNode, cancellationToken).ConfigureAwait(false);
                }

                _ = durable.Source.TrySetResult();
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

    private sealed record LearnItem(VersionedValue<TValue> Committed, TaskCompletionSource<bool> Source): WorkItem;

    private sealed record DurableItem(TaskCompletionSource Source): WorkItem;
}
