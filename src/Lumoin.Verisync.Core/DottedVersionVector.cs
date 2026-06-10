using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A vector clock paired with an optional <see cref="Dot"/> identifying the most recent event this
/// value represents. The dot is <em>contained</em>: when present, its counter equals the context
/// entry for its replica.
/// </summary>
/// <remarks>
/// <para>
/// A dotted version vector distinguishes "the most recent event from this replica" (the dot) from
/// "everything this replica has observed" (the context). It is an immutable value;
/// <see cref="AdvanceDot(ReplicaId)"/> and <see cref="Merge(DottedVersionVector)"/> return new
/// instances and never mutate the receiver.
/// </para>
/// <para>
/// A single dot cannot represent two concurrent sibling events. When a merge would need to retain two
/// distinct current dots, the dot is cleared; retaining concurrent siblings is the responsibility of a
/// dotted-version-vector <em>set</em>, introduced with the observed-remove set in a later wave.
/// </para>
/// <para>
/// Because of that clearing rule, only the <see cref="Context"/> is a join-semilattice. The dot
/// component is not associative: re-merging a state whose dot was already incorporated can bring a
/// cleared dot back, so replicas that received the same states in different orders converge on
/// <see cref="Context"/> but may disagree on <see cref="Dot"/> indefinitely. Do not build replicated
/// decisions on <see cref="Dot"/> after gossip; treat it as a local hint. Convergent
/// concurrent-value tracking lives in <see cref="DottedVersionVectorSet{TValue}"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class DottedVersionVector: IEquatable<DottedVersionVector>
{
    private DottedVersionVector(VectorClock context, Dot? dot)
    {
        Context = context;
        Dot = dot;
    }


    /// <summary>An empty dotted version vector: an empty context and no dot.</summary>
    public static DottedVersionVector Empty { get; } = new(VectorClock.Empty, null);


    /// <summary>The causal context: everything the owning replica has observed.</summary>
    public VectorClock Context { get; }

    /// <summary>The most recent event this value represents, or <see langword="null"/> if there is none.</summary>
    public Dot? Dot { get; }


    /// <summary>
    /// Returns a new dotted version vector advanced by one event for <paramref name="replica"/>: the
    /// context entry is incremented and the dot is set to the new (replica, counter) pair.
    /// </summary>
    /// <param name="replica">The replica producing the new event.</param>
    /// <returns>A new <see cref="DottedVersionVector"/>; this instance is not modified.</returns>
    public DottedVersionVector AdvanceDot(ReplicaId replica)
    {
        VectorClock advanced = Context.Increment(replica);

        return new DottedVersionVector(advanced, new Dot(replica, advanced[replica]));
    }


    /// <summary>
    /// Returns the merge of this dotted version vector and <paramref name="other"/>: the element-wise
    /// maximum of the two contexts, retaining a dot only if exactly one current dot survives.
    /// </summary>
    /// <param name="other">The dotted version vector to merge with.</param>
    /// <returns>A new <see cref="DottedVersionVector"/>; neither operand is modified.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public DottedVersionVector Merge(DottedVersionVector other)
    {
        ArgumentNullException.ThrowIfNull(other);

        VectorClock mergedContext = Context.Merge(other.Context);
        Dot? mergedDot = SelectDot(mergedContext, Dot, other.Dot);

        return new DottedVersionVector(mergedContext, mergedDot);
    }


    /// <summary>
    /// Compares this dotted version vector with <paramref name="other"/> in the happens-before partial
    /// order of their contexts.
    /// </summary>
    /// <param name="other">The dotted version vector to compare with.</param>
    /// <returns>The causal relationship between the two contexts.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public Causality Compare(DottedVersionVector other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Context.Compare(other.Context);
    }


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] DottedVersionVector? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Context.Equals(other.Context) && Equals(Dot, other.Dot);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DottedVersionVector other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Context, Dot);


    private static Dot? SelectDot(VectorClock mergedContext, Dot? left, Dot? right)
    {
        bool leftCurrent = IsCurrent(mergedContext, left);
        bool rightCurrent = IsCurrent(mergedContext, right);

        if(leftCurrent && rightCurrent)
        {
            return left!.Equals(right) ? left : null;
        }

        if(leftCurrent)
        {
            return left;
        }

        if(rightCurrent)
        {
            return right;
        }

        return null;
    }


    private static bool IsCurrent(VectorClock context, [NotNullWhen(true)] Dot? dot)
    {
        return dot is not null && context[dot.Replica] == dot.Counter;
    }


    private string DebuggerDisplay => Dot is null
        ? $"DVV: {Context}, dot=(none)"
        : $"DVV: {Context}, dot={Dot.Replica}@{Dot.Counter}";
}
