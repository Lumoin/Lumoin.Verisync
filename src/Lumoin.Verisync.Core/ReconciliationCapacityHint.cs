using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// Pre-sizing hints for a reconciliation encoder or decoder: a lower bound on the items it will hold and a lower
/// bound on the cells it will touch. Either may be zero, which lets the store grow from its minimum.
/// </summary>
public readonly record struct ReconciliationCapacityHint
{
    /// <summary>Initializes a hint of <paramref name="items"/> items and <paramref name="cells"/> cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="items"/> or <paramref name="cells"/> is negative.</exception>
    public ReconciliationCapacityHint(int items, int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(items);
        ArgumentOutOfRangeException.ThrowIfNegative(cells);

        Items = items;
        Cells = cells;
    }


    /// <summary>A lower bound on the items the store will hold; sizes the item arena and the pending cursors.</summary>
    public int Items { get; }

    /// <summary>A lower bound on the cells the session will touch; sizes the produced-cell store.</summary>
    public int Cells { get; }

    /// <summary>The hint that lets every store grow from its minimum.</summary>
    public static ReconciliationCapacityHint None { get; } = new(0, 0);
}
