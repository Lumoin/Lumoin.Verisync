using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The checkpoint-offset sequence CRDT: collaborative edits over an immutable, consensus-agreed base
/// snapshot. Live inserts anchor either at base positions (<see cref="OffsetAnchor.AtBase(int)"/>) or
/// at other live elements, base elements are removed by offset, and live elements by tombstone — all
/// grow-only, so merge is a union and replicas converge.
/// </summary>
/// <typeparam name="TValue">The element type.</typeparam>
/// <remarks>
/// <para>
/// The base is one <em>compaction generation</em>: an agreed checkpoint snapshot whose positions are
/// stable precisely because consensus froze it. Sequences merge only within a generation —
/// <see cref="Merge(OffsetAnchoredSequence{TValue})"/> fails closed on differing bases, because
/// cross-generation merging requires the anchor translation that arrives with compaction. Keeping a
/// group on the same generation is the composition's job, via the agreed checkpoint and frontier.
/// </para>
/// <para>
/// Ordering: base elements in offset order; immediately after each base position (and after the
/// virtual head) its live subtree, with concurrent siblings in descending (counter, replica) order and
/// insert identities assigned Lamport-style — a fresh insert dominates every sibling it has observed
/// and lands immediately after its anchor, the same intention-preservation rule as the RGA strategy.
/// </para>
/// <para>
/// It is an immutable value; every operation returns a new sequence.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class OffsetAnchoredSequence<TValue>: IEquatable<OffsetAnchoredSequence<TValue>>
{
    private OffsetAnchoredSequence(ImmutableArray<TValue> baseSnapshot, FrozenSet<int> removedBaseOffsets, VectorClock context, FrozenDictionary<Dot, Vertex> vertices, FrozenSet<Dot> tombstones, FrozenDictionary<Dot, OffsetAnchor> compactedDotAnchors, FrozenDictionary<int, int> compactedBaseOffsets)
    {
        Base = baseSnapshot;
        RemovedBaseOffsets = removedBaseOffsets;
        Context = context;
        Vertices = vertices;
        Tombstones = tombstones;
        CompactedDotAnchors = compactedDotAnchors;
        CompactedBaseOffsets = compactedBaseOffsets;
    }


    /// <summary>An empty sequence: an empty base and no live edits — the generation before any checkpoint.</summary>
    public static OffsetAnchoredSequence<TValue> Empty { get; } = new(ImmutableArray<TValue>.Empty, FrozenSet<int>.Empty, VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenSet<Dot>.Empty, FrozenDictionary<Dot, OffsetAnchor>.Empty, FrozenDictionary<int, int>.Empty);


    /// <summary>The agreed base snapshot this generation edits over.</summary>
    public ImmutableArray<TValue> Base { get; }

    private FrozenSet<int> RemovedBaseOffsets { get; }
    private VectorClock Context { get; }
    private FrozenDictionary<Dot, Vertex> Vertices { get; }
    private FrozenSet<Dot> Tombstones { get; }

    //Dots compacted away → their current-generation anchor.
    private FrozenDictionary<Dot, OffsetAnchor> CompactedDotAnchors { get; }

    //Previous-generation base offset → current base offset.
    private FrozenDictionary<int, int> CompactedBaseOffsets { get; }


    /// <summary>
    /// Creates a fresh generation over <paramref name="baseSnapshot"/> with no live edits.
    /// </summary>
    /// <param name="baseSnapshot">The agreed checkpoint snapshot.</param>
    /// <returns>A new sequence.</returns>
    public static OffsetAnchoredSequence<TValue> WithBase(ImmutableArray<TValue> baseSnapshot)
    {
        return new OffsetAnchoredSequence<TValue>(baseSnapshot, FrozenSet<int>.Empty, VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenSet<Dot>.Empty, FrozenDictionary<Dot, OffsetAnchor>.Empty, FrozenDictionary<int, int>.Empty);
    }


    /// <summary>The visible values in sequence order: base elements interleaved with their live subtrees.</summary>
    public IReadOnlyList<TValue> Values
    {
        get
        {
            IReadOnlyList<(OffsetAnchor Anchor, TValue Value)> visible = VisibleElements;
            var result = new List<TValue>(visible.Count);
            foreach((OffsetAnchor _, TValue value) in visible)
            {
                result.Add(value);
            }

            return result;
        }
    }


    /// <summary>
    /// The visible elements in sequence order, each paired with its anchor — what an editor needs to
    /// address the element it is editing relative to.
    /// </summary>
    public IReadOnlyList<(OffsetAnchor Anchor, TValue Value)> VisibleElements
    {
        get
        {
            var result = new List<(OffsetAnchor, TValue)>(Base.Length + Vertices.Count);
            Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor = BuildChildren();
            for(int slot = -1; slot < Base.Length; slot++)
            {
                if(slot >= 0 && !RemovedBaseOffsets.Contains(slot))
                {
                    result.Add((OffsetAnchor.AtBase(slot), Base[slot]));
                }

                AppendSubtree(slot < 0 ? OffsetAnchor.Head : OffsetAnchor.AtBase(slot), childrenByAnchor, result);
            }

            return result;
        }
    }


    /// <summary>
    /// Inserts <paramref name="value"/> at the head of the sequence.
    /// </summary>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new sequence and the anchor of the inserted element.</returns>
    public (OffsetAnchoredSequence<TValue> Result, OffsetAnchor InsertedId) InsertAtHead(TValue value, ReplicaId replica)
    {
        return Insert(OffsetAnchor.Head, value, replica);
    }


    /// <summary>
    /// Inserts <paramref name="value"/> immediately after the element anchored by <paramref name="after"/>.
    /// </summary>
    /// <param name="after">The anchor of the element to insert after.</param>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new sequence and the anchor of the inserted element.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="after"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="after"/> names a base offset outside the base or a live element not in this sequence.</exception>
    public (OffsetAnchoredSequence<TValue> Result, OffsetAnchor InsertedId) InsertAfter(OffsetAnchor after, TValue value, ReplicaId replica)
    {
        ArgumentNullException.ThrowIfNull(after);
        ValidateAnchor(after);

        return Insert(after, value, replica);
    }


    /// <summary>
    /// Removes the element anchored by <paramref name="anchor"/>: a base element is hidden by offset, a
    /// live element by tombstone; both are retained for ordering.
    /// </summary>
    /// <param name="anchor">The anchor of the element to remove.</param>
    /// <returns>The new sequence; this sequence if the element was already removed.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="anchor"/> is the head, a base offset outside the base, or a live element not in this sequence.</exception>
    public OffsetAnchoredSequence<TValue> Remove(OffsetAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if(!anchor.IsLive && anchor.BaseOffset < 0)
        {
            throw new ArgumentException("The head is a position, not an element.", nameof(anchor));
        }

        ValidateAnchor(anchor);

        if(anchor.LiveId is { } liveId)
        {
            if(Tombstones.Contains(liveId))
            {
                return this;
            }

            var tombstones = new HashSet<Dot>(Tombstones) { liveId };

            return new OffsetAnchoredSequence<TValue>(Base, RemovedBaseOffsets, Context, Vertices, tombstones.ToFrozenSet(), CompactedDotAnchors, CompactedBaseOffsets);
        }

        if(RemovedBaseOffsets.Contains(anchor.BaseOffset))
        {
            return this;
        }

        var removed = new HashSet<int>(RemovedBaseOffsets) { anchor.BaseOffset };

        return new OffsetAnchoredSequence<TValue>(Base, removed.ToFrozenSet(), Context, Vertices, Tombstones, CompactedDotAnchors, CompactedBaseOffsets);
    }


    /// <summary>
    /// Merges this sequence with <paramref name="other"/> of the same generation: the union of their
    /// removals, vertices, and tombstones.
    /// </summary>
    /// <param name="other">The sequence to merge with.</param>
    /// <returns>A new sequence; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the bases differ — cross-generation merging requires the anchor translation that arrives with compaction, so a generation mismatch fails closed.</exception>
    public OffsetAnchoredSequence<TValue> Merge(OffsetAnchoredSequence<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if(!BaseEqual(Base, other.Base))
        {
            throw new InvalidOperationException("Cannot merge sequences over different base generations; align the group on the agreed checkpoint first.");
        }

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
        var mergedRemoved = new HashSet<int>(RemovedBaseOffsets);
        mergedRemoved.UnionWith(other.RemovedBaseOffsets);

        //Both translation maps union; the other operand wins a key collision, which only arises for
        //identically derived entries or a harmless ghost-GC asymmetry, so the choice is immaterial.
        var mergedDotAnchors = new Dictionary<Dot, OffsetAnchor>(CompactedDotAnchors.Count + other.CompactedDotAnchors.Count);
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            mergedDotAnchors[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<Dot, OffsetAnchor> entry in other.CompactedDotAnchors)
        {
            mergedDotAnchors[entry.Key] = entry.Value;
        }

        var mergedBaseOffsets = new Dictionary<int, int>(CompactedBaseOffsets.Count + other.CompactedBaseOffsets.Count);
        foreach(KeyValuePair<int, int> entry in CompactedBaseOffsets)
        {
            mergedBaseOffsets[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<int, int> entry in other.CompactedBaseOffsets)
        {
            mergedBaseOffsets[entry.Key] = entry.Value;
        }

        return new OffsetAnchoredSequence<TValue>(Base, mergedRemoved.ToFrozenSet(), Context.Merge(other.Context), mergedVertices.ToFrozenDictionary(), mergedTombstones.ToFrozenSet(), mergedDotAnchors.ToFrozenDictionary(), mergedBaseOffsets.ToFrozenDictionary());
    }


    /// <summary>
    /// Compacts the waterline: stable visible vertices collapse into new base entries at their
    /// linearization positions, stable removed state is reclaimed, and the result edits over the agreed
    /// <paramref name="checkpoint"/> as its new generation. Visible values are unchanged.
    /// </summary>
    /// <param name="stabilityFrontier">
    /// The group stability frontier — see <see cref="StabilityFrontier"/>. A dot is stable when the
    /// frontier's counter for its replica is at least the dot's counter; stable state can never again be
    /// referenced by any member, so it is safe to collapse.
    /// </param>
    /// <param name="checkpoint">The agreed checkpoint content the compacted region collapses into.</param>
    /// <returns>The compacted sequence; this sequence is never modified, and is returned unchanged when nothing converts, nothing drops, and the base length is unchanged.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="checkpoint"/> is default.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the derived stable visible content does not equal <paramref name="checkpoint"/>
    /// element-wise. The agreed checkpoint is the content line; a mismatch means the (frontier,
    /// checkpoint) pair is misaligned. The check runs before any result is constructed — this fails
    /// closed rather than guessing, because a wrong base would silently fork the group's generations.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Every old base entry is carried into the new base, removed ones included — their new offsets join
    /// the new removed set. Dropping a removed base entry would be replica-dependent through its
    /// above-frontier children, breaking base equality across members and reordering re-anchored
    /// children into foreign sibling sets, so the entry is always kept.
    /// </para>
    /// <para>
    /// A surviving vertex under a surviving parent keeps its recorded anchor; every other survivor is
    /// re-anchored at its <em>gap anchor</em> — the most recently materialized new-base entry at the
    /// vertex's own position in the walk. The gap anchor is replica-independent (converted entries hang
    /// only under stable ancestry, because stability is causally closed downward through anchor chains,
    /// and surviving subtrees contribute no entries), and it preserves the visible order: an insert's
    /// counter always exceeds its anchor's, so a survivor nested under a converted vertex outranks every
    /// survivor that sorted after that vertex once both share the gap anchor's sibling set.
    /// </para>
    /// <para>
    /// A stable tombstoned vertex is kept as a ghost exactly when its subtree still reaches an unstable
    /// vertex through tombstoned stable vertices; otherwise it is dropped and translates to its gap
    /// anchor. <see cref="Context"/> is unchanged — compaction reclaims storage, not causal knowledge.
    /// </para>
    /// </remarks>
    public OffsetAnchoredSequence<TValue> Compact(VectorClock stabilityFrontier, ImmutableArray<TValue> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);
        if(checkpoint.IsDefault)
        {
            throw new ArgumentException("The checkpoint content is required.", nameof(checkpoint));
        }

        //Classify every vertex against the frontier. A vertex is "retained" (survives in the new vertex
        //set) when it is unstable, or when it is a stable tombstone whose subtree still reaches an
        //unstable vertex through tombstoned stable vertices. Stable visible vertices convert to base.
        Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor = BuildChildren();
        var retention = new Dictionary<Dot, bool>(Vertices.Count);
        foreach(Dot dot in Vertices.Keys)
        {
            ComputeRetention(dot, stabilityFrontier, childrenByAnchor, retention);
        }

        //Walk the full linearization (every base entry, removed or not, then its subtree depth-first in
        //the canonical sibling order) to build the new base, the offset/dot maps, and the survivors'
        //gap anchors.
        var newBase = new List<TValue>(Base.Length + Vertices.Count);
        var newRemoved = new HashSet<int>();
        var oldToNew = new Dictionary<int, int>(Base.Length);
        var dotAnchor = new Dictionary<Dot, OffsetAnchor>();
        var retainedAnchors = new Dictionary<Dot, OffsetAnchor>();
        int conversions = 0;
        for(int slot = -1; slot < Base.Length; slot++)
        {
            if(slot >= 0)
            {
                int newOffset = newBase.Count;
                newBase.Add(Base[slot]);
                oldToNew[slot] = newOffset;
                if(RemovedBaseOffsets.Contains(slot))
                {
                    newRemoved.Add(newOffset);
                }
            }

            OffsetAnchor anchor = slot < 0 ? OffsetAnchor.Head : OffsetAnchor.AtBase(slot);
            WalkSubtree(anchor, false, childrenByAnchor, retention, newBase, dotAnchor, retainedAnchors, ref conversions);
        }

        //Checkpoint check: the new base minus its removed offsets, in order, must equal the agreed
        //content. This runs before any result is built so a misaligned pair fails closed.
        var stableVisible = new List<TValue>(newBase.Count);
        for(int offset = 0; offset < newBase.Count; offset++)
        {
            if(!newRemoved.Contains(offset))
            {
                stableVisible.Add(newBase[offset]);
            }
        }

        if(stableVisible.Count != checkpoint.Length)
        {
            throw new InvalidOperationException("The derived stable visible content does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
        }

        for(int i = 0; i < stableVisible.Count; i++)
        {
            if(!EqualityComparer<TValue>.Default.Equals(stableVisible[i], checkpoint[i]))
            {
                throw new InvalidOperationException("The derived stable visible content does not match the agreed checkpoint; the (frontier, checkpoint) pair is misaligned.");
            }
        }

        int drops = 0;
        foreach(KeyValuePair<Dot, bool> entry in retention)
        {
            if(!entry.Value && Tombstones.Contains(entry.Key))
            {
                drops++;
            }
        }

        //No-op shortcut: nothing converts, nothing drops, and the base length is unchanged, so repeat
        //compaction at the same (frontier, checkpoint) is trivially idempotent.
        if(conversions == 0 && drops == 0 && newBase.Count == Base.Length)
        {
            return this;
        }

        //Re-anchor every retained vertex at the anchor the walk recorded for it: its own recorded anchor
        //under a retained parent, its gap anchor otherwise.
        var newVertices = new Dictionary<Dot, Vertex>(retention.Count);
        var newTombstones = new HashSet<Dot>();
        foreach(KeyValuePair<Dot, bool> entry in retention)
        {
            if(!entry.Value)
            {
                continue;
            }

            Vertex vertex = Vertices[entry.Key];
            newVertices[entry.Key] = new Vertex(retainedAnchors[entry.Key], vertex.Value);
            if(Tombstones.Contains(entry.Key))
            {
                newTombstones.Add(entry.Key);
            }
        }

        //Translation maps. Prior dot-map entries compose through this compaction (the anchors they point
        //at have themselves moved this generation); converted and dropped dots got fresh entries during
        //the walk, which win any collision. The base-offset map is REPLACED by this generation's
        //oldToNew: a previous-generation base anchor can no longer arrive once the line passed the
        //previous checkpoint, so composing the old map would only retain unreachable entries.
        var newDotAnchors = new Dictionary<Dot, OffsetAnchor>(CompactedDotAnchors.Count + dotAnchor.Count);
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            newDotAnchors[entry.Key] = ComposeThroughCompaction(entry.Value, oldToNew, dotAnchor, retention);
        }

        foreach(KeyValuePair<Dot, OffsetAnchor> entry in dotAnchor)
        {
            newDotAnchors[entry.Key] = entry.Value;
        }

        return new OffsetAnchoredSequence<TValue>(newBase.ToImmutableArray(), newRemoved.ToFrozenSet(), Context, newVertices.ToFrozenDictionary(), newTombstones.ToFrozenSet(), newDotAnchors.ToFrozenDictionary(), oldToNew.ToFrozenDictionary());
    }


    /// <summary>
    /// Translates an anchor that may name a previous compaction generation into this generation's
    /// equivalent, or <see langword="null"/> when the anchor is unservable here.
    /// </summary>
    /// <param name="anchor">The possibly stale anchor.</param>
    /// <returns>The current anchor — the input itself when no translation is needed — or <see langword="null"/> when unservable.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchor"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A translated anchor at a dropped tombstone resolves to the gap anchor of its old position — the
    /// nearest preceding checkpoint entry — which can place a later insert ahead of surviving subtrees
    /// the tombstone's position would have followed. Convergence is unaffected — every member translates
    /// through the same replica-independent map — and intention degrades only for anchors at elements
    /// that were already removed.
    /// </remarks>
    public OffsetAnchor? TranslateAnchor(OffsetAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);

        //Never compacted: the maps are empty, so the anchor is the identity when it is servable in this
        //sequence and null otherwise.
        if(CompactedDotAnchors.Count == 0 && CompactedBaseOffsets.Count == 0)
        {
            if(!anchor.IsLive && anchor.BaseOffset < 0)
            {
                return OffsetAnchor.Head;
            }

            if(anchor.LiveId is { } liveDot)
            {
                return Vertices.ContainsKey(liveDot) ? anchor : null;
            }

            return anchor.BaseOffset < Base.Length ? anchor : null;
        }

        if(!anchor.IsLive && anchor.BaseOffset < 0)
        {
            return OffsetAnchor.Head;
        }

        if(anchor.LiveId is { } dot)
        {
            if(Vertices.ContainsKey(dot))
            {
                return anchor;
            }

            return CompactedDotAnchors.TryGetValue(dot, out OffsetAnchor? translated) ? translated : null;
        }

        return CompactedBaseOffsets.TryGetValue(anchor.BaseOffset, out int newOffset) ? OffsetAnchor.AtBase(newOffset) : null;
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] OffsetAnchoredSequence<TValue>? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        if(!BaseEqual(Base, other.Base)
            || !RemovedBaseOffsets.SetEquals(other.RemovedBaseOffsets)
            || !Context.Equals(other.Context)
            || Vertices.Count != other.Vertices.Count
            || !Tombstones.SetEquals(other.Tombstones)
            || CompactedDotAnchors.Count != other.CompactedDotAnchors.Count
            || CompactedBaseOffsets.Count != other.CompactedBaseOffsets.Count)
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

        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            if(!other.CompactedDotAnchors.TryGetValue(entry.Key, out OffsetAnchor? otherAnchor) || !entry.Value.Equals(otherAnchor))
            {
                return false;
            }
        }

        foreach(KeyValuePair<int, int> entry in CompactedBaseOffsets)
        {
            if(!other.CompactedBaseOffsets.TryGetValue(entry.Key, out int otherOffset) || entry.Value != otherOffset)
            {
                return false;
            }
        }

        return true;
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is OffsetAnchoredSequence<TValue> other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int verticesHash = 0;
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            verticesHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        int dotAnchorsHash = 0;
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            dotAnchorsHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        int baseOffsetsHash = 0;
        foreach(KeyValuePair<int, int> entry in CompactedBaseOffsets)
        {
            baseOffsetsHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return HashCode.Combine(Base.Length, RemovedBaseOffsets.Count, Context, verticesHash, Tombstones.Count, dotAnchorsHash, baseOffsetsHash);
    }


    private (OffsetAnchoredSequence<TValue> Result, OffsetAnchor InsertedId) Insert(OffsetAnchor after, TValue value, ReplicaId replica)
    {
        //The new identity must dominate every observed identity so the insert lands immediately after
        //its anchor, ahead of older siblings — the same rule as the RGA strategy.
        VectorClock advanced = Context.IncrementPastAll(replica);
        var id = new Dot(replica, advanced[replica]);

        var updated = new Dictionary<Dot, Vertex>(Vertices.Count + 1);
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            updated[entry.Key] = entry.Value;
        }

        updated[id] = new Vertex(after, value);

        return (new OffsetAnchoredSequence<TValue>(Base, RemovedBaseOffsets, advanced, updated.ToFrozenDictionary(), Tombstones, CompactedDotAnchors, CompactedBaseOffsets), OffsetAnchor.AtLive(id));
    }


    private void ValidateAnchor(OffsetAnchor anchor)
    {
        if(anchor.LiveId is { } liveId)
        {
            if(!Vertices.ContainsKey(liveId))
            {
                throw new ArgumentException("The live anchor is not an element of this sequence.", nameof(anchor));
            }

            return;
        }

        if(anchor.BaseOffset >= Base.Length)
        {
            throw new ArgumentException($"The base offset {anchor.BaseOffset} is outside the base of {Base.Length} element(s).", nameof(anchor));
        }
    }


    private Dictionary<OffsetAnchor, List<Dot>> BuildChildren()
    {
        var childrenByAnchor = new Dictionary<OffsetAnchor, List<Dot>>();
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            if(childrenByAnchor.TryGetValue(entry.Value.Anchor, out List<Dot>? siblings))
            {
                siblings.Add(entry.Key);
            }
            else
            {
                childrenByAnchor[entry.Value.Anchor] = [entry.Key];
            }
        }

        foreach(List<Dot> siblings in childrenByAnchor.Values)
        {
            siblings.Sort(static (left, right) => CompareDots(right, left));
        }

        return childrenByAnchor;
    }


    private void AppendSubtree(OffsetAnchor anchor, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, List<(OffsetAnchor, TValue)> result)
    {
        if(!childrenByAnchor.TryGetValue(anchor, out List<Dot>? children))
        {
            return;
        }

        foreach(Dot child in children)
        {
            if(!Tombstones.Contains(child))
            {
                result.Add((OffsetAnchor.AtLive(child), Vertices[child].Value));
            }

            AppendSubtree(OffsetAnchor.AtLive(child), childrenByAnchor, result);
        }
    }


    //A vertex is retained when it is unstable, or a stable tombstone whose subtree still reaches an
    //unstable vertex through tombstoned stable vertices. Stable visible vertices convert, so they are
    //not retained as vertices and do not keep a ghost ancestor alive. Memoized over the dot.
    private bool ComputeRetention(Dot dot, VectorClock frontier, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, Dictionary<Dot, bool> retention)
    {
        if(retention.TryGetValue(dot, out bool cached))
        {
            return cached;
        }

        if(!IsStable(dot, frontier))
        {
            retention[dot] = true;

            return true;
        }

        if(!Tombstones.Contains(dot))
        {
            retention[dot] = false;

            return false;
        }

        bool retained = false;
        if(childrenByAnchor.TryGetValue(OffsetAnchor.AtLive(dot), out List<Dot>? children))
        {
            foreach(Dot child in children)
            {
                if(ComputeRetention(child, frontier, childrenByAnchor, retention))
                {
                    retained = true;
                }
            }
        }

        retention[dot] = retained;

        return retained;
    }


    //Walks the live subtree under anchor in canonical sibling order. A converting vertex (stable,
    //visible) is appended to the new base at its depth-first position; a retained vertex under a
    //retained parent keeps its recorded anchor, while every other retained vertex is re-anchored at the
    //gap anchor of its position; a dropped tombstone records its gap anchor as its translation. The gap
    //anchor at any moment is the most recently appended new-base entry, so it never needs separate
    //bookkeeping.
    private void WalkSubtree(OffsetAnchor anchor, bool parentRetained, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, Dictionary<Dot, bool> retention, List<TValue> newBase, Dictionary<Dot, OffsetAnchor> dotAnchor, Dictionary<Dot, OffsetAnchor> retainedAnchors, ref int conversions)
    {
        if(!childrenByAnchor.TryGetValue(anchor, out List<Dot>? children))
        {
            return;
        }

        foreach(Dot child in children)
        {
            bool retained = retention[child];
            if(retained)
            {
                retainedAnchors[child] = parentRetained ? Vertices[child].Anchor : GapAnchor(newBase);
            }
            else if(!Tombstones.Contains(child))
            {
                int newOffset = newBase.Count;
                newBase.Add(Vertices[child].Value);
                dotAnchor[child] = OffsetAnchor.AtBase(newOffset);
                conversions++;
            }
            else
            {
                dotAnchor[child] = GapAnchor(newBase);
            }

            WalkSubtree(OffsetAnchor.AtLive(child), retained, childrenByAnchor, retention, newBase, dotAnchor, retainedAnchors, ref conversions);
        }
    }


    //Composes a prior translation target into this generation: Head stays Head, a base offset shifts
    //through oldToNew, and a live anchor stays put when its vertex is retained and otherwise resolves to
    //the fresh entry the walk recorded for it.
    private static OffsetAnchor ComposeThroughCompaction(OffsetAnchor anchor, Dictionary<int, int> oldToNew, Dictionary<Dot, OffsetAnchor> dotAnchor, Dictionary<Dot, bool> retention)
    {
        if(anchor.LiveId is { } dot)
        {
            if(retention.TryGetValue(dot, out bool retained) && retained)
            {
                return anchor;
            }

            return dotAnchor[dot];
        }

        if(anchor.BaseOffset < 0)
        {
            return OffsetAnchor.Head;
        }

        return OffsetAnchor.AtBase(oldToNew[anchor.BaseOffset]);
    }


    private static OffsetAnchor GapAnchor(List<TValue> newBase)
    {
        return newBase.Count == 0 ? OffsetAnchor.Head : OffsetAnchor.AtBase(newBase.Count - 1);
    }


    private static bool IsStable(Dot dot, VectorClock frontier)
    {
        return frontier[dot.Replica] >= dot.Counter;
    }


    private static bool BaseEqual(ImmutableArray<TValue> left, ImmutableArray<TValue> right)
    {
        if(left.Length != right.Length)
        {
            return false;
        }

        for(int i = 0; i < left.Length; i++)
        {
            if(!EqualityComparer<TValue>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }


    private static int CompareDots(Dot left, Dot right)
    {
        int byCounter = left.Counter.CompareTo(right.Counter);

        return byCounter != 0 ? byCounter : left.Replica.CompareTo(right.Replica);
    }


    private string DebuggerDisplay => $"OffsetAnchoredSequence: base {Base.Length}, {Vertices.Count} live, {Tombstones.Count} tombstoned";


    private sealed record Vertex(OffsetAnchor Anchor, TValue Value);
}
