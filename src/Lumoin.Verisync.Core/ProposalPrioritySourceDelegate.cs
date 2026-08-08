namespace Lumoin.Verisync.Core;

/// <summary>
/// Draws the priority a proposer attaches to its working proposal in a phase-zero send.
/// </summary>
/// <returns>An ordinary priority, that is one satisfying <see cref="ProposalPriority.IsOrdinary"/>.</returns>
/// <remarks>
/// <para>
/// A source that returns <see cref="ProposalPriority.None"/> or <see cref="ProposalPriority.Reserved"/> is a
/// protocol violation and the caller refuses to send it. Returning the reserved priority forges a leader
/// claim, and returning the absent one puts the aggregate's identity element on the wire.
/// </para>
/// <para>
/// The production source is <see cref="ProposalPriority.Cryptographic"/>. A deterministic source lets a test
/// construct a specific ordering, and the priority draw is per recorder, so a seeded source reproduces a run
/// exactly only while the caller keeps its recorder iteration order.
/// </para>
/// </remarks>
public delegate ProposalPriority ProposalPrioritySourceDelegate();
