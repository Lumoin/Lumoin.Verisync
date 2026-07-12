using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A replicated growable array: a sequence CRDT for collaborative ordered lists and text. Elements are
/// inserted relative to an existing position and removed by tombstoning, so concurrent edits converge
/// to the same sequence on every replica.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <remarks>
/// <para>
/// Every inserted element carries a unique <see cref="Dot"/> identity and records the identity of the
/// element it was inserted after (a head insert records none). The visible order is derived by a
/// deterministic traversal: after a given predecessor, concurrently inserted siblings appear in
/// descending (counter, replica) order, so the element with the higher identity sits closer to the
/// predecessor. Identities are assigned Lamport-style — a new element's counter is one more than the
/// largest counter the inserting replica has observed — so a fresh insert dominates every sibling it
/// knows of and lands immediately after its predecessor, the intention-preservation rule of RGA.
/// Because vertices and tombstones are grow-only, <see cref="Merge(Rga{TValue})"/> is a
/// union and the derived order is identical on all replicas; concurrent tombstones of the same element
/// union their remove-dots per target.
/// </para>
/// <para>
/// It is an immutable value; every operation returns a new array. <see cref="InsertAfter(Dot, TValue, ReplicaId)"/>
/// returns the new array together with the identity assigned to the inserted element, which the caller
/// uses as the predecessor of a following insert or as the target of a <see cref="Remove(Dot, ReplicaId)"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class Rga<TValue>: IEquatable<Rga<TValue>>
{
    private VectorClock Context { get; }
    private FrozenDictionary<Dot, Vertex> Vertices { get; }

    //A tombstone maps a removed element's insert-dot to the dotted remove events that hide it. A non-empty
    //set carries genuine remove-dots the frontier can certify; an empty set is a legacy (v1-loaded)
    //tombstone whose remove predates dotting, so it can never be certified and is retained forever.
    private FrozenDictionary<Dot, FrozenSet<Dot>> Tombstones { get; }

    //Dropped dots map to their nearest retained ancestor at drop time, so anchors expressed against a dot
    //that compaction removed can still be served. The rga.v2 strategy never populates this — only the
    //compactable rga-rle.v2 strategy does — so nothing changes for the flat strategy.
    private FrozenDictionary<Dot, Dot> CompactedPredecessors { get; }


    private Rga(VectorClock context, FrozenDictionary<Dot, Vertex> vertices, FrozenDictionary<Dot, FrozenSet<Dot>> tombstones, FrozenDictionary<Dot, Dot> compactedPredecessors)
    {
        Context = context;
        Vertices = vertices;
        Tombstones = tombstones;
        CompactedPredecessors = compactedPredecessors;
    }


    /// <summary>An empty array.</summary>
    public static Rga<TValue> Empty { get; } = new(VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenDictionary<Dot, FrozenSet<Dot>>.Empty, FrozenDictionary<Dot, Dot>.Empty);


    /// <summary>The visible (non-tombstoned) values in sequence order.</summary>
    public IReadOnlyList<TValue> Values
    {
        get
        {
            List<Dot> order = ComputeOrder();
            var result = new List<TValue>(order.Count);
            foreach(Dot id in order)
            {
                if(!Tombstones.ContainsKey(id))
                {
                    result.Add(Vertices[id].Value);
                }
            }

            return result;
        }
    }


    /// <summary>The number of visible (non-tombstoned) elements.</summary>
    public int Count
    {
        get
        {
            int count = 0;
            foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
            {
                if(!Tombstones.ContainsKey(entry.Key))
                {
                    count++;
                }
            }

            return count;
        }
    }


    /// <summary>
    /// The causal context of this array, for gossip digests and stability frontiers. Advertise it with
    /// <c>new GossipDigest(origin, rga.CausalContext)</c>; the frontier folded from a group's digests then
    /// certifies removes group-wide.
    /// </summary>
    /// <remarks>
    /// The context is carried unchanged through <see cref="Compact"/>, so a compacted member still reports
    /// full causal knowledge and pins the frontier correctly. This is the gossip path, distinct from the
    /// serialization path: use <see cref="ToState"/> or <see cref="ToRunState"/> to persist or transfer the
    /// array.
    /// </remarks>
    public VectorClock CausalContext => Context;


    /// <summary>
    /// Inserts <paramref name="value"/> at the head of the array.
    /// </summary>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the insert.</param>
    /// <returns>The new array and the identity assigned to the inserted element.</returns>
    public (Rga<TValue> Result, Dot InsertedId) InsertAtHead(TValue value, ReplicaId replica)
    {
        return Insert(null, value, replica);
    }


    /// <summary>
    /// Inserts <paramref name="value"/> immediately after the element identified by
    /// <paramref name="after"/>.
    /// </summary>
    /// <param name="after">The identity of the element to insert after.</param>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the insert.</param>
    /// <returns>The new array and the identity assigned to the inserted element.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="after"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="after"/> is not an element of this array.</exception>
    public (Rga<TValue> Result, Dot InsertedId) InsertAfter(Dot after, TValue value, ReplicaId replica)
    {
        ArgumentNullException.ThrowIfNull(after);
        if(!Vertices.ContainsKey(after))
        {
            throw new ArgumentException("The predecessor is not an element of this array.", nameof(after));
        }

        return Insert(after, value, replica);
    }


    /// <summary>
    /// Returns a new array with the element identified by <paramref name="id"/> tombstoned by a fresh
    /// dotted remove event minted on <paramref name="replica"/>'s axis. The element is retained for
    /// ordering but no longer visible, and the remove is a first-class event a stability frontier can
    /// certify.
    /// </summary>
    /// <param name="id">The identity of the element to remove.</param>
    /// <param name="replica">The replica performing the removal.</param>
    /// <returns>A new <see cref="Rga{TValue}"/>, or this array if the element was already tombstoned.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="OverflowException">Thrown when advancing <paramref name="replica"/>'s counter would overflow, propagated from <see cref="VectorClock.Increment(ReplicaId)"/>; this array is not modified when the throw occurs.</exception>
    /// <remarks>
    /// <para>
    /// The remove-dot is minted with <see cref="VectorClock.Increment(ReplicaId)"/>, not
    /// <see cref="VectorClock.IncrementPastAll(ReplicaId)"/>: a remove-dot needs uniqueness, monotonicity,
    /// and stability-trackability, but not Lamport dominance, so the gentler tick is used. Removal is
    /// idempotent by target — re-removing an already-tombstoned element (dotted or legacy) mints no new dot
    /// and returns this array; two remove-dots for one target arise only through <see cref="Merge(Rga{TValue})"/>
    /// of genuinely concurrent removes. The target need not be a vertex here: a remove can be serialized and
    /// merged separately from the insert it hides, so an orphan remove is legal.
    /// </para>
    /// <para>
    /// The remove and inserts share one counter plane. A remove tick raises the replica's own axis, so its
    /// next insert — assigned Lamport-style past the observed maximum — can outrank a concurrent sibling it
    /// would otherwise have tied with, flipping the descending (counter, replica) tie-break between those
    /// concurrent inserts. Convergence and intention preservation are unaffected; only the relative order of
    /// concurrent siblings can move.
    /// </para>
    /// </remarks>
    public Rga<TValue> Remove(Dot id, ReplicaId replica)
    {
        ArgumentNullException.ThrowIfNull(id);
        if(Tombstones.ContainsKey(id))
        {
            return this;
        }

        VectorClock advanced = Context.Increment(replica);
        var removeDot = new Dot(replica, advanced[replica]);

        var updated = new Dictionary<Dot, FrozenSet<Dot>>(Tombstones.Count + 1);
        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            updated[entry.Key] = entry.Value;
        }

        updated[id] = FrozenSet.ToFrozenSet([removeDot]);

        return new Rga<TValue>(advanced, Vertices, updated.ToFrozenDictionary(), CompactedPredecessors);
    }


    /// <summary>
    /// Returns the merge of this array and <paramref name="other"/>: the union of their vertices, the
    /// per-target union of their tombstones' remove-dots, and the deterministic resolution of their
    /// translation maps.
    /// </summary>
    /// <param name="other">The array to merge with.</param>
    /// <returns>A new <see cref="Rga{TValue}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when an operand is a stale pre-remove state: it presents an element live — untombstoned on
    /// both sides — that the other operand's lineage compacted away after the element's remove was observed
    /// group-wide, witnessed permanently by that operand's translation map. A replica rejoining after
    /// eviction, restore, or replay must adopt a current state wholesale — the quorum-read rejoin contract,
    /// a host concern that lands with the container seal — rather than merge; merging a stale pre-remove
    /// state would resurrect the element, so it fails closed. Also thrown on a forged input whose
    /// translation maps are mutually inconsistent — a candidate resolving to neither a merged vertex nor a
    /// further translation, or a dropped dot with no resolvable target. Also thrown when the operands carry
    /// CONFLICTING vertices for one insert identity — a dot mints exactly one immutable vertex, so the
    /// conflict is equivocation or an adoption recovery that ran more than once and re-minted the identity
    /// divergently (run the recovery at most once per lost context and persist it before gossiping);
    /// overwriting would let merge order pick a winner silently.
    /// </exception>
    /// <remarks>
    /// Tombstones union their remove-dots per target: a legacy empty set unioned with a dotted set yields
    /// the dotted set, so a v1-loaded tombstone is upgraded by any peer that holds the dotted remove. The
    /// translation map merges by the max-counter resolution over the edge union of both maps — every
    /// dropped dot resolves to the maximum-counter merged vertex reachable through the union, the nearest
    /// retained ancestor and identical on every member — so the merge is commutative, associative, and
    /// idempotent. A laggard that observed the remove re-enters the ghost vertex together with its
    /// tombstone, converged-correct because the drop gate certifies the remove before dropping. The
    /// conflicting-vertex check compares values with
    /// <see cref="System.Collections.Generic.EqualityComparer{T}.Default"/>, so <typeparamref name="TValue"/>
    /// must carry VALUE equality: a reference-equality element type would compare honest copies of one
    /// vertex unequal after any serialization boundary and fail honest merges closed.
    /// </remarks>
    public Rga<TValue> Merge(Rga<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        //Fail closed on a stale pre-remove operand in either orientation before building anything: an
        //element live here but compacted away there (with no tombstone masking it on either side) is a
        //replay of state from before a universally-observed remove.
        ThrowOnStaleReplay(this, other);
        ThrowOnStaleReplay(other, this);

        var mergedVertices = new Dictionary<Dot, Vertex>(Vertices.Count + other.Vertices.Count);
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            mergedVertices[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<Dot, Vertex> entry in other.Vertices)
        {
            //A dot mints exactly one immutable vertex, and compaction never rewrites a retained vertex's
            //predecessor or value (dropped anchors are served through the translation map instead), so
            //operands disagreeing on a shared dot's vertex is never an honest edge: it is equivocation or
            //an adoption recovery that ran twice and re-minted the identity divergently. Overwriting would
            //let merge order pick a winner silently — fail closed.
            if(mergedVertices.TryGetValue(entry.Key, out Vertex? mine) && !mine.Equals(entry.Value))
            {
                throw new InvalidOperationException("The operands carry conflicting vertices for one insert identity: a dot mints exactly one immutable vertex, so this state is equivocation or an adoption recovery that ran more than once and re-minted the identity divergently. Merging would let merge order choose silently; the divergent member must rejoin by wholesale adoption.");
            }

            mergedVertices[entry.Key] = entry.Value;
        }

        var mergedTombstones = new Dictionary<Dot, FrozenSet<Dot>>(Tombstones.Count + other.Tombstones.Count);
        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            mergedTombstones[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in other.Tombstones)
        {
            if(mergedTombstones.TryGetValue(entry.Key, out FrozenSet<Dot>? existing))
            {
                var union = new HashSet<Dot>(existing);
                union.UnionWith(entry.Value);
                mergedTombstones[entry.Key] = union.ToFrozenSet();
            }
            else
            {
                mergedTombstones[entry.Key] = entry.Value;
            }
        }

        Dictionary<Dot, Dot> mergedPredecessors = ResolveTranslations(mergedVertices, other);

        return new Rga<TValue>(Context.Merge(other.Context), mergedVertices.ToFrozenDictionary(), mergedTombstones.ToFrozenDictionary(), mergedPredecessors.ToFrozenDictionary());
    }


    private static void ThrowOnStaleReplay(Rga<TValue> holder, Rga<TValue> witnesser)
    {
        foreach(Dot dropped in witnesser.CompactedPredecessors.Keys)
        {
            if(holder.Vertices.ContainsKey(dropped)
                && !holder.Tombstones.ContainsKey(dropped)
                && !witnesser.Vertices.ContainsKey(dropped)
                && !witnesser.Tombstones.ContainsKey(dropped))
            {
                throw new InvalidOperationException("A merge operand presents an element live that the other operand's lineage compacted after its remove was universally observed; the operand is a stale pre-remove state. A replica rejoining after eviction, restore, or replay must adopt a current state wholesale instead of merging.");
            }
        }
    }


    private Dictionary<Dot, Dot> ResolveTranslations(Dictionary<Dot, Vertex> mergedVertices, Rga<TValue> other)
    {
        //Union of edges from both operands, a multimap dropped-dot -> candidate targets, deduplicating
        //identical (key, value) pairs.
        var edges = new Dictionary<Dot, List<Dot>>();
        AddEdges(edges, CompactedPredecessors);
        AddEdges(edges, other.CompactedPredecessors);

        var mergedPredecessors = new Dictionary<Dot, Dot>(edges.Count);
        foreach(Dot droppedDot in edges.Keys)
        {
            //Resolve every candidate transitively through the edge union until it lands on a merged vertex;
            //counters strictly decrease along any predecessor chain (a child is minted past its observed
            //predecessor), so resolution terminates and the maximum-counter resolved candidate is the
            //nearest retained ancestor in the merge. The visited set fails closed on a forged cycle.
            var resolved = new List<Dot>();
            var pending = new Stack<Dot>(edges[droppedDot]);
            var visited = new HashSet<Dot>();
            while(pending.Count > 0)
            {
                Dot candidate = pending.Pop();
                if(!visited.Add(candidate))
                {
                    continue;
                }

                if(mergedVertices.ContainsKey(candidate))
                {
                    resolved.Add(candidate);
                }
                else if(edges.TryGetValue(candidate, out List<Dot>? onward))
                {
                    foreach(Dot next in onward)
                    {
                        pending.Push(next);
                    }
                }
                else
                {
                    throw new InvalidOperationException("A translation candidate resolves to neither a merged vertex nor a further translation; the operands' translation maps are inconsistent.");
                }
            }

            if(resolved.Count == 0)
            {
                throw new InvalidOperationException("A dropped dot has no resolvable translation target in the merge.");
            }

            Dot best = resolved[0];
            for(int i = 1; i < resolved.Count; i++)
            {
                if(CompareDots(resolved[i], best) > 0)
                {
                    best = resolved[i];
                }
            }

            mergedPredecessors[droppedDot] = best;
        }

        return mergedPredecessors;
    }


    private static void AddEdges(Dictionary<Dot, List<Dot>> edges, FrozenDictionary<Dot, Dot> map)
    {
        foreach(KeyValuePair<Dot, Dot> entry in map)
        {
            if(edges.TryGetValue(entry.Key, out List<Dot>? candidates))
            {
                if(!candidates.Contains(entry.Value))
                {
                    candidates.Add(entry.Value);
                }
            }
            else
            {
                edges[entry.Key] = [entry.Value];
            }
        }
    }


    /// <summary>
    /// Returns the serializable state of this array, for persistence or transfer.
    /// </summary>
    /// <returns>The array's state.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when this array carries compaction translations. The flat v1 state shape has no slot for the
    /// translation map, and silently dropping it would leave anchors expressed against compacted-away dots
    /// unservable after a round-trip — fail closed. Use <see cref="ToRunState"/> for the compactable strategy.
    /// </exception>
    public RgaState<TValue> ToState()
    {
        if(CompactedPredecessors.Count > 0)
        {
            throw new InvalidOperationException("This array carries compaction translations that the v1 state shape cannot represent; use ToRunState for the compactable strategy.");
        }

        ImmutableArray<RgaVertexEntry<TValue>>.Builder vertexBuilder = ImmutableArray.CreateBuilder<RgaVertexEntry<TValue>>(Vertices.Count);
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            var id = new DotState(ImmutableArray.Create(entry.Key.Replica.AsSpan()), entry.Key.Counter);
            DotState? predecessor = entry.Value.Predecessor is null
                ? null
                : new DotState(ImmutableArray.Create(entry.Value.Predecessor.Replica.AsSpan()), entry.Value.Predecessor.Counter);
            vertexBuilder.Add(new RgaVertexEntry<TValue>(id, predecessor, entry.Value.Value));
        }

        //Deterministic output: entries by target (replica, counter), remove-dots within an entry the same
        //way, so equal arrays serialize to equal records regardless of operation order.
        var orderedTargets = new List<Dot>(Tombstones.Keys);
        orderedTargets.Sort(CompareDotsByReplica);
        ImmutableArray<RgaTombstoneEntry>.Builder tombstoneBuilder = ImmutableArray.CreateBuilder<RgaTombstoneEntry>(Tombstones.Count);
        foreach(Dot target in orderedTargets)
        {
            var orderedRemoveDots = new List<Dot>(Tombstones[target]);
            orderedRemoveDots.Sort(CompareDotsByReplica);
            ImmutableArray<DotState>.Builder removeDotBuilder = ImmutableArray.CreateBuilder<DotState>(orderedRemoveDots.Count);
            foreach(Dot removeDot in orderedRemoveDots)
            {
                removeDotBuilder.Add(ToDotState(removeDot));
            }

            tombstoneBuilder.Add(new RgaTombstoneEntry(ToDotState(target), removeDotBuilder.ToImmutable()));
        }

        return new RgaState<TValue>(Context.ToState(), vertexBuilder.ToImmutable(), tombstoneBuilder.ToImmutable());
    }


    private static DotState ToDotState(Dot dot)
    {
        return new DotState(ImmutableArray.Create(dot.Replica.AsSpan()), dot.Counter);
    }


    private static Dot FromDotState(DotState state)
    {
        return new Dot(ReplicaId.FromSpan(state.Replica.AsSpan()), state.Counter);
    }


    /// <summary>
    /// Reconstructs an array from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed array.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown, failing closed, if any of the following holds: the vertex, tombstone, or per-tombstone
    /// remove-dot array is default — an absent field on a deserialization path that leaves unset members
    /// default, which is not the same statement as an explicitly empty array — or a vertex id, a tombstone
    /// target, or a
    /// remove-dot has a non-positive counter; a vertex id appears more than once; a vertex's predecessor is
    /// not itself a vertex, or the predecessor graph contains a cycle; a tombstone target appears in more
    /// than one entry; a remove-dot appears more than once within an entry, or in more than one entry; a
    /// vertex dot or a remove-dot is not covered by the context (invariant context-covers-dots); or a
    /// remove-dot equals a vertex id (insert- and remove-dots are provably disjoint on an honest history).
    /// No honest history produces any of these, and admitting one would silently desynchronize
    /// <see cref="Values"/> from <see cref="Count"/> or forge a certifiable remove, so each fails closed.
    /// </exception>
    /// <remarks>
    /// A tombstone whose target is not a vertex is accepted and harmless: a remove can be serialized and
    /// merged separately from the insert it hides, so the target may legitimately be absent here. Such an
    /// orphan target's own coverage by the context is not required — the insert may not have arrived — but
    /// its remove-dots must still be covered, because those are events this state itself witnesses.
    /// </remarks>
    public static Rga<TValue> FromState(RgaState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        //Default (uninitialized) arrays arrive from deserializers that leave an absent member unset, such
        //as the source-generated System.Text.Json path. An absent array is not the same statement as an
        //explicitly empty one — a legacy tombstone declares an EMPTY remove-dot list — so it fails closed
        //here rather than being silently reinterpreted (or crashing on a Length read).
        if(state.Vertices.IsDefault || state.Tombstones.IsDefault)
        {
            throw new ArgumentException("The vertex and tombstone arrays are required; a default array marks an absent field.", nameof(state));
        }

        VectorClock context = VectorClock.FromState(state.Context);
        var vertices = new Dictionary<Dot, Vertex>(state.Vertices.Length);
        foreach(RgaVertexEntry<TValue> entry in state.Vertices)
        {
            var id = new Dot(ReplicaId.FromSpan(entry.Id.Replica.AsSpan()), entry.Id.Counter);
            if(id.Counter < 1)
            {
                throw new ArgumentException("A vertex counter must be positive.", nameof(state));
            }

            Dot? predecessor = entry.Predecessor is null
                ? null
                : new Dot(ReplicaId.FromSpan(entry.Predecessor.Replica.AsSpan()), entry.Predecessor.Counter);
            if(!vertices.TryAdd(id, new Vertex(predecessor, entry.Value)))
            {
                throw new ArgumentException("A vertex id appears more than once.", nameof(state));
            }
        }

        ValidatePredecessors(vertices);

        //Invariant context-covers-dots for the vertices; orphan tombstone targets are exempt (their insert
        //may not have arrived) but their remove-dots are not, and are covered in the tombstone loop below.
        foreach(Dot id in vertices.Keys)
        {
            if(context[id.Replica] < id.Counter)
            {
                throw new ArgumentException("A vertex dot is not covered by the context.", nameof(state));
            }
        }

        var tombstones = new Dictionary<Dot, FrozenSet<Dot>>(state.Tombstones.Length);
        var allRemoveDots = new HashSet<Dot>();
        foreach(RgaTombstoneEntry entry in state.Tombstones)
        {
            Dot target = FromDotState(entry.Target);
            if(target.Counter < 1)
            {
                throw new ArgumentException("A tombstone target counter must be positive.", nameof(state));
            }

            if(entry.RemoveDots.IsDefault)
            {
                throw new ArgumentException("A tombstone's remove-dot array is required; a default array marks an absent field, while a legacy tombstone declares an explicitly empty one.", nameof(state));
            }

            var removeDots = new HashSet<Dot>(entry.RemoveDots.Length);
            foreach(DotState removeDotState in entry.RemoveDots)
            {
                Dot removeDot = FromDotState(removeDotState);
                if(removeDot.Counter < 1)
                {
                    throw new ArgumentException("A remove dot counter must be positive.", nameof(state));
                }

                if(!removeDots.Add(removeDot))
                {
                    throw new ArgumentException("A remove dot appears more than once in a tombstone.", nameof(state));
                }

                if(vertices.ContainsKey(removeDot))
                {
                    throw new ArgumentException("A remove dot equals a vertex id.", nameof(state));
                }

                if(context[removeDot.Replica] < removeDot.Counter)
                {
                    throw new ArgumentException("A remove dot is not covered by the context.", nameof(state));
                }

                if(!allRemoveDots.Add(removeDot))
                {
                    throw new ArgumentException("A remove dot appears in more than one tombstone.", nameof(state));
                }
            }

            if(!tombstones.TryAdd(target, removeDots.ToFrozenSet()))
            {
                throw new ArgumentException("A tombstone target appears more than once.", nameof(state));
            }
        }

        return new Rga<TValue>(context, vertices.ToFrozenDictionary(), tombstones.ToFrozenDictionary(), FrozenDictionary<Dot, Dot>.Empty);
    }


    /// <summary>
    /// The certified dotted projection at <paramref name="stabilityFrontier"/>: the visible order filtered to
    /// stable insert-dots, excluding elements whose remove is certified at the frontier, each paired with its
    /// serialized identity. This is the checkpoint a container seals to.
    /// </summary>
    /// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>.</param>
    /// <returns>The projected entries in visible order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A LOCALLY tombstoned element whose remove-dots are all above the frontier stays IN the projection — from
    /// the group's certified viewpoint the remove has not happened yet — so the projection is a pure function of
    /// the frontier for every member whose context dominates it, and two honest members at the same frontier
    /// compute the identical checkpoint. The same core drives <see cref="Compact"/>'s integrity check, so the
    /// projection and the assertion can never disagree on the predicate.
    /// </remarks>
    public ImmutableArray<SequenceCheckpointEntry<TValue>> CertifiedProjection(VectorClock stabilityFrontier)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);

        List<(Dot Dot, TValue Value)> projection = CertifiedProjectionCore(stabilityFrontier);
        ImmutableArray<SequenceCheckpointEntry<TValue>>.Builder builder = ImmutableArray.CreateBuilder<SequenceCheckpointEntry<TValue>>(projection.Count);
        foreach((Dot dot, TValue value) in projection)
        {
            builder.Add(new SequenceCheckpointEntry<TValue>(ToDotState(dot), value));
        }

        return builder.ToImmutable();
    }


    //The one certified-projection core the public projection and Compact's integrity check share: the visible
    //order filtered to stable insert-dots, excluding elements whose remove is certified at the frontier. A single
    //source means the predicate can never drift between the emitted checkpoint and the compaction assertion.
    private List<(Dot Dot, TValue Value)> CertifiedProjectionCore(VectorClock frontier)
    {
        List<Dot> order = ComputeOrder();
        var projection = new List<(Dot Dot, TValue Value)>(order.Count);
        foreach(Dot dot in order)
        {
            if(!IsStable(dot, frontier))
            {
                continue;
            }

            if(Tombstones.TryGetValue(dot, out FrozenSet<Dot>? removeDots) && HasCertifiedRemove(removeDots, frontier))
            {
                continue;
            }

            projection.Add((dot, Vertices[dot].Value));
        }

        return projection;
    }


    /// <summary>
    /// Compacts the waterline: stable tombstones whose remove is certified at the frontier and that have no
    /// retained descendant collapse out of the vertex and tombstone sets, and the dots they leave behind
    /// are served from a translation map. Visible vertices never move, so the visible order — and every
    /// visible value — is unchanged.
    /// </summary>
    /// <param name="stabilityFrontier">
    /// The group stability frontier. A dot is stable when the frontier's counter for its replica is at least
    /// the dot's counter; stable state can never again be referenced by any member, so it is safe to collapse.
    /// </param>
    /// <param name="checkpoint">
    /// The agreed checkpoint: the certified projection at the frontier as dotted (identity, value) entries — the
    /// visible order filtered to stable insert-dots, excluding elements whose remove is certified at the frontier.
    /// </param>
    /// <returns>The compacted array; this array is never modified, and is returned unchanged when nothing drops.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="checkpoint"/> is default.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the certified projection does not equal <paramref name="checkpoint"/> element-wise on both dot
    /// and value. The agreed checkpoint is the content line; a mismatch means the (frontier, checkpoint) pair is
    /// misaligned. The check runs before any result is constructed — this fails closed rather than guessing,
    /// because a wrong checkpoint would silently fork the group's generations.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A vertex is <em>retained</em> when it is unstable, or visible, or tombstoned with a remove that is
    /// not yet certified at the frontier (a legacy tombstone loaded from pre-dotted state carries no
    /// remove-dot, so it can never be certified and is retained forever), or head-anchored (a head-anchored
    /// tombstone has no element to translate to, so it can never drop), or any of its children is retained.
    /// A dropped vertex therefore has only dropped children and no retained vertex ever has a dropped
    /// predecessor — the retained set's predecessor closure is intact and no vertex is ever re-parented.
    /// <see cref="Context"/> is unchanged: compaction reclaims storage, not causal knowledge, and it still
    /// covers every dropped remove-dot, which keeps the remove certified forever after the tombstone bytes
    /// are gone.
    /// </para>
    /// <para>
    /// Each dropped dot maps to the first retained vertex on its recorded predecessor chain. Prior map
    /// entries compose through this compaction — a prior target that stays retained is kept, a prior target
    /// dropped this pass routes to that target's fresh entry — and the fresh entries win any collision.
    /// </para>
    /// </remarks>
    public Rga<TValue> Compact(VectorClock stabilityFrontier, ImmutableArray<SequenceCheckpointEntry<TValue>> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);
        if(checkpoint.IsDefault)
        {
            throw new ArgumentException("The checkpoint content is required.", nameof(checkpoint));
        }

        //Retention over the drop gate, computed by an iterative post-order walk (no recursion): a vertex is
        //retained when it is unstable, visible, tombstoned with an uncertified remove, head-anchored, or has
        //any retained child. Tombstone entries with no corresponding vertex are outside retention entirely
        //and are carried unchanged.
        Dictionary<Dot, List<Dot>> childrenByParent = BuildChildrenByParent();
        Dictionary<Dot, bool> retention = ComputeRetention(stabilityFrontier, childrenByParent);

        //Checkpoint check before any result construction: the certified projection — the visible order filtered
        //to stable insert-dots, excluding elements whose remove is certified — must equal the agreed content
        //element-wise on BOTH dot and value, otherwise the (frontier, checkpoint) pair is misaligned. This shares
        //one core with CertifiedProjection so the predicate can never drift.
        List<(Dot Dot, TValue Value)> certifiedProjection = CertifiedProjectionCore(stabilityFrontier);
        if(certifiedProjection.Count != checkpoint.Length)
        {
            throw new InvalidOperationException("The certified projection does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
        }

        for(int i = 0; i < certifiedProjection.Count; i++)
        {
            if(!certifiedProjection[i].Dot.Equals(FromDotState(checkpoint[i].Dot))
                || !EqualityComparer<TValue>.Default.Equals(certifiedProjection[i].Value, checkpoint[i].Value))
            {
                throw new InvalidOperationException("The certified projection does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
            }
        }

        //No-op shortcut: with nothing to drop the array is unchanged, so repeat compaction at the same
        //(frontier, checkpoint) is trivially idempotent.
        var dropped = new List<Dot>();
        foreach(KeyValuePair<Dot, bool> entry in retention)
        {
            if(!entry.Value)
            {
                dropped.Add(entry.Key);
            }
        }

        if(dropped.Count == 0)
        {
            return this;
        }

        //Result: dropped vertices leave both the vertex set and the tombstone set; retained vertices keep
        //their recorded predecessors unchanged (no re-anchoring of any kind).
        var newVertices = new Dictionary<Dot, Vertex>(retention.Count);
        foreach(KeyValuePair<Dot, bool> entry in retention)
        {
            if(entry.Value)
            {
                newVertices[entry.Key] = Vertices[entry.Key];
            }
        }

        var newTombstones = new Dictionary<Dot, FrozenSet<Dot>>(Tombstones.Count);
        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            //Orphan tombstones (no corresponding vertex) are untouched; a tombstone of a retained vertex
            //stays; a tombstone of a dropped vertex leaves with the vertex, remove-dot set and all.
            if(!Vertices.ContainsKey(entry.Key) || newVertices.ContainsKey(entry.Key))
            {
                newTombstones[entry.Key] = entry.Value;
            }
        }

        //Translation map: for every dropped dot, the first retained vertex on its recorded predecessor
        //chain (the chain always reaches a retained vertex because head-anchored tombstones never drop).
        var newPredecessors = new Dictionary<Dot, Dot>(CompactedPredecessors.Count + dropped.Count);
        foreach(Dot drop in dropped)
        {
            newPredecessors[drop] = FirstRetainedAncestor(drop, retention);
        }

        //Prior entries compose: a prior target that is still retained is kept; a prior target dropped this
        //pass routes to that target's fresh entry (every vertex dropped this pass has one). Fresh entries
        //already in the map win collisions, so prior entries never overwrite them.
        foreach(KeyValuePair<Dot, Dot> entry in CompactedPredecessors)
        {
            if(newPredecessors.ContainsKey(entry.Key))
            {
                continue;
            }

            newPredecessors[entry.Key] = retention.TryGetValue(entry.Value, out bool retained) && !retained
                ? newPredecessors[entry.Value]
                : entry.Value;
        }

        return new Rga<TValue>(Context, newVertices.ToFrozenDictionary(), newTombstones.ToFrozenDictionary(), newPredecessors.ToFrozenDictionary());
    }


    /// <summary>
    /// Translates an anchor that may name a dot compaction dropped into the vertex that now serves it, or
    /// <see langword="null"/> when the anchor is unservable here.
    /// </summary>
    /// <param name="anchor">The possibly dropped anchor.</param>
    /// <returns>The anchor itself when it is still a vertex, the serving vertex when the dot was dropped, or <see langword="null"/> when unservable.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchor"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The translated target may itself be a tombstoned vertex — that is a legal RGA predecessor. An insert
    /// translated onto a dropped tombstone's nearest retained ancestor can land ahead of sibling subtrees the
    /// tombstone's position would have followed; convergence is unaffected — the map is identical on every
    /// member — and intention degrades only for anchors at elements that were already removed.
    /// </remarks>
    public Dot? TranslateAnchor(Dot anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        if(Vertices.ContainsKey(anchor))
        {
            return anchor;
        }

        return CompactedPredecessors.TryGetValue(anchor, out Dot? target) ? target : null;
    }


    /// <summary>
    /// Returns the run-length-encoded serializable state of this array, for persistence or transfer. Unlike
    /// <see cref="ToState"/> this shape carries dotted removes and the compaction translation map, so a
    /// compacted, remove-certifying array survives a round-trip with its anchor servability intact.
    /// </summary>
    /// <returns>The array's run-length state.</returns>
    /// <remarks>
    /// Consecutive same-replica vertices that form a predecessor chain coalesce into one
    /// <see cref="RgaRunEntry{TValue}"/>. A single-replica contiguous deletion pass — one replica removing a run
    /// of consecutive elements, each carrying one aligned remove-dot — coalesces into one two-range
    /// <see cref="RgaTombstoneSpan"/>; a tombstone a span cannot express (concurrent removes, a legacy empty, or
    /// non-aligned arithmetic) becomes an <see cref="RgaConcurrentTombstone"/>. A maximal contiguous run of
    /// dropped dots sharing one retained target and covering no live vertex coalesces into an
    /// <see cref="RgaTranslationSpan"/>; anything else — including a resurrected ghost-with-witness — stays a
    /// singleton <see cref="RgaTranslationEntry"/>. Output order is deterministic: runs by (replica, first
    /// counter), spans by (target replica, target from), irregulars by target, translations by dropped,
    /// translation spans by (dropped replica, from).
    /// </remarks>
    public RgaRunState<TValue> ToRunState()
    {
        ImmutableArray<RgaRunEntry<TValue>> runs = BuildRuns();
        (ImmutableArray<RgaTombstoneSpan> spans, ImmutableArray<RgaConcurrentTombstone> irregulars) = BuildTombstones();
        (ImmutableArray<RgaTranslationEntry> translations, ImmutableArray<RgaTranslationSpan> translationSpans) = BuildTranslations();

        return new RgaRunState<TValue>(Context.ToState(), runs, spans, irregulars, translations, translationSpans);
    }


    /// <summary>
    /// Reconstructs an array from previously serialized run-length <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The run-length state to reconstruct from.</param>
    /// <returns>The reconstructed array.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown, failing closed, if any serialized array is default (an absent field), a run's values are empty, a
    /// counter is not positive, a run's expanded vertex counters overflow, a tombstone or translation span has an
    /// out-of-range bound, a two-range span's
    /// remove-dot counters overflow, an irregular tombstone's remove-dot array is default, a dot appears in more
    /// than one run, a tombstone target appears in more than one span or irregular entry, a remove-dot repeats
    /// within an entry or across entries, a remove-dot equals a vertex id or is not covered by the context, an
    /// expanded vertex's predecessor is not itself a vertex (or the predecessor graph contains a cycle), a vertex
    /// dot is not covered by the context, a translation target is not a vertex of the state, a translation span's
    /// dropped dot is not covered by the context or lands on a live vertex, a dropped dot appears in more than one
    /// translation, or a singleton translation's dropped dot is a live untombstoned vertex (the stale-replay
    /// W-shape).
    /// </exception>
    /// <remarks>
    /// <para>
    /// Tombstones rebuild from three sources under one posture: two-range spans (one aligned remove-dot per
    /// target), irregular entries (concurrent removes, or a legacy empty set the drop gate can never certify —
    /// retained forever), and their duplicate-target detection is unified. A singleton translation's
    /// <see cref="RgaTranslationEntry.Dropped"/> dot may still be present in the vertices when it also carries a
    /// tombstone: a laggard merge can resurrect a dropped tombstone while the map entry remains — the
    /// ghost-plus-witness shape — which is harmless because <see cref="TranslateAnchor"/> consults the vertices
    /// first. A dropped dot present as a live vertex WITHOUT a tombstone is rejected as the stale-replay witness;
    /// a translation SPAN may never land on any vertex at all, since the ghost-with-witness shape serializes only
    /// as a singleton.
    /// </para>
    /// <para>
    /// EXPANSION SIZE: every expanded dot — a span target through its remove-dot coverage, a translation-span
    /// dropped dot through its context coverage — is bounded by the context the payload itself declares, which
    /// bounds honest input. A source that forges both an inflated context and matching giant spans can still
    /// demand a large expansion; run-state deserialization trusts its persistence source, and hostile-wire
    /// resource-bounding is a transport or host concern, not this method's.
    /// </para>
    /// </remarks>
    public static Rga<TValue> FromRunState(RgaRunState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        //A default array marks an absent field on a deserialization path that leaves unset members default; it is
        //not the same statement as an explicitly empty array, so it fails closed rather than crashing on a read.
        if(state.Runs.IsDefault || state.TombstoneSpans.IsDefault || state.IrregularTombstones.IsDefault || state.Translations.IsDefault || state.TranslationSpans.IsDefault)
        {
            throw new ArgumentException("The run, tombstone, irregular-tombstone, translation, and translation-span arrays are required; a default array marks an absent field.", nameof(state));
        }

        VectorClock context = VectorClock.FromState(state.Context);

        var vertices = new Dictionary<Dot, Vertex>();
        foreach(RgaRunEntry<TValue> run in state.Runs)
        {
            if(run.Values.IsDefaultOrEmpty)
            {
                throw new ArgumentException("A run must carry at least one value.", nameof(state));
            }

            ReplicaId replica = ReplicaId.FromSpan(run.First.Replica.AsSpan());
            int firstCounter = run.First.Counter;
            if(firstCounter <= 0)
            {
                throw new ArgumentException("A run counter must be positive.", nameof(state));
            }

            //The expanded counters must stay positive: an unchecked wrap past int.MaxValue would slip a
            //negative-counter vertex past both this run's positivity check and the coverage loop below,
            //mirroring the overflow guard the tombstone spans carry.
            if((long)firstCounter + run.Values.Length - 1 > int.MaxValue)
            {
                throw new ArgumentException("A run's vertex counters overflow.", nameof(state));
            }

            Dot? predecessor = run.Predecessor is null ? null : FromDotState(run.Predecessor);
            for(int i = 0; i < run.Values.Length; i++)
            {
                var dot = new Dot(replica, firstCounter + i);
                if(!vertices.TryAdd(dot, new Vertex(i == 0 ? predecessor : new Dot(replica, firstCounter + i - 1), run.Values[i])))
                {
                    throw new ArgumentException("A dot appears in more than one run.", nameof(state));
                }
            }
        }

        ValidatePredecessors(vertices);

        //Invariant context-covers-dots for every vertex; the v1 run path carries no remove-dots to cover.
        foreach(Dot id in vertices.Keys)
        {
            if(context[id.Replica] < id.Counter)
            {
                throw new ArgumentException("A vertex dot is not covered by the context.", nameof(state));
            }
        }

        //Tombstones rebuild from spans and irregulars into one dictionary; a target duplicated across ANY source
        //throws, and every remove-dot is validated the way the flat FromState validates: positive, disjoint from
        //vertices, covered by the context, unique within its entry and across all entries.
        var tombstones = new Dictionary<Dot, FrozenSet<Dot>>();
        var allRemoveDots = new HashSet<Dot>();
        foreach(RgaTombstoneSpan span in state.TombstoneSpans)
        {
            if(span.TargetFrom < 1 || span.TargetTo < span.TargetFrom || span.RemoveFrom < 1)
            {
                throw new ArgumentException("A tombstone span must satisfy 1 <= TargetFrom <= TargetTo and RemoveFrom >= 1.", nameof(state));
            }

            if((long)span.RemoveFrom + (span.TargetTo - span.TargetFrom) > int.MaxValue)
            {
                throw new ArgumentException("A tombstone span's remove-dot counters overflow.", nameof(state));
            }

            ReplicaId targetReplica = ReplicaId.FromSpan(span.TargetReplica.AsSpan());
            ReplicaId removeReplica = ReplicaId.FromSpan(span.RemoveReplica.AsSpan());
            int length = span.TargetTo - span.TargetFrom + 1;
            for(int i = 0; i < length; i++)
            {
                var target = new Dot(targetReplica, span.TargetFrom + i);
                var removeDot = new Dot(removeReplica, span.RemoveFrom + i);
                if(vertices.ContainsKey(removeDot))
                {
                    throw new ArgumentException("A remove dot equals a vertex id.", nameof(state));
                }

                if(context[removeDot.Replica] < removeDot.Counter)
                {
                    throw new ArgumentException("A remove dot is not covered by the context.", nameof(state));
                }

                if(!allRemoveDots.Add(removeDot))
                {
                    throw new ArgumentException("A remove dot appears in more than one tombstone.", nameof(state));
                }

                if(!tombstones.TryAdd(target, FrozenSet.ToFrozenSet([removeDot])))
                {
                    throw new ArgumentException("A tombstone target appears in more than one entry.", nameof(state));
                }
            }
        }

        foreach(RgaConcurrentTombstone irregular in state.IrregularTombstones)
        {
            if(irregular.RemoveDots.IsDefault)
            {
                throw new ArgumentException("An irregular tombstone's remove-dot array is required; a default array marks an absent field.", nameof(state));
            }

            Dot target = FromDotState(irregular.Target);
            if(target.Counter < 1)
            {
                throw new ArgumentException("A tombstone target counter must be positive.", nameof(state));
            }

            var removeDots = new HashSet<Dot>(irregular.RemoveDots.Length);
            foreach(DotState removeDotState in irregular.RemoveDots)
            {
                Dot removeDot = FromDotState(removeDotState);
                if(removeDot.Counter < 1)
                {
                    throw new ArgumentException("A remove dot counter must be positive.", nameof(state));
                }

                if(!removeDots.Add(removeDot))
                {
                    throw new ArgumentException("A remove dot appears more than once in a tombstone.", nameof(state));
                }

                if(vertices.ContainsKey(removeDot))
                {
                    throw new ArgumentException("A remove dot equals a vertex id.", nameof(state));
                }

                if(context[removeDot.Replica] < removeDot.Counter)
                {
                    throw new ArgumentException("A remove dot is not covered by the context.", nameof(state));
                }

                if(!allRemoveDots.Add(removeDot))
                {
                    throw new ArgumentException("A remove dot appears in more than one tombstone.", nameof(state));
                }
            }

            if(!tombstones.TryAdd(target, removeDots.ToFrozenSet()))
            {
                throw new ArgumentException("A tombstone target appears in more than one entry.", nameof(state));
            }
        }

        //Singleton translations keep the slice-1 posture (last-wins among themselves, ghost-plus-witness legal),
        //processed before the spans so a span colliding with a singleton throws.
        var compactedPredecessors = new Dictionary<Dot, Dot>(state.Translations.Length + state.TranslationSpans.Length);
        foreach(RgaTranslationEntry translation in state.Translations)
        {
            Dot target = FromDotState(translation.Target);
            if(!vertices.ContainsKey(target))
            {
                throw new ArgumentException("A translation target is not a vertex of the state.", nameof(state));
            }

            //A dropped dot that is a live vertex with no tombstone is the permanent witness of a stale pre-remove
            //state; the tombstoned ghost-plus-witness shape stays legal.
            Dot dropped = FromDotState(translation.Dropped);
            if(vertices.ContainsKey(dropped) && !tombstones.ContainsKey(dropped))
            {
                throw new ArgumentException("A translation's dropped dot is a live vertex of the state.", nameof(state));
            }

            compactedPredecessors[dropped] = target;
        }

        foreach(RgaTranslationSpan span in state.TranslationSpans)
        {
            if(span.FromCounter < 1 || span.ToCounter < span.FromCounter)
            {
                throw new ArgumentException("A translation span must satisfy 1 <= FromCounter <= ToCounter.", nameof(state));
            }

            Dot target = FromDotState(span.Target);
            if(!vertices.ContainsKey(target))
            {
                throw new ArgumentException("A translation target is not a vertex of the state.", nameof(state));
            }

            ReplicaId replica = ReplicaId.FromSpan(span.DroppedReplica.AsSpan());
            for(int counter = span.FromCounter; counter <= span.ToCounter; counter++)
            {
                var dropped = new Dot(replica, counter);
                if(context[replica] < counter)
                {
                    throw new ArgumentException("A translation span's dropped dot is not covered by the context.", nameof(state));
                }

                //A span never covers a live vertex — the resurrected ghost-with-witness serializes as a singleton
                //entry — so landing on any vertex is a forged span.
                if(vertices.ContainsKey(dropped))
                {
                    throw new ArgumentException("A translation span's dropped dot is a vertex of the state.", nameof(state));
                }

                if(!compactedPredecessors.TryAdd(dropped, target))
                {
                    throw new ArgumentException("A dropped dot appears in more than one translation.", nameof(state));
                }
            }
        }

        return new Rga<TValue>(context, vertices.ToFrozenDictionary(), tombstones.ToFrozenDictionary(), compactedPredecessors.ToFrozenDictionary());
    }


    private Dictionary<Dot, List<Dot>> BuildChildrenByParent()
    {
        var childrenByParent = new Dictionary<Dot, List<Dot>>();
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            Dot? predecessor = entry.Value.Predecessor;
            if(predecessor is null)
            {
                continue;
            }

            if(childrenByParent.TryGetValue(predecessor, out List<Dot>? siblings))
            {
                siblings.Add(entry.Key);
            }
            else
            {
                childrenByParent[predecessor] = [entry.Key];
            }
        }

        return childrenByParent;
    }


    //Classifies every vertex's retention against the frontier without recursion: a post-order walk over the
    //predecessor forest (one predecessor per vertex, acyclic by deserialization), then one pass classifying
    //children before parents. A vertex is retained when its base gate holds or any child is already retained.
    private Dictionary<Dot, bool> ComputeRetention(VectorClock frontier, Dictionary<Dot, List<Dot>> childrenByParent)
    {
        var postOrder = new List<Dot>(Vertices.Count);
        var stack = new Stack<(Dot Dot, bool ChildrenPushed)>();
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            if(entry.Value.Predecessor is null)
            {
                stack.Push((entry.Key, false));
            }
        }

        while(stack.Count > 0)
        {
            (Dot dot, bool childrenPushed) = stack.Pop();
            if(childrenPushed)
            {
                postOrder.Add(dot);

                continue;
            }

            stack.Push((dot, true));
            if(childrenByParent.TryGetValue(dot, out List<Dot>? children))
            {
                foreach(Dot child in children)
                {
                    stack.Push((child, false));
                }
            }
        }

        var retention = new Dictionary<Dot, bool>(Vertices.Count);
        foreach(Dot dot in postOrder)
        {
            bool retained = BaseRetained(dot, frontier);
            if(!retained && childrenByParent.TryGetValue(dot, out List<Dot>? children))
            {
                foreach(Dot child in children)
                {
                    if(retention[child])
                    {
                        retained = true;

                        break;
                    }
                }
            }

            retention[dot] = retained;
        }

        return retention;
    }


    //The base retention gate, before any child is considered: a vertex is retained on its own account when
    //it is unstable, visible, head-anchored, or tombstoned with a remove that is not certified at the
    //frontier (a legacy empty set is never certified, so it lands here forever).
    private bool BaseRetained(Dot dot, VectorClock frontier)
    {
        if(!IsStable(dot, frontier) || Vertices[dot].Predecessor is null)
        {
            return true;
        }

        if(!Tombstones.TryGetValue(dot, out FrozenSet<Dot>? removeDots))
        {
            return true;
        }

        return !HasCertifiedRemove(removeDots, frontier);
    }


    private Dot FirstRetainedAncestor(Dot dropped, Dictionary<Dot, bool> retention)
    {
        //Walk recorded predecessors through dropped vertices; head-anchored tombstones are always retained,
        //so the chain reaches a retained vertex before it would run off a null predecessor.
        Dot? current = Vertices[dropped].Predecessor;
        while(current is not null)
        {
            if(!retention.TryGetValue(current, out bool retained) || retained)
            {
                return current;
            }

            current = Vertices[current].Predecessor;
        }

        throw new InvalidOperationException("A dropped dot has no retained ancestor; head-anchored tombstones must never drop.");
    }


    private ImmutableArray<RgaRunEntry<TValue>> BuildRuns()
    {
        //A vertex starts a run when its predecessor is NOT the same-replica counter-minus-one vertex present
        //in the set; a run extends while the same-replica counter-plus-one vertex exists and records the
        //current vertex as its predecessor. Tombstone status does not break runs.
        var starts = new List<Dot>();
        foreach(Dot dot in Vertices.Keys)
        {
            var previous = new Dot(dot.Replica, dot.Counter - 1);
            bool extendsPrevious = dot.Counter > 1
                && Vertices.ContainsKey(previous)
                && Vertices[dot].Predecessor is { } predecessor
                && predecessor.Equals(previous);
            if(!extendsPrevious)
            {
                starts.Add(dot);
            }
        }

        starts.Sort(CompareDotsByReplica);

        ImmutableArray<RgaRunEntry<TValue>>.Builder builder = ImmutableArray.CreateBuilder<RgaRunEntry<TValue>>(starts.Count);
        foreach(Dot start in starts)
        {
            var values = new List<TValue>();
            Dot current = start;
            while(true)
            {
                values.Add(Vertices[current].Value);
                var next = new Dot(current.Replica, current.Counter + 1);
                if(Vertices.TryGetValue(next, out Vertex? nextVertex) && nextVertex.Predecessor is { } nextPredecessor && nextPredecessor.Equals(current))
                {
                    current = next;

                    continue;
                }

                break;
            }

            DotState? predecessor = Vertices[start].Predecessor is null ? null : ToDotState(Vertices[start].Predecessor!);
            builder.Add(new RgaRunEntry<TValue>(ToDotState(start), predecessor, [.. values]));
        }

        return builder.ToImmutable();
    }


    private (ImmutableArray<RgaTombstoneSpan> Spans, ImmutableArray<RgaConcurrentTombstone> Irregulars) BuildTombstones()
    {
        if(Tombstones.Count == 0)
        {
            return ([], []);
        }

        var sorted = new List<Dot>(Tombstones.Keys);
        sorted.Sort(CompareDotsByReplica);

        var spans = new List<RgaTombstoneSpan>();
        var irregulars = new List<RgaConcurrentTombstone>();
        int i = 0;
        while(i < sorted.Count)
        {
            Dot target = sorted[i];
            FrozenSet<Dot> removeSet = Tombstones[target];

            //Only a single-remove-dot target can begin a two-range span; a legacy empty set or a concurrent
            //multi-dot removal is irregular by construction.
            if(removeSet.Count != 1)
            {
                irregulars.Add(BuildIrregular(target, removeSet));
                i++;

                continue;
            }

            Dot removeDot = SingleDot(removeSet);
            ReplicaId targetReplica = target.Replica;
            int targetFrom = target.Counter;
            ReplicaId removeReplica = removeDot.Replica;
            int removeFrom = removeDot.Counter;

            //Extend the run while consecutive targets are same-replica counter+1, each carries exactly one
            //remove-dot on the same remove-replica, and those remove counters advance by exactly one from the
            //first — the aligned two-range shape.
            int length = 1;
            int j = i + 1;
            while(j < sorted.Count)
            {
                Dot nextTarget = sorted[j];
                if(!nextTarget.Replica.Equals(targetReplica) || nextTarget.Counter != targetFrom + length)
                {
                    break;
                }

                FrozenSet<Dot> nextSet = Tombstones[nextTarget];
                if(nextSet.Count != 1)
                {
                    break;
                }

                Dot nextRemoveDot = SingleDot(nextSet);
                if(!nextRemoveDot.Replica.Equals(removeReplica) || nextRemoveDot.Counter != removeFrom + length)
                {
                    break;
                }

                length++;
                j++;
            }

            spans.Add(new RgaTombstoneSpan(ImmutableArray.Create(targetReplica.AsSpan()), targetFrom, targetFrom + length - 1, ImmutableArray.Create(removeReplica.AsSpan()), removeFrom));
            i = j;
        }

        return ([.. spans], [.. irregulars]);
    }


    private static RgaConcurrentTombstone BuildIrregular(Dot target, FrozenSet<Dot> removeDots)
    {
        var ordered = new List<Dot>(removeDots);
        ordered.Sort(CompareDotsByReplica);
        ImmutableArray<DotState>.Builder builder = ImmutableArray.CreateBuilder<DotState>(ordered.Count);
        foreach(Dot removeDot in ordered)
        {
            builder.Add(ToDotState(removeDot));
        }

        return new RgaConcurrentTombstone(ToDotState(target), builder.ToImmutable());
    }


    private static Dot SingleDot(FrozenSet<Dot> set)
    {
        foreach(Dot dot in set)
        {
            return dot;
        }

        throw new InvalidOperationException("A single-element set was empty.");
    }


    private (ImmutableArray<RgaTranslationEntry> Entries, ImmutableArray<RgaTranslationSpan> Spans) BuildTranslations()
    {
        if(CompactedPredecessors.Count == 0)
        {
            return ([], []);
        }

        var sorted = new List<Dot>(CompactedPredecessors.Keys);
        sorted.Sort(CompareDotsByReplica);

        var entries = new List<RgaTranslationEntry>();
        var spans = new List<RgaTranslationSpan>();
        int i = 0;
        while(i < sorted.Count)
        {
            Dot dropped = sorted[i];
            Dot target = CompactedPredecessors[dropped];

            //A dropped dot that is again a live vertex — the resurrected ghost-with-witness — is the load-bearing
            //witness for the detector, so it is always a singleton entry, never coalesced into a span.
            if(Vertices.ContainsKey(dropped))
            {
                entries.Add(new RgaTranslationEntry(ToDotState(dropped), ToDotState(target)));
                i++;

                continue;
            }

            //Extend a coalescing run while dropped counters stay contiguous on one replica, share the identical
            //retained target, and cover no live vertex.
            ReplicaId replica = dropped.Replica;
            int fromCounter = dropped.Counter;
            int length = 1;
            int j = i + 1;
            while(j < sorted.Count)
            {
                Dot next = sorted[j];
                if(!next.Replica.Equals(replica) || next.Counter != fromCounter + length)
                {
                    break;
                }

                if(!CompactedPredecessors[next].Equals(target) || Vertices.ContainsKey(next))
                {
                    break;
                }

                length++;
                j++;
            }

            if(length >= 2)
            {
                spans.Add(new RgaTranslationSpan(ImmutableArray.Create(replica.AsSpan()), fromCounter, fromCounter + length - 1, ToDotState(target)));
            }
            else
            {
                entries.Add(new RgaTranslationEntry(ToDotState(dropped), ToDotState(target)));
            }

            i = j;
        }

        return ([.. entries], [.. spans]);
    }


    private static int CompareDotsByReplica(Dot left, Dot right)
    {
        int byReplica = left.Replica.CompareTo(right.Replica);

        return byReplica != 0 ? byReplica : left.Counter.CompareTo(right.Counter);
    }


    private static void ValidatePredecessors(Dictionary<Dot, Vertex> vertices)
    {
        //Every non-head predecessor must resolve to a vertex, and following predecessors from any vertex
        //must terminate at a head (null predecessor) rather than loop. A global done-set records vertices
        //already proven to reach a head, so each chain is walked at most once across all starting vertices.
        var done = new HashSet<Dot>(vertices.Count);
        var onPath = new HashSet<Dot>();
        foreach(Dot start in vertices.Keys)
        {
            if(done.Contains(start))
            {
                continue;
            }

            onPath.Clear();
            Dot current = start;
            while(true)
            {
                if(done.Contains(current))
                {
                    break;
                }

                if(!onPath.Add(current))
                {
                    throw new ArgumentException("The predecessor graph contains a cycle.");
                }

                Dot? predecessor = vertices[current].Predecessor;
                if(predecessor is null)
                {
                    break;
                }

                if(!vertices.ContainsKey(predecessor))
                {
                    throw new ArgumentException("A vertex predecessor is not an element of the array.");
                }

                current = predecessor;
            }

            foreach(Dot visited in onPath)
            {
                done.Add(visited);
            }
        }
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] Rga<TValue>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(!Context.Equals(other.Context) || Vertices.Count != other.Vertices.Count || Tombstones.Count != other.Tombstones.Count || CompactedPredecessors.Count != other.CompactedPredecessors.Count)
        {
            return false;
        }

        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            if(!other.Vertices.TryGetValue(entry.Key, out Vertex? otherVertex) || !entry.Value.Equals(otherVertex))
            {
                return false;
            }
        }

        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            //Per-target set equality: a legacy empty set equals only an empty set.
            if(!other.Tombstones.TryGetValue(entry.Key, out FrozenSet<Dot>? otherSet) || !entry.Value.SetEquals(otherSet))
            {
                return false;
            }
        }

        foreach(KeyValuePair<Dot, Dot> entry in CompactedPredecessors)
        {
            if(!other.CompactedPredecessors.TryGetValue(entry.Key, out Dot? otherTarget) || !entry.Value.Equals(otherTarget))
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Rga<TValue> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int verticesHash = 0;
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            verticesHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        int tombstonesHash = 0;
        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            int removeDotsFold = 0;
            foreach(Dot removeDot in entry.Value)
            {
                removeDotsFold ^= removeDot.GetHashCode();
            }

            tombstonesHash ^= HashCode.Combine(entry.Key, removeDotsFold);
        }

        int compactedHash = 0;
        foreach(KeyValuePair<Dot, Dot> entry in CompactedPredecessors)
        {
            compactedHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return HashCode.Combine(Context, verticesHash, tombstonesHash, compactedHash);
    }


    private (Rga<TValue> Result, Dot InsertedId) Insert(Dot? after, TValue value, ReplicaId replica)
    {
        //The new identity must dominate every observed identity, not just this replica's own counter;
        //otherwise an insert lands behind older siblings from other replicas instead of immediately
        //after its predecessor.
        VectorClock advanced = Context.IncrementPastAll(replica);
        var id = new Dot(replica, advanced[replica]);

        var updated = new Dictionary<Dot, Vertex>(Vertices.Count + 1);
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            updated[entry.Key] = entry.Value;
        }

        updated[id] = new Vertex(after, value);

        return (new Rga<TValue>(advanced, updated.ToFrozenDictionary(), Tombstones, CompactedPredecessors), id);
    }


    private List<Dot> ComputeOrder()
    {
        var roots = new List<Dot>();
        var childrenByParent = new Dictionary<Dot, List<Dot>>();

        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            Dot? predecessor = entry.Value.Predecessor;
            if(predecessor is null)
            {
                roots.Add(entry.Key);
            }
            else if(childrenByParent.TryGetValue(predecessor, out List<Dot>? siblings))
            {
                siblings.Add(entry.Key);
            }
            else
            {
                childrenByParent[predecessor] = [entry.Key];
            }
        }

        SortDescending(roots);
        foreach(List<Dot> siblings in childrenByParent.Values)
        {
            SortDescending(siblings);
        }

        var order = new List<Dot>(Vertices.Count);
        var stack = new Stack<Dot>();
        for(int i = roots.Count - 1; i >= 0; i--)
        {
            stack.Push(roots[i]);
        }

        while(stack.Count > 0)
        {
            Dot id = stack.Pop();
            order.Add(id);
            if(childrenByParent.TryGetValue(id, out List<Dot>? children))
            {
                for(int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }

        return order;
    }


    private static void SortDescending(List<Dot> dots)
    {
        dots.Sort(static (left, right) => CompareDots(right, left));
    }


    private static int CompareDots(Dot left, Dot right)
    {
        int byCounter = left.Counter.CompareTo(right.Counter);

        return byCounter != 0 ? byCounter : left.Replica.CompareTo(right.Replica);
    }


    private static bool IsStable(Dot dot, VectorClock frontier)
    {
        return frontier[dot.Replica] >= dot.Counter;
    }


    //A remove is certified at the frontier when at least one of its dots has been observed group-wide.
    //The empty set (a v1-legacy tombstone) is never certified: those tombstones are retained forever.
    private static bool HasCertifiedRemove(FrozenSet<Dot> removeDots, VectorClock frontier)
    {
        foreach(Dot removeDot in removeDots)
        {
            if(IsStable(removeDot, frontier))
            {
                return true;
            }
        }

        return false;
    }


    private string DebuggerDisplay => $"Rga: {Count} visible, {Tombstones.Count} tombstoned";


    private sealed record Vertex(Dot? Predecessor, TValue Value);
}
