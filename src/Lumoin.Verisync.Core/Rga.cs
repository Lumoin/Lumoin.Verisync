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
/// union and the derived order is identical on all replicas.
/// </para>
/// <para>
/// It is an immutable value; every operation returns a new array. <see cref="InsertAfter(Dot, TValue, ReplicaId)"/>
/// returns the new array together with the identity assigned to the inserted element, which the caller
/// uses as the predecessor of a following insert or as the target of a <see cref="Remove(Dot)"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class Rga<TValue>: IEquatable<Rga<TValue>>
{
    private VectorClock Context { get; }
    private FrozenDictionary<Dot, Vertex> Vertices { get; }
    private FrozenSet<Dot> Tombstones { get; }

    //Dropped dots map to their nearest retained ancestor at drop time, so anchors expressed against a dot
    //that compaction removed can still be served. The rga.v1 strategy never populates this — only the
    //compactable rga-rle.v1 strategy does — so nothing changes for the existing strategy.
    private FrozenDictionary<Dot, Dot> CompactedPredecessors { get; }


    private Rga(VectorClock context, FrozenDictionary<Dot, Vertex> vertices, FrozenSet<Dot> tombstones, FrozenDictionary<Dot, Dot> compactedPredecessors)
    {
        Context = context;
        Vertices = vertices;
        Tombstones = tombstones;
        CompactedPredecessors = compactedPredecessors;
    }


    /// <summary>An empty array.</summary>
    public static Rga<TValue> Empty { get; } = new(VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenSet<Dot>.Empty, FrozenDictionary<Dot, Dot>.Empty);


    /// <summary>The visible (non-tombstoned) values in sequence order.</summary>
    public IReadOnlyList<TValue> Values
    {
        get
        {
            List<Dot> order = ComputeOrder();
            var result = new List<TValue>(order.Count);
            foreach(Dot id in order)
            {
                if(!Tombstones.Contains(id))
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
                if(!Tombstones.Contains(entry.Key))
                {
                    count++;
                }
            }

            return count;
        }
    }


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
    /// Returns a new array with the element identified by <paramref name="id"/> tombstoned. The element
    /// is retained for ordering but no longer visible.
    /// </summary>
    /// <param name="id">The identity of the element to remove.</param>
    /// <returns>A new <see cref="Rga{TValue}"/>, or this array if the element was already tombstoned.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="id"/> is <see langword="null"/>.</exception>
    public Rga<TValue> Remove(Dot id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if(Tombstones.Contains(id))
        {
            return this;
        }

        var updated = new HashSet<Dot>(Tombstones) { id };

        return new Rga<TValue>(Context, Vertices, updated.ToFrozenSet(), CompactedPredecessors);
    }


    /// <summary>
    /// Returns the merge of this array and <paramref name="other"/>: the union of their vertices and
    /// tombstones.
    /// </summary>
    /// <param name="other">The array to merge with.</param>
    /// <returns>A new <see cref="Rga{TValue}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The compaction translation map is unioned too — <paramref name="other"/> wins on a key collision, but
    /// collisions only arise for identically derived entries (a dropped dot resolves to the same nearest
    /// retained ancestor on every member), so the choice is immaterial. Merging an uncompacted laggard back
    /// into a compacted state unions the dropped tombstone straight back in, which is converged-correct: a
    /// later compaction at the same frontier drops it again, and lookups always check the vertices first.
    /// </remarks>
    public Rga<TValue> Merge(Rga<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var mergedVertices = new Dictionary<Dot, Vertex>(Vertices.Count + other.Vertices.Count);
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            mergedVertices[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<Dot, Vertex> entry in other.Vertices)
        {
            mergedVertices[entry.Key] = entry.Value;
        }

        var mergedTombstones = new HashSet<Dot>(Tombstones);
        mergedTombstones.UnionWith(other.Tombstones);

        var mergedPredecessors = new Dictionary<Dot, Dot>(CompactedPredecessors.Count + other.CompactedPredecessors.Count);
        foreach(KeyValuePair<Dot, Dot> entry in CompactedPredecessors)
        {
            mergedPredecessors[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<Dot, Dot> entry in other.CompactedPredecessors)
        {
            mergedPredecessors[entry.Key] = entry.Value;
        }

        return new Rga<TValue>(Context.Merge(other.Context), mergedVertices.ToFrozenDictionary(), mergedTombstones.ToFrozenSet(), mergedPredecessors.ToFrozenDictionary());
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

        ImmutableArray<DotState>.Builder tombstoneBuilder = ImmutableArray.CreateBuilder<DotState>(Tombstones.Count);
        foreach(Dot tombstone in Tombstones)
        {
            tombstoneBuilder.Add(new DotState(ImmutableArray.Create(tombstone.Replica.AsSpan()), tombstone.Counter));
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
    /// Thrown if a vertex's predecessor is not itself a vertex, or if the predecessor graph contains a
    /// cycle. Every non-head insert records an existing predecessor and predecessors only ever point at
    /// earlier elements, so a missing predecessor or a cycle never occurs in an honest history; admitting one
    /// would silently drop the orphaned or cyclic vertices from <see cref="Values"/> (the order traversal
    /// never reaches them) while <see cref="Count"/> still counts them, leaving the two inconsistent.
    /// </exception>
    /// <remarks>
    /// A tombstone referencing a dot that is not a vertex is accepted and harmless: a remove can be
    /// serialized and merged separately from the insert it tombstones, so its target may legitimately be
    /// absent here. Such a tombstone simply matches no vertex and affects neither <see cref="Values"/> nor
    /// <see cref="Count"/>.
    /// </remarks>
    public static Rga<TValue> FromState(RgaState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        VectorClock context = VectorClock.FromState(state.Context);
        var vertices = new Dictionary<Dot, Vertex>(state.Vertices.Length);
        foreach(RgaVertexEntry<TValue> entry in state.Vertices)
        {
            var id = new Dot(ReplicaId.FromSpan(entry.Id.Replica.AsSpan()), entry.Id.Counter);
            Dot? predecessor = entry.Predecessor is null
                ? null
                : new Dot(ReplicaId.FromSpan(entry.Predecessor.Replica.AsSpan()), entry.Predecessor.Counter);
            vertices[id] = new Vertex(predecessor, entry.Value);
        }

        ValidatePredecessors(vertices);

        var tombstones = new HashSet<Dot>(state.Tombstones.Length);
        foreach(DotState tombstone in state.Tombstones)
        {
            tombstones.Add(new Dot(ReplicaId.FromSpan(tombstone.Replica.AsSpan()), tombstone.Counter));
        }

        return new Rga<TValue>(context, vertices.ToFrozenDictionary(), tombstones.ToFrozenSet(), FrozenDictionary<Dot, Dot>.Empty);
    }


    /// <summary>
    /// Compacts the waterline: stable tombstones that have no retained descendant collapse out of the
    /// vertex and tombstone sets, and the dots they leave behind are served from a translation map. Visible
    /// vertices never move, so the visible order — and every visible value — is unchanged.
    /// </summary>
    /// <param name="stabilityFrontier">
    /// The group stability frontier. A dot is stable when the frontier's counter for its replica is at least
    /// the dot's counter; stable state can never again be referenced by any member, so it is safe to collapse.
    /// </param>
    /// <param name="checkpoint">The agreed checkpoint content: the visible order filtered to stable dots.</param>
    /// <returns>The compacted array; this array is never modified, and is returned unchanged when nothing drops.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="checkpoint"/> is default.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the stable visible values do not equal <paramref name="checkpoint"/> element-wise. The
    /// agreed checkpoint is the content line; a mismatch means the (frontier, checkpoint) pair is misaligned.
    /// The check runs before any result is constructed — this fails closed rather than guessing, because a
    /// wrong checkpoint would silently fork the group's generations.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A vertex is <em>retained</em> when it is unstable, or visible, or head-anchored (a head-anchored
    /// tombstone has no element to translate to, so it can never drop), or any of its children is retained,
    /// recursively. A dropped vertex therefore has only dropped children and no retained vertex ever has a
    /// dropped predecessor — the retained set's predecessor closure is intact and no vertex is ever
    /// re-parented. <see cref="Context"/> is unchanged: compaction reclaims storage, not causal knowledge.
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

        //Retention is memoized over the dot: retained iff unstable, visible, head-anchored, or any child is
        //retained (recursively). Tombstone entries with no corresponding vertex are outside retention
        //entirely and are carried unchanged; under state-based merging a stable orphan tombstone cannot occur.
        Dictionary<Dot, List<Dot>> childrenByParent = BuildChildrenByParent();
        var retention = new Dictionary<Dot, bool>(Vertices.Count);
        foreach(Dot dot in Vertices.Keys)
        {
            ComputeRetention(dot, stabilityFrontier, childrenByParent, retention);
        }

        //Checkpoint check before any result construction: the visible order filtered to stable dots must
        //equal the agreed content element-wise, otherwise the (frontier, checkpoint) pair is misaligned.
        List<Dot> order = ComputeOrder();
        var stableVisible = new List<TValue>(order.Count);
        foreach(Dot dot in order)
        {
            if(!Tombstones.Contains(dot) && IsStable(dot, stabilityFrontier))
            {
                stableVisible.Add(Vertices[dot].Value);
            }
        }

        if(stableVisible.Count != checkpoint.Length)
        {
            throw new InvalidOperationException("The stable visible content does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
        }

        for(int i = 0; i < stableVisible.Count; i++)
        {
            if(!EqualityComparer<TValue>.Default.Equals(stableVisible[i], checkpoint[i]))
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

        var newTombstones = new HashSet<Dot>(Tombstones.Count);
        foreach(Dot tombstone in Tombstones)
        {
            //Orphan tombstones (no corresponding vertex) are untouched; a tombstone of a retained vertex
            //stays; a tombstone of a dropped vertex leaves with the vertex.
            if(!Vertices.ContainsKey(tombstone) || newVertices.ContainsKey(tombstone))
            {
                newTombstones.Add(tombstone);
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

        return new Rga<TValue>(Context, newVertices.ToFrozenDictionary(), newTombstones.ToFrozenSet(), newPredecessors.ToFrozenDictionary());
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
    /// <remarks>
    /// Consecutive same-replica vertices that form a predecessor chain coalesce into one
    /// <see cref="RgaRunEntry{TValue}"/> and per-replica consecutive tombstones coalesce into one
    /// <see cref="RgaTombstoneSpan"/>. Output order is deterministic: runs by (replica, first counter), spans
    /// by (replica, from counter), translations by (dropped replica, dropped counter).
    /// </remarks>
    public RgaRunState<TValue> ToRunState()
    {
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
    /// Thrown if a run's values are empty, a counter is not positive, a span has <c>FromCounter &lt; 1</c> or
    /// <c>ToCounter &lt; FromCounter</c>, a dot appears in more than one run, an expanded vertex's predecessor
    /// is not itself a vertex (or the predecessor graph contains a cycle), or a translation target is not a
    /// vertex of the state. A dangling target would leave the dropped dot unservable, so it fails closed.
    /// </exception>
    /// <remarks>
    /// A translation's <see cref="RgaTranslationEntry.Dropped"/> dot is not required to be absent from the
    /// vertices: a laggard merge can resurrect a dropped tombstone while the map entry remains, which is
    /// harmless because <see cref="TranslateAnchor"/> consults the vertices first.
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

        var tombstones = new HashSet<Dot>();
        foreach(RgaTombstoneSpan span in state.TombstoneSpans)
        {
            if(span.FromCounter < 1 || span.ToCounter < span.FromCounter)
            {
                throw new ArgumentException("A tombstone span must satisfy 1 <= FromCounter <= ToCounter.", nameof(state));
            }

            ReplicaId replica = ReplicaId.FromSpan(span.Replica.AsSpan());
            for(int counter = span.FromCounter; counter <= span.ToCounter; counter++)
            {
                tombstones.Add(new Dot(replica, counter));
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

            compactedPredecessors[FromDotState(translation.Dropped)] = target;
        }

        return new Rga<TValue>(context, vertices.ToFrozenDictionary(), tombstones.ToFrozenSet(), compactedPredecessors.ToFrozenDictionary());
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


    //A vertex is retained when it is unstable, visible, head-anchored, or has any retained child
    //(recursively). Memoized over the dot so each vertex's subtree is classified at most once.
    private bool ComputeRetention(Dot dot, VectorClock frontier, Dictionary<Dot, List<Dot>> childrenByParent, Dictionary<Dot, bool> retention)
    {
        if(retention.TryGetValue(dot, out bool cached))
        {
            return cached;
        }

        if(!IsStable(dot, frontier) || !Tombstones.Contains(dot) || Vertices[dot].Predecessor is null)
        {
            retention[dot] = true;

            return true;
        }

        bool retained = false;
        if(childrenByParent.TryGetValue(dot, out List<Dot>? children))
        {
            foreach(Dot child in children)
            {
                if(ComputeRetention(child, frontier, childrenByParent, retention))
                {
                    retained = true;
                }
            }
        }

        retention[dot] = retained;

        return retained;
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

        var sorted = new List<Dot>(Tombstones);
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

        if(!Context.Equals(other.Context) || Vertices.Count != other.Vertices.Count || !Tombstones.SetEquals(other.Tombstones) || CompactedPredecessors.Count != other.CompactedPredecessors.Count)
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
        foreach(Dot tombstone in Tombstones)
        {
            tombstonesHash ^= tombstone.GetHashCode();
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


    private string DebuggerDisplay => $"Rga: {Count} visible, {Tombstones.Count} tombstoned";


    private sealed record Vertex(Dot? Predecessor, TValue Value);
}
