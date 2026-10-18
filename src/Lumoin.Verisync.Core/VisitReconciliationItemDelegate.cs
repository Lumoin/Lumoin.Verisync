using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Receives one item of a snapshot during an enumeration. The span is valid only for the duration of the call.
/// </summary>
/// <param name="item">The item bytes, exactly the contract's item width.</param>
public delegate void VisitReconciliationItemDelegate(ReadOnlySpan<byte> item);
