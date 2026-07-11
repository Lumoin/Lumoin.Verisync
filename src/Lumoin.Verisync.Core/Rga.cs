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
    /// further translation, or a dropped dot with no resolvable target.
    /// </exception>
    /// <remarks>
    /// Tombstones union their remove-dots per target: a legacy empty set unioned with a dotted set yields
    /// the dotted set, so a v1-loaded tombstone is upgraded by any peer that holds the dotted remove. The
    /// translation map merges by the max-counter resolution over the edge union of both maps — every
    /// dropped dot resolves to the maximum-counter merged vertex reachable through the union, the nearest
    /// retained ancestor and identical on every member — so the merge is commutative, associative, and
    /// idempotent. A laggard that observed the remove re-enters the ghost vertex together with its
    /// tombstone, converged-correct because the drop gate certifies the remove before dropping.
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
    /// The agreed checkpoint content: the certified projection at the frontier — the visible order filtered
    /// to stable insert-dots, excluding elements whose remove is certified at the frontier.
    /// </param>
    /// <returns>The compacted array; this array is never modified, and is returned unchanged when nothing drops.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="checkpoint"/> is default.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the certified projection does not equal <paramref name="checkpoint"/> element-wise. The
    /// agreed checkpoint is the content line; a mismatch means the (frontier, checkpoint) pair is misaligned.
    /// The check runs before any result is constructed — this fails closed rather than guessing, because a
    /// wrong checkpoint would silently fork the group's generations.
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
    public Rga<TValue> Compact(VectorClock stabilityFrontier, ImmutableArray<TValue> checkpoint)
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

        //Checkpoint check before any result construction: the certified projection — the visible order
        //filtered to stable insert-dots, excluding elements whose remove is certified — must equal the
        //agreed content element-wise, otherwise the (frontier, checkpoint) pair is misaligned.
        List<Dot> order = ComputeOrder();
        var certifiedVisible = new List<TValue>(order.Count);
        foreach(Dot dot in order)
        {
            if(!IsStable(dot, stabilityFrontier))
            {
                continue;
            }

            if(Tombstones.TryGetValue(dot, out FrozenSet<Dot>? removeDots) && HasCertifiedRemove(removeDots, stabilityFrontier))
            {
                continue;
            }

            certifiedVisible.Add(Vertices[dot].Value);
        }

        if(certifiedVisible.Count != checkpoint.Length)
        {
            throw new InvalidOperationException("The stable visible content does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
        }

        for(int i = 0; i < certifiedVisible.Count; i++)
        {
            if(!EqualityComparer<TValue>.Default.Equals(certifiedVisible[i], checkpoint[i]))
            {
                throw new InvalidOperationException("The stable visible content does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
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
    /// <see cref="ToState"/> this shape carries the compaction translation map, so a compacted array survives
    /// a round-trip with its anchor servability intact.
    /// </summary>
    /// <returns>The array's run-length state.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any tombstone carries a dotted remove event. The v1 run shape has no slot for remove-dots,
    /// so a state that certifies its removes cannot round-trip through it — fail closed. A state whose
    /// tombstones are all legacy (or absent) still serializes; run-length persistence of dotted state
    /// resumes with the rga-rle.v2 run shape in the container slice.
    /// </exception>
    /// <remarks>
    /// Consecutive same-replica vertices that form a predecessor chain coalesce into one
    /// <see cref="RgaRunEntry{TValue}"/> and per-replica consecutive tombstones coalesce into one
    /// <see cref="RgaTombstoneSpan"/>. Output order is deterministic: runs by (replica, first counter), spans
    /// by (replica, from counter), translations by (dropped replica, dropped counter).
    /// </remarks>
    public RgaRunState<TValue> ToRunState()
    {
        foreach(FrozenSet<Dot> removeDots in Tombstones.Values)
        {
            if(removeDots.Count > 0)
            {
                throw new InvalidOperationException("This array carries dotted removes that the v1 run-length shape cannot represent; the rga-rle.v2 run shape lands with the container slice.");
            }
        }

        ImmutableArray<RgaRunEntry<TValue>> runs = BuildRuns();
        ImmutableArray<RgaTombstoneSpan> spans = BuildTombstoneSpans();

        var translationList = new List<RgaTranslationEntry>(CompactedPredecessors.Count);
        foreach(KeyValuePair<Dot, Dot> entry in CompactedPredecessors)
        {
            translationList.Add(new RgaTranslationEntry(ToDotState(entry.Key), ToDotState(entry.Value)));
        }

        translationList.Sort(static (left, right) => CompareDotStates(left.Dropped, right.Dropped));

        return new RgaRunState<TValue>(Context.ToState(), runs, spans, [.. translationList]);
    }


    /// <summary>
    /// Reconstructs an array from previously serialized run-length <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The run-length state to reconstruct from.</param>
    /// <returns>The reconstructed array.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown, failing closed, if a run's values are empty, a counter is not positive, a span has
    /// <c>FromCounter &lt; 1</c> or <c>ToCounter &lt; FromCounter</c>, a dot appears in more than one run, a
    /// tombstone target appears in more than one span, an expanded vertex's predecessor is not itself a
    /// vertex (or the predecessor graph contains a cycle), a vertex dot is not covered by the context, a
    /// translation target is not a vertex of the state (a dangling target would leave the dropped dot
    /// unservable), or a translation's dropped dot is a live untombstoned vertex of the state (the
    /// stale-replay W-shape).
    /// </exception>
    /// <remarks>
    /// The v1 run shape carries no remove-dots, so every expanded tombstone is legacy: an empty remove-dot
    /// set the drop gate can never certify, retained forever. A translation's
    /// <see cref="RgaTranslationEntry.Dropped"/> dot may still be present in the vertices when it also carries
    /// a tombstone: a laggard merge can resurrect a dropped tombstone while the map entry remains — the
    /// ghost-plus-witness shape — which is harmless because <see cref="TranslateAnchor"/> consults the
    /// vertices first. A dropped dot present as a live vertex WITHOUT a tombstone is rejected: it is the
    /// permanent witness of a stale pre-remove state, not an honest ghost.
    /// </remarks>
    public static Rga<TValue> FromRunState(RgaRunState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

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

        var tombstones = new Dictionary<Dot, FrozenSet<Dot>>();
        foreach(RgaTombstoneSpan span in state.TombstoneSpans)
        {
            if(span.FromCounter < 1 || span.ToCounter < span.FromCounter)
            {
                throw new ArgumentException("A tombstone span must satisfy 1 <= FromCounter <= ToCounter.", nameof(state));
            }

            ReplicaId replica = ReplicaId.FromSpan(span.Replica.AsSpan());
            for(int counter = span.FromCounter; counter <= span.ToCounter; counter++)
            {
                //Every expanded tombstone is legacy: an empty remove-dot set, retained forever. Overlapping
                //spans that name a target twice are the throw-on-duplicate posture, not a silent dedup.
                if(!tombstones.TryAdd(new Dot(replica, counter), FrozenSet<Dot>.Empty))
                {
                    throw new ArgumentException("A tombstone target appears in more than one span.", nameof(state));
                }
            }
        }

        var compactedPredecessors = new Dictionary<Dot, Dot>(state.Translations.Length);
        foreach(RgaTranslationEntry translation in state.Translations)
        {
            Dot target = FromDotState(translation.Target);
            if(!vertices.ContainsKey(target))
            {
                throw new ArgumentException("A translation target is not a vertex of the state.", nameof(state));
            }

            //A dropped dot that is a live vertex with no tombstone is the permanent witness of a stale
            //pre-remove state; the tombstoned ghost-plus-witness shape stays legal.
            Dot dropped = FromDotState(translation.Dropped);
            if(vertices.ContainsKey(dropped) && !tombstones.ContainsKey(dropped))
            {
                throw new ArgumentException("A translation's dropped dot is a live vertex of the state.", nameof(state));
            }

            compactedPredecessors[dropped] = target;
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


    private ImmutableArray<RgaTombstoneSpan> BuildTombstoneSpans()
    {
        if(Tombstones.Count == 0)
        {
            return [];
        }

        var sorted = new List<Dot>(Tombstones.Keys);
        sorted.Sort(CompareDotsByReplica);

        var spans = new List<RgaTombstoneSpan>();
        ReplicaId spanReplica = sorted[0].Replica;
        int from = sorted[0].Counter;
        int to = sorted[0].Counter;
        for(int i = 1; i < sorted.Count; i++)
        {
            Dot dot = sorted[i];
            if(dot.Replica.Equals(spanReplica) && dot.Counter == to + 1)
            {
                to = dot.Counter;

                continue;
            }

            spans.Add(new RgaTombstoneSpan(ImmutableArray.Create(spanReplica.AsSpan()), from, to));
            spanReplica = dot.Replica;
            from = dot.Counter;
            to = dot.Counter;
        }

        spans.Add(new RgaTombstoneSpan(ImmutableArray.Create(spanReplica.AsSpan()), from, to));

        return [.. spans];
    }


    private static int CompareDotsByReplica(Dot left, Dot right)
    {
        int byReplica = left.Replica.CompareTo(right.Replica);

        return byReplica != 0 ? byReplica : left.Counter.CompareTo(right.Counter);
    }


    private static int CompareDotStates(DotState left, DotState right)
    {
        ReplicaId leftReplica = ReplicaId.FromSpan(left.Replica.AsSpan());
        ReplicaId rightReplica = ReplicaId.FromSpan(right.Replica.AsSpan());
        int byReplica = leftReplica.CompareTo(rightReplica);

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
