using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A single event in a replica's history: the replica that produced it paired with its counter value.
/// </summary>
/// <param name="Replica">The replica that produced the event.</param>
/// <param name="Counter">The replica-local counter value of the event.</param>
/// <remarks>
/// <para>
/// Within a <see cref="DottedVersionVector"/> the dot is <em>contained</em>: its
/// <see cref="Counter"/> equals the dotted vector's context entry for <see cref="Replica"/>, marking
/// the most recent event this value represents.
/// </para>
/// </remarks>
[DebuggerDisplay("{Replica}@{Counter}")]
public sealed record Dot(ReplicaId Replica, int Counter);
