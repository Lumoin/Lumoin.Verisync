namespace Lumoin.Verisync.Core;

/// <summary>
/// One member of a configuration: the replica identity a quorum counts and the store incarnation admitted to
/// answer for it.
/// </summary>
/// <param name="Replica">The replica identity, which is what quorum arithmetic counts and what a schedule orders.</param>
/// <param name="Incarnation">The store instance admitted to answer for <paramref name="Replica"/>.</param>
/// <remarks>
/// <para>
/// The pair is one value and not two lists. A configuration holding identities beside a separate array of
/// incarnations could be built with the two of different lengths or in different orders, and a member would
/// then be admitted under another member's store; holding the pair makes that unconstructible, for the same
/// reason the member list is not split into a recorder set and a hedging order.
/// </para>
/// <para>
/// A configuration lists an identity at most once however many stores exist for it. Quorums are counted over
/// distinct identities, so two incarnations of one identity in one configuration would answer twice and be
/// counted twice, and a decision would be taken by fewer replicas than the arithmetic claims. Replacing a
/// member's store is therefore a configuration change and not an addition.
/// </para>
/// </remarks>
public readonly record struct ConfiguredMember(ReplicaId Replica, StoreIncarnation Incarnation);
