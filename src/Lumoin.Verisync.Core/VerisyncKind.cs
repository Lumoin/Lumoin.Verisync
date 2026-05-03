namespace Lumoin.Verisync.Core;

/// <summary>
/// Identifies the kind of byte payload carried by a <see cref="TaggedMemory"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a closed set defined by the protocol. Applications do not extend it;
/// a separate value-typed dynamic enumeration carries application-defined
/// operation classifications on a different axis. Applications attaching
/// additional metadata use other keys within the same <see cref="Tag"/>.
/// </para>
/// </remarks>
/// <seealso cref="VerisyncTags"/>
public enum VerisyncKind
{
    /// <summary>The pseudonymous random bytes that identify a participating replica.</summary>
    ReplicaId,

    /// <summary>The bytes identifying a single operation within a replica's history.</summary>
    OperationId,

    /// <summary>The encoded bytes of a consensus ballot.</summary>
    BallotEncoding,

    /// <summary>The serialized bytes of a delta-state CRDT update.</summary>
    SerializedDelta,

    /// <summary>The opaque bytes of an authorization witness presented at the protocol surface.</summary>
    AuthorizationWitness,

    /// <summary>The raw bytes of a register value.</summary>
    RegisterValueBytes,

    /// <summary>The digest bytes exchanged during anti-entropy gossip.</summary>
    GossipDigest
}
