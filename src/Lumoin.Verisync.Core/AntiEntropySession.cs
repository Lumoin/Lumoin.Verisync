using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Drives one point-to-point reconciliation session over a message-driven loop, the host-side runner for a
/// single set-reconciliation exchange. All inbound work — a peer envelope through <see cref="SubmitAsync"/> or
/// a host batch trigger through <see cref="TriggerBatchAsync"/> — is enqueued onto an unbounded single-reader
/// channel; <see cref="RunAsync"/> is the sole consumer and the only code that touches the encoder, the
/// decoder, and the state machine.
/// </summary>
/// <typeparam name="TElement">The application element type the items identify and the elements messages carry.</typeparam>
/// <remarks>
/// <para>
/// A host runs a session when it finds itself out of step with a peer — a replica whose
/// <see cref="GossipDigest.IsBehind"/> or <see cref="GossipDigest.IsAheadOf"/> against a peer holds reconciles
/// against it. That selection of peer and roles is host-side gossip policy, above this runner; the runner adds
/// no timers and draws no entropy, so a responder streams a batch only when the host calls
/// <see cref="TriggerBatchAsync"/>.
/// </para>
/// <para>
/// A session pins ONE set version: the constructor copies the projected item snapshot and builds the encoder
/// (both roles) and the decoder (initiator) over it once, so a stream prefix covers a single set version. A
/// host may restrict the projected snapshot — for example to above-frontier state, to keep the difference
/// small — but the restriction boundary must be one below which the two replicas' restricted states are KNOWN
/// identical, for example a previously certified convergence point, not merely event-stable, because
/// observed-remove knowledge is not a dotted event and does not travel with clock stability.
/// </para>
/// <para>
/// A session is add-only by default — a difference of present elements converges — and becomes remove-aware
/// when constructed with a local causal context. A remove-aware session exchanges each side's context once,
/// right after the offer, and propagates observed removes as drops, so a tombstone never resurrects; an
/// add-only session sends and accepts no context and is byte-for-byte as before. Because every outbound send
/// happens inside the single consumer loop, the session's writes to the transport are serialized BY
/// CONSTRUCTION — two tasks cannot tear a frame on one shared writer.
/// </para>
/// </remarks>
public sealed class AntiEntropySession<TElement>: IDisposable
{
    private const int DefaultBatchSize = 4;


    private readonly Channel<WorkItem> work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly ReconciliationEncoder encoder;
    private readonly ReconciliationDecoder? decoder;

    //Advisory lifecycle phase, written only by the consumer loop and read by hosts. Stored as the int backing
    //of AntiEntropySessionState behind Volatile.Read/Write so a host's cross-thread read is never torn.
    private int stateValue;

    //Guards single entry into RunAsync. The encoder, decoder, and state machine assume exactly one consumer.
    private int started;

    private bool disposed;

    //The peer's causal context, captured once from the inbound context message before any symbol completes the
    //decode; null until captured, and never set in an add-only session. Written and read only by the consumer
    //loop, which serializes every handler, so no synchronization guards it.
    private VectorClock? peerContext;

    //Whether the peer's context has already been folded into the local one by an apply or drop, so the terminal
    //merge runs only on a path where no apply ran. Written and read only by the consumer loop.
    private bool contextFolded;


    /// <summary>
    /// Creates a session over <paramref name="items"/> with the default batch size of four, copying the items
    /// into a pinned snapshot and building the encoder (and, for an initiator, the decoder) over it.
    /// </summary>
    /// <param name="role">The fixed role this side plays for the session's lifetime.</param>
    /// <param name="contract">The contract both sides must agree on before symbols subtract.</param>
    /// <param name="items">The projected item snapshot this set version reconciles.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contract"/> or <paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="role"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException">Thrown when an item's length differs from the contract's item width or two items are byte-equal.</exception>
    public AntiEntropySession(AntiEntropyRole role, ReconciliationContract contract, IReadOnlyCollection<ReadOnlyMemory<byte>> items)
        : this(role, contract, items, DefaultBatchSize, pool: null, localContext: null)
    {
    }


    /// <summary>
    /// Creates a session over <paramref name="items"/> with the given batch size, copying the items into a
    /// pinned snapshot and building the encoder (and, for an initiator, the decoder) over it.
    /// </summary>
    /// <param name="role">The fixed role this side plays for the session's lifetime.</param>
    /// <param name="contract">The contract both sides must agree on before symbols subtract.</param>
    /// <param name="items">The projected item snapshot this set version reconciles.</param>
    /// <param name="batchSize">The number of symbols a responder produces per host trigger; at least one.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contract"/> or <paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="role"/> is not a defined value or <paramref name="batchSize"/> is below one.</exception>
    /// <exception cref="ArgumentException">Thrown when an item's length differs from the contract's item width or two items are byte-equal.</exception>
    public AntiEntropySession(AntiEntropyRole role, ReconciliationContract contract, IReadOnlyCollection<ReadOnlyMemory<byte>> items, int batchSize)
        : this(role, contract, items, batchSize, pool: null, localContext: null)
    {
    }


    /// <summary>
    /// Creates a session over <paramref name="items"/> with the default batch size of four, renting the
    /// encoder's and decoder's cell stores from <paramref name="pool"/> so the session's reconciliation memory
    /// is tracked and accountable.
    /// </summary>
    /// <param name="role">The fixed role this side plays for the session's lifetime.</param>
    /// <param name="contract">The contract both sides must agree on before symbols subtract.</param>
    /// <param name="items">The projected item snapshot this set version reconciles.</param>
    /// <param name="pool">The pool the session's encoder and decoder rent from, or <see langword="null"/> for the heap fallback.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contract"/> or <paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="role"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException">Thrown when an item's length differs from the contract's item width or two items are byte-equal.</exception>
    public AntiEntropySession(AntiEntropyRole role, ReconciliationContract contract, IReadOnlyCollection<ReadOnlyMemory<byte>> items, MemoryPool<byte>? pool)
        : this(role, contract, items, DefaultBatchSize, pool, localContext: null)
    {
    }


    /// <summary>
    /// Creates a session over <paramref name="items"/> with the given batch size, renting the encoder's and
    /// decoder's cell stores from <paramref name="pool"/>, copying the items into a pinned snapshot and
    /// building the encoder (and, for an initiator, the decoder) over it.
    /// </summary>
    /// <param name="role">The fixed role this side plays for the session's lifetime.</param>
    /// <param name="contract">The contract both sides must agree on before symbols subtract.</param>
    /// <param name="items">The projected item snapshot this set version reconciles.</param>
    /// <param name="batchSize">The number of symbols a responder produces per host trigger; at least one.</param>
    /// <param name="pool">The pool the session's encoder and decoder rent from, or <see langword="null"/> for the heap fallback.</param>
    /// <param name="localContext">
    /// The local causal context the session exchanges to become remove-aware, typically a dotted projection's
    /// context; <see langword="null"/> for an add-only session, which sends and accepts no context and behaves
    /// byte-for-byte as before. When non-null it is reconstructed with <see cref="VectorClock.FromState"/>, so a
    /// malformed context fails closed at construction exactly as the snapshot does.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="contract"/> or <paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="role"/> is not a defined value or <paramref name="batchSize"/> is below one.</exception>
    /// <exception cref="ArgumentException">Thrown when an item's length differs from the contract's item width, two items are byte-equal, or <paramref name="localContext"/> carries a negative counter.</exception>
    public AntiEntropySession(AntiEntropyRole role, ReconciliationContract contract, IReadOnlyCollection<ReadOnlyMemory<byte>> items, int batchSize, MemoryPool<byte>? pool, VectorClockState? localContext = null)
    {
        if(role is not (AntiEntropyRole.Initiator or AntiEntropyRole.Responder))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "The role must be a defined value.");
        }

        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(items);

        if(batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "A batch size must be at least one.");
        }

        Role = role;
        Contract = contract;
        BatchSize = batchSize;

        //A non-null local context makes the session remove-aware; reconstructing it now reuses the clock's own
        //validation so a malformed context fails closed at construction, as the snapshot does.
        LocalContext = localContext is null ? null : VectorClock.FromState(localContext);

        //Copy every item into the pinned snapshot, validating width and rejecting duplicates that would
        //XOR-cancel silently, then feed the encoder once. The injectivity obligation surfaces here as a throw.
        ImmutableArray<ReadOnlyMemory<byte>> snapshot = CopyAndValidate(contract, items);
        Snapshot = snapshot;

        //The snapshot size is the lower bound on the symbols and cells the session touches, so it pre-sizes
        //the cell stores past the doubling churn; the pool flows through so the rentals are tracked.
        encoder = new ReconciliationEncoder(contract, ReconciliationInjectivityEnforcement.None, pool, snapshot.Length);
        foreach(ReadOnlyMemory<byte> item in snapshot)
        {
            encoder.Add(item.Span);
        }

        decoder = role == AntiEntropyRole.Initiator ? new ReconciliationDecoder(contract, pool, snapshot.Length) : null;
    }


    /// <summary>The fixed role this side plays for the session's lifetime.</summary>
    public AntiEntropyRole Role { get; }

    /// <summary>The contract both sides must agree on before their coded streams subtract.</summary>
    public ReconciliationContract Contract { get; }

    /// <summary>The number of symbols a responder produces per host trigger.</summary>
    public int BatchSize { get; }

    /// <summary>
    /// The session's advisory lifecycle phase. It is written only by the consumer loop and read here with a
    /// volatile-safe read, so a host may poll it from another thread to pace its triggers without tearing; it
    /// is advisory because it may lag the loop's true position by one dispatched item.
    /// </summary>
    public AntiEntropySessionState State => (AntiEntropySessionState)Volatile.Read(ref stateValue);

    /// <summary>
    /// The items the initiator decoded as the symmetric difference, forwarding the decoder's list; an empty
    /// list for a responder, which never decodes.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> DecodedItems => decoder?.DecodedItems ?? [];


    private ImmutableArray<ReadOnlyMemory<byte>> Snapshot { get; }

    //The local causal context when remove-aware, or null when add-only. Non-null gates every remove-aware
    //behaviour: the context exchange, the context and drop dispatch arms, the local drops, and the terminal merge.
    private VectorClock? LocalContext { get; }


    /// <summary>
    /// Runs the single-consumer loop. It sends the offer, then dispatches every inbound work item against the
    /// encoder, decoder, and state machine until the channel completes after <see cref="Complete"/> or, for an
    /// initiator, until it reaches <see cref="AntiEntropySessionState.Completed"/>.
    /// </summary>
    /// <param name="send">The outbound transport edge; see <see cref="SendReconciliationEnvelopeDelegate{TElement}"/>.</param>
    /// <param name="resolveDifference">The initiator's classification seam; required for an initiator, unused by a responder.</param>
    /// <param name="serveFetch">The responder's lookup seam; required for a responder, unused by an initiator.</param>
    /// <param name="applyElements">The seam that admits received elements to the local replica; required for a remove-aware responder, optional otherwise.</param>
    /// <param name="applyDrops">The seam that drops named dots from the local replica; required for both roles when remove-aware, unused when add-only.</param>
    /// <param name="mergeContext">The terminal context-fold seam for paths where no apply ran; required for both roles when remove-aware, unused when add-only.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the loop returns.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="send"/> is <see langword="null"/>, an initiator's <paramref name="resolveDifference"/> is <see langword="null"/>, a responder's <paramref name="serveFetch"/> is <see langword="null"/>, or a remove-aware session is missing its role's <paramref name="applyElements"/>, <paramref name="applyDrops"/>, or <paramref name="mergeContext"/> hook.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="RunAsync"/> has already been called or a dispatch rule is violated.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signalled.</exception>
    /// <remarks>
    /// A throwing <paramref name="send"/>, <paramref name="resolveDifference"/>, <paramref name="serveFetch"/>,
    /// <paramref name="applyElements"/>, <paramref name="applyDrops"/>, or <paramref name="mergeContext"/>
    /// propagates out of this method and ends the loop — the fail-closed posture. Every dispatch-rule violation
    /// throws <see cref="InvalidOperationException"/> naming the rule.
    /// </remarks>
    public async Task RunAsync(
        SendReconciliationEnvelopeDelegate<TElement> send,
        ResolveReconciliationDifferenceDelegate<TElement>? resolveDifference = null,
        ServeReconciliationFetchDelegate<TElement>? serveFetch = null,
        ApplyReconciliationElementsDelegate<TElement>? applyElements = null,
        ApplyReconciliationDropsDelegate<TElement>? applyDrops = null,
        MergeReconciliationContextDelegate? mergeContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);

        if(Role == AntiEntropyRole.Initiator && resolveDifference is null)
        {
            throw new ArgumentNullException(nameof(resolveDifference), "An initiator requires a difference resolver.");
        }

        if(Role == AntiEntropyRole.Responder && serveFetch is null)
        {
            throw new ArgumentNullException(nameof(serveFetch), "A responder requires a fetch server.");
        }

        //Remove-aware sessions need the drop and terminal-merge hooks on both roles, the apply hook on the
        //responder that admits the initiator's push, and (already checked) the resolver on the initiator; an
        //add-only session needs none of these and is unchanged.
        if(LocalContext is not null)
        {
            if(applyDrops is null)
            {
                throw new ArgumentNullException(nameof(applyDrops), "A remove-aware session requires a drop applier.");
            }

            if(mergeContext is null)
            {
                throw new ArgumentNullException(nameof(mergeContext), "A remove-aware session requires a terminal context merger.");
            }

            if(Role == AntiEntropyRole.Responder && applyElements is null)
            {
                throw new ArgumentNullException(nameof(applyElements), "A remove-aware responder requires an elements applier.");
            }
        }

        if(Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("RunAsync may only be called once per session.");
        }

        //Send the offer that pins the contract, then move to Pinning before consuming any peer work.
        await send(ReconciliationEnvelope<TElement>.ForOffer(ReconciliationOffer.FromContract(Contract)), cancellationToken).ConfigureAwait(false);
        SetState(AntiEntropySessionState.Pinning);

        //A remove-aware session ships its causal context once, right after the offer and before any symbol, so
        //the ordered point-to-point channel delivers the peer its context ahead of the decode that needs it.
        if(LocalContext is not null)
        {
            await send(ReconciliationEnvelope<TElement>.ForContext(new ReconciliationContext(LocalContext.ToState())), cancellationToken).ConfigureAwait(false);
        }

        await foreach(WorkItem item in work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await DispatchAsync(item, send, resolveDifference, serveFetch, applyElements, applyDrops, mergeContext, cancellationToken).ConfigureAwait(false);

            //An initiator returns promptly once it has completed, draining nothing further.
            if(Role == AntiEntropyRole.Initiator && State == AntiEntropySessionState.Completed)
            {
                return;
            }
        }

        //The channel completed through Complete(); fold the peer's context if no apply did so on this side, then
        //the session is terminal whatever phase it last held. The merge hook is non-null whenever the session is
        //remove-aware, by the eager validation, so the explicit check only satisfies flow analysis.
        if(LocalContext is not null && !contextFolded && mergeContext is not null)
        {
            await MergePeerContextAsync(mergeContext, cancellationToken).ConfigureAwait(false);
        }

        SetState(AntiEntropySessionState.Completed);
    }


    /// <summary>
    /// Submits an inbound envelope received from the peer for the session to handle.
    /// </summary>
    /// <param name="envelope">The envelope to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the envelope is enqueued.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="envelope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="envelope"/> does not carry exactly one payload.</exception>
    public ValueTask SubmitAsync(ReconciliationEnvelope<TElement> envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.EnsureSinglePayload(nameof(envelope));

        return work.Writer.WriteAsync(new EnvelopeItem(envelope), cancellationToken);
    }


    /// <summary>
    /// Triggers a symbol batch: the host's pacing signal that the responder should stream the next run of
    /// coded symbols. The library adds no timers, so this heartbeat is the host's policy.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the trigger is enqueued.</returns>
    /// <exception cref="InvalidOperationException">Thrown synchronously on an initiator, which never streams batches.</exception>
    public ValueTask TriggerBatchAsync(CancellationToken cancellationToken = default)
    {
        if(Role == AntiEntropyRole.Initiator)
        {
            throw new InvalidOperationException("Only a responder streams symbol batches; an initiator cannot be triggered.");
        }

        return work.Writer.WriteAsync(TriggerItem.Instance, cancellationToken);
    }


    /// <summary>
    /// Completes the work channel: no further work is accepted, and <see cref="RunAsync"/> drains the queued
    /// items, sets <see cref="State"/> to <see cref="AntiEntropySessionState.Completed"/>, and returns. The
    /// host calls this to wind the transport down, because a responder cannot know a fetch is not still coming.
    /// </summary>
    public void Complete()
    {
        work.Writer.TryComplete();
    }


    /// <summary>
    /// Disposes the encoder and decoder this session owns, releasing their pooled cell stores; the call is
    /// idempotent. The host disposes the session after <see cref="RunAsync"/> returns — disposing mid-run is
    /// the caller's error, guarded only by the existing single-run <see cref="Interlocked"/> gate. The injected
    /// pool is never disposed here: the session owns its rentals, not the pool, so the host may share one pool
    /// across many sessions and dispose it once they have all completed.
    /// </summary>
    public void Dispose()
    {
        if(disposed)
        {
            return;
        }

        disposed = true;
        encoder.Dispose();
        decoder?.Dispose();
    }


    private static ImmutableArray<ReadOnlyMemory<byte>> CopyAndValidate(ReconciliationContract contract, IReadOnlyCollection<ReadOnlyMemory<byte>> items)
    {
        ImmutableArray<ReadOnlyMemory<byte>>.Builder copies = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>(items.Count);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            if(item.Length != contract.ItemWidth)
            {
                throw new ArgumentException($"An item must be exactly {contract.ItemWidth} bytes.", nameof(items));
            }

            for(int j = 0; j < copies.Count; j++)
            {
                if(copies[j].Span.SequenceEqual(item.Span))
                {
                    throw new ArgumentException("A pinned snapshot cannot carry duplicate items.", nameof(items));
                }
            }

            copies.Add(item.ToArray());
        }

        return copies.ToImmutable();
    }


    private async ValueTask DispatchAsync(
        WorkItem item,
        SendReconciliationEnvelopeDelegate<TElement> send,
        ResolveReconciliationDifferenceDelegate<TElement>? resolveDifference,
        ServeReconciliationFetchDelegate<TElement>? serveFetch,
        ApplyReconciliationElementsDelegate<TElement>? applyElements,
        ApplyReconciliationDropsDelegate<TElement>? applyDrops,
        MergeReconciliationContextDelegate? mergeContext,
        CancellationToken cancellationToken)
    {
        switch(item)
        {
            case EnvelopeItem envelopeItem:
                await HandleEnvelopeAsync(envelopeItem.Envelope, send, resolveDifference, serveFetch, applyElements, applyDrops, mergeContext, cancellationToken).ConfigureAwait(false);

                break;

            case TriggerItem:
                await HandleTriggerAsync(send, cancellationToken).ConfigureAwait(false);

                break;

            default:
                throw new InvalidOperationException($"Unknown work item kind '{item.GetType().Name}'.");
        }
    }


    private async ValueTask HandleEnvelopeAsync(
        ReconciliationEnvelope<TElement> envelope,
        SendReconciliationEnvelopeDelegate<TElement> send,
        ResolveReconciliationDifferenceDelegate<TElement>? resolveDifference,
        ServeReconciliationFetchDelegate<TElement>? serveFetch,
        ApplyReconciliationElementsDelegate<TElement>? applyElements,
        ApplyReconciliationDropsDelegate<TElement>? applyDrops,
        MergeReconciliationContextDelegate? mergeContext,
        CancellationToken cancellationToken)
    {
        if(envelope.Offer is { } offer)
        {
            HandleOffer(offer);

            return;
        }

        if(envelope.Context is { } context)
        {
            HandleContext(context);

            return;
        }

        if(envelope.Symbols is { } symbols)
        {
            await HandleSymbolsAsync(symbols, send, resolveDifference, applyDrops, mergeContext, cancellationToken).ConfigureAwait(false);

            return;
        }

        if(envelope.Done is not null)
        {
            HandleDone();

            return;
        }

        if(envelope.Fetch is { } fetch)
        {
            await HandleFetchAsync(fetch, send, serveFetch, cancellationToken).ConfigureAwait(false);

            return;
        }

        if(envelope.Elements is { } elements)
        {
            await HandleElementsAsync(elements, send, applyElements, mergeContext, cancellationToken).ConfigureAwait(false);

            return;
        }

        if(envelope.Drop is { } drop)
        {
            await HandleDropAsync(drop, applyDrops, cancellationToken).ConfigureAwait(false);

            return;
        }

        //SubmitAsync validates the single-payload invariant before enqueue, so a malformed envelope never
        //reaches here; guard anyway so a future caller bypassing SubmitAsync fails closed.
        throw new InvalidOperationException("A reconciliation envelope must carry exactly one payload.");
    }


    private void HandleOffer(ReconciliationOffer offer)
    {
        if(State != AntiEntropySessionState.Pinning)
        {
            throw new InvalidOperationException("An offer is legal only while pinning the contract.");
        }

        if(!offer.Matches(Contract))
        {
            throw new InvalidOperationException("The peer's offer does not match the local contract.");
        }

        SetState(AntiEntropySessionState.Reconciling);
    }


    private void HandleContext(ReconciliationContext context)
    {
        if(LocalContext is null)
        {
            throw new InvalidOperationException("An add-only session must not receive a causal context.");
        }

        AntiEntropySessionState state = State;
        if(state is not (AntiEntropySessionState.Pinning or AntiEntropySessionState.Reconciling))
        {
            throw new InvalidOperationException("A causal context is legal only while pinning or reconciling.");
        }

        if(peerContext is not null)
        {
            throw new InvalidOperationException("A causal context is exchanged once; a second context is not legal.");
        }

        //Reconstructing the clock reuses its validation, so a malformed peer context fails closed here, before
        //the decode that classifies against it; the ordered channel guarantees this lands before any symbol.
        peerContext = VectorClock.FromState(context.Clock);
    }


    private async ValueTask HandleSymbolsAsync(
        ReconciliationSymbolBatch batch,
        SendReconciliationEnvelopeDelegate<TElement> send,
        ResolveReconciliationDifferenceDelegate<TElement>? resolveDifference,
        ApplyReconciliationDropsDelegate<TElement>? applyDrops,
        MergeReconciliationContextDelegate? mergeContext,
        CancellationToken cancellationToken)
    {
        if(Role != AntiEntropyRole.Initiator)
        {
            throw new InvalidOperationException("Only an initiator absorbs symbol batches.");
        }

        AntiEntropySessionState state = State;
        if(state == AntiEntropySessionState.Resolving)
        {
            //Stragglers that raced the done signal are ignored, not absorbed, so AbsorbedCount stays put.
            return;
        }

        if(state != AntiEntropySessionState.Reconciling)
        {
            throw new InvalidOperationException("A symbol batch is legal only while reconciling.");
        }

        ReconciliationDecoder localDecoder = decoder!;
        if(batch.StartIndex != localDecoder.AbsorbedCount)
        {
            throw new InvalidOperationException("A symbol batch must start at the decoder's absorbed count for gap-free, in-order streaming.");
        }

        foreach(ReconciliationSymbol remoteSymbol in batch.Symbols)
        {
            localDecoder.Absorb(encoder.ProduceNext().Combine(remoteSymbol));
            if(localDecoder.IsComplete)
            {
                await CompleteDecodeAsync(localDecoder, send, resolveDifference!, applyDrops, mergeContext, cancellationToken).ConfigureAwait(false);

                //Remaining symbols of this batch after completion are not absorbed.
                return;
            }
        }
    }


    private async ValueTask CompleteDecodeAsync(
        ReconciliationDecoder localDecoder,
        SendReconciliationEnvelopeDelegate<TElement> send,
        ResolveReconciliationDifferenceDelegate<TElement> resolveDifference,
        ApplyReconciliationDropsDelegate<TElement>? applyDrops,
        MergeReconciliationContextDelegate? mergeContext,
        CancellationToken cancellationToken)
    {
        //The ordered channel delivers the peer's context before any symbol, so an honest peer never reaches a
        //remove-aware decode with a null peer context; guard anyway so a missing context fails closed.
        if(LocalContext is not null && peerContext is null)
        {
            throw new InvalidOperationException("A remove-aware decode completed before the peer's causal context arrived.");
        }

        await send(ReconciliationEnvelope<TElement>.ForDone(new ReconciliationDone(localDecoder.AbsorbedCount)), cancellationToken).ConfigureAwait(false);

        //Classify the recovered difference against the peer's context (the empty clock's state when add-only),
        //so a held item the peer observed and removed becomes a local drop rather than a push.
        VectorClockState peerContextState = PeerContextState();
        ReconciliationDifferenceResolution<TElement> resolution = resolveDifference(localDecoder.DecodedItems, peerContextState);
        if(resolution is null)
        {
            throw new InvalidOperationException("A difference resolver must not return null.");
        }

        //Apply the local drops first: the entries the peer observed and removed leave this side immediately, and
        //the fold the applier performs lets the merged context dominate them so they never resurrect.
        if(!resolution.LocalDrops.IsEmpty)
        {
            await applyDrops!(resolution.LocalDrops, peerContextState, cancellationToken).ConfigureAwait(false);
            contextFolded = true;
        }

        if(!resolution.Fetch.IsEmpty)
        {
            await send(ReconciliationEnvelope<TElement>.ForFetch(new ReconciliationFetch(resolution.Fetch)), cancellationToken).ConfigureAwait(false);
        }

        if(!resolution.Push.IsEmpty)
        {
            await send(ReconciliationEnvelope<TElement>.ForElements(new ReconciliationElements<TElement>(resolution.Push)), cancellationToken).ConfigureAwait(false);
        }

        //Resolving when a fetch went out and an answer is outstanding; otherwise the session is complete, and on
        //completion a remove-aware side that no apply folded for runs the terminal merge once before finishing.
        if(resolution.Fetch.IsEmpty)
        {
            if(LocalContext is not null && !contextFolded)
            {
                await MergePeerContextAsync(mergeContext!, cancellationToken).ConfigureAwait(false);
            }

            SetState(AntiEntropySessionState.Completed);

            return;
        }

        SetState(AntiEntropySessionState.Resolving);
    }


    private void HandleDone()
    {
        if(Role != AntiEntropyRole.Responder)
        {
            throw new InvalidOperationException("Only a responder receives the done signal.");
        }

        if(State != AntiEntropySessionState.Reconciling)
        {
            throw new InvalidOperationException("A done signal is legal only while reconciling.");
        }

        SetState(AntiEntropySessionState.Resolving);
    }


    private async ValueTask HandleFetchAsync(
        ReconciliationFetch fetch,
        SendReconciliationEnvelopeDelegate<TElement> send,
        ServeReconciliationFetchDelegate<TElement>? serveFetch,
        CancellationToken cancellationToken)
    {
        if(Role != AntiEntropyRole.Responder)
        {
            throw new InvalidOperationException("Only a responder serves a fetch.");
        }

        if(State != AntiEntropySessionState.Resolving)
        {
            throw new InvalidOperationException("A fetch is legal only while resolving.");
        }

        IReadOnlyList<ReadOnlyMemory<byte>> items = fetch.Items;
        IReadOnlyList<ReconciliationElementEntry<TElement>> served = serveFetch!(items);
        if(served is null)
        {
            throw new InvalidOperationException("A fetch server must not return null.");
        }

        EnsureCoversExactly(items, served);

        await send(ReconciliationEnvelope<TElement>.ForElements(new ReconciliationElements<TElement>([.. served])), cancellationToken).ConfigureAwait(false);
    }


    private async ValueTask HandleElementsAsync(
        ReconciliationElements<TElement> elements,
        SendReconciliationEnvelopeDelegate<TElement> send,
        ApplyReconciliationElementsDelegate<TElement>? applyElements,
        MergeReconciliationContextDelegate? mergeContext,
        CancellationToken cancellationToken)
    {
        if(State != AntiEntropySessionState.Resolving)
        {
            throw new InvalidOperationException("An elements message is legal only while resolving.");
        }

        if(applyElements is null)
        {
            throw new InvalidOperationException("An elements message arrived without an apply hook.");
        }

        //The uniform apply admits the genuine adds and returns the local tombstones the pre-fold context already
        //covered; the applier folds the peer context, so this side's context is merged whenever entries arrive.
        ImmutableArray<DotState> drops = await applyElements(elements.Entries, PeerContextState(), cancellationToken).ConfigureAwait(false);
        if(LocalContext is not null)
        {
            contextFolded = true;
        }

        //The initiator applying its fetch answer may surface local tombstones the peer must honour; it sends one
        //drop. The responder applying the initiator's pre-filtered push surfaces none, so it sends nothing.
        if(!drops.IsEmpty)
        {
            await send(ReconciliationEnvelope<TElement>.ForDrop(new ReconciliationDrop(drops)), cancellationToken).ConfigureAwait(false);
        }

        //The initiator's outstanding fetch is answered, so it completes; the responder stays resolving until the
        //host completes the channel. The apply already folded, so the terminal merge does not run again here.
        if(Role == AntiEntropyRole.Initiator)
        {
            if(LocalContext is not null && !contextFolded)
            {
                await MergePeerContextAsync(mergeContext!, cancellationToken).ConfigureAwait(false);
            }

            SetState(AntiEntropySessionState.Completed);
        }
    }


    private async ValueTask HandleDropAsync(
        ReconciliationDrop drop,
        ApplyReconciliationDropsDelegate<TElement>? applyDrops,
        CancellationToken cancellationToken)
    {
        if(LocalContext is null)
        {
            throw new InvalidOperationException("An add-only session must not receive a drop.");
        }

        if(State != AntiEntropySessionState.Resolving)
        {
            throw new InvalidOperationException("A drop is legal only while resolving.");
        }

        if(applyDrops is null)
        {
            throw new InvalidOperationException("A drop arrived without a drop hook.");
        }

        //The initiator's tombstones leave this side and the applier folds the peer context so the merged context
        //dominates them; the responder stays resolving until the host completes the channel.
        await applyDrops(drop.Dots, PeerContextState(), cancellationToken).ConfigureAwait(false);
        contextFolded = true;
    }


    private async ValueTask HandleTriggerAsync(SendReconciliationEnvelopeDelegate<TElement> send, CancellationToken cancellationToken)
    {
        //A trigger only streams while reconciling; in any other phase the done signal has already raced past
        //the host's pacing loop, which is normal, so the trigger is a no-op.
        if(State != AntiEntropySessionState.Reconciling)
        {
            return;
        }

        int startIndex = encoder.ProducedCount;
        ImmutableArray<ReconciliationSymbol>.Builder symbols = ImmutableArray.CreateBuilder<ReconciliationSymbol>(BatchSize);
        for(int i = 0; i < BatchSize; i++)
        {
            symbols.Add(encoder.ProduceNext());
        }

        await send(ReconciliationEnvelope<TElement>.ForSymbols(new ReconciliationSymbolBatch(startIndex, symbols.MoveToImmutable())), cancellationToken).ConfigureAwait(false);
    }


    private static void EnsureCoversExactly(IReadOnlyList<ReadOnlyMemory<byte>> requested, IReadOnlyList<ReconciliationElementEntry<TElement>> served)
    {
        if(served.Count != requested.Count)
        {
            throw new InvalidOperationException("A fetch answer must cover exactly the requested items.");
        }

        foreach(ReadOnlyMemory<byte> item in requested)
        {
            bool found = false;
            foreach(ReconciliationElementEntry<TElement> entry in served)
            {
                if(entry.Item.Span.SequenceEqual(item.Span))
                {
                    found = true;

                    break;
                }
            }

            if(!found)
            {
                throw new InvalidOperationException("A fetch answer must cover exactly the requested items.");
            }
        }
    }


    private async ValueTask MergePeerContextAsync(MergeReconciliationContextDelegate mergeContext, CancellationToken cancellationToken)
    {
        //The terminal fold for a path where no apply ran; idempotent, so it is harmless even if it overlaps a
        //context the local side already dominates, and it leaves both sides at the merged context.
        await mergeContext(PeerContextState(), cancellationToken).ConfigureAwait(false);
        contextFolded = true;
    }


    private VectorClockState PeerContextState()
    {
        //The peer's context as state when remove-aware (captured before the decode), or the empty clock's state
        //when add-only, where the hooks ignore it and fold nothing.
        return peerContext is null ? VectorClock.Empty.ToState() : peerContext.ToState();
    }


    private void SetState(AntiEntropySessionState state)
    {
        Volatile.Write(ref stateValue, (int)state);
    }


    private abstract record WorkItem;


    private sealed record EnvelopeItem(ReconciliationEnvelope<TElement> Envelope): WorkItem;


    private sealed record TriggerItem: WorkItem
    {
        public static TriggerItem Instance { get; } = new();
    }
}
