namespace Lumoin.Verisync.Core;

/// <summary>
/// Computes the value a versioned register's write proposes from the value that register currently believes
/// committed.
/// </summary>
/// <typeparam name="TValue">The application value type.</typeparam>
/// <param name="current">The value this register believes committed, or the default when it believes none is.</param>
/// <returns>The value to propose at the next version.</returns>
/// <remarks>
/// <para>
/// It runs outside the consensus round and once per attempt. QuePaxa decides among whole proposals rather
/// than composing them, so a losing attempt's proposal is discarded entirely and the next attempt recomputes
/// from the winner; an update written as though its result were composed with the winner would silently lose
/// a write.
/// </para>
/// <para>
/// The argument is a local belief and not a linearizable read. It is what this register learned, which may
/// be behind what a quorum has decided, and the write is what resolves that: it commits, proving the belief
/// current, or it comes back superseded carrying the record that won.
/// </para>
/// </remarks>
public delegate TValue ComputeRegisterValueDelegate<TValue>(TValue? current);
