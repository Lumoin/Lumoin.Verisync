using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A replica's causal summary, advertised to peers during anti-entropy so each side can decide which
/// direction state needs to flow.
/// </summary>
/// <remarks>
/// <para>
/// A digest pairs the advertising replica (<see cref="Origin"/>) with the <see cref="VectorClock"/> of
/// everything it has observed (<see cref="Summary"/>). Comparing two digests yields the causal
/// relationship between their summaries: a replica that is <see cref="Causality.Before"/> or
/// <see cref="Causality.Concurrent"/> relative to a peer has events to pull from that peer.
/// </para>
/// <para>
/// The digest carries no deltas itself. Catch-up on rejoin and read repair are performed by merging the
/// underlying CRDT state; the transport that exchanges digests and state is wired in above the core
/// through delegates rather than owned here.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class GossipDigest: IEquatable<GossipDigest>
{
    /// <summary>
    /// Initializes a new digest.
    /// </summary>
    /// <param name="origin">The replica advertising the digest.</param>
    /// <param name="summary">The causal summary of everything that replica has observed.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="summary"/> is <see langword="null"/>.</exception>
    public GossipDigest(ReplicaId origin, VectorClock summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Origin = origin;
        Summary = summary;
    }


    /// <summary>The replica advertising this digest.</summary>
    public ReplicaId Origin { get; }

    /// <summary>The causal summary of everything the advertising replica has observed.</summary>
    public VectorClock Summary { get; }


    /// <summary>
    /// Compares this digest's summary with <paramref name="other"/>'s in the happens-before partial order.
    /// </summary>
    /// <param name="other">The digest to compare with.</param>
    /// <returns>The causal relationship between the two summaries.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public Causality Compare(GossipDigest other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Summary.Compare(other.Summary);
    }


    /// <summary>
    /// Whether this replica is missing events that <paramref name="other"/> has observed, and so should
    /// pull from it.
    /// </summary>
    /// <param name="other">The peer digest.</param>
    /// <returns><see langword="true"/> if the comparison is <see cref="Causality.Before"/> or <see cref="Causality.Concurrent"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public bool IsBehind(GossipDigest other) => Compare(other) is Causality.Before or Causality.Concurrent;


    /// <summary>
    /// Whether this replica has observed events that <paramref name="other"/> is missing, and so should
    /// push to it.
    /// </summary>
    /// <param name="other">The peer digest.</param>
    /// <returns><see langword="true"/> if the comparison is <see cref="Causality.After"/> or <see cref="Causality.Concurrent"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public bool IsAheadOf(GossipDigest other) => Compare(other) is Causality.After or Causality.Concurrent;


    /// <summary>
    /// Whether this replica has observed everything <paramref name="other"/> has, needing nothing from it.
    /// </summary>
    /// <param name="other">The peer digest.</param>
    /// <returns><see langword="true"/> if the comparison is <see cref="Causality.After"/> or <see cref="Causality.Equal"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="other"/> is <see langword="null"/>.</exception>
    public bool IsUpToDateWith(GossipDigest other) => Compare(other) is Causality.After or Causality.Equal;


    /// <inheritdoc/>
    public bool Equals([NotNullWhen(true)] GossipDigest? other)
    {
        if(other is null)
        {
            return false;
        }

        if(ReferenceEquals(this, other))
        {
            return true;
        }

        return Origin.Equals(other.Origin) && Summary.Equals(other.Summary);
    }


    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GossipDigest other && Equals(other);


    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Origin, Summary);


    private string DebuggerDisplay => $"GossipDigest: {Origin} knows {Summary}";
}
