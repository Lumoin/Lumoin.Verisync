using Lumoin.Base;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Pre-built <see cref="Tag"/> instances, one per <see cref="VerisyncKind"/> value.
/// </summary>
/// <remarks>
/// <para>
/// Each tag carries the matching <see cref="VerisyncKind"/> entry. The tags are shared
/// singletons; concrete <see cref="TaggedMemory"/> subclasses pick the matching one at
/// construction time rather than allocating a new tag per instance.
/// </para>
/// <code>
/// var replicaTag = VerisyncTags.ReplicaId;
/// VerisyncKind kind = replicaTag.Get&lt;VerisyncKind&gt;();
/// </code>
/// </remarks>
public static class VerisyncTags
{
    /// <summary>Tag carrying <see cref="VerisyncKind.ReplicaId"/>.</summary>
    public static Tag ReplicaId { get; } = Tag.Create(VerisyncKind.ReplicaId);

    /// <summary>Tag carrying <see cref="VerisyncKind.OperationId"/>.</summary>
    public static Tag OperationId { get; } = Tag.Create(VerisyncKind.OperationId);

    /// <summary>Tag carrying <see cref="VerisyncKind.BallotEncoding"/>.</summary>
    public static Tag BallotEncoding { get; } = Tag.Create(VerisyncKind.BallotEncoding);

    /// <summary>Tag carrying <see cref="VerisyncKind.SerializedDelta"/>.</summary>
    public static Tag SerializedDelta { get; } = Tag.Create(VerisyncKind.SerializedDelta);

    /// <summary>Tag carrying <see cref="VerisyncKind.AuthorizationWitness"/>.</summary>
    public static Tag AuthorizationWitness { get; } = Tag.Create(VerisyncKind.AuthorizationWitness);

    /// <summary>Tag carrying <see cref="VerisyncKind.RegisterValueBytes"/>.</summary>
    public static Tag RegisterValueBytes { get; } = Tag.Create(VerisyncKind.RegisterValueBytes);

    /// <summary>Tag carrying <see cref="VerisyncKind.GossipDigest"/>.</summary>
    public static Tag GossipDigest { get; } = Tag.Create(VerisyncKind.GossipDigest);
}
