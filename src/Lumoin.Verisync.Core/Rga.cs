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
/// predecessor. Because vertices and tombstones are grow-only, <see cref="Merge(Rga{TValue})"/> is a
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


    private Rga(VectorClock context, FrozenDictionary<Dot, Vertex> vertices, FrozenSet<Dot> tombstones)
    {
        Context = context;
        Vertices = vertices;
        Tombstones = tombstones;
    }


    /// <summary>An empty array.</summary>
    public static Rga<TValue> Empty { get; } = new(VectorClock.Empty, FrozenDictionary<Dot, Vertex>.Empty, FrozenSet<Dot>.Empty);


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

        return new Rga<TValue>(Context, Vertices, updated.ToFrozenSet());
    }


    /// <summary>
    /// Returns the merge of this array and <paramref name="other"/>: the union of their vertices and
    /// tombstones.
    /// </summary>
    /// <param name="other">The array to merge with.</param>
    /// <returns>A new <see cref="Rga{TValue}"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
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

        return new Rga<TValue>(Context.Merge(other.Context), mergedVertices.ToFrozenDictionary(), mergedTombstones.ToFrozenSet());
    }


    /// <summary>
    /// Returns the serializable state of this array, for persistence or transfer.
    /// </summary>
    /// <returns>The array's state.</returns>
    public RgaState<TValue> ToState()
    {
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


    /// <summary>
    /// Reconstructs an array from previously serialized <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The state to reconstruct from.</param>
    /// <returns>The reconstructed array.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="state"/> is <see langword="null"/>.</exception>
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

        var tombstones = new HashSet<Dot>(state.Tombstones.Length);
        foreach(DotState tombstone in state.Tombstones)
        {
            tombstones.Add(new Dot(ReplicaId.FromSpan(tombstone.Replica.AsSpan()), tombstone.Counter));
        }

        return new Rga<TValue>(context, vertices.ToFrozenDictionary(), tombstones.ToFrozenSet());
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

        if(!Context.Equals(other.Context) || Vertices.Count != other.Vertices.Count || !Tombstones.SetEquals(other.Tombstones))
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

        return HashCode.Combine(Context, verticesHash, tombstonesHash);
    }


    private (Rga<TValue> Result, Dot InsertedId) Insert(Dot? after, TValue value, ReplicaId replica)
    {
        VectorClock advanced = Context.Increment(replica);
        var id = new Dot(replica, advanced[replica]);

        var updated = new Dictionary<Dot, Vertex>(Vertices.Count + 1);
        foreach(KeyValuePair<Dot, Vertex> entry in Vertices)
        {
            updated[entry.Key] = entry.Value;
        }

        updated[id] = new Vertex(after, value);

        return (new Rga<TValue>(advanced, updated.ToFrozenDictionary(), Tombstones), id);
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


    private string DebuggerDisplay => $"Rga: {Count} visible, {Tombstones.Count} tombstoned";


    private sealed record Vertex(Dot? Predecessor, TValue Value);
}
