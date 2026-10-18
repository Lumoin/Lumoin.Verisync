using System.Threading;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Enumerates every item of one pinned snapshot, once each, in an order that is stable for that snapshot, by
/// calling <paramref name="visit"/> for each. The enumeration is restartable: every call sees the same items in
/// the same order until the snapshot is released. The injectivity obligation of
/// <see cref="ProjectReconciliationItemsDelegate{TState}"/> applies: distinct elements project to distinct items
/// of exactly the contract's width, and an item enumerated twice XOR-cancels silently.
/// </summary>
/// <param name="visit">The receiver of each item, called synchronously during this enumeration only.</param>
/// <param name="cancellationToken">Observed at least once per item; cancellation faults the encoder.</param>
public delegate void EnumerateReconciliationItemsDelegate(VisitReconciliationItemDelegate visit, CancellationToken cancellationToken);
