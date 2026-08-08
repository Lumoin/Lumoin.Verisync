using System;
using System.Diagnostics;

namespace Lumoin.Verisync.Core;

/// <summary>
/// The version a versioned register's value carries: a monotone counter whose every value is one
/// consensus instance.
/// </summary>
/// <param name="Value">The version number. Must lie between zero and the value of <see cref="MaxValue"/>.</param>
/// <remarks>
/// <para>
/// The bound comes from the wire format and not from arithmetic. A version crosses JSON as a bare number, and
/// every integer up to two to the fifty-third is exactly representable as an IEEE double while nothing above
/// it is, so a consumer that parses JSON numbers as doubles reads every representable version exactly. The
/// hazard <see cref="ProposalPriority"/> has to document, a reserved priority silently demoted by a foreign
/// reader, does not arise for this field. <see cref="ProposalPriority"/> cannot take the same cap, because its
/// reserved value is the whole range's maximum and the protocol depends on it.
/// </para>
/// <para>
/// The cost of the cap is that no value can detect a double-parsing reader: inside this range no two versions
/// collapse onto one double, so every value round-trips through either kind of reader alike. A codec pins its
/// accessor with a non-integral token instead of with a boundary value.
/// </para>
/// <para>
/// The range bounds the field and not the instances a peer can name. A recorder host serves one instance and
/// refuses every other version, and that rule, not this range, keeps a peer from forcing an allocation per
/// message.
/// </para>
/// <para>
/// The <see langword="default"/> value is <see cref="Unwritten"/>, the version of a register that was never
/// written. No request ever carries it.
/// </para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly record struct RegisterVersion(ulong Value): IComparable<RegisterVersion>
{
    private const ulong MaxVersionValue = (1UL << 53) - 1;


    /// <summary>
    /// The version number. It is validated on construction and on a <c>with</c> expression alike, because the
    /// initializer writes the backing field directly and no accessor runs for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the value is above the value of <see cref="MaxValue"/>.</exception>
    public ulong Value { get; init { field = Validate(value); } } = Validate(Value);


    /// <summary>The version of a register that was never written. No request ever carries it.</summary>
    public static RegisterVersion Unwritten { get; } = new(0UL);

    /// <summary>The first version a write can produce.</summary>
    public static RegisterVersion First { get; } = new(1UL);

    /// <summary>The highest representable version, which is one below two to the fifty-third.</summary>
    public static RegisterVersion MaxValue { get; } = new(MaxVersionValue);


    /// <summary>Whether this is the last representable version, so that no successor exists.</summary>
    public bool IsExhausted => Value == MaxVersionValue;

    /// <summary>Whether a write has produced this version, which every version above <see cref="Unwritten"/> has.</summary>
    public bool IsWritten => Value != 0UL;


    /// <summary>The version after this one.</summary>
    /// <returns>The successor version.</returns>
    /// <exception cref="InvalidOperationException">Thrown if this is <see cref="MaxValue"/>.</exception>
    /// <remarks>
    /// The throw is the fail-closed backstop for a caller that did not test <see cref="IsExhausted"/> first. A
    /// register that has spent the range is reconfigured rather than wrapped, because a wrapped version would
    /// name a consensus instance that has already decided.
    /// </remarks>
    public RegisterVersion Next()
    {
        if(IsExhausted)
        {
            throw new InvalidOperationException("The version range is spent; the last representable version has no successor.");
        }

        return new RegisterVersion(Value + 1);
    }


    /// <summary>Compares this version with <paramref name="other"/> by number.</summary>
    /// <param name="other">The version to compare with.</param>
    /// <returns>A negative value, zero, or a positive value per the standard comparison contract.</returns>
    public int CompareTo(RegisterVersion other) => Value.CompareTo(other.Value);


    /// <summary>Determines whether <paramref name="left"/> orders before <paramref name="right"/>.</summary>
    public static bool operator <(RegisterVersion left, RegisterVersion right) => left.CompareTo(right) < 0;

    /// <summary>Determines whether <paramref name="left"/> orders before or equal to <paramref name="right"/>.</summary>
    public static bool operator <=(RegisterVersion left, RegisterVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Determines whether <paramref name="left"/> orders after <paramref name="right"/>.</summary>
    public static bool operator >(RegisterVersion left, RegisterVersion right) => left.CompareTo(right) > 0;

    /// <summary>Determines whether <paramref name="left"/> orders after or equal to <paramref name="right"/>.</summary>
    public static bool operator >=(RegisterVersion left, RegisterVersion right) => left.CompareTo(right) >= 0;


    private static ulong Validate(ulong value)
    {
        //The exception must name the public property, not the validator's parameter.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxVersionValue, nameof(Value));

        return value;
    }


    private string DebuggerDisplay => $"RegisterVersion: {Value}";
}
