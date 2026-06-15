using System.Diagnostics.CodeAnalysis;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The byte space a reconciliation contract draws its items from. The domain is part of the contract
/// because mixed domains XOR incompatible byte spaces and silently fail to peel: two replicas must agree
/// on how raw element state becomes fixed-width items before their coded streams can be subtracted.
/// </summary>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "The domain values are a pinned wire contract; there is no neutral domain, and a stream must commit to exactly one, so no zero member exists.")]
public enum ReconciliationItemDomain
{
    /// <summary>
    /// Items are digests of canonical element bytes, produced upstream through a
    /// <see cref="ComputeDigestDelegate"/>. Injectivity — distinct elements yielding distinct items —
    /// holds up to a digest collision, dismissible at the contract's 32-byte width.
    /// </summary>
    ContentHash = 1,

    /// <summary>
    /// Items are raw fixed-width identifiers carried straight through. Injectivity is then a structural
    /// obligation of the projection rather than a probabilistic property of a hash.
    /// </summary>
    Structural = 2,
}
