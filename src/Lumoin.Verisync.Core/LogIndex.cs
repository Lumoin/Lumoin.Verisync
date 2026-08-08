using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// A position in a Raft replicated log. Protocol indices are 1-based: index <c>i</c> is the entry at
/// zero-based position <c>i - 1</c>.
/// </summary>
/// <param name="Value">The index. Must not be negative.</param>
/// <remarks>
/// <para>
/// An index is a distinct type from <see cref="Term"/> for the reason <see cref="Term"/> states: the Figure 2
/// messages carry an index and a term side by side, and two bare integers there exchange silently.
/// </para>
/// <para>
/// The <see langword="default"/> value is <see cref="BeforeFirst"/>, the empty prefix ahead of the first
/// entry. Every log matches there, which is what makes it the consistency check's base case and the value a
/// leader's <c>matchIndex</c> starts a follower at. It names no entry, so <see cref="Position"/> and
/// <see cref="Previous"/> refuse it.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct LogIndex(long Value): IComparable<LogIndex>
{
    /// <summary>
    /// The index. It is validated on construction and on a <c>with</c> expression alike, because the
    /// initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is negative.</exception>
    public long Value { get; init { field = Validate(value); } } = Validate(Value);


    /// <summary>The empty prefix ahead of the first entry, which every log matches and which names no entry.</summary>
    public static LogIndex BeforeFirst { get; } = new(0);

    /// <summary>The index of the first entry a log can hold.</summary>
    public static LogIndex First { get; } = new(1);


    /// <summary>Whether this is the empty prefix, so that it names no entry.</summary>
    public bool IsBeforeFirst => Value == 0;

    /// <summary>
    /// The zero-based position of this entry in the log's backing storage.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if this is <see cref="BeforeFirst"/>, which names no entry.</exception>
    /// <exception cref="OverflowException">Thrown if the position lies beyond what an in-memory log can address.</exception>
    public int Position
    {
        get
        {
            if(IsBeforeFirst)
            {
                throw new InvalidOperationException("The empty prefix names no entry, so it has no position in the log.");
            }

            return checked((int)(Value - 1));
        }
    }


    /// <summary>The index of the entry after this one.</summary>
    /// <returns>The successor index.</returns>
    /// <exception cref="OverflowException">Thrown if the index range is spent.</exception>
    public LogIndex Next() => new(checked(Value + 1));


    /// <summary>The index of the entry before this one.</summary>
    /// <returns>The predecessor index, which is <see cref="BeforeFirst"/> for <see cref="First"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is <see cref="BeforeFirst"/>, which has no predecessor.</exception>
    public LogIndex Previous()
    {
        if(IsBeforeFirst)
        {
            throw new InvalidOperationException("The empty prefix is the base of the log and has no predecessor.");
        }

        return new LogIndex(Value - 1);
    }


    /// <summary>The index <paramref name="count"/> entries past this one.</summary>
    /// <param name="count">How many entries to advance by. Must not be negative.</param>
    /// <returns>The advanced index, which is this one when <paramref name="count"/> is zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="count"/> is negative.</exception>
    /// <exception cref="OverflowException">Thrown if the index range is spent.</exception>
    public LogIndex Advance(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return new LogIndex(checked(Value + count));
    }


    /// <summary>The lower of two indices.</summary>
    /// <param name="left">The first index.</param>
    /// <param name="right">The second index.</param>
    /// <returns>Whichever orders first.</returns>
    public static LogIndex Min(LogIndex left, LogIndex right) => left <= right ? left : right;


    /// <summary>The higher of two indices.</summary>
    /// <param name="left">The first index.</param>
    /// <param name="right">The second index.</param>
    /// <returns>Whichever orders last.</returns>
    public static LogIndex Max(LogIndex left, LogIndex right) => left >= right ? left : right;


    /// <summary>Compares this index with <paramref name="other"/> by position.</summary>
    /// <param name="other">The index to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(LogIndex other) => Value.CompareTo(other.Value);


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(LogIndex left, LogIndex right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(LogIndex left, LogIndex right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(LogIndex left, LogIndex right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(LogIndex left, LogIndex right) => left.CompareTo(right) >= 0;


    private static long Validate(long value)
    {
        //The exception must name the public property, not the validator's parameter.
        ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(Value));

        return value;
    }


    private string DebuggerDisplay => $"LogIndex: {Value}";
}
