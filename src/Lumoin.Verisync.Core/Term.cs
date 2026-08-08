using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A Raft term: the logical clock that divides time into election epochs, each of which elects at most one
/// leader.
/// </summary>
/// <param name="Value">The term number. Must not be negative.</param>
/// <remarks>
/// <para>
/// A term is a distinct type from <see cref="LogIndex"/> because the Figure 2 messages carry the two in
/// adjacent pairs: a candidate's last log index beside that entry's term, and a leader's previous log index
/// beside that entry's term. Two bare integers in those positions exchange without a diagnostic and change
/// which rule the receiver applies, so they are two types instead.
/// </para>
/// <para>
/// A term is monotone at a node. <see cref="RaftNode{TCommand}.CurrentTerm"/> only rises, and a node that
/// observes a higher one adopts it and reverts to a follower before acting on the message that carried it.
/// </para>
/// <para>
/// The <see langword="default"/> value is <see cref="Zero"/>, the term a node occupies before any election
/// has been held. It is also the term an empty log reports for its last entry, which is what leaves every
/// candidate at least as up to date as a node holding nothing. A term that tags a real log entry is
/// <see cref="First"/> or above.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct Term(long Value): IComparable<Term>
{
    /// <summary>
    /// The term number. It is validated on construction and on a <c>with</c> expression alike, because the
    /// initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public long Value { get; init { field = Validate(value); } } = Validate(Value);


    /// <summary>
    /// The term a node occupies before any election has been held, and the term an empty log reports for its
    /// last entry. No log entry carries it.
    /// </summary>
    public static Term Zero { get; } = new(0);

    /// <summary>The first term an election can produce, and the lowest term that can tag a log entry.</summary>
    public static Term First { get; } = new(1);


    /// <summary>The term after this one, which a candidate adopts when it starts an election.</summary>
    /// <returns>The successor term.</returns>
    /// <exception cref="OverflowException">Thrown if the term range is spent.</exception>
    public Term Next() => new(checked(Value + 1));


    /// <summary>Compares this term with <paramref name="other"/> by number.</summary>
    /// <param name="other">The term to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(Term other) => Value.CompareTo(other.Value);


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(Term left, Term right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(Term left, Term right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(Term left, Term right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(Term left, Term right) => left.CompareTo(right) >= 0;


    private static long Validate(long value)
    {
        //The exception must name the public property, not the validator's parameter.
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Value));

        return value;
    }


    private string DebuggerDisplay => $"Term: {Value}";
}
