namespace Lumoin.Verisync.Core;

/// <summary>
/// How strictly an encoder polices the projection's injectivity obligation: that an item is added at most
/// once and removed only when present. This is a local-only posture and never part of the wire contract,
/// so two peers may run it differently without affecting what their streams subtract to.
/// </summary>
public enum ReconciliationInjectivityEnforcement
{
    /// <summary>
    /// No checking. A double <c>Add</c> cancels and an unmatched <c>Remove</c> introduces the item, both
    /// following XOR set semantics; the algorithm avoids the membership set's O(n) memory entirely. The default.
    /// </summary>
    None = 0,

    /// <summary>
    /// The same membership checks as <see cref="Strict"/>, but raised through <see cref="System.Diagnostics.Debug.Assert(bool)"/>
    /// so they are present in debug builds and elided in release.
    /// </summary>
    DebugAssert = 1,

    /// <summary>
    /// Keeps a membership set and throws on a duplicate <c>Add</c> or an unmatched <c>Remove</c>. It costs the
    /// O(n) memory the algorithm otherwise avoids, so it is right for structural short identifiers, test rigs,
    /// and adversarial experiments rather than production reconciliation of large sets.
    /// </summary>
    Strict = 2,
}
