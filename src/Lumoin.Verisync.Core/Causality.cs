namespace Lumoin.Verisync.Core;

/// <summary>
/// The causal relationship between two causal contexts (for example two <see cref="VectorClock"/> values).
/// </summary>
/// <remarks>
/// <para>
/// Causal contexts form a partial order, not a total order: two contexts that each observed an event
/// the other did not are <see cref="Concurrent"/>.
/// </para>
/// </remarks>
public enum Causality
{
    /// <summary>The left context happened strictly before the right context.</summary>
    Before,

    /// <summary>The left context happened strictly after the right context.</summary>
    After,

    /// <summary>The two contexts are equal.</summary>
    Equal,

    /// <summary>The two contexts are concurrent: neither dominates the other.</summary>
    Concurrent
}
