using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Drives a single <see cref="RaftNode{TCommand}"/> over a message-driven loop, adding the production story
/// the in-memory node leaves to its host: a single-consumer work queue that preserves the node's
/// single-threaded contract, a persist-before-output durability seam, and an apply seam over the committed
/// command stream. It is the log-replication counterpart to the runner role
/// <see cref="ConsensusNode{TValue}"/> plays for the register plane.
/// </summary>
/// <typeparam name="TCommand">The application command type replicated and ordered by the log.</typeparam>
/// <remarks>
/// <para>
/// All inbound work — an envelope from a peer, a host election or heartbeat trigger, or a client proposal —
/// is enqueued onto an unbounded channel; <see cref="RunAsync"/> is the sole consumer and the only code that
/// touches the node, so the node's "one message at a time, not safe for concurrent calls" contract holds
/// even though <see cref="SubmitAsync"/>, the triggers, and <see cref="ProposeAsync"/> are thread-safe
/// producers callable from anywhere.
/// </para>
/// <para>
/// Every handled work item runs the same sequence: handle it against the node, then — when a persist hook is
/// supplied — make <see cref="RaftNode{TCommand}.ToState"/> durable, then apply every newly committed entry
/// to the apply hook in index order, then send any outbound envelopes. Persisting unconditionally after each
/// handled item (rather than only when the durable triple changed) is the fail-closed choice; skipping
/// unchanged snapshots is an optimization deliberately not taken. Because persist precedes send, no vote or
/// appended entry becomes observable to a peer before it is durable.
/// </para>
/// <para>
/// <strong>Liveness stays external.</strong> The runner adds no timers and draws no entropy: elections and
/// heartbeats happen only when the host calls <see cref="TriggerElectionAsync"/> or
/// <see cref="TriggerHeartbeatAsync"/>, exactly as <see cref="RaftNode{TCommand}"/> requires.
/// </para>
/// </remarks>
public sealed class RaftRunner<TCommand>
{
    private readonly RaftNode<TCommand> node;
    private readonly Channel<WorkItem> work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    //Runner-local applied watermark; the highest index already handed to the apply hook this process lifetime.
    //Volatile bookkeeping like the node's commit index, it starts at zero and is touched only by RunAsync.
    private long lastApplied;

    //Guards single entry into RunAsync. The node's single-threaded contract assumes exactly one consumer.
    private int started;


    /// <summary>
    /// Creates a runner over <paramref name="node"/>. The runner owns the node for the duration of
    /// <see cref="RunAsync"/>; no other code should touch the node while the loop is running.
    /// </summary>
    /// <param name="node">The node to drive.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="node"/> is <see langword="null"/>.</exception>
    public RaftRunner(RaftNode<TCommand> node)
    {
        ArgumentNullException.ThrowIfNull(node);

        this.node = node;
    }


    /// <summary>
    /// Runs the single-consumer loop until the channel is completed by <see cref="Complete"/> (after which it
    /// drains and returns) or <paramref name="cancellationToken"/> is signalled (after which it throws).
    /// </summary>
    /// <param name="send">The outbound transport edge; see <see cref="SendRaftEnvelopeDelegate{TCommand}"/>.</param>
    /// <param name="persistState">
    /// An optional durability hook. When supplied it is awaited after every handled item, receiving an
    /// immutable snapshot, and before any outbound send, so the durable triple is stable before it becomes
    /// observable. When <see langword="null"/>, no persistence happens — the in-memory behavior.
    /// </param>
    /// <param name="applyCommitted">
    /// An optional apply hook. When supplied it is awaited for each newly committed entry, in index order.
    /// When <see langword="null"/>, the commit watermark still advances but no application happens.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the channel is drained after completion.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="send"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="RunAsync"/> has already been called.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// A throwing <paramref name="send"/>, <paramref name="persistState"/>, or <paramref name="applyCommitted"/>
    /// propagates out of this method and ends the loop — the fail-closed posture, since a node whose transport,
    /// durable store, or state machine has failed cannot keep serving. A faulted proposal (proposing on a
    /// non-leader) only faults its own <see cref="Task"/> and never ends the loop. Whenever the loop ends
    /// early, every proposal not yet completed — the queued ones and the one being dispatched — is completed
    /// before the exception leaves this method: cancelled on cancellation, faulted with an
    /// <see cref="InvalidOperationException"/> carrying the loop failure as its inner exception otherwise. The
    /// work channel is completed at the same time, so a later <see cref="ProposeAsync"/>,
    /// <see cref="SubmitAsync"/>, or trigger fails fast with <see cref="ChannelClosedException"/> instead of
    /// enqueuing into a loop that no longer runs. A null <paramref name="send"/> fails the same way before the
    /// loop even starts: the writer is completed and every already-enqueued proposal is faulted, so the misuse
    /// cannot leave a proposal waiting on a runner that will never run.
    /// </remarks>
    public async Task RunAsync(
        SendRaftEnvelopeDelegate<TCommand> send,
        PersistRaftStateDelegate<TCommand>? persistState = null,
        ApplyCommittedDelegate<TCommand>? applyCommitted = null,
        CancellationToken cancellationToken = default)
    {
        if(send is null)
        {
            //Argument validation fails before the loop can run, and a runner started with a null transport
            //will never dispatch. Fail closed exactly as an early loop exit does — complete the writer and
            //fault every already-enqueued proposal — so a pre-enqueued or later proposal surfaces the misuse
            //loudly instead of hanging forever. The fault only runs when this call would have owned the run,
            //so a concurrent healthy run keeps its channel; that run then poisons this second entry below.
            ArgumentNullException validation = new(nameof(send));
            if(Interlocked.Exchange(ref started, 1) == 0)
            {
                AbandonPendingProposals(null, new InvalidOperationException("The runner loop was started with a null send delegate and will never run; the inner exception is the validation failure.", validation), cancellationToken);
            }

            throw validation;
        }

        if(Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("RunAsync may only be called once per runner.");
        }

        //The proposal being dispatched has already left the channel, so the drain below cannot see it; track
        //it here so an early loop exit completes it along with the queued ones instead of orphaning it.
        ProposeItem? inFlight = null;
        try
        {
            await foreach(WorkItem item in work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                inFlight = item as ProposeItem;
                await DispatchAsync(item, send, persistState, applyCommitted, cancellationToken).ConfigureAwait(false);
                inFlight = null;
            }
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
        {
            //The runner's own token cancelled the loop, so the pending proposals cancel under it and carry
            //that token as their cancellation cause. A hook's internal cancellation — an OperationCanceledException
            //thrown for the hook's own reasons while the runner token is NOT signalled — fails this filter and
            //flows to the fault path below, where it faults the proposals rather than masquerading as a clean stop.
            AbandonPendingProposals(inFlight, fault: null, cancellationToken);

            throw;
        }
        catch(Exception exception)
        {
            AbandonPendingProposals(inFlight, new InvalidOperationException("The runner loop ended before the proposal completed; the inner exception is the loop failure.", exception), cancellationToken);

            throw;
        }
    }


    /// <summary>
    /// Submits an inbound envelope received from a peer for the runner to handle.
    /// </summary>
    /// <param name="envelope">The envelope to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the envelope is enqueued.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="envelope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="envelope"/> does not carry exactly one payload.</exception>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the runner loop has ended.</exception>
    public ValueTask SubmitAsync(RaftEnvelope<TCommand> envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.EnsureSinglePayload(nameof(envelope));

        return work.Writer.WriteAsync(new EnvelopeItem(envelope), cancellationToken);
    }


    /// <summary>
    /// Proposes <paramref name="command"/> to the cluster through this node.
    /// </summary>
    /// <param name="command">The command to replicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A task that completes with the command's 1-based log index once the leader has appended it, or faults
    /// with <see cref="InvalidOperationException"/> when this node is not the leader or when the runner loop
    /// ends before the proposal completes (the inner exception then carries the loop failure). The task is
    /// cancelled when the runner is cancelled before the proposal completes.
    /// </returns>
    /// <remarks>
    /// The returned task reflects the leader's local append, not cluster commitment; commitment is observed
    /// through the apply hook as the entry crosses the commit threshold. Proposing on a non-leader faults this
    /// task alone and never disturbs the runner loop. A faulted or cancelled proposal means the proposal did
    /// not COMPLETE, not that the command is absent: the append and the persist precede the task's completion,
    /// so a proposal abandoned mid-dispatch may already sit in the leader's durable log and may later commit.
    /// A host that retries on fault or cancellation must therefore tolerate or deduplicate a possible
    /// duplicate command. A proposal issued after the runner has stopped accepting
    /// work — <see cref="Complete"/> was called or the loop has ended — faults with
    /// <see cref="ChannelClosedException"/> instead of hanging on a loop that will never dispatch it.
    /// </remarks>
    public Task<long> ProposeAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        var source = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask write = work.Writer.WriteAsync(new ProposeItem(command, source), cancellationToken);
        if(write.IsCompletedSuccessfully)
        {
            return source.Task;
        }

        return AwaitWriteThenSource(write, source);
    }


    /// <summary>
    /// Triggers an election: the host's signal that it judges the leader lost. The runner calls
    /// <see cref="RaftNode{TCommand}.StartElection"/> and broadcasts the vote request to every peer.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the trigger is enqueued.</returns>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the runner loop has ended.</exception>
    public ValueTask TriggerElectionAsync(CancellationToken cancellationToken = default)
    {
        return work.Writer.WriteAsync(ElectionItem.Instance, cancellationToken);
    }


    /// <summary>
    /// Triggers a round of heartbeats: when this node is the leader, the runner sends a per-follower
    /// <see cref="RaftNode{TCommand}.CreateAppendEntries(ReplicaId)"/> to every peer; otherwise it is a no-op.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the trigger is enqueued.</returns>
    /// <exception cref="ChannelClosedException">Thrown through the returned task when no further work is accepted after <see cref="Complete"/> or after the runner loop has ended.</exception>
    public ValueTask TriggerHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        return work.Writer.WriteAsync(HeartbeatItem.Instance, cancellationToken);
    }


    /// <summary>
    /// Completes the work channel: no further work is accepted, and <see cref="RunAsync"/> returns once the
    /// already-queued items are drained. After this call a <see cref="SubmitAsync"/>, <see cref="ProposeAsync"/>,
    /// <see cref="TriggerElectionAsync"/>, or <see cref="TriggerHeartbeatAsync"/> faults its returned task with
    /// <see cref="ChannelClosedException"/>.
    /// </summary>
    public void Complete()
    {
        work.Writer.TryComplete();
    }


    private static async Task<long> AwaitWriteThenSource(ValueTask write, TaskCompletionSource<long> source)
    {
        await write.ConfigureAwait(false);

        return await source.Task.ConfigureAwait(false);
    }


    private async ValueTask DispatchAsync(
        WorkItem item,
        SendRaftEnvelopeDelegate<TCommand> send,
        PersistRaftStateDelegate<TCommand>? persistState,
        ApplyCommittedDelegate<TCommand>? applyCommitted,
        CancellationToken cancellationToken)
    {
        switch(item)
        {
            case EnvelopeItem envelopeItem:
                await HandleEnvelopeAsync(envelopeItem.Envelope, send, persistState, applyCommitted, cancellationToken).ConfigureAwait(false);

                break;

            case ElectionItem:
                RequestVoteRequest voteRequest = node.StartElection();
                await PersistAsync(persistState, cancellationToken).ConfigureAwait(false);
                await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);
                await BroadcastAsync(RaftEnvelope<TCommand>.ForVoteRequest(node.Id, voteRequest), send, cancellationToken).ConfigureAwait(false);

                break;

            case HeartbeatItem:
                //Building requests mutates nothing, so no persist is needed; the apply walk still runs for
                //uniformity even though it cannot advance here.
                await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);
                if(node.Role == RaftRole.Leader)
                {
                    await SendToEachPeerAsync(send, cancellationToken).ConfigureAwait(false);
                }

                break;

            case ProposeItem proposeItem:
                await HandleProposeAsync(proposeItem, send, persistState, applyCommitted, cancellationToken).ConfigureAwait(false);

                break;

            default:
                throw new InvalidOperationException($"Unknown work item kind '{item.GetType().Name}'.");
        }
    }


    private async ValueTask HandleEnvelopeAsync(
        RaftEnvelope<TCommand> envelope,
        SendRaftEnvelopeDelegate<TCommand> send,
        PersistRaftStateDelegate<TCommand>? persistState,
        ApplyCommittedDelegate<TCommand>? applyCommitted,
        CancellationToken cancellationToken)
    {
        ReplicaId from = envelope.From;

        if(envelope.VoteRequest is { } voteRequest)
        {
            RequestVoteReply reply = node.HandleRequestVote(voteRequest);
            await PersistAsync(persistState, cancellationToken).ConfigureAwait(false);
            await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);
            await send(from, RaftEnvelope<TCommand>.ForVoteReply(node.Id, reply), cancellationToken).ConfigureAwait(false);

            return;
        }

        if(envelope.VoteReply is { } voteReply)
        {
            bool elected = node.ReceiveVote(from, voteReply);
            await PersistAsync(persistState, cancellationToken).ConfigureAwait(false);
            await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);
            if(elected)
            {
                await SendToEachPeerAsync(send, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if(envelope.AppendRequest is { } appendRequest)
        {
            AppendEntriesReply reply = node.HandleAppendEntries(appendRequest);
            await PersistAsync(persistState, cancellationToken).ConfigureAwait(false);
            await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);
            await send(from, RaftEnvelope<TCommand>.ForAppendReply(node.Id, reply), cancellationToken).ConfigureAwait(false);

            return;
        }

        if(envelope.AppendReply is { } appendReply)
        {
            node.ReceiveAppendEntriesReply(from, appendReply);
            await PersistAsync(persistState, cancellationToken).ConfigureAwait(false);
            await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);

            //Continuation rule: re-send to this follower only when the reply was a same-term failure (re-probe
            //after the nextIndex back-off) or the freshly built request carries entries (continue catch-up).
            //Both conditions shrink with progress, so the chatter quiesces: failures back nextIndex toward one
            //until a matching prefix is found, successes either exhaust the log or yield an empty request.
            if(node.Role == RaftRole.Leader)
            {
                AppendEntriesRequest<TCommand> request = node.CreateAppendEntries(from);
                bool sameTermFailure = !appendReply.Success && appendReply.Term == node.CurrentTerm;
                if(sameTermFailure || !request.Entries.IsEmpty)
                {
                    await send(from, RaftEnvelope<TCommand>.ForAppendRequest(node.Id, request), cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        //SubmitAsync validates the single-payload invariant before enqueue, so a malformed envelope never
        //reaches here; guard anyway so a future caller bypassing SubmitAsync fails closed.
        throw new ArgumentException("A Raft envelope must carry exactly one payload.", nameof(envelope));
    }


    private async ValueTask HandleProposeAsync(
        ProposeItem proposeItem,
        SendRaftEnvelopeDelegate<TCommand> send,
        PersistRaftStateDelegate<TCommand>? persistState,
        ApplyCommittedDelegate<TCommand>? applyCommitted,
        CancellationToken cancellationToken)
    {
        if(node.Role != RaftRole.Leader)
        {
            //Fault the proposal alone; the loop must keep running for a non-leader node.
            proposeItem.Source.TrySetException(new InvalidOperationException("Only the leader can propose commands."));

            return;
        }

        long index = node.Propose(proposeItem.Command);
        await PersistAsync(persistState, cancellationToken).ConfigureAwait(false);
        await ApplyAsync(applyCommitted, cancellationToken).ConfigureAwait(false);
        proposeItem.Source.TrySetResult(index);
        await SendToEachPeerAsync(send, cancellationToken).ConfigureAwait(false);
    }


    private ValueTask PersistAsync(PersistRaftStateDelegate<TCommand>? persistState, CancellationToken cancellationToken)
    {
        if(persistState is null)
        {
            return ValueTask.CompletedTask;
        }

        return persistState(node.ToState(), cancellationToken);
    }


    private async ValueTask ApplyAsync(ApplyCommittedDelegate<TCommand>? applyCommitted, CancellationToken cancellationToken)
    {
        long commitIndex = node.CommitIndex;
        while(lastApplied < commitIndex)
        {
            long next = lastApplied + 1;
            if(applyCommitted is not null)
            {
                //Protocol index next is the entry at zero-based position next - 1.
                await applyCommitted(next, node.Log[(int)(next - 1)].Command, cancellationToken).ConfigureAwait(false);
            }

            lastApplied = next;
        }
    }


    private async ValueTask BroadcastAsync(RaftEnvelope<TCommand> envelope, SendRaftEnvelopeDelegate<TCommand> send, CancellationToken cancellationToken)
    {
        foreach(ReplicaId peer in node.Members)
        {
            if(peer.Equals(node.Id))
            {
                continue;
            }

            await send(peer, envelope, cancellationToken).ConfigureAwait(false);
        }
    }


    private async ValueTask SendToEachPeerAsync(SendRaftEnvelopeDelegate<TCommand> send, CancellationToken cancellationToken)
    {
        foreach(ReplicaId peer in node.Members)
        {
            if(peer.Equals(node.Id))
            {
                continue;
            }

            AppendEntriesRequest<TCommand> request = node.CreateAppendEntries(peer);
            await send(peer, RaftEnvelope<TCommand>.ForAppendRequest(node.Id, request), cancellationToken).ConfigureAwait(false);
        }
    }


    private void AbandonPendingProposals(ProposeItem? inFlight, Exception? fault, CancellationToken cancellationToken)
    {
        //Completing the writer first makes a later ProposeAsync fail fast with ChannelClosedException, and
        //guarantees the drain below observes every write that succeeded before the completion.
        work.Writer.TryComplete();

        if(inFlight is not null)
        {
            AbandonProposal(inFlight, fault, cancellationToken);
        }

        while(work.Reader.TryRead(out WorkItem? item))
        {
            if(item is ProposeItem proposeItem)
            {
                AbandonProposal(proposeItem, fault, cancellationToken);
            }
        }
    }


    private static void AbandonProposal(ProposeItem proposeItem, Exception? fault, CancellationToken cancellationToken)
    {
        //TrySet* keeps this a no-op for a proposal whose dispatch already set its result or non-leader fault.
        //A cancellation carries the runner token so the cancelled proposal task attributes its cancellation
        //to that token; a loop failure faults it with the wrapping exception instead, where the token is unused.
        if(fault is null)
        {
            proposeItem.Source.TrySetCanceled(cancellationToken);
        }
        else
        {
            proposeItem.Source.TrySetException(fault);
        }
    }


    private abstract record WorkItem;


    private sealed record EnvelopeItem(RaftEnvelope<TCommand> Envelope): WorkItem;


    private sealed record ProposeItem(TCommand Command, TaskCompletionSource<long> Source): WorkItem;


    private sealed record ElectionItem: WorkItem
    {
        public static ElectionItem Instance { get; } = new();
    }


    private sealed record HeartbeatItem: WorkItem
    {
        public static HeartbeatItem Instance { get; } = new();
    }
}
