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
/// stable precisely because consensus froze it. Sequences merge only within a generation, and the
/// generation is identified by its <em>base frontier</em> — the consensus-agreed stability frontier the
/// base was last materialized at, stamped only by base-changing compactions. Reclamation lets base
/// value arrays cycle, so value equality alone cannot identify a generation;
/// <see cref="Merge(OffsetAnchoredSequence{TValue})"/> fails closed on differing identities, and value
/// equality remains behind the fence as an integrity assertion. Keeping a group on the same generation
/// is the composition's job, via the agreed checkpoint and frontier.
/// </para>
/// <para>
/// Ordering: base elements in offset order; immediately after each base position (and after the
/// virtual head) its live subtree, with concurrent siblings in descending (counter, replica) order and
/// insert identities assigned Lamport-style — a fresh insert dominates every sibling it has observed
/// and lands immediately after its anchor, the same intention-preservation rule as the RGA strategy.
/// </para>
/// <para>
/// Both removal kinds are dotted: a live tombstone and a base-offset removal each mint a remove event
/// on the shared counter plane, so the stability frontier certifies that a remove was observed
/// group-wide before any reclamation acts on it. At compaction the retention taxonomy is four-way: an
/// unstable vertex is retained; a stable visible vertex converts into the new base; a stable
/// tombstoned vertex whose remove is uncertified converts as <em>pending-removed</em> — always, even
/// with retained descendants — carrying its remove-dots onto its new base offset, so members that
/// disagree on an uncertified remove still materialize the identical base; a stable tombstoned vertex
/// whose remove is certified is retained as a ghost exactly when a child is retained, and dropped
/// otherwise. A removed base entry is never reclaimed by this compaction: it stays as the ordering
/// placeholder for its subtree, hidden, its remove-dots riding forward — certified removal makes it
/// RECLAIMABLE by a follow-on that agrees the reclamation set through consensus, because a
/// frontier-local reclamation cannot be simultaneously frontier-pure and order-preserving.
/// </para>
/// <para>
/// <see cref="Compact"/> requires an insert-quiescent frontier — one that covers every vertex's
/// insert-dot — and fails closed otherwise. Because base positions linearize after the live head
/// region, converting an element into the base across a retained region would reorder the visible
/// sequence, and members that compacted along different frontier paths could reach divergent bases
/// under one generation identity; requiring quiescence forecloses both. With no unstable vertex nothing
/// is instability-retained, so the ghost arm of the taxonomy is unreachable at compaction and kept only
/// defensively for the reclamation follow-on. <see cref="CertifiedProjection"/> carries no such
/// restriction: it is a pure read at any frontier, which digests, verification, and adoption rely on.
/// </para>
/// <para>
/// Re-anchoring is replica-agreed: every member re-anchors a given surviving child to the identical
/// anchor, because a gap anchor is a function of the agreed base, the agreed reclamation set, and
/// frontier-stable converting vertices only — never of which above-frontier children a member holds —
/// so no member can orphan a survivor another member keeps.
/// </para>
/// <para>
/// It is an immutable value; every operation returns a new sequence.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class OffsetAnchoredSequence<TValue>: IEquatable<OffsetAnchoredSequence<TValue>>
{
    private OffsetAnchoredSequence(ImmutableArray<TValue> baseSnapshot, VectorClock baseFrontier, int baseGeneration, FrozenDictionary<int, FrozenSet<Dot>> removedBaseOffsets, VectorClock context, FrozenDictionary<Dot, Vertex> vertices, FrozenDictionary<Dot, FrozenSet<Dot>> tombstones, FrozenDictionary<Dot, OffsetAnchor> compactedDotAnchors, FrozenDictionary<int, OffsetAnchor> compactedBaseOffsets)
    {
        Base = baseSnapshot;
        BaseFrontier = baseFrontier;
        BaseGeneration = baseGeneration;
        RemovedBaseOffsets = removedBaseOffsets;
        Context = context;
        Vertices = vertices;
        Tombstones = tombstones;
        CompactedDotAnchors = compactedDotAnchors;
        CompactedBaseOffsets = compactedBaseOffsets;
    }


    /// <summary>An empty sequence: an empty base and no live edits — the generation before any checkpoint.</summary>
    public static OffsetAnchoredSequence<TValue> Empty { get; } = new(ImmutableArray<TValue>.Empty, VectorClock.Empty, 0, FrozenDictionary<int, FrozenSet<Dot>>.Empty, VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenDictionary<Dot, FrozenSet<Dot>>.Empty, FrozenDictionary<Dot, OffsetAnchor>.Empty, FrozenDictionary<int, OffsetAnchor>.Empty);


    /// <summary>The agreed base snapshot this generation edits over.</summary>
    public ImmutableArray<TValue> Base { get; }

    //The generation identity: the consensus-agreed stability frontier the base was last materialized at.
    //Only a base-changing compaction advances it; the genesis generation carries the empty clock.
    private VectorClock BaseFrontier { get; }

    //The base-generation ordinal: the count of base-changing compactions this generation descends from,
    //stamped and inherited only together with the base frontier so honest members at one frontier always
    //agree on it. Genesis carries zero, exactly when the base frontier is empty; only a base-changing
    //compaction advances it, and it addresses base anchors across the one-generation translation window.
    private int BaseGeneration { get; }

    //Removed base offset → the dotted remove events that hide it. An empty set is a legacy (v1-loaded)
    //removal: hidden but uncertifiable, so the entry is retained forever.
    private FrozenDictionary<int, FrozenSet<Dot>> RemovedBaseOffsets { get; }

    private VectorClock Context { get; }
    private FrozenDictionary<Dot, Vertex> Vertices { get; }

    //Tombstoned live target → the dotted remove events that hide it. An empty set is a legacy (v1-loaded)
    //tombstone: hidden but uncertifiable, so the vertex is retained forever.
    private FrozenDictionary<Dot, FrozenSet<Dot>> Tombstones { get; }

    //Dots compacted away → their current-generation anchor.
    private FrozenDictionary<Dot, OffsetAnchor> CompactedDotAnchors { get; }

    //Previous-generation base offset → its current-generation anchor: the shifted base position when the
    //entry survived, the gap anchor when reclamation dropped it.
    private FrozenDictionary<int, OffsetAnchor> CompactedBaseOffsets { get; }


    /// <summary>
    /// The causal context of this sequence, for gossip digests and stability frontiers. Advertise it with
    /// <c>new GossipDigest(origin, sequence.CausalContext)</c>; the frontier folded from a group's digests
    /// then certifies removes group-wide.
    /// </summary>
    /// <remarks>
    /// The context is carried unchanged through <see cref="Compact"/>, so a compacted member still reports
    /// full causal knowledge and pins the frontier correctly. This is the gossip path, distinct from the
    /// serialization path: use <see cref="ToState"/> to persist or transfer the sequence.
    /// </remarks>
    public VectorClock CausalContext => Context;


    /// <summary>
    /// Creates a fresh generation over <paramref name="baseSnapshot"/> with no live edits.
    /// </summary>
    /// <param name="baseSnapshot">The agreed checkpoint snapshot.</param>
    /// <returns>A new sequence.</returns>
    /// <remarks>
    /// The generation identity is the empty frontier: a fresh generation has never base-changed, exactly
    /// as <see cref="Empty"/>.
    /// </remarks>
    public static OffsetAnchoredSequence<TValue> WithBase(ImmutableArray<TValue> baseSnapshot)
    {
        return new OffsetAnchoredSequence<TValue>(baseSnapshot, VectorClock.Empty, 0, FrozenDictionary<int, FrozenSet<Dot>>.Empty, VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenDictionary<Dot, FrozenSet<Dot>>.Empty, FrozenDictionary<Dot, OffsetAnchor>.Empty, FrozenDictionary<int, OffsetAnchor>.Empty);
    }


    /// <summary>The visible values in sequence order: base elements interleaved with their live subtrees.</summary>
    public IReadOnlyList<TValue> Values
    {
        get
        {
            IReadOnlyList<(OffsetAddress Anchor, TValue Value)> visible = VisibleElements;
            var result = new List<TValue>(visible.Count);
            foreach((OffsetAddress _, TValue value) in visible)
            {
                result.Add(value);
            }

            return result;
        }
    }


    /// <summary>
    /// The visible elements in sequence order, each paired with its address — what an editor needs to
    /// address the element it is editing relative to. A base element carries the current base generation,
    /// a live element the canonical generation zero, so a freshly captured address is self-describing.
    /// </summary>
    public IReadOnlyList<(OffsetAddress Anchor, TValue Value)> VisibleElements
    {
        get
        {
            var result = new List<(OffsetAddress, TValue)>(Base.Length + Vertices.Count);
            Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor = BuildChildren();
            for(int slot = -1; slot < Base.Length; slot++)
            {
                if(slot >= 0 && !RemovedBaseOffsets.ContainsKey(slot))
                {
                    result.Add((new OffsetAddress(OffsetAnchor.AtBase(slot), BaseGeneration), Base[slot]));
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
    /// <returns>The new sequence and the address of the inserted element, a live address of canonical generation zero.</returns>
    public (OffsetAnchoredSequence<TValue> Result, OffsetAddress InsertedId) InsertAtHead(TValue value, ReplicaId replica)
    {
        return Insert(OffsetAnchor.Head, value, replica);
    }


    /// <summary>
    /// Inserts <paramref name="value"/> immediately after the element addressed by <paramref name="after"/>.
    /// </summary>
    /// <param name="after">The address of the element to insert after.</param>
    /// <param name="value">The value to insert.</param>
    /// <param name="replica">The replica performing the edit.</param>
    /// <returns>The new sequence and the address of the inserted element, a live address of canonical generation zero.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="after"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="after"/> names a base offset outside the base or a live element not in this sequence, or if it names a base generation other than this sequence's — a stale-generation address must be translated through <see cref="TranslateAnchor"/> first.</exception>
    public (OffsetAnchoredSequence<TValue> Result, OffsetAddress InsertedId) InsertAfter(OffsetAddress after, TValue value, ReplicaId replica)
    {
        ArgumentNullException.ThrowIfNull(after);
        ValidateAnchor(after);

        return Insert(after.Anchor, value, replica);
    }


    /// <summary>
    /// Removes the element anchored by <paramref name="anchor"/> with a fresh dotted remove event minted
    /// on <paramref name="replica"/>'s axis: a base element is hidden by offset, a live element by
    /// tombstone; both are retained for ordering, and the remove is a first-class event a stability
    /// frontier can certify.
    /// </summary>
    /// <param name="anchor">The address of the element to remove.</param>
    /// <param name="replica">The replica performing the removal.</param>
    /// <returns>The new sequence; this sequence if the element was already removed.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="anchor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="anchor"/> is the head or a base offset outside the base, or if it names a base generation other than this sequence's — a stale-generation address must be translated through <see cref="TranslateAnchor"/> first.</exception>
    /// <exception cref="OverflowException">Thrown when advancing <paramref name="replica"/>'s counter would overflow, propagated from <see cref="VectorClock.Increment(ReplicaId)"/>; this sequence is not modified when the throw occurs.</exception>
    /// <remarks>
    /// <para>
    /// The remove-dot is minted with <see cref="VectorClock.Increment(ReplicaId)"/>, not
    /// <see cref="VectorClock.IncrementPastAll(ReplicaId)"/>: a remove-dot needs uniqueness, monotonicity,
    /// and stability-trackability, but not Lamport dominance, so the gentler tick is used. Removal is
    /// idempotent by target — re-removing an already-removed element (dotted or legacy) mints no new dot
    /// and returns this sequence; two remove-dots for one target arise only through
    /// <see cref="Merge(OffsetAnchoredSequence{TValue})"/> of genuinely concurrent removes. A live target
    /// need not be a vertex: a remove can be serialized and merged separately from the insert it hides, so
    /// an orphan live remove is legal. A base removal can never be orphaned — the offset must fall within
    /// this generation's base, and a stale cross-generation base anchor must be translated through
    /// <see cref="TranslateAnchor"/> first.
    /// </para>
    /// <para>
    /// The remove and inserts share one counter plane. A remove tick raises the replica's own axis, so its
    /// next insert — assigned Lamport-style past the observed maximum — can outrank a concurrent sibling
    /// it would otherwise have tied with. Convergence and intention preservation are unaffected; only the
    /// relative order of concurrent siblings can move.
    /// </para>
    /// </remarks>
    public OffsetAnchoredSequence<TValue> Remove(OffsetAddress anchor, ReplicaId replica)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        OffsetAnchor bare = anchor.Anchor;
        if(!bare.IsLive && bare.BaseOffset < 0)
        {
            throw new ArgumentException("The head is a position, not an element.", nameof(anchor));
        }

        if(bare.LiveId is { } liveId)
        {
            if(Tombstones.ContainsKey(liveId))
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

            updated[liveId] = FrozenSet.ToFrozenSet([removeDot]);

            return new OffsetAnchoredSequence<TValue>(Base, BaseFrontier, BaseGeneration, RemovedBaseOffsets, advanced, Vertices, updated.ToFrozenDictionary(), CompactedDotAnchors, CompactedBaseOffsets);
        }

        ValidateAnchor(anchor);

        if(RemovedBaseOffsets.ContainsKey(bare.BaseOffset))
        {
            return this;
        }

        VectorClock advancedForBase = Context.Increment(replica);
        var baseRemoveDot = new Dot(replica, advancedForBase[replica]);

        var updatedRemoved = new Dictionary<int, FrozenSet<Dot>>(RemovedBaseOffsets.Count + 1);
        foreach(KeyValuePair<int, FrozenSet<Dot>> entry in RemovedBaseOffsets)
        {
            updatedRemoved[entry.Key] = entry.Value;
        }

        updatedRemoved[bare.BaseOffset] = FrozenSet.ToFrozenSet([baseRemoveDot]);

        return new OffsetAnchoredSequence<TValue>(Base, BaseFrontier, BaseGeneration, updatedRemoved.ToFrozenDictionary(), advancedForBase, Vertices, Tombstones, CompactedDotAnchors, CompactedBaseOffsets);
    }


    /// <summary>
    /// Merges this sequence with <paramref name="other"/> of the same generation: the union of their
    /// removals, vertices, and tombstones.
    /// </summary>
    /// <param name="other">The sequence to merge with.</param>
    /// <returns>A new sequence; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the generation identities differ — two operands are mergeable only within one base
    /// generation, identified by the consensus-agreed frontier the generation was materialized at, because
    /// reclamation lets base value arrays cycle and value equality alone cannot fence a generation. Also
    /// thrown when same-identity operands carry divergent base values or divergent base-generation
    /// ordinals, which no honest history produces — the frontier and the ordinal are stamped and inherited
    /// only together, so honest members at one frontier always agree on both. Also thrown when an operand
    /// is a stale pre-remove state: it presents an element live — untombstoned
    /// on both sides — that the other operand's lineage compacted away after the element's remove was
    /// observed group-wide, witnessed permanently by that operand's translation map. A replica rejoining
    /// after eviction, restore, or replay must adopt a current state wholesale rather than merge; merging a
    /// stale pre-remove state would resurrect the element, so it fails closed. Also thrown when the operands
    /// carry CONFLICTING vertices for one insert identity — a dot mints exactly one immutable vertex, so the
    /// conflict is equivocation or an adoption recovery that ran more than once and re-minted the identity
    /// divergently (run the recovery at most once per lost context and persist it before gossiping);
    /// overwriting would let merge order pick a winner silently.
    /// </exception>
    /// <remarks>
    /// Tombstones union their remove-dots per target, and base removals per offset: a legacy empty set
    /// unioned with a dotted set yields the dotted set, so a v1-loaded removal is upgraded by any peer that
    /// holds the dotted remove. Both translation maps union with the other operand winning a key collision,
    /// which within the fenced generation only arises for identically derived entries or a harmless
    /// ghost-GC asymmetry, so the choice is immaterial. The conflicting-vertex check compares values with
    /// <see cref="System.Collections.Generic.EqualityComparer{T}.Default"/>, so <typeparamref name="TValue"/>
    /// must carry VALUE equality: a reference-equality element type would compare honest copies of one
    /// vertex unequal after any serialization boundary and fail honest merges closed.
    /// </remarks>
    public OffsetAnchoredSequence<TValue> Merge(OffsetAnchoredSequence<TValue> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        //The generation fence: reclamation lets base value arrays cycle, so only the consensus-agreed
        //identity the generation was materialized at can fence a merge. It never removes metadata; it
        //only refuses to combine states from different generations.
        if(!BaseFrontier.Equals(other.BaseFrontier))
        {
            throw new InvalidOperationException("Cannot merge sequences from different base generations; the generation identities differ. Align the group on the agreed checkpoint first.");
        }

        //Behind the fence, value equality is an integrity assertion: same-identity operands with
        //divergent base values are forged or corrupt.
        if(!BaseEqual(Base, other.Base))
        {
            throw new InvalidOperationException("Cannot merge sequences over different base generations; align the group on the agreed checkpoint first.");
        }

        //The base-generation ordinal is stamped and inherited only together with the base frontier, so
        //honest members at one frontier always agree on it; a divergent ordinal behind the fence is forged
        //or corrupt state.
        if(BaseGeneration != other.BaseGeneration)
        {
            throw new InvalidOperationException("Cannot merge sequences whose base-generation ordinals differ at one base frontier; the ordinal is stamped with the frontier, so a mismatch behind the generation fence is forged or corrupt state.");
        }

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
            //A dot mints exactly one immutable vertex, and within a fenced generation no compaction
            //rewrites a retained vertex (drop-only walks re-anchor nothing; base-changing walks are
            //fence-blocked before this point), so operands disagreeing on a shared dot's vertex is never
            //an honest edge: it is equivocation or an adoption recovery that ran twice and re-minted the
            //identity divergently. Overwriting would let merge order pick a winner silently — fail closed.
            //The drop-only claim is a COROLLARY of the insert-quiescence guard (a successful Compact
            //retains no vertex at all, so there is nothing to re-anchor); a reclamation follow-on that
            //makes the ghost retention arm reachable must re-establish this premise.
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

        var mergedRemoved = new Dictionary<int, FrozenSet<Dot>>(RemovedBaseOffsets.Count + other.RemovedBaseOffsets.Count);
        foreach(KeyValuePair<int, FrozenSet<Dot>> entry in RemovedBaseOffsets)
        {
            mergedRemoved[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<int, FrozenSet<Dot>> entry in other.RemovedBaseOffsets)
        {
            if(mergedRemoved.TryGetValue(entry.Key, out FrozenSet<Dot>? existing))
            {
                var union = new HashSet<Dot>(existing);
                union.UnionWith(entry.Value);
                mergedRemoved[entry.Key] = union.ToFrozenSet();
            }
            else
            {
                mergedRemoved[entry.Key] = entry.Value;
            }
        }

        //Both translation maps union; the other operand wins a key collision, which within the fenced
        //generation only arises for identically derived entries or a harmless ghost-GC asymmetry, so the
        //choice is immaterial.
        var mergedDotAnchors = new Dictionary<Dot, OffsetAnchor>(CompactedDotAnchors.Count + other.CompactedDotAnchors.Count);
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            mergedDotAnchors[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<Dot, OffsetAnchor> entry in other.CompactedDotAnchors)
        {
            mergedDotAnchors[entry.Key] = entry.Value;
        }

        var mergedBaseOffsets = new Dictionary<int, OffsetAnchor>(CompactedBaseOffsets.Count + other.CompactedBaseOffsets.Count);
        foreach(KeyValuePair<int, OffsetAnchor> entry in CompactedBaseOffsets)
        {
            mergedBaseOffsets[entry.Key] = entry.Value;
        }

        foreach(KeyValuePair<int, OffsetAnchor> entry in other.CompactedBaseOffsets)
        {
            mergedBaseOffsets[entry.Key] = entry.Value;
        }

        return new OffsetAnchoredSequence<TValue>(Base, BaseFrontier, BaseGeneration, mergedRemoved.ToFrozenDictionary(), Context.Merge(other.Context), mergedVertices.ToFrozenDictionary(), mergedTombstones.ToFrozenDictionary(), mergedDotAnchors.ToFrozenDictionary(), mergedBaseOffsets.ToFrozenDictionary());
    }


    private static void ThrowOnStaleReplay(OffsetAnchoredSequence<TValue> holder, OffsetAnchoredSequence<TValue> witnesser)
    {
        foreach(Dot dropped in witnesser.CompactedDotAnchors.Keys)
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


    /// <summary>
    /// The insert-quiescence probe: the vertex insert-dots <paramref name="stabilityFrontier"/> does not
    /// cover, ascending by (Replica, Counter) — empty exactly when <see cref="Compact"/> at this frontier
    /// passes its insert-quiescence guard.
    /// </summary>
    /// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>.</param>
    /// <returns>The uncovered vertex insert-dots ascending by (Replica, Counter), or empty when the frontier is insert-quiescent.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Each returned dot is a vertex insert the frontier does not cover, so the per-replica maximum of the
    /// result is what every member's digest must reach before a frontier folded from those digests covers
    /// this state. Remove-dots never appear: quiescence reads insert-stability only, and a frontier that
    /// certifies no pending remove is still quiescent. The guard and this probe run the one shared scan, so
    /// they can never disagree on which state is quiescent.
    /// </remarks>
    public ImmutableArray<Dot> UnstableInserts(VectorClock stabilityFrontier)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);

        return CollectUnstableInserts(stabilityFrontier);
    }


    /// <summary>
    /// The certified dotted projection at <paramref name="stabilityFrontier"/>: the visible-order walk
    /// where a live element is included when it is stable and its remove is not certified at the frontier,
    /// carrying its real insert-dot, and a base element is included when its removal is not certified,
    /// carrying a sentinel identity. This is the checkpoint a container seals to.
    /// </summary>
    /// <param name="stabilityFrontier">The group stability frontier — see <see cref="StabilityFrontier"/>.</param>
    /// <returns>The projected entries in visible order.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A LOCALLY removed element whose remove-dots are all above the frontier stays IN the projection —
    /// from the group's certified viewpoint the remove has not happened yet — so the projection is a pure
    /// function of the frontier for every member whose context dominates it, and two honest members at the
    /// same frontier compute the identical checkpoint even when they disagree on an uncertified remove.
    /// The same core drives <see cref="Compact"/>'s integrity check, so the projection and the assertion
    /// can never disagree on the predicate.
    /// </para>
    /// <para>
    /// A base position has no insert-dot, so its projection identity is a sentinel: the full 32-byte
    /// replica value of 254 followed by 31 zero bytes, with counter = base offset + 1. Production
    /// <see cref="ReplicaId"/> values are opaque random bytes with NO reserved range; non-collision rests
    /// on the sentinel's entropy — a random 32-byte id equals the full sentinel with negligible
    /// probability — and no code may detect a placeholder by its first byte. The sentinel is positional,
    /// not provenance, identity, and it lives only in checkpoints and commitments, never in the sequence
    /// state or the context.
    /// </para>
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


    //The one certified-projection core the public projection and Compact's integrity check share: the
    //visible-order walk where a base slot is included unless its removal is certified (carrying the
    //sentinel identity for its current offset) and a live vertex is included when it is stable and its
    //remove is not certified (carrying its real dot). A single source means the predicate can never drift
    //between the emitted checkpoint and the compaction assertion.
    private List<(Dot Dot, TValue Value)> CertifiedProjectionCore(VectorClock frontier)
    {
        Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor = BuildChildren();
        var projection = new List<(Dot Dot, TValue Value)>(Base.Length + Vertices.Count);
        var stack = new Stack<Dot>();
        for(int slot = -1; slot < Base.Length; slot++)
        {
            if(slot >= 0
                && !(RemovedBaseOffsets.TryGetValue(slot, out FrozenSet<Dot>? slotRemoveDots) && HasCertifiedRemove(slotRemoveDots, frontier)))
            {
                projection.Add((SentinelDot(slot), Base[slot]));
            }

            PushChildren(slot < 0 ? OffsetAnchor.Head : OffsetAnchor.AtBase(slot), childrenByAnchor, stack);
            while(stack.Count > 0)
            {
                Dot child = stack.Pop();
                if(IsStable(child, frontier)
                    && !(Tombstones.TryGetValue(child, out FrozenSet<Dot>? removeDots) && HasCertifiedRemove(removeDots, frontier)))
                {
                    projection.Add((child, Vertices[child].Value));
                }

                PushChildren(OffsetAnchor.AtLive(child), childrenByAnchor, stack);
            }
        }

        return projection;
    }


    /// <summary>
    /// Compacts the waterline: stable visible vertices collapse into new base entries at their
    /// linearization positions, stable tombstoned vertices whose remove is uncertified collapse into
    /// pending-removed base entries, certified-removed state is reclaimed, and the result edits over the
    /// agreed <paramref name="checkpoint"/> as its new generation. Visible values are unchanged.
    /// </summary>
    /// <param name="stabilityFrontier">
    /// The group stability frontier — see <see cref="StabilityFrontier"/>. A dot is stable when the
    /// frontier's counter for its replica is at least the dot's counter; stable state can never again be
    /// referenced by any member, so it is safe to collapse.
    /// </param>
    /// <param name="checkpoint">The agreed checkpoint: the certified projection at the frontier as dotted (identity, value) entries.</param>
    /// <returns>The compacted sequence; this sequence is never modified, and is returned unchanged when nothing converts, nothing drops, and nothing is reclaimed.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="stabilityFrontier"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="checkpoint"/> is default.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown, before anything else, when the frontier is not insert-quiescent — some vertex's insert-dot
    /// is above it. The base-materializing model materializes only a fully-stable line: an unstable vertex
    /// would let a base conversion cross a retained region and reorder the visible sequence, so compaction
    /// fails closed rather than fork the group. Also thrown when the certified projection does not equal
    /// <paramref name="checkpoint"/> element-wise on both dot and value. The agreed checkpoint is the
    /// content line; a mismatch means the (frontier, checkpoint) pair is misaligned. The check runs before
    /// any result is constructed — this fails closed rather than guessing, because a wrong base would
    /// silently fork the group's generations.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Compaction requires an INSERT-QUIESCENT frontier: it must cover every vertex's insert-dot, and
    /// throws otherwise, checked first before any classification. The base-materializing model can
    /// materialize only a fully-stable line, because base slots linearize after the live head region — so
    /// a live-to-base conversion that crosses a retained (instability-held or ghost) region reorders the
    /// visible sequence, and two members compacting along different frontier paths reach byte-different
    /// bases under one generation identity, wedging them on the base integrity assertion and falsifying
    /// certified-projection determinism. The guard forecloses all of this: with no unstable vertex nothing
    /// is instability-retained, ghosts are unconstructible, and every below-line element converts or drops
    /// in walk order, so order preservation and frontier-purity hold by construction. The guard reads only
    /// insert-stability, so it is itself frontier-pure, and <see cref="CertifiedProjection"/> stays
    /// unrestricted — a pure read at any frontier for digests, verification, and adoption.
    /// </para>
    /// <para>
    /// The retention taxonomy is four-way. An unstable vertex is retained. A stable visible vertex
    /// converts into the new base at its walk position. A stable tombstoned vertex whose remove is
    /// uncertified converts as pending-removed — always, even with retained descendants, which re-anchor
    /// at the gap — appending its value, marking the new offset removed with the vertex's remove-dots, and
    /// recording its translation; members that disagree on an uncertified remove therefore materialize the
    /// identical base value array and differ only in grow-only removal metadata. A stable tombstoned
    /// vertex whose remove is certified is retained as a ghost exactly when a child is retained, and
    /// otherwise dropped, translating to its gap anchor. A legacy tombstone carries no remove-dot, is
    /// never certified, and so always lands on the pending-removed branch.
    /// </para>
    /// <para>
    /// Every base slot is KEPT — removed slots included, rebasing their remove-dots to their new offsets.
    /// Reclamation is deferred: a frontier-local reclamation cannot be both frontier-pure and
    /// order-preserving (gating on retained descendants reads above-frontier state that honest members
    /// disagree on, forking the reclamation set; reclaiming unconditionally re-anchors survivors into
    /// foreign sibling sets and reorders the visible sequence), so a certified removal marks the slot
    /// RECLAIMABLE for a follow-on that carries a consensus-agreed reclamation set. The base-offset
    /// translation map is REPLACED by this generation's shift; the dot-translation map composes.
    /// </para>
    /// <para>
    /// A surviving vertex under a surviving parent keeps its recorded anchor; every other survivor is
    /// re-anchored at its <em>gap anchor</em> — the most recently materialized new-base entry at the
    /// vertex's own position in the walk. The gap anchor is replica-independent: it is a function of the
    /// agreed base, the agreed reclamation set, and frontier-stable converting vertices only, never of
    /// which above-frontier children a member holds, so every member re-anchors a shared survivor
    /// identically.
    /// </para>
    /// <para>
    /// <see cref="Context"/> is unchanged — compaction reclaims storage, not causal knowledge. The
    /// generation identity advances only when this compaction changed the base (any conversion or any
    /// reclamation); a drop-only compaction carries it unchanged. The fence's cross-member agreement
    /// inherits the existing frontier-coordination contract: the frontier and checkpoint are
    /// consensus-agreed inputs, and this method does not self-coordinate — the container's seal supplies
    /// the committed frontier.
    /// </para>
    /// </remarks>
    public OffsetAnchoredSequence<TValue> Compact(VectorClock stabilityFrontier, ImmutableArray<SequenceCheckpointEntry<TValue>> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(stabilityFrontier);
        if(checkpoint.IsDefault)
        {
            throw new ArgumentException("The checkpoint content is required.", nameof(checkpoint));
        }

        //Insert-quiescence guard, before any classification: the base-materializing model materializes
        //only a fully-stable line. Every vertex's insert-dot must be covered by the frontier, otherwise
        //this fails closed. An unstable vertex could stand as a retained element between a converting
        //element and the base region, and because base slots linearize after the live head region that
        //conversion would reorder the visible sequence, wedge honest members on frontier-path-dependent
        //bases at one generation identity, and break certified-projection determinism. Reading only
        //insert-stability keeps the guard frontier-pure. The scan is the shared CollectUnstableInserts core
        //the public UnstableInserts probe also runs, so the guard's fail-closed set and the probe's report
        //can never drift; a passing guard scans every vertex exactly as before, only the failing path — which
        //throws anyway — pays for collecting the whole uncovered set for the diagnostic.
        ImmutableArray<Dot> uncovered = CollectUnstableInserts(stabilityFrontier);
        if(!uncovered.IsEmpty)
        {
            var replicaAxes = new HashSet<ReplicaId>();
            foreach(Dot vertex in uncovered)
            {
                replicaAxes.Add(vertex.Replica);
            }

            string axisWord = replicaAxes.Count == 1 ? "axis" : "axes";
            throw new InvalidOperationException($"Compaction requires an insert-quiescent frontier: {uncovered.Length} vertex insert-dot(s) across {replicaAxes.Count} replica {axisWord} are above it, so the base-materializing model cannot materialize a fully-stable line. An unstable vertex would let a base conversion reorder the visible sequence and fork honest members' bases at one generation identity. Advance the frontier past every uncovered insert — a host sealing a group drives it to insert-quiescence first — or read the state with CertifiedProjection, which is unrestricted.");
        }

        //Classify every vertex against the frontier with the four-way taxonomy.
        Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor = BuildChildren();
        Dictionary<Dot, bool> retention = ComputeRetention(stabilityFrontier, childrenByAnchor);

        //Walk the full linearization (every base slot, then its subtree depth-first in the canonical
        //sibling order) to build the new base, the removal markings, the translation maps, and the
        //survivors' gap anchors. The base-changed flag is set explicitly by the changing branches — a
        //visible conversion, a pending-removed conversion, or a reclamation — never derived from a counter
        //or from value comparison, because a value-cycling generation must still stamp a new identity.
        var newBase = new List<TValue>(Base.Length + Vertices.Count);
        var newRemoved = new Dictionary<int, FrozenSet<Dot>>();
        var oldToNew = new Dictionary<int, OffsetAnchor>(Base.Length);
        var dotAnchor = new Dictionary<Dot, OffsetAnchor>();
        var retainedAnchors = new Dictionary<Dot, OffsetAnchor>();
        bool baseChanged = false;
        for(int slot = -1; slot < Base.Length; slot++)
        {
            if(slot >= 0)
            {
                //Every base slot is KEPT, removed or not — reclamation is deferred to a follow-on with a
                //consensus-carried reclamation set. The two constraints are jointly unsatisfiable inside a
                //frontier-local compaction: gating reclamation on retained descendants reads above-frontier
                //state and forks the reclamation set between honest same-frontier members, while reclaiming
                //unconditionally re-anchors survivors into foreign sibling sets and reorders the visible
                //sequence. A removed slot therefore stays as the ordering placeholder for its subtree,
                //hidden, its remove-dots riding forward — certified and reclaimable the moment the group
                //can agree on a reclamation set through the seal.
                int newOffset = newBase.Count;
                newBase.Add(Base[slot]);
                oldToNew[slot] = OffsetAnchor.AtBase(newOffset);
                if(RemovedBaseOffsets.TryGetValue(slot, out FrozenSet<Dot>? slotRemoveDots))
                {
                    newRemoved[newOffset] = slotRemoveDots;
                }
            }

            WalkSubtree(slot < 0 ? OffsetAnchor.Head : OffsetAnchor.AtBase(slot), childrenByAnchor, retention, stabilityFrontier, newBase, newRemoved, dotAnchor, retainedAnchors, ref baseChanged);
        }

        int drops = 0;
        foreach(KeyValuePair<Dot, bool> entry in retention)
        {
            if(!entry.Value
                && Tombstones.TryGetValue(entry.Key, out FrozenSet<Dot>? removeDots)
                && HasCertifiedRemove(removeDots, stabilityFrontier))
            {
                drops++;
            }
        }

        //No-op shortcut BEFORE the integrity check: a no-op mutates nothing, so there is no generation
        //fork for the checkpoint assertion to protect — and a base-changing compaction re-identifies
        //converted elements (real dots become base sentinels at new offsets), so the PREVIOUS
        //generation's checkpoint can never validate against the compacted state's projection. Repeat
        //compaction at the same (frontier, checkpoint) is idempotent exactly through this path.
        if(!baseChanged && drops == 0)
        {
            return this;
        }

        //Checkpoint check before any result construction: the certified projection must equal the agreed
        //content element-wise on BOTH dot and value, otherwise the (frontier, checkpoint) pair is
        //misaligned. This shares one core with CertifiedProjection so the predicate can never drift.
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

        //Re-anchor every retained vertex at the anchor the walk recorded for it: its own recorded anchor
        //under a retained parent, its gap anchor otherwise. Orphan tombstones — targets that are not
        //vertices — are outside retention entirely and are carried unchanged.
        var newVertices = new Dictionary<Dot, Vertex>(retention.Count);
        var newTombstones = new Dictionary<Dot, FrozenSet<Dot>>();
        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            if(!Vertices.ContainsKey(entry.Key))
            {
                newTombstones[entry.Key] = entry.Value;
            }
        }

        foreach(KeyValuePair<Dot, bool> entry in retention)
        {
            if(!entry.Value)
            {
                continue;
            }

            Vertex vertex = Vertices[entry.Key];
            newVertices[entry.Key] = new Vertex(retainedAnchors[entry.Key], vertex.Value);
            if(Tombstones.TryGetValue(entry.Key, out FrozenSet<Dot>? removeDots))
            {
                newTombstones[entry.Key] = removeDots;
            }
        }

        //Translation maps. Prior dot-map entries compose through this compaction (the anchors they point
        //at have themselves moved this generation); converted and dropped dots got fresh entries during
        //the walk, which win any collision. The base-offset map is REPLACED by this generation's
        //oldToNew only when the base CHANGED: a previous-generation base anchor can no longer arrive once
        //the line passed the previous checkpoint, so composing the old map would only retain unreachable
        //entries. A DROP-ONLY walk keeps the prior map instead — its own oldToNew is the identity (every
        //base slot is kept at its offset), so keeping the prior map IS the exact composition, whereas
        //installing the identity would silently retarget still-in-window previous-generation base anchors
        //to wrong elements mid-window.
        var newDotAnchors = new Dictionary<Dot, OffsetAnchor>(CompactedDotAnchors.Count + dotAnchor.Count);
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            newDotAnchors[entry.Key] = ComposeThroughCompaction(entry.Value, oldToNew, dotAnchor, retention);
        }

        foreach(KeyValuePair<Dot, OffsetAnchor> entry in dotAnchor)
        {
            newDotAnchors[entry.Key] = entry.Value;
        }

        //Only a base-changing compaction advances the generation identity; the base frontier and the
        //ordinal that counts base changes are stamped together, and a drop-only compaction leaves the base
        //and both identity fields untouched.
        VectorClock newBaseFrontier = baseChanged ? stabilityFrontier : BaseFrontier;
        int newBaseGeneration = baseChanged ? BaseGeneration + 1 : BaseGeneration;

        return new OffsetAnchoredSequence<TValue>(newBase.ToImmutableArray(), newBaseFrontier, newBaseGeneration, newRemoved.ToFrozenDictionary(), Context, newVertices.ToFrozenDictionary(), newTombstones.ToFrozenDictionary(), newDotAnchors.ToFrozenDictionary(), baseChanged ? oldToNew.ToFrozenDictionary() : CompactedBaseOffsets);
    }


    /// <summary>
    /// Translates an <paramref name="address"/> that may name a previous compaction generation into this
    /// generation's equivalent address, or <see langword="null"/> when it is unservable here.
    /// </summary>
    /// <param name="address">The possibly stale address.</param>
    /// <returns>The current address — the input itself when no translation is needed — or <see langword="null"/> when unservable.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="address"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A translated address at a dropped tombstone or a reclaimed base offset resolves to the gap anchor of
    /// its old position — the nearest preceding checkpoint entry — which can place a later insert ahead of
    /// surviving subtrees the removed element's position would have followed. Convergence is unaffected —
    /// every member translates through the same replica-independent map — and intention degrades only for
    /// addresses at elements that were already removed.
    /// </para>
    /// <para>
    /// Head and live-dot addresses translate exactly across arbitrarily many generations: the head is the
    /// same virtual position in every generation, and dots are globally unique with a permanently composing
    /// map. Every returned address is canonical — a base result carries the current base generation, a head
    /// or live result carries zero, including a map lookup whose target is the head. A base address is a
    /// three-way decision on its generation: at the current generation it is its own address when its offset
    /// is in range and <see langword="null"/> otherwise; at exactly the immediately preceding generation the
    /// base-offset map serves it, or <see langword="null"/> when the map has no such key; and any older or
    /// newer generation is <see langword="null"/>, fail closed. The base map serves exactly one generation,
    /// so a generation it cannot serve is refused rather than guessed.
    /// </para>
    /// </remarks>
    public OffsetAddress? TranslateAnchor(OffsetAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        OffsetAnchor anchor = address.Anchor;

        //Never compacted: the maps are empty, so a servable anchor is its own current address and anything
        //else is null. The base arm is generation-checked the same way — a never-base-changed sequence is
        //at generation zero, so the map arm is unreachable and only the identity and fail-closed arms run.
        if(CompactedDotAnchors.Count == 0 && CompactedBaseOffsets.Count == 0)
        {
            if(!anchor.IsLive && anchor.BaseOffset < 0)
            {
                return CurrentAddress(OffsetAnchor.Head);
            }

            if(anchor.LiveId is { } liveDot)
            {
                return Vertices.ContainsKey(liveDot) ? CurrentAddress(anchor) : null;
            }

            return ResolveBaseAddress(address);
        }

        if(!anchor.IsLive && anchor.BaseOffset < 0)
        {
            return CurrentAddress(OffsetAnchor.Head);
        }

        if(anchor.LiveId is { } dot)
        {
            if(Vertices.ContainsKey(dot))
            {
                return CurrentAddress(anchor);
            }

            return CompactedDotAnchors.TryGetValue(dot, out OffsetAnchor? translated) ? CurrentAddress(translated) : null;
        }

        return ResolveBaseAddress(address);
    }


    //The three-way base-address decision: a current-generation offset is its own address when in range, the
    //one immediately preceding generation the base-offset map serves is translated through it, and any older
    //or newer generation fails closed. The base map serves exactly one generation, so a generation it cannot
    //serve is refused rather than guessed.
    private OffsetAddress? ResolveBaseAddress(OffsetAddress address)
    {
        int offset = address.Anchor.BaseOffset;
        if(address.Generation == BaseGeneration)
        {
            return offset < Base.Length ? CurrentAddress(address.Anchor) : null;
        }

        if(address.Generation == BaseGeneration - 1)
        {
            return CompactedBaseOffsets.TryGetValue(offset, out OffsetAnchor? target) ? CurrentAddress(target) : null;
        }

        return null;
    }


    //Stamps a bare current-generation result anchor into its canonical address: a base anchor carries the
    //current base generation, the head or a live anchor carries zero.
    private OffsetAddress CurrentAddress(OffsetAnchor anchor)
    {
        return anchor.IsLive || anchor.BaseOffset < 0 ? new OffsetAddress(anchor, 0) : new OffsetAddress(anchor, BaseGeneration);
    }


    /// <summary>
    /// Returns the serializable state of this sequence, for persistence or transfer. The output is
    /// deterministic: vertices and tombstone targets in (replica, counter) order, remove-dots within an
    /// entry the same way, removed offsets ascending, the dot-translation map in dropped-dot
    /// (replica, counter) order, and the base-offset-translation map in previous-offset order, so equal
    /// sequences serialize to equal records regardless of operation order.
    /// </summary>
    /// <returns>The sequence's state, carrying both translation maps for a compacted generation.</returns>
    public OffsetAnchoredSequenceState<TValue> ToState()
    {
        var orderedRemovedOffsets = new List<int>(RemovedBaseOffsets.Keys);
        orderedRemovedOffsets.Sort();
        ImmutableArray<OffsetBaseRemovalEntry>.Builder removedBuilder = ImmutableArray.CreateBuilder<OffsetBaseRemovalEntry>(orderedRemovedOffsets.Count);
        foreach(int offset in orderedRemovedOffsets)
        {
            removedBuilder.Add(new OffsetBaseRemovalEntry(offset, ToOrderedDotStates(RemovedBaseOffsets[offset])));
        }

        var orderedVertices = new List<Dot>(Vertices.Keys);
        orderedVertices.Sort(CompareDotsByReplica);
        ImmutableArray<OffsetVertexEntry<TValue>>.Builder vertexBuilder = ImmutableArray.CreateBuilder<OffsetVertexEntry<TValue>>(orderedVertices.Count);
        foreach(Dot dot in orderedVertices)
        {
            Vertex vertex = Vertices[dot];
            vertexBuilder.Add(new OffsetVertexEntry<TValue>(ToDotState(dot), ToAnchorState(vertex.Anchor), vertex.Value));
        }

        var orderedTargets = new List<Dot>(Tombstones.Keys);
        orderedTargets.Sort(CompareDotsByReplica);
        ImmutableArray<OffsetTombstoneEntry>.Builder tombstoneBuilder = ImmutableArray.CreateBuilder<OffsetTombstoneEntry>(orderedTargets.Count);
        foreach(Dot target in orderedTargets)
        {
            tombstoneBuilder.Add(new OffsetTombstoneEntry(ToDotState(target), ToOrderedDotStates(Tombstones[target])));
        }

        var dotAnchors = new List<OffsetTranslationEntry>(CompactedDotAnchors.Count);
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            dotAnchors.Add(new OffsetTranslationEntry(ToDotState(entry.Key), ToAnchorState(entry.Value)));
        }

        dotAnchors.Sort(static (left, right) => CompareDotStatesByReplica(left.Dropped, right.Dropped));

        var baseOffsets = new List<OffsetBaseAnchorEntry>(CompactedBaseOffsets.Count);
        foreach(KeyValuePair<int, OffsetAnchor> entry in CompactedBaseOffsets)
        {
            baseOffsets.Add(new OffsetBaseAnchorEntry(entry.Key, ToAnchorState(entry.Value)));
        }

        baseOffsets.Sort(static (left, right) => left.PreviousOffset.CompareTo(right.PreviousOffset));

        return new OffsetAnchoredSequenceState<TValue>(Base, BaseFrontier.ToState(), BaseGeneration, removedBuilder.ToImmutable(), Context.ToState(), vertexBuilder.ToImmutable(), tombstoneBuilder.ToImmutable(), [.. dotAnchors], [.. baseOffsets]);
    }


    /// <summary>
    /// Reconstructs a sequence from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed sequence.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown, failing closed, if any of the following holds: any array — the base, the removals, the
    /// vertices, the tombstones, the translation maps, or a per-entry remove-dot array — is default (an
    /// absent field is not the same statement as an explicitly empty one); a vertex id, tombstone target,
    /// or remove-dot has a non-positive counter; a vertex id or tombstone target is duplicated; a remove-dot
    /// appears more than once within or across the two removal axes (one cross-axis pool — a forged aliased
    /// dot would let an honest certification reclaim an unremoved entry); a remove-dot equals a vertex id;
    /// a vertex dot or any remove-dot is not covered by the context (invariant context-covers-dots — orphan
    /// tombstone targets are exempt, their remove-dots and all base remove-dots are not); the context does
    /// not dominate the base frontier element-wise (the generation identity cannot arrive inconsistent with
    /// the context that certifies it); the base generation is negative, or its genesis disagrees with the
    /// base frontier — generation zero materializes exactly at the empty frontier; a removed base offset is outside <c>[0, Base.Length)</c> or
    /// duplicated; an anchor (a vertex anchor or a translation target) violates the canonical shape, names
    /// a base offset at or beyond the base, or names a live dot that is not a vertex; the live-anchor graph
    /// contains a cycle; a dot-translation entry's dropped dot is duplicated or is simultaneously a live
    /// untombstoned vertex (the W-shape); or a base-offset translation has a negative or duplicated
    /// previous offset. No honest history produces any of these, and admitting one would silently
    /// desynchronize the visible order from the vertex set or forge a certifiable remove, so each fails
    /// closed.
    /// </exception>
    /// <remarks>
    /// A tombstone naming a dot that is not a vertex is accepted and harmless: a remove can be serialized
    /// and merged separately from the insert it tombstones, so its target may legitimately be absent. A
    /// dot-translation entry whose dropped dot is a tombstoned vertex is accepted too: a laggard merge can
    /// resurrect a dropped ghost while the entry remains, which is harmless because
    /// <see cref="TranslateAnchor"/> consults the vertices first.
    /// </remarks>
    public static OffsetAnchoredSequence<TValue> FromState(OffsetAnchoredSequenceState<TValue> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        //Default (uninitialized) arrays arrive from deserializers that leave an absent member unset. An
        //absent array is not the same statement as an explicitly empty one — a legacy removal declares an
        //EMPTY remove-dot list — so it fails closed here rather than being silently reinterpreted.
        if(state.Base.IsDefault || state.RemovedBaseOffsets.IsDefault || state.Vertices.IsDefault || state.Tombstones.IsDefault || state.CompactedDotAnchors.IsDefault || state.CompactedBaseOffsets.IsDefault)
        {
            throw new ArgumentException("The base, removal, vertex, tombstone, and translation arrays are required; a default array marks an absent field.", nameof(state));
        }

        VectorClock context = VectorClock.FromState(state.Context);
        VectorClock baseFrontier = VectorClock.FromState(state.BaseFrontier);

        //The generation identity is stamped from a frontier the member's context dominated when the base
        //materialized, so a context below it is forged or corrupt.
        Causality frontierOrder = context.Compare(baseFrontier);
        if(frontierOrder != Causality.After && frontierOrder != Causality.Equal)
        {
            throw new ArgumentException("The context does not dominate the base frontier; a generation identity the context cannot certify is forged or corrupt.", nameof(state));
        }

        //The base-generation ordinal is a non-negative count stamped together with the base frontier, so it
        //is genesis exactly when the frontier is: zero at the empty frontier, positive at a base-changed
        //one. A negative ordinal, or a genesis ordinal at a non-genesis frontier (or the converse), is
        //forged or corrupt.
        if(state.BaseGeneration < 0)
        {
            throw new ArgumentException($"The base generation {state.BaseGeneration} is negative.", nameof(state));
        }

        if((state.BaseGeneration == 0) != baseFrontier.Equals(VectorClock.Empty))
        {
            throw new ArgumentException("The base generation and the base frontier disagree on genesis; generation zero materializes exactly at the empty frontier.", nameof(state));
        }

        var vertices = new Dictionary<Dot, Vertex>(state.Vertices.Length);
        foreach(OffsetVertexEntry<TValue> entry in state.Vertices)
        {
            Dot id = FromDotState(entry.Id);
            if(id.Counter < 1)
            {
                throw new ArgumentException("A vertex counter must be positive.", nameof(state));
            }

            if(!vertices.TryAdd(id, new Vertex(FromAnchorState(entry.Anchor), entry.Value)))
            {
                throw new ArgumentException("A vertex id is duplicated.", nameof(state));
            }
        }

        ValidateAnchors(vertices, state.Base.Length);

        //Invariant context-covers-dots for the vertices; orphan tombstone targets are exempt (their insert
        //may not have arrived) but their remove-dots are not, and are covered in the removal loops below.
        foreach(Dot id in vertices.Keys)
        {
            if(context[id.Replica] < id.Counter)
            {
                throw new ArgumentException("A vertex dot is not covered by the context.", nameof(state));
            }
        }

        //One cross-axis remove-dot pool: every live remove-dot and every base remove-dot joins it, so a
        //duplicate within or across the axes fails closed — a forged aliased dot would otherwise let an
        //honest live certification reclaim an unremoved base slot.
        var allRemoveDots = new HashSet<Dot>();

        var tombstones = new Dictionary<Dot, FrozenSet<Dot>>(state.Tombstones.Length);
        foreach(OffsetTombstoneEntry entry in state.Tombstones)
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
                ValidateRemoveDot(removeDot, vertices, context, allRemoveDots, nameof(state));
                removeDots.Add(removeDot);
            }

            if(!tombstones.TryAdd(target, removeDots.ToFrozenSet()))
            {
                throw new ArgumentException("A tombstone target appears more than once.", nameof(state));
            }
        }

        var removedBaseOffsets = new Dictionary<int, FrozenSet<Dot>>(state.RemovedBaseOffsets.Length);
        foreach(OffsetBaseRemovalEntry entry in state.RemovedBaseOffsets)
        {
            if(entry.Offset < 0 || entry.Offset >= state.Base.Length)
            {
                throw new ArgumentException($"A removed base offset {entry.Offset} is outside the base of {state.Base.Length} element(s).", nameof(state));
            }

            if(entry.RemoveDots.IsDefault)
            {
                throw new ArgumentException("A base removal's remove-dot array is required; a default array marks an absent field, while a legacy base removal declares an explicitly empty one.", nameof(state));
            }

            var removeDots = new HashSet<Dot>(entry.RemoveDots.Length);
            foreach(DotState removeDotState in entry.RemoveDots)
            {
                Dot removeDot = FromDotState(removeDotState);
                ValidateRemoveDot(removeDot, vertices, context, allRemoveDots, nameof(state));
                removeDots.Add(removeDot);
            }

            if(!removedBaseOffsets.TryAdd(entry.Offset, removeDots.ToFrozenSet()))
            {
                throw new ArgumentException($"The removed base offset {entry.Offset} is duplicated.", nameof(state));
            }
        }

        var compactedDotAnchors = new Dictionary<Dot, OffsetAnchor>(state.CompactedDotAnchors.Length);
        foreach(OffsetTranslationEntry entry in state.CompactedDotAnchors)
        {
            Dot dropped = FromDotState(entry.Dropped);
            OffsetAnchor target = FromAnchorState(entry.Target);
            ValidateTargetAnchor(target, vertices, state.Base.Length);

            //The W-shape: a dropped dot presented as a live untombstoned vertex is a forged state — the
            //tombstoned ghost-plus-witness shape stays legal.
            if(vertices.ContainsKey(dropped) && !tombstones.ContainsKey(dropped))
            {
                throw new ArgumentException("A dot-translation entry's dropped dot is a live untombstoned vertex.", nameof(state));
            }

            if(!compactedDotAnchors.TryAdd(dropped, target))
            {
                throw new ArgumentException("A dot-translation entry's dropped dot is duplicated.", nameof(state));
            }
        }

        var compactedBaseOffsets = new Dictionary<int, OffsetAnchor>(state.CompactedBaseOffsets.Length);
        foreach(OffsetBaseAnchorEntry entry in state.CompactedBaseOffsets)
        {
            if(entry.PreviousOffset < 0)
            {
                throw new ArgumentException($"A compacted base offset has a negative previous offset {entry.PreviousOffset}.", nameof(state));
            }

            OffsetAnchor target = FromAnchorState(entry.Target);

            //Honest base-offset translations point only at base positions or the head (the walk writes the
            //shifted slot or the gap anchor, never a live dot). A live target would additionally dangle
            //uncomposed through a drop-only compaction, which keeps this map verbatim while the vertex may
            //drop — reject the shape at the model boundary instead.
            if(target.IsLive)
            {
                throw new ArgumentException($"The compacted base offset {entry.PreviousOffset} targets a live anchor; base-offset translations point at base positions or the head.", nameof(state));
            }

            ValidateTargetAnchor(target, vertices, state.Base.Length);
            if(!compactedBaseOffsets.TryAdd(entry.PreviousOffset, target))
            {
                throw new ArgumentException($"The compacted base offset's previous offset {entry.PreviousOffset} is duplicated.", nameof(state));
            }
        }

        return new OffsetAnchoredSequence<TValue>(state.Base, baseFrontier, state.BaseGeneration, removedBaseOffsets.ToFrozenDictionary(), context, vertices.ToFrozenDictionary(), tombstones.ToFrozenDictionary(), compactedDotAnchors.ToFrozenDictionary(), compactedBaseOffsets.ToFrozenDictionary());
    }


    private static void ValidateRemoveDot(Dot removeDot, Dictionary<Dot, Vertex> vertices, VectorClock context, HashSet<Dot> allRemoveDots, string parameterName)
    {
        if(removeDot.Counter < 1)
        {
            throw new ArgumentException("A remove dot counter must be positive.", parameterName);
        }

        if(vertices.ContainsKey(removeDot))
        {
            throw new ArgumentException("A remove dot equals a vertex id.", parameterName);
        }

        if(context[removeDot.Replica] < removeDot.Counter)
        {
            throw new ArgumentException("A remove dot is not covered by the context.", parameterName);
        }

        if(!allRemoveDots.Add(removeDot))
        {
            throw new ArgumentException("A remove dot appears more than once across the removal axes.", parameterName);
        }
    }


    private static DotState ToDotState(Dot dot)
    {
        return new DotState(ImmutableArray.Create(dot.Replica.AsSpan()), dot.Counter);
    }


    private static Dot FromDotState(DotState state)
    {
        return new Dot(ReplicaId.FromSpan(state.Replica.AsSpan()), state.Counter);
    }


    private static ImmutableArray<DotState> ToOrderedDotStates(FrozenSet<Dot> dots)
    {
        var ordered = new List<Dot>(dots);
        ordered.Sort(CompareDotsByReplica);
        ImmutableArray<DotState>.Builder builder = ImmutableArray.CreateBuilder<DotState>(ordered.Count);
        foreach(Dot dot in ordered)
        {
            builder.Add(ToDotState(dot));
        }

        return builder.ToImmutable();
    }


    //Maps a live anchor to its canonical state shape: the head and a base anchor carry a null live id, a
    //live anchor carries its dot and a base offset of -1.
    private static OffsetAnchorState ToAnchorState(OffsetAnchor anchor)
    {
        return anchor.LiveId is { } liveId ? new OffsetAnchorState(-1, ToDotState(liveId)) : new OffsetAnchorState(anchor.BaseOffset, null);
    }


    //Rebuilds a live anchor from its state, enforcing the one-canonical-shape-per-anchor discipline: a live
    //id forces a base offset of -1, the head is -1 with no live id, a base anchor is a non-negative offset.
    private static OffsetAnchor FromAnchorState(OffsetAnchorState state)
    {
        if(state.LiveId is { } liveId)
        {
            if(state.BaseOffset != -1)
            {
                throw new ArgumentException($"A live anchor must carry base offset -1, got {state.BaseOffset}.", nameof(state));
            }

            return OffsetAnchor.AtLive(FromDotState(liveId));
        }

        if(state.BaseOffset == -1)
        {
            return OffsetAnchor.Head;
        }

        if(state.BaseOffset < 0)
        {
            throw new ArgumentException($"A base anchor offset cannot be {state.BaseOffset}.", nameof(state));
        }

        return OffsetAnchor.AtBase(state.BaseOffset);
    }


    //Validates an anchor that addresses content of this sequence: a base anchor must fall within the base
    //and a live anchor must name a vertex. The head is always servable.
    private static void ValidateTargetAnchor(OffsetAnchor anchor, Dictionary<Dot, Vertex> vertices, int baseLength)
    {
        if(anchor.LiveId is { } liveId)
        {
            if(!vertices.ContainsKey(liveId))
            {
                throw new ArgumentException("A live anchor names a dot that is not a vertex.", nameof(vertices));
            }

            return;
        }

        if(anchor.BaseOffset >= baseLength)
        {
            throw new ArgumentException($"A base anchor offset {anchor.BaseOffset} is at or beyond the base of {baseLength} element(s).", nameof(vertices));
        }
    }


    //Validates every vertex anchor against the base and the vertex set, then proves the live-anchor graph is
    //acyclic: following vertex anchors through AtLive links must terminate at a head or a base anchor rather
    //than loop. A global done-set records vertices already proven to terminate, so each chain is walked once.
    private static void ValidateAnchors(Dictionary<Dot, Vertex> vertices, int baseLength)
    {
        foreach(Vertex vertex in vertices.Values)
        {
            ValidateTargetAnchor(vertex.Anchor, vertices, baseLength);
        }

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
                    throw new ArgumentException("The live-anchor graph contains a cycle.", nameof(vertices));
                }

                if(vertices[current].Anchor.LiveId is not { } parent)
                {
                    break;
                }

                current = parent;
            }

            foreach(Dot visited in onPath)
            {
                done.Add(visited);
            }
        }
    }


    private static int CompareDotsByReplica(Dot left, Dot right)
    {
        int byReplica = left.Replica.CompareTo(right.Replica);

        return byReplica != 0 ? byReplica : left.Counter.CompareTo(right.Counter);
    }


    private static int CompareDotStatesByReplica(DotState left, DotState right)
    {
        ReplicaId leftReplica = ReplicaId.FromSpan(left.Replica.AsSpan());
        ReplicaId rightReplica = ReplicaId.FromSpan(right.Replica.AsSpan());
        int byReplica = leftReplica.CompareTo(rightReplica);

        return byReplica != 0 ? byReplica : left.Counter.CompareTo(right.Counter);
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
            || !BaseFrontier.Equals(other.BaseFrontier)
            || BaseGeneration != other.BaseGeneration
            || !Context.Equals(other.Context)
            || Vertices.Count != other.Vertices.Count
            || Tombstones.Count != other.Tombstones.Count
            || RemovedBaseOffsets.Count != other.RemovedBaseOffsets.Count
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

        foreach(KeyValuePair<Dot, FrozenSet<Dot>> entry in Tombstones)
        {
            //Per-target set equality: a legacy empty set equals only an empty set.
            if(!other.Tombstones.TryGetValue(entry.Key, out FrozenSet<Dot>? otherSet) || !entry.Value.SetEquals(otherSet))
            {
                return false;
            }
        }

        foreach(KeyValuePair<int, FrozenSet<Dot>> entry in RemovedBaseOffsets)
        {
            if(!other.RemovedBaseOffsets.TryGetValue(entry.Key, out FrozenSet<Dot>? otherSet) || !entry.Value.SetEquals(otherSet))
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

        foreach(KeyValuePair<int, OffsetAnchor> entry in CompactedBaseOffsets)
        {
            if(!other.CompactedBaseOffsets.TryGetValue(entry.Key, out OffsetAnchor? otherTarget) || !entry.Value.Equals(otherTarget))
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

        int removedHash = 0;
        foreach(KeyValuePair<int, FrozenSet<Dot>> entry in RemovedBaseOffsets)
        {
            int removeDotsFold = 0;
            foreach(Dot removeDot in entry.Value)
            {
                removeDotsFold ^= removeDot.GetHashCode();
            }

            removedHash ^= HashCode.Combine(entry.Key, removeDotsFold);
        }

        int dotAnchorsHash = 0;
        foreach(KeyValuePair<Dot, OffsetAnchor> entry in CompactedDotAnchors)
        {
            dotAnchorsHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        int baseOffsetsHash = 0;
        foreach(KeyValuePair<int, OffsetAnchor> entry in CompactedBaseOffsets)
        {
            baseOffsetsHash ^= HashCode.Combine(entry.Key, entry.Value);
        }

        return HashCode.Combine(HashCode.Combine(Base.Length, BaseFrontier, BaseGeneration), Context, verticesHash, tombstonesHash, removedHash, dotAnchorsHash, baseOffsetsHash);
    }


    private (OffsetAnchoredSequence<TValue> Result, OffsetAddress InsertedId) Insert(OffsetAnchor after, TValue value, ReplicaId replica)
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

        return (new OffsetAnchoredSequence<TValue>(Base, BaseFrontier, BaseGeneration, RemovedBaseOffsets, advanced, updated.ToFrozenDictionary(), Tombstones, CompactedDotAnchors, CompactedBaseOffsets), new OffsetAddress(OffsetAnchor.AtLive(id), 0));
    }


    private void ValidateAnchor(OffsetAddress anchor)
    {
        OffsetAnchor bare = anchor.Anchor;
        if(bare.LiveId is { } liveId)
        {
            //A live address is canonical generation zero by construction, so there is nothing to compare.
            if(!Vertices.ContainsKey(liveId))
            {
                throw new ArgumentException("The live anchor is not an element of this sequence.", nameof(anchor));
            }

            return;
        }

        //A base address must name this generation, checked before the range check: a stale offset may be out
        //of range in the current base for the wrong reason, so the generation mismatch is the truthful
        //diagnosis. The head is canonical generation zero and has no offset to range-check.
        if(bare.BaseOffset >= 0 && anchor.Generation != BaseGeneration)
        {
            throw new ArgumentException($"The address names base generation {anchor.Generation} but the sequence is at generation {BaseGeneration}; translate the address through TranslateAnchor before editing at it.", nameof(anchor));
        }

        if(bare.BaseOffset >= Base.Length)
        {
            throw new ArgumentException($"The base offset {bare.BaseOffset} is outside the base of {Base.Length} element(s).", nameof(anchor));
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


    //Pushes an anchor's children in reverse canonical order, so an explicit-stack walk pops them in the
    //canonical sibling order with each child's subtree ahead of its next sibling.
    private static void PushChildren(OffsetAnchor anchor, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, Stack<Dot> stack)
    {
        if(!childrenByAnchor.TryGetValue(anchor, out List<Dot>? children))
        {
            return;
        }

        for(int i = children.Count - 1; i >= 0; i--)
        {
            stack.Push(children[i]);
        }
    }


    private static void PushChildren(OffsetAnchor anchor, bool parentRetained, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, Stack<(Dot Child, bool ParentRetained)> stack)
    {
        if(!childrenByAnchor.TryGetValue(anchor, out List<Dot>? children))
        {
            return;
        }

        for(int i = children.Count - 1; i >= 0; i--)
        {
            stack.Push((children[i], parentRetained));
        }
    }


    //Emits the visible members of the live subtree under anchor in canonical order, by explicit stack so a
    //long stable chain cannot overflow the call stack.
    private void AppendSubtree(OffsetAnchor anchor, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, List<(OffsetAddress, TValue)> result)
    {
        var stack = new Stack<Dot>();
        PushChildren(anchor, childrenByAnchor, stack);
        while(stack.Count > 0)
        {
            Dot child = stack.Pop();
            if(!Tombstones.ContainsKey(child))
            {
                result.Add((new OffsetAddress(OffsetAnchor.AtLive(child), 0), Vertices[child].Value));
            }

            PushChildren(OffsetAnchor.AtLive(child), childrenByAnchor, stack);
        }
    }


    //Classifies every vertex against the frontier without recursion: a post-order walk over the anchor
    //forest, then one pass classifying children before parents with the four-way taxonomy — unstable is
    //retained; stable visible converts; stable tombstoned with an uncertified remove converts as
    //pending-removed, always (a legacy empty set is never certified, so it lands here forever); stable
    //tombstoned with a certified remove is retained exactly when a child is retained. The second map folds
    private Dictionary<Dot, bool> ComputeRetention(VectorClock frontier, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor)
    {
        var postOrder = new List<Dot>(Vertices.Count);
        var stack = new Stack<(Dot Dot, bool ChildrenPushed)>();
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            if(entry.Value.Anchor.LiveId is null)
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
            if(childrenByAnchor.TryGetValue(OffsetAnchor.AtLive(dot), out List<Dot>? children))
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
            bool anyChildRetained = false;
            if(childrenByAnchor.TryGetValue(OffsetAnchor.AtLive(dot), out List<Dot>? children))
            {
                foreach(Dot child in children)
                {
                    anyChildRetained |= retention[child];
                }
            }

            bool retained;
            if(!IsStable(dot, frontier))
            {
                retained = true;
            }
            else if(!Tombstones.TryGetValue(dot, out FrozenSet<Dot>? removeDots))
            {
                retained = false;
            }
            else if(!HasCertifiedRemove(removeDots, frontier))
            {
                retained = false;
            }
            else
            {
                //The ghost arm: a certified-removed vertex kept alive because a child is retained. It is
                //DEFENSIVE under Compact's insert-quiescence guard — with no unstable vertex nothing is
                //instability-retained, so anyChildRetained is always false here and this yields false. The
                //arm is kept for the reclamation follow-on, which lifts the guard behind a consensus-carried
                //reclamation set.
                retained = anyChildRetained;
            }

            retention[dot] = retained;
        }

        return retention;
    }


    //Walks the live subtree under anchor in canonical sibling order by explicit stack. A stable visible
    //vertex converts: its value is appended to the new base at its depth-first position. A stable
    //tombstoned vertex with an uncertified remove converts as pending-removed: appended the same way, its
    //new offset marked removed with its remove-dot set riding forward. A retained vertex under a retained
    //parent keeps its recorded anchor, while every other retained vertex is re-anchored at the gap anchor
    //of its position. A dropped certified tombstone records its gap anchor as its translation. The gap
    //anchor at any moment is the most recently appended new-base entry, so it never needs separate
    //bookkeeping. Both conversion branches set the base-changed flag explicitly.
    private void WalkSubtree(OffsetAnchor anchor, Dictionary<OffsetAnchor, List<Dot>> childrenByAnchor, Dictionary<Dot, bool> retention, VectorClock frontier, List<TValue> newBase, Dictionary<int, FrozenSet<Dot>> newRemoved, Dictionary<Dot, OffsetAnchor> dotAnchor, Dictionary<Dot, OffsetAnchor> retainedAnchors, ref bool baseChanged)
    {
        var stack = new Stack<(Dot Child, bool ParentRetained)>();
        PushChildren(anchor, false, childrenByAnchor, stack);
        while(stack.Count > 0)
        {
            (Dot child, bool parentRetained) = stack.Pop();
            bool retained = retention[child];
            if(retained)
            {
                retainedAnchors[child] = parentRetained ? Vertices[child].Anchor : GapAnchor(newBase);
            }
            else if(!Tombstones.TryGetValue(child, out FrozenSet<Dot>? removeDots))
            {
                int newOffset = newBase.Count;
                newBase.Add(Vertices[child].Value);
                dotAnchor[child] = OffsetAnchor.AtBase(newOffset);
                baseChanged = true;
            }
            else if(!HasCertifiedRemove(removeDots, frontier))
            {
                int newOffset = newBase.Count;
                newBase.Add(Vertices[child].Value);
                newRemoved[newOffset] = removeDots;
                dotAnchor[child] = OffsetAnchor.AtBase(newOffset);
                baseChanged = true;
            }
            else
            {
                dotAnchor[child] = GapAnchor(newBase);
            }

            PushChildren(OffsetAnchor.AtLive(child), retained, childrenByAnchor, stack);
        }
    }


    //Composes a prior translation target into this generation: Head stays Head, a base offset resolves
    //through the anchor-typed oldToNew (a reclaimed offset resolves to its gap anchor directly), and a
    //live anchor stays put when its vertex is retained and otherwise resolves to the fresh entry the walk
    //recorded for it.
    private static OffsetAnchor ComposeThroughCompaction(OffsetAnchor anchor, Dictionary<int, OffsetAnchor> oldToNew, Dictionary<Dot, OffsetAnchor> dotAnchor, Dictionary<Dot, bool> retention)
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

        return oldToNew[anchor.BaseOffset];
    }


    private static OffsetAnchor GapAnchor(List<TValue> newBase)
    {
        return newBase.Count == 0 ? OffsetAnchor.Head : OffsetAnchor.AtBase(newBase.Count - 1);
    }


    private static bool IsStable(Dot dot, VectorClock frontier)
    {
        return frontier[dot.Replica] >= dot.Counter;
    }


    //The one insert-quiescence scan Compact's guard and the public UnstableInserts probe share, so the
    //guard's fail-closed set and the probe's report can never drift. Every vertex whose insert-dot the
    //frontier does not cover, ascending by (Replica, Counter) so members holding equal state report the
    //identical order. Empty when the frontier is insert-quiescent, so a passing guard allocates nothing.
    private ImmutableArray<Dot> CollectUnstableInserts(VectorClock stabilityFrontier)
    {
        List<Dot>? uncovered = null;
        foreach(Dot vertex in Vertices.Keys)
        {
            if(!IsStable(vertex, stabilityFrontier))
            {
                uncovered ??= new List<Dot>();
                uncovered.Add(vertex);
            }
        }

        if(uncovered is null)
        {
            return ImmutableArray<Dot>.Empty;
        }

        uncovered.Sort(CompareDotsByReplica);

        return [.. uncovered];
    }


    //A remove is certified at the frontier when at least one of its dots has been observed group-wide.
    //The empty set (a v1-legacy removal) is never certified: that state is retained forever.
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


    //The one constructor of the reserved projection identity. Nothing else may build a 254-prefixed id,
    //and no code may detect a placeholder by its first byte: production replica ids have no reserved
    //range, and non-collision rests on the sentinel's entropy — a random 32-byte id equals the full
    //sentinel value with negligible probability.
    private static ReplicaId SentinelReplica { get; } = BuildSentinelReplica();


    private static ReplicaId BuildSentinelReplica()
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[0] = 254;

        return ReplicaId.FromSpan(bytes);
    }


    private static Dot SentinelDot(int offset)
    {
        return new Dot(SentinelReplica, offset + 1);
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
