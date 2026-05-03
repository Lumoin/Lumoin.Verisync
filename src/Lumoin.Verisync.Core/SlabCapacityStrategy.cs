namespace Lumoin.Verisync.Core;

/// <summary>
/// Determines how many segments a new slab allocates for a given segment size, letting callers tune the
/// amortization of allocation across rentals.
/// </summary>
/// <param name="segmentSize">The size of each segment in elements.</param>
/// <returns>The number of segments to allocate in the new slab. Must be greater than zero.</returns>
public delegate int SlabCapacityStrategy(int segmentSize);
