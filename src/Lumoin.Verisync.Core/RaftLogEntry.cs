using System;

namespace Lumoin.Verisync.Core;

/// <summary>
/// One entry in a Raft replicated log: an application <paramref name="Command"/> tagged with the
/// <paramref name="Term"/> in which the leader that originated it held office.
/// </summary>
/// <typeparam name="TCommand">The application command type carried by the entry.</typeparam>
/// <param name="Term">
/// The term of the leader that created this entry, which is <see cref="Core.Term.First"/> or above because
/// only an elected leader creates one. The term stamp is what lets followers detect log divergence: two logs
/// that agree on the term at a given index agree on every entry up to it, the inductive invariant that the
/// consistency check in <see cref="RaftNode{TCommand}.HandleAppendEntries"/> relies on.
/// </param>
/// <param name="Command">The application command this entry replicates and, once committed, applies.</param>
/// <remarks>
/// An entry is immutable. The log itself is an ordered, append-mostly sequence whose protocol indices are
/// 1-based — protocol index <c>i</c> is the entry at zero-based position <c>i - 1</c> — while truncation on
/// conflict is the only way an already-stored index changes, and only on a follower reconciling with a
/// newer leader.
/// </remarks>
public sealed record RaftLogEntry<TCommand>(Term Term, TCommand Command)
{
    /// <summary>
    /// The term of the leader that created this entry. It is validated on construction and on a <c>with</c>
    /// expression alike, because the initializer writes the backing field directly and no accessor runs for
    /// it. <see cref="Core.Term.Zero"/> is the term a node holds before any election and the one an empty log
    /// reports for its last entry, so it tags no real entry and an entry carrying it is unrepresentable rather
    /// than refused downstream.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the term is below <see cref="Core.Term.First"/>.</exception>
    public Term Term { get; init { field = Validate(value); } } = Validate(Term);


    private static Term Validate(Term value)
    {
        //The exception must name the public property, not the validator's parameter.
        ArgumentOutOfRangeException.ThrowIfLessThan(value, Core.Term.First, nameof(Term));

        return value;
    }
}
