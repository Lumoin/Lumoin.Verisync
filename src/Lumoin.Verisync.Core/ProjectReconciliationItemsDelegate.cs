using System;
using System.Collections.Generic;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Projects one pinned snapshot of replica state into the fixed-width items a reconciliation contract
/// encodes. The projection carries the injectivity obligation: distinct elements must project to distinct
/// items of exactly <c>contract.ItemWidth</c> bytes, and the projection must be a pure function of the
/// element — replica-independent and time-independent — so two replicas project shared elements to identical
/// items.
/// </summary>
/// <typeparam name="TState">The replica state type the snapshot is taken from.</typeparam>
/// <param name="state">The pinned state snapshot to project.</param>
/// <param name="contract">The contract whose item width and domain the produced items must satisfy.</param>
/// <returns>The items representing the snapshot, each exactly <c>contract.ItemWidth</c> bytes.</returns>
/// <remarks>
/// In <see cref="ReconciliationItemDomain.ContentHash"/> mode the injectivity obligation holds up to a digest
/// collision; in <see cref="ReconciliationItemDomain.Structural"/> mode it is a structural property the
/// projection must guarantee. The caller restricts the reconciled set to above-frontier state, because
/// stability makes below-frontier state identical on both sides and so keeps the encoded difference small.
/// </remarks>
public delegate IReadOnlyCollection<ReadOnlyMemory<byte>> ProjectReconciliationItemsDelegate<in TState>(TState state, ReconciliationContract contract);
