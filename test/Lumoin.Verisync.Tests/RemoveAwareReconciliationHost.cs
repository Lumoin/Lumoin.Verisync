using Lumoin.Base;
using Lumoin.Verisync.Core;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Lumoin.Verisync.Tests;

/// <summary>
/// One replica's host for a remove-aware (dot-cloud) anti-entropy session: a mutable
/// <see cref="DottedVersionVectorSet{T}"/> projected once into the reconciliation items, with the classifier
/// and the apply, drop, and merge hooks the session drives. It reproduces
/// <see cref="DottedVersionVectorSet{T}.Merge(DottedVersionVectorSet{T})"/> by manipulating the set's state
/// directly and rebuilding with <see cref="DottedVersionVectorSet{T}.FromState"/>, folding the peer context
/// together with every insert so the merged context dominates every retained dot. It is shared by the
/// in-process law tests (<see cref="RemoveAwareReconciliationLawTests"/>) and the serialized-socket
/// integration test (<see cref="RemoveAwareReconciliationSocketTests"/>): the host logic is identical, only
/// the transport differs.
/// </summary>
internal sealed class RemoveAwareReconciliationHost
{
    private static ReconciliationContract Contract { get; } = ReconciliationContract.ContentHashDefault;

    private DottedReconciliationProjection<string> Projection { get; }

    //The replica's causal context at session start, the baseline the apply rule classifies a received dot
    //against: a fetched dot the pinned context already covers is a local tombstone, not a fresh add. The
    //running Current.Context cannot serve this — the initiator folds the peer context in via its local drops
    //BEFORE the fetch answer arrives, so a later read of Current.Context would mis-classify a genuine add.
    private VectorClock PinnedContext { get; }


    public RemoveAwareReconciliationHost(DottedVersionVectorSet<string> start)
    {
        Current = start;
        PinnedContext = start.Context;
        Projection = new DottedReconciliationProjection<string>(start.ToState(), Contract, Digest, Canonicalize, BaseMemoryPool.Shared);
    }


    //The current converged state, read after the session completes.
    public DottedVersionVectorSet<string> Current { get; private set; }

    //The pinned items the session encodes, one per present dotted entry.
    public ReadOnlyMemory<byte>[] Items => [.. Projection.Items];

    //The local causal context the session ships once, the projected state's context.
    public VectorClockState LocalContext => Projection.Context;


    //The initiator classifier mirroring DottedVersionVectorSet.Merge. A decoded item held here is a
    //present-here, absent-there dot: covered by the peer context means the peer observed and removed it, so it
    //is a local drop; otherwise it is pushed for the peer to add. A decoded item not held here is fetched; the
    //fetch answer carries the dot.
    public ReconciliationDifferenceResolution<DottedEntry<string>> ResolveDifference(IReadOnlyList<ReadOnlyMemory<byte>> decodedItems, VectorClockState peerContext)
    {
        VectorClock peer = VectorClock.FromState(peerContext);

        ImmutableArray<ReadOnlyMemory<byte>>.Builder fetch = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        ImmutableArray<ReconciliationElementEntry<DottedEntry<string>>>.Builder push = ImmutableArray.CreateBuilder<ReconciliationElementEntry<DottedEntry<string>>>();
        ImmutableArray<DotState>.Builder localDrops = ImmutableArray.CreateBuilder<DotState>();

        foreach(ReadOnlyMemory<byte> item in decodedItems)
        {
            if(Projection.TryResolve(item, out DottedEntry<string>? entry))
            {
                if(ContextCovers(peer, entry))
                {
                    localDrops.Add(new DotState(entry.Replica, entry.Counter));
                }
                else
                {
                    push.Add(new ReconciliationElementEntry<DottedEntry<string>>(item, entry));
                }
            }
            else
            {
                fetch.Add(item);
            }
        }

        return new ReconciliationDifferenceResolution<DottedEntry<string>>(fetch.ToImmutable(), push.ToImmutable(), localDrops.ToImmutable());
    }


    //The responder serves a fetch with elements only: it holds every requested item, so each resolves to its
    //dotted entry and no drop arises.
    [SuppressMessage("Performance", "CA1859", Justification = "ServeFetch is a ServeReconciliationFetchDelegate target, so its return type must be the delegate's IReadOnlyList rather than the concrete List.")]
    public IReadOnlyList<ReconciliationElementEntry<DottedEntry<string>>> ServeFetch(IReadOnlyList<ReadOnlyMemory<byte>> items)
    {
        var entries = new List<ReconciliationElementEntry<DottedEntry<string>>>(items.Count);
        foreach(ReadOnlyMemory<byte> item in items)
        {
            if(!Projection.TryResolve(item, out DottedEntry<string>? entry))
            {
                throw new InvalidOperationException("The responder was asked to serve an item it does not hold.");
            }

            entries.Add(new ReconciliationElementEntry<DottedEntry<string>>(item, entry));
        }

        return entries;
    }


    //The uniform apply rule. Each received entry the local pre-fold context already covers is a local
    //tombstone returned as a push-drop and not added; the rest are added under their exact dots. The peer
    //context is folded together with the inserts so the merged context dominates every retained dot.
    public ValueTask<ImmutableArray<DotState>> ApplyElements(IReadOnlyList<ReconciliationElementEntry<DottedEntry<string>>> entries, VectorClockState peerContext, CancellationToken cancellationToken)
    {
        VectorClock peer = VectorClock.FromState(peerContext);

        ImmutableArray<DotState>.Builder pushDrops = ImmutableArray.CreateBuilder<DotState>();
        var additions = new List<DottedEntry<string>>(entries.Count);
        foreach(ReconciliationElementEntry<DottedEntry<string>> entry in entries)
        {
            DottedEntry<string> dotted = entry.Element;
            if(ContextCovers(PinnedContext, dotted))
            {
                pushDrops.Add(new DotState(dotted.Replica, dotted.Counter));
            }
            else
            {
                additions.Add(dotted);
            }
        }

        Current = RebuildWithAdditions(additions, peer);

        return new ValueTask<ImmutableArray<DotState>>(pushDrops.ToImmutable());
    }


    //Drops each present entry a dot names, keeps the rest, and folds the peer context so the merged context
    //dominates the dropped dot — the removed entry never resurrects.
    public ValueTask ApplyDrops(IReadOnlyList<DotState> dots, VectorClockState peerContext, CancellationToken cancellationToken)
    {
        VectorClock peer = VectorClock.FromState(peerContext);
        var toDrop = new HashSet<(string Replica, int Counter)>();
        foreach(DotState dot in dots)
        {
            toDrop.Add((Convert.ToHexString(dot.Replica.AsSpan()), dot.Counter));
        }

        DottedVersionVectorSetState<string> state = Current.ToState();
        ImmutableArray<DottedEntry<string>>.Builder retained = ImmutableArray.CreateBuilder<DottedEntry<string>>();
        foreach(DottedEntry<string> entry in state.Entries)
        {
            if(!toDrop.Contains((Convert.ToHexString(entry.Replica.AsSpan()), entry.Counter)))
            {
                retained.Add(entry);
            }
        }

        VectorClock mergedContext = Current.Context.Merge(peer);
        Current = DottedVersionVectorSet<string>.FromState(new DottedVersionVectorSetState<string>(mergedContext.ToState(), retained.ToImmutable()));

        return ValueTask.CompletedTask;
    }


    //The terminal fold for the paths where no apply ran: the context advances to the merged context while the
    //entries stay as they are.
    public ValueTask MergeContext(VectorClockState peerContext, CancellationToken cancellationToken)
    {
        VectorClock peer = VectorClock.FromState(peerContext);
        VectorClock mergedContext = Current.Context.Merge(peer);
        DottedVersionVectorSetState<string> state = Current.ToState();
        Current = DottedVersionVectorSet<string>.FromState(new DottedVersionVectorSetState<string>(mergedContext.ToState(), state.Entries));

        return ValueTask.CompletedTask;
    }


    //Rebuilds the set with the additions folded in under their exact dots and the context advanced to the
    //merged context. The added dots come from the peer whose context dominates them and the retained dots are
    //dominated by the local context, so the merged context dominates every retained dot — the reconstruction
    //invariant FromState enforces.
    private DottedVersionVectorSet<string> RebuildWithAdditions(List<DottedEntry<string>> additions, VectorClock peer)
    {
        DottedVersionVectorSetState<string> state = Current.ToState();
        ImmutableArray<DottedEntry<string>>.Builder entries = ImmutableArray.CreateBuilder<DottedEntry<string>>(state.Entries.Length + additions.Count);
        entries.AddRange(state.Entries);
        entries.AddRange(additions);

        VectorClock mergedContext = Current.Context.Merge(peer);

        return DottedVersionVectorSet<string>.FromState(new DottedVersionVectorSetState<string>(mergedContext.ToState(), entries.ToImmutable()));
    }


    //contextCovers(ctx, dot) = ctx[dot.Replica] >= dot.Counter, the literal Merge rule reading the context
    //entry for the dot's replica against the dot's counter.
    private static bool ContextCovers(VectorClock context, DottedEntry<string> entry)
    {
        return context[ReplicaId.FromSpan(entry.Replica.AsSpan())] >= entry.Counter;
    }


    //The canonical value bytes are the UTF-8 encoding of the string, as the slice-1 projection tests use.
    private static ReadOnlyMemory<byte> Canonicalize(string value) => Encoding.UTF8.GetBytes(value);


    //The digest is SHA-256 over the frame, the 32-byte content-hash the default contract expects.
    private static ReadOnlyMemory<byte> Digest(ReadOnlyMemory<byte> canonicalBytes) => SHA256.HashData(canonicalBytes.Span);
}
